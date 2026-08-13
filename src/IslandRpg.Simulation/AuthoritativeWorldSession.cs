using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner, headless authority for connected actors. Network workers only
/// enqueue operations; all authoritative mutation occurs in <see cref="Tick"/>
/// or <see cref="Drain"/> on one owning thread.
/// </summary>
public sealed class AuthoritativeWorldSession
{
    private readonly SimulationLimits _limits;
    private readonly ISessionIdentitySource _identitySource;
    private readonly IWorldNavigationQuery _navigation;
    private readonly IWorldNavigationObstacleSource _obstacles;
    private readonly AuthoritativeWorldTransactions _worldTransactions;
    private readonly Channel<QueuedOperation> _inbound;
    private readonly Dictionary<ActorId, MutableActor> _actors = [];
    private readonly Dictionary<PlayerId, ActorId> _actorsByPlayer = [];
    private readonly Dictionary<ClientConnectionId, PlayerId> _playersByConnection = [];
    private readonly Queue<ChatMessageSnapshot> _chatHistory = [];
    private readonly Dictionary<ActorId, ActiveCookingJob> _cookingJobs = [];
    private SessionSnapshot _latestSnapshot;
    private int? _ownerThreadId;
    private int _executing;
    private long _nextChatMessageId;

    public AuthoritativeWorldSession(
        SimulationLimits? limits = null,
        ISessionIdentitySource? identitySource = null,
        SessionId? sessionId = null,
        IWorldNavigationQuery? navigation = null,
        IWorldNavigationObstacleSource? obstacles = null,
        AuthoritativeWorldTransactions? worldTransactions = null)
    {
        _limits = (limits ?? SimulationLimits.Default).ValidatedCopy();
        _identitySource = identitySource ?? new SecureSessionIdentitySource();
        _navigation = navigation ?? OpenWorldNavigationQuery.Instance;
        _obstacles = obstacles ?? EmptyWorldNavigationObstacleSource.Instance;
        _worldTransactions = worldTransactions ??
            new AuthoritativeWorldTransactions();
        Id = sessionId is { } provided && provided.Value != Guid.Empty
            ? provided
            : SessionId.New();
        Clock = new DeterministicSimulationClock();
        _latestSnapshot = SessionSnapshot.Empty(Id);
        _inbound = Channel.CreateBounded<QueuedOperation>(new BoundedChannelOptions(
            _limits.InboundCommandCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public SessionId Id { get; }

    public DeterministicSimulationClock Clock { get; }

    public SimulationLimits Limits => _limits;

    public int? OwnerThreadId => _ownerThreadId;

    public int ActorCount
    {
        get
        {
            EnsureOwnerThread();
            return _actors.Count;
        }
    }

    /// <summary>
    /// The last 20 Hz publication. It is immutable and safe to read from any thread.
    /// </summary>
    public SessionSnapshot LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    /// <summary>
    /// Raised on the single authority thread after a world transaction has
    /// committed. Observers must be fast and must not mutate the session.
    /// </summary>
    public event Action<WorldTransactionResult>? WorldTransactionCommitted;

    public event Action<CookingCompletionSnapshot>? CookingCompleted;

    public Task<JoinResult> EnqueueJoinAsync(JoinRequest request)
    {
        var completion = NewCompletion<JoinResult>();
        if (!_inbound.Writer.TryWrite(new JoinOperation(request, completion)))
        {
            completion.SetResult(new JoinResult(
                JoinStatus.QueueFull,
                default,
                default,
                0,
                "The authoritative command queue is full."));
        }

        return completion.Task;
    }

    public Task<ReconnectResult> EnqueueReconnectAsync(ReconnectRequest request)
    {
        var completion = NewCompletion<ReconnectResult>();
        if (!_inbound.Writer.TryWrite(new ReconnectOperation(request, completion)))
        {
            completion.SetResult(new ReconnectResult(
                ReconnectStatus.QueueFull,
                default,
                0,
                "The authoritative command queue is full."));
        }

        return completion.Task;
    }

    public Task<DisconnectResult> EnqueueDisconnectAsync(DisconnectRequest request)
    {
        var completion = NewCompletion<DisconnectResult>();
        if (!_inbound.Writer.TryWrite(new DisconnectOperation(request, completion)))
        {
            completion.SetResult(new DisconnectResult(
                DisconnectStatus.QueueFull,
                "The authoritative command queue is full."));
        }

        return completion.Task;
    }

    /// <summary>
    /// Enqueues a command without allocating an acknowledgement task. This is the
    /// preferred path for high-frequency movement input.
    /// </summary>
    public bool TryEnqueueIntent(ActorCommand command) =>
        _inbound.Writer.TryWrite(new IntentOperation(command, null));

    public Task<IntentResult> EnqueueIntentAsync(ActorCommand command)
    {
        var completion = NewCompletion<IntentResult>();
        if (!_inbound.Writer.TryWrite(new IntentOperation(command, completion)))
        {
            completion.SetResult(new IntentResult(
                IntentStatus.QueueFull,
                0,
                "The authoritative command queue is full."));
        }

        return completion.Task;
    }

    /// <summary>
    /// Processes inbound operations without advancing simulation time.
    /// Must always be called from the same owning thread as <see cref="Tick"/>.
    /// </summary>
    public int Drain(int? maximumCommands = null)
    {
        EnterOwner();
        try
        {
            var maximum = maximumCommands ?? _limits.MaximumCommandsPerTick;
            if (maximum <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCommands));
            }

            var processed = 0;
            while (processed < maximum && _inbound.Reader.TryRead(out var operation))
            {
                Process(operation);
                processed++;
            }

            return processed;
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Executes exactly one 1/60-second authoritative step and publishes an
    /// immutable snapshot every third tick (20 Hz).
    /// </summary>
    public SessionTickResult Tick()
    {
        EnterOwner();
        try
        {
            var processed = DrainCore(_limits.MaximumCommandsPerTick);
            AdvanceActors();
            var publish = Clock.AdvanceOneTick();
            AdvanceCookingJobs();
            if (!publish)
            {
                return new SessionTickResult(processed, null);
            }

            var snapshot = CaptureSnapshotCore(Clock.AdvanceSnapshotSequence());
            Volatile.Write(ref _latestSnapshot, snapshot);
            return new SessionTickResult(processed, snapshot);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Captures current state outside the normal publication schedule. Intended
    /// for server diagnostics and initial connection bootstrap.
    /// </summary>
    public SessionSnapshot CaptureSnapshot()
    {
        EnterOwner();
        try
        {
            return CaptureSnapshotCore(Clock.SnapshotSequence);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Trusted owner-thread seam for initial world generation and persistence
    /// restoration. Network input must never call this method directly.
    /// </summary>
    public AuthoritativeWorldObjectSnapshot SeedWorldObject(
        WorldObjectSeed seed)
    {
        EnterOwner();
        try
        {
            return _worldTransactions.AddObject(seed);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Captures one immutable world object on the session owner thread.
    /// </summary>
    public AuthoritativeWorldObjectSnapshot CaptureWorldObject(Guid objectId)
    {
        EnterOwner();
        try
        {
            return _worldTransactions.CaptureObject(objectId);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Captures the authoritative revision used to optimistic-lock a chunk.
    /// </summary>
    public uint CaptureWorldChunkRevision(WorldChunkKey chunk)
    {
        EnterOwner();
        try
        {
            return _worldTransactions.CaptureChunkRevision(chunk);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Captures all durable authority state on the owning simulation thread.
    /// Reconnect hashes are copied into immutable storage and never logged.
    /// </summary>
    public AuthoritativeSessionCheckpoint CaptureCheckpoint()
    {
        EnterOwner();
        try
        {
            var actors = _actors.Values
                .OrderBy(static value => value.Identity.PlayerId.Value)
                .Select(static value => new AuthoritativeActorCheckpoint(
                    value.Identity,
                    value.DisplayName,
                    value.Position,
                    value.WorldLevel,
                    value.LastProcessedCommandSequence,
                    value.DisconnectedAtTick,
                    value.Gameplay.ToSnapshot(),
                    ImmutableArray.CreateRange(value.ReconnectTokenHash),
                    value.CaptureReceipts()))
                .ToImmutableArray();
            var cooking = _cookingJobs.Values
                .OrderBy(static value => value.ActorId.Value)
                .Select(static value => value.ToCheckpoint())
                .ToImmutableArray();
            return new(
                Id,
                Clock.Tick,
                Clock.SnapshotSequence,
                actors,
                _worldTransactions.CaptureCheckpoint(),
                cooking);
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Restores trusted durable state into a pristine matching session. All
    /// actors start disconnected; clients must prove their reconnect token.
    /// </summary>
    public void RestoreCheckpoint(AuthoritativeSessionCheckpoint checkpoint)
    {
        EnterOwner();
        try
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (checkpoint.SessionId != Id || Clock.Tick != 0 ||
                Clock.SnapshotSequence != 0 || _actors.Count != 0 ||
                _actorsByPlayer.Count != 0 || _playersByConnection.Count != 0 ||
                _chatHistory.Count != 0 || _nextChatMessageId != 0 ||
                _cookingJobs.Count != 0)
            {
                throw new InvalidOperationException(
                    "A checkpoint can only restore a pristine matching session.");
            }

            if (checkpoint.Actors.IsDefault ||
                checkpoint.Actors.Length > _limits.MaximumActors)
            {
                throw new InvalidDataException(
                    "The checkpoint exceeds the session actor limit.");
            }

            var validatedClock = new DeterministicSimulationClock();
            validatedClock.Restore(
                checkpoint.Tick,
                checkpoint.SnapshotSequence);

            var actors = new Dictionary<ActorId, MutableActor>();
            var actorsByPlayer = new Dictionary<PlayerId, ActorId>();
            foreach (var value in checkpoint.Actors)
            {
                if (value.Identity.PlayerId.Value == Guid.Empty ||
                    value.Identity.ActorId.Value == Guid.Empty ||
                    !TryNormalizeDisplayName(value.DisplayName, out var name) ||
                    name != value.DisplayName ||
                    !TrySanitizePosition(value.Position, out var position) ||
                    position != value.Position ||
                    !_navigation.SupportsWorldLevel(value.WorldLevel) ||
                    value.LastProcessedCommandSequence < 0 ||
                    value.DisconnectedAtTick is < 0 ||
                    value.DisconnectedAtTick > checkpoint.Tick ||
                    value.ReconnectTokenHash.Length != 32 ||
                    value.CommandReceipts.IsDefault ||
                    value.CommandReceipts.Length >
                        _limits.CommandReceiptCapacity ||
                    actors.ContainsKey(value.Identity.ActorId) ||
                    actorsByPlayer.ContainsKey(value.Identity.PlayerId))
                {
                    throw new InvalidDataException(
                        "The checkpoint contains an invalid actor.");
                }

                var actor = new MutableActor(
                    value.Identity,
                    name,
                    position,
                    value.WorldLevel,
                    default,
                    value.ReconnectTokenHash.ToArray())
                {
                    Connected = false,
                    LastProcessedCommandSequence =
                        value.LastProcessedCommandSequence,
                    // A running server may checkpoint a connected actor. On
                    // restore every actor is offline until it proves its
                    // reconnect token, so record the restart as the point at
                    // which that connection was lost.
                    DisconnectedAtTick = value.DisconnectedAtTick ?? checkpoint.Tick
                };
                actor.Gameplay.ReplaceWith(value.Gameplay);
                actor.RestoreReceipts(
                    value.CommandReceipts,
                    _limits.CommandReceiptCapacity);
                actors.Add(value.Identity.ActorId, actor);
                actorsByPlayer.Add(
                    value.Identity.PlayerId,
                    value.Identity.ActorId);
            }

            var cooking = new Dictionary<ActorId, ActiveCookingJob>();
            var persistedCooking = checkpoint.CookingJobs.IsDefault
                ? ImmutableArray<AuthoritativeCookingJobCheckpoint>.Empty
                : checkpoint.CookingJobs;
            foreach (var value in persistedCooking)
            {
                actors.TryGetValue(value.ActorId, out var cookingActor);
                var expected = cookingActor is null ||
                               !CookingSkill.TryProfile(value.RawItemId, out _)
                    ? default(CookingResult?)
                    : ResolveCookingOutcome(
                        checkpoint.SessionId.Value,
                        value.ActorId.Value,
                        value.CommandId,
                        value.RawItemId,
                        cookingActor.Gameplay.CookingExperience);
                if (value.CommandId == Guid.Empty ||
                    value.ActorId.Value == Guid.Empty ||
                    value.CampfireId == Guid.Empty ||
                    value.DropObjectId == Guid.Empty ||
                    cookingActor is null ||
                    !float.IsFinite(value.CampfirePosition.X) ||
                    !float.IsFinite(value.CampfirePosition.Y) ||
                    value.PreferredInventorySlot is < 0 or >=
                        PlayerInventory.Capacity ||
                    expected is null ||
                    value.ResultItemId != expected.Value.ItemId ||
                    value.Experience != expected.Value.Experience ||
                    value.Burnt != expected.Value.Burnt ||
                    value.CompletesAtTick <= checkpoint.Tick ||
                    !cooking.TryAdd(value.ActorId,
                        ActiveCookingJob.FromCheckpoint(value)))
                {
                    throw new InvalidDataException(
                        "The checkpoint contains an invalid cooking job.");
                }
            }

            if (checkpoint.World.Objects.IsDefault)
                throw new InvalidDataException(
                    "The checkpoint world state is incomplete.");
            var persistedObjects = checkpoint.World.Objects
                .ToDictionary(static value => value.Object.ObjectId);
            foreach (var job in cooking.Values)
            {
                if (!persistedObjects.TryGetValue(
                        job.CampfireId, out var fireEntry) ||
                    fireEntry.Object.Chunk != job.CampfireChunk ||
                    fireEntry.Object.Position != job.CampfirePosition ||
                    fireEntry.Object.DefinitionId != ItemIds.Campfire ||
                    persistedObjects.ContainsKey(job.DropObjectId))
                    throw new InvalidDataException(
                        "A cooking job does not reference its persisted campfire.");
            }
            // Both restorers validate completely before their first commit.
            _worldTransactions.RestoreCheckpoint(checkpoint.World);
            Clock.Restore(checkpoint.Tick, checkpoint.SnapshotSequence);
            foreach (var value in actors) _actors.Add(value.Key, value.Value);
            foreach (var value in actorsByPlayer)
                _actorsByPlayer.Add(value.Key, value.Value);
            foreach (var value in cooking)
                _cookingJobs.Add(value.Key, value.Value);
            Volatile.Write(ref _latestSnapshot,
                CaptureSnapshotCore(Clock.SnapshotSequence));
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Owner-thread integration seam for authoritative loot/reward systems.
    /// Kept internal until a world-item command owns this path; networking
    /// cannot invoke it directly.
    /// </summary>
    internal bool TryGrantInventoryItem(
        PlayerId playerId,
        string itemId,
        int quantity = 1)
    {
        EnterOwner();
        try
        {
            if (!TryGetActor(playerId, out var actor))
            {
                return false;
            }

            var updated = actor.Gameplay.Inventory.Clone();
            if (!updated.TryAdd(itemId, quantity))
            {
                return false;
            }

            var nextRevision = checked(actor.Gameplay.InventoryRevision + 1);
            actor.Gameplay.Inventory = updated;
            actor.Gameplay.InventoryRevision = nextRevision;
            return true;
        }
        finally
        {
            ExitOwner();
        }
    }

    private int DrainCore(int maximum)
    {
        var processed = 0;
        while (processed < maximum && _inbound.Reader.TryRead(out var operation))
        {
            Process(operation);
            processed++;
        }

        return processed;
    }

    private void Process(QueuedOperation operation)
    {
        switch (operation)
        {
            case JoinOperation join:
                join.Completion.SetResult(ProcessJoin(join.Request));
                break;
            case ReconnectOperation reconnect:
                reconnect.Completion.SetResult(ProcessReconnect(reconnect.Request));
                break;
            case DisconnectOperation disconnect:
                disconnect.Completion.SetResult(ProcessDisconnect(disconnect.Request));
                break;
            case IntentOperation intent:
                intent.Completion?.SetResult(ProcessIntent(intent.Command));
                break;
            default:
                throw new InvalidOperationException("Unknown authoritative operation.");
        }
    }

    private JoinResult ProcessJoin(JoinRequest request)
    {
        if (request.ConnectionId.Value == Guid.Empty ||
            !TryNormalizeDisplayName(request.DisplayName, out var displayName) ||
            !TrySanitizePosition(request.SpawnPosition, out var spawn) ||
            !float.IsFinite(request.InitialHunger) ||
            request.InitialHunger is < 0 or > SurvivalService.MaximumHunger ||
            !_navigation.SupportsWorldLevel(request.SpawnWorldLevel))
        {
            return new JoinResult(
                JoinStatus.InvalidRequest,
                default,
                default,
                0,
                "A valid connection, display name, spawn position and starting hunger are required.");
        }

        if (_playersByConnection.ContainsKey(request.ConnectionId))
        {
            return new JoinResult(
                JoinStatus.ConnectionAlreadyJoined,
                default,
                default,
                0,
                "This connection is already attached to a player.");
        }

        if (_actors.Count >= _limits.MaximumActors)
        {
            return new JoinResult(
                JoinStatus.SessionFull,
                default,
                default,
                0,
                "The session has reached its actor limit.");
        }

        var identity = CreateUniqueIdentity();
        var reconnectToken = _identitySource.CreateReconnectToken();
        if (reconnectToken.IsEmpty)
        {
            throw new InvalidOperationException("The identity source returned an empty reconnect token.");
        }

        var actor = new MutableActor(
            identity,
            displayName,
            spawn,
            request.SpawnWorldLevel,
            request.ConnectionId,
            HashToken(reconnectToken));
        actor.Gameplay.Hunger = request.InitialHunger;
        if (request.InitialInventory is { Count: > 0 } initialInventory)
        {
            var inventory = actor.Gameplay.Inventory.Clone();
            foreach (var item in initialInventory)
            {
                if (string.IsNullOrWhiteSpace(item.ItemId) ||
                    item.Quantity <= 0 ||
                    !inventory.TryAdd(item.ItemId, item.Quantity))
                {
                    return new JoinResult(
                        JoinStatus.InvalidRequest,
                        default,
                        default,
                        0,
                        "The server-authored starting inventory is invalid or too large.");
                }
            }
            actor.Gameplay.Inventory = inventory;
        }
        _actors.Add(identity.ActorId, actor);
        _actorsByPlayer.Add(identity.PlayerId, identity.ActorId);
        _playersByConnection.Add(request.ConnectionId, identity.PlayerId);

        return new JoinResult(
            JoinStatus.Accepted,
            identity,
            reconnectToken,
            1,
            null)
        {
            Gameplay = actor.Gameplay.ToSnapshot()
        };
    }

    private ReconnectResult ProcessReconnect(ReconnectRequest request)
    {
        if (request.ConnectionId.Value == Guid.Empty || request.PlayerId.Value == Guid.Empty ||
            request.ReconnectToken.IsEmpty)
        {
            return new ReconnectResult(
                ReconnectStatus.InvalidRequest,
                default,
                0,
                "A connection, player identity and reconnect token are required.");
        }

        if (_playersByConnection.ContainsKey(request.ConnectionId))
        {
            return new ReconnectResult(
                ReconnectStatus.ConnectionAlreadyJoined,
                default,
                0,
                "This connection is already attached to a player.");
        }

        if (!TryGetActor(request.PlayerId, out var actor))
        {
            return new ReconnectResult(
                ReconnectStatus.UnknownPlayer,
                default,
                0,
                "The player does not exist in this session.");
        }

        if (actor.Connected)
        {
            return new ReconnectResult(
                ReconnectStatus.AlreadyConnected,
                actor.Identity,
                checked(actor.LastProcessedCommandSequence + 1),
                "The player is already connected.");
        }

        if (!TokenMatches(actor.ReconnectTokenHash, request.ReconnectToken))
        {
            return new ReconnectResult(
                ReconnectStatus.InvalidToken,
                default,
                0,
                "The reconnect token is invalid.");
        }

        actor.ConnectionId = request.ConnectionId;
        actor.Connected = true;
        actor.DisconnectedAtTick = null;
        _playersByConnection.Add(request.ConnectionId, actor.Identity.PlayerId);

        return new ReconnectResult(
            ReconnectStatus.Accepted,
            actor.Identity,
            checked(actor.LastProcessedCommandSequence + 1),
            null)
        {
            Gameplay = actor.Gameplay.ToSnapshot()
        };
    }

    private DisconnectResult ProcessDisconnect(DisconnectRequest request)
    {
        if (!TryGetActor(request.PlayerId, out var actor))
        {
            return new DisconnectResult(
                DisconnectStatus.UnknownPlayer,
                "The player does not exist in this session.");
        }

        if (!actor.Connected)
        {
            return new DisconnectResult(
                DisconnectStatus.AlreadyDisconnected,
                "The player is already disconnected.");
        }

        if (request.ConnectionId.Value == Guid.Empty || actor.ConnectionId != request.ConnectionId)
        {
            return new DisconnectResult(
                DisconnectStatus.InvalidConnection,
                "The connection does not own this player.");
        }

        actor.Connected = false;
        actor.ConnectionId = default;
        actor.ClearRoute();
        actor.DisconnectedAtTick = Clock.Tick;
        _playersByConnection.Remove(request.ConnectionId);
        return new DisconnectResult(DisconnectStatus.Accepted, null);
    }

    private IntentResult ProcessIntent(ActorCommand command)
    {
        if (!TryGetActor(command.PlayerId, out var actor))
        {
            return new IntentResult(
                IntentStatus.UnknownPlayer,
                0,
                "The player does not exist in this session.");
        }

        if (!actor.Connected)
        {
            return new IntentResult(
                IntentStatus.Disconnected,
                actor.LastProcessedCommandSequence,
                "The player is disconnected.");
        }

        if (command.ConnectionId.Value == Guid.Empty || actor.ConnectionId != command.ConnectionId)
        {
            return new IntentResult(
                IntentStatus.InvalidConnection,
                actor.LastProcessedCommandSequence,
                "The connection does not own this player.");
        }

        if (command.Sequence <= 0)
        {
            return new IntentResult(
                IntentStatus.InvalidSequence,
                actor.LastProcessedCommandSequence,
                "Command sequences begin at one.");
        }

        if (command.Intent is GameplayIntent replayed &&
            replayed.CommandId != Guid.Empty &&
            actor.TryGetReceipt(replayed.CommandId, out var receipt))
        {
            if (command.Sequence > actor.LastProcessedCommandSequence)
            {
                actor.LastProcessedCommandSequence = command.Sequence;
            }

            if (!string.Equals(
                    receipt.PayloadFingerprint,
                    GameplayIntentFingerprint.Create(replayed),
                    StringComparison.Ordinal))
            {
                return Rejected(
                    IntentStatus.CommandIdConflict,
                    actor,
                    "The command identifier is already bound to a different gameplay request.",
                    replayed.CommandId);
            }

            if (receipt.Restored)
            {
                return new IntentResult(
                    receipt.Result.Status,
                    actor.LastProcessedCommandSequence,
                    receipt.Result.Error)
                {
                    CommandId = replayed.CommandId,
                    InventoryRevision = actor.Gameplay.InventoryRevision,
                    ActorRevision = actor.Gameplay.ActorRevision,
                    Duplicate = true,
                    Gameplay = actor.Gameplay.ToSnapshot()
                };
            }

            return receipt.Result with
            {
                LastProcessedSequence = actor.LastProcessedCommandSequence,
                Duplicate = true
            };
        }

        if (command.Sequence <= actor.LastProcessedCommandSequence)
        {
            return new IntentResult(
                IntentStatus.StaleSequence,
                actor.LastProcessedCommandSequence,
                "The command was already processed or superseded.");
        }

        // Authenticate and consume the sequence before validating its payload so a
        // malformed packet cannot be replayed indefinitely.
        actor.LastProcessedCommandSequence = command.Sequence;
        if (command.Intent is null)
        {
            return Rejected(IntentStatus.InvalidIntent, actor, "An intent is required.");
        }

        if (command.Intent is GameplayIntent gameplay)
        {
            var result = ProcessGameplayIntent(actor, gameplay);
            if (gameplay.CommandId != Guid.Empty)
            {
                actor.RememberReceipt(
                    gameplay,
                    result,
                    _limits.CommandReceiptCapacity);
            }

            return result;
        }

        return command.Intent switch
        {
            WalkIntent walk => ProcessWalk(actor, walk),
            StopIntent => ProcessStop(actor),
            ChatIntent chat => ProcessChat(actor, chat),
            _ => Rejected(IntentStatus.InvalidIntent, actor, "The intent type is unsupported.")
        };
    }

    private IntentResult ProcessGameplayIntent(
        MutableActor actor,
        GameplayIntent intent)
    {
        if (intent.CommandId == Guid.Empty)
        {
            return Rejected(
                IntentStatus.InvalidCommandId,
                actor,
                "Gameplay commands require a non-empty command identifier.");
        }

        // The world aggregate performs the same actor/inventory optimistic
        // concurrency checks and preserves its exact rejection receipt.
        if (intent is WorldGameplayIntent world)
        {
            return ProcessWorldIntent(actor, world);
        }

        if (intent.ExpectedInventoryRevision !=
            actor.Gameplay.InventoryRevision)
        {
            return Rejected(
                IntentStatus.StaleInventoryRevision,
                actor,
                "The inventory revision is stale.",
                intent.CommandId);
        }

        if (intent.ExpectedActorRevision != actor.Gameplay.ActorRevision)
        {
            return Rejected(
                IntentStatus.StaleActorRevision,
                actor,
                "The actor gameplay revision is stale.",
                intent.CommandId);
        }

        return intent switch
        {
            SwapInventorySlotsIntent swap =>
                ProcessSwapInventorySlots(actor, swap),
            CombineInventorySlotsIntent combine =>
                ProcessCombineInventorySlots(actor, combine),
            CraftRecipeIntent craft => ProcessCraftRecipe(actor, craft),
            ConsumeFoodIntent consume => ProcessConsumeFood(actor, consume),
            _ => Rejected(
                IntentStatus.InvalidIntent,
                actor,
                "The gameplay intent type is unsupported.",
                intent.CommandId)
        };
    }

    private IntentResult ProcessWorldIntent(
        MutableActor actor,
        WorldGameplayIntent intent)
    {
        if (intent is PlaceConstructionIntent placement &&
            (!_navigation.SupportsWorldLevel(placement.WorldLevel) ||
             !_navigation.CanStandAt(placement.Position, placement.WorldLevel)))
        {
            return Rejected(
                IntentStatus.InvalidPlacement,
                actor,
                "Construction must be placed on traversable terrain.",
                intent.CommandId);
        }

        var gameSeconds = Clock.Current.ElapsedSeconds;
        var context = new WorldTransactionContext(
            intent.CommandId,
            actor.Identity.ActorId,
            intent.ExpectedActorRevision,
            intent.ExpectedInventoryRevision);
        var input = new WorldTransactionActorInput(
            actor.Identity.ActorId,
            actor.Position,
            actor.WorldLevel,
            actor.Gameplay.ToSnapshot());
        var transaction = intent switch
        {
            PickUpWorldObjectIntent pickUp => _worldTransactions.Execute(
                input,
                new PickUpWorldObjectTransaction(context, pickUp.Object)),
            DropInventoryItemIntent drop => _worldTransactions.Execute(
                input,
                new DropInventoryItemTransaction(
                    context,
                    drop.InventorySlot,
                    drop.Quantity,
                    drop.Position,
                    drop.WorldLevel,
                    drop.ExpectedChunkRevision)),
            OpenWorldContainerIntent open => _worldTransactions.Execute(
                input,
                new OpenWorldContainerTransaction(context, open.Container)),
            TransferWorldContainerIntent transfer => _worldTransactions.Execute(
                input,
                new TransferWorldContainerTransaction(
                    context,
                    transfer.Container,
                    transfer.Direction,
                    transfer.InventorySlot,
                    transfer.ContainerSlot,
                    transfer.Quantity)),
            AddCampfireFuelIntent fuel => _worldTransactions.Execute(
                input,
                new AddCampfireFuelTransaction(
                    context,
                    fuel.Campfire,
                    fuel.InventorySlot,
                    gameSeconds)),
            TakeCampfireFuelIntent takeFuel => _worldTransactions.Execute(
                input,
                new TakeCampfireFuelTransaction(
                    context,
                    takeFuel.Campfire,
                    gameSeconds)),
            LightCampfireIntent light => _worldTransactions.Execute(
                input,
                new LightCampfireTransaction(
                    context,
                    light.Campfire,
                    gameSeconds)),
            CookOnCampfireIntent cook => BeginCooking(
                actor, input, context, cook, gameSeconds),
            PlaceConstructionIntent place => _worldTransactions.Execute(
                input,
                new PlaceConstructionTransaction(
                    context,
                    place.DefinitionId,
                    place.Position,
                    place.WorldLevel,
                    place.Rotation,
                    place.ExpectedChunkRevision)),
            BuildConstructionIntent build => _worldTransactions.Execute(
                input,
                new BuildConstructionTransaction(
                    context,
                    build.Construction)),
            DemolishWorldObjectIntent demolish => _worldTransactions.Execute(
                input,
                new DemolishWorldObjectTransaction(context, demolish.Object)),
            _ => throw new InvalidOperationException(
                "The world gameplay intent type is unsupported.")
        };

        // Accepted read-only operations intentionally return the same actor
        // revisions. Mutating operations return a replacement immutable
        // snapshot, which becomes the session's sole mutable actor state once.
        if (transaction.Accepted && transaction.Gameplay is { } gameplay &&
            (gameplay.ActorRevision != actor.Gameplay.ActorRevision ||
             gameplay.Inventory.Revision != actor.Gameplay.InventoryRevision))
        {
            actor.Gameplay.ReplaceWith(gameplay);
        }

        if (transaction.Accepted &&
            (!transaction.ObjectDeltas.IsDefaultOrEmpty ||
             !transaction.ChunkDeltas.IsDefaultOrEmpty))
            WorldTransactionCommitted?.Invoke(transaction);

        return new IntentResult(
            MapWorldStatus(transaction.Status),
            actor.LastProcessedCommandSequence,
            transaction.Accepted
                ? null
                : string.IsNullOrWhiteSpace(transaction.Detail)
                    ? transaction.Status.ToString()
                    : transaction.Detail)
        {
            CommandId = intent.CommandId,
            InventoryRevision = actor.Gameplay.InventoryRevision,
            ActorRevision = actor.Gameplay.ActorRevision,
            Gameplay = actor.Gameplay.ToSnapshot(),
            WorldTransaction = transaction
        };
    }

    private static IntentStatus MapWorldStatus(
        WorldTransactionStatus status) => status switch
        {
            WorldTransactionStatus.Accepted => IntentStatus.Accepted,
            WorldTransactionStatus.InvalidCommand =>
                IntentStatus.WorldCommandInvalid,
            WorldTransactionStatus.CommandIdConflict =>
                IntentStatus.CommandIdConflict,
            WorldTransactionStatus.ActorNotFound => IntentStatus.ActorNotFound,
            WorldTransactionStatus.DeadActor => IntentStatus.DeadActor,
            WorldTransactionStatus.StaleActorRevision =>
                IntentStatus.StaleActorRevision,
            WorldTransactionStatus.StaleInventoryRevision =>
                IntentStatus.StaleInventoryRevision,
            WorldTransactionStatus.ObjectNotFound => IntentStatus.ObjectNotFound,
            WorldTransactionStatus.ObjectLocationMismatch =>
                IntentStatus.ObjectLocationMismatch,
            WorldTransactionStatus.StaleObjectRevision =>
                IntentStatus.StaleObjectRevision,
            WorldTransactionStatus.StaleChunkRevision =>
                IntentStatus.StaleChunkRevision,
            WorldTransactionStatus.StaleContainerRevision =>
                IntentStatus.StaleContainerRevision,
            WorldTransactionStatus.WrongWorldLevel =>
                IntentStatus.WrongWorldLevel,
            WorldTransactionStatus.OutOfRange => IntentStatus.OutOfRange,
            WorldTransactionStatus.AccessDenied => IntentStatus.AccessDenied,
            WorldTransactionStatus.InvalidItem => IntentStatus.InvalidItem,
            WorldTransactionStatus.InvalidQuantity => IntentStatus.InvalidQuantity,
            WorldTransactionStatus.InvalidInventorySlot =>
                IntentStatus.InvalidInventorySlot,
            WorldTransactionStatus.ItemUnavailable => IntentStatus.ItemUnavailable,
            WorldTransactionStatus.InventoryFull => IntentStatus.InventoryFull,
            WorldTransactionStatus.NotPortable => IntentStatus.NotPortable,
            WorldTransactionStatus.NotContainer => IntentStatus.NotContainer,
            WorldTransactionStatus.ContainerFull => IntentStatus.ContainerFull,
            WorldTransactionStatus.ContainerItemUnavailable =>
                IntentStatus.ContainerItemUnavailable,
            WorldTransactionStatus.ContainerDepositDenied =>
                IntentStatus.ContainerDepositDenied,
            WorldTransactionStatus.NotCampfire => IntentStatus.NotCampfire,
            WorldTransactionStatus.InvalidCampfireState =>
                IntentStatus.InvalidCampfireState,
            WorldTransactionStatus.CampfireLightingRequirementsMissing =>
                IntentStatus.CampfireLightingRequirementsMissing,
            WorldTransactionStatus.InvalidConstruction =>
                IntentStatus.InvalidConstruction,
            WorldTransactionStatus.MissingConstructionResources =>
                IntentStatus.MissingConstructionResources,
            WorldTransactionStatus.ConstructionLocked =>
                IntentStatus.ConstructionLocked,
            WorldTransactionStatus.InvalidPlacement => IntentStatus.InvalidPlacement,
            WorldTransactionStatus.NotConstructionSite =>
                IntentStatus.NotConstructionSite,
            WorldTransactionStatus.NoDemolitionRefund =>
                IntentStatus.NoDemolitionRefund,
            WorldTransactionStatus.NotCookable => IntentStatus.NotCookable,
            WorldTransactionStatus.CookingLocked => IntentStatus.CookingLocked,
            WorldTransactionStatus.AlreadyCooking => IntentStatus.AlreadyCooking,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private WorldTransactionResult BeginCooking(
        MutableActor actor,
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        CookOnCampfireIntent intent,
        double gameSeconds)
    {
        if (_cookingJobs.ContainsKey(actor.Identity.ActorId))
            return new WorldTransactionResult(
                context.CommandId,
                WorldTransactionStatus.AlreadyCooking,
                actor.Gameplay.ActorRevision,
                actor.Gameplay.InventoryRevision,
                [], [], actor.Gameplay.ToSnapshot(), null,
                "You are already cooking something.");

        var raw = (uint)intent.InventorySlot <
                  (uint)actor.Gameplay.Inventory.Capacity
            ? actor.Gameplay.Inventory[intent.InventorySlot]?.ItemId
            : null;
        var transaction = _worldTransactions.Execute(
            input,
            new BeginCampfireCookingTransaction(
                context,
                intent.Campfire,
                intent.InventorySlot,
                gameSeconds));
        if (!transaction.Accepted || raw is null) return transaction;

        var outcome = ResolveCookingOutcome(
            Id.Value,
            actor.Identity.ActorId.Value,
            intent.CommandId,
            raw,
            actor.Gameplay.CookingExperience);
        var fire = _worldTransactions.CaptureObject(
            intent.Campfire.ObjectId);
        var duration = CookingSkill.PlacementAnimationSeconds +
                       CookingSkill.CookingSeconds;
        var durationTicks = checked((long)Math.Ceiling(
            duration * SimulationTiming.TicksPerSecond));
        _cookingJobs.Add(actor.Identity.ActorId, new ActiveCookingJob(
            intent.CommandId,
            actor.Identity.ActorId,
            fire.ObjectId,
            fire.Chunk,
            fire.Position,
            intent.InventorySlot,
            raw,
            outcome.ItemId,
            outcome.Experience,
            outcome.Burnt,
            DeterministicCookingDropId(
                Id.Value, actor.Identity.ActorId.Value, intent.CommandId),
            checked(Clock.Tick + durationTicks)));
        return transaction;
    }

    private void AdvanceCookingJobs()
    {
        if (_cookingJobs.Count == 0) return;
        foreach (var job in _cookingJobs.Values
                     .Where(job => job.CompletesAtTick <= Clock.Tick)
                     .OrderBy(static job => job.ActorId.Value)
                     .ToArray())
        {
            if (!_actors.TryGetValue(job.ActorId, out var actor))
            {
                throw new InvalidOperationException(
                    "A durable cooking job has no authoritative actor.");
            }
            var input = new WorldTransactionActorInput(
                actor.Identity.ActorId,
                actor.Position,
                actor.WorldLevel,
                actor.Gameplay.ToSnapshot());
            var transaction = _worldTransactions.CompleteCooking(
                input,
                new CompleteCampfireCookingTransaction(
                    job.CommandId,
                    job.CampfireId,
                    job.CampfireChunk,
                    job.CampfirePosition,
                    job.PreferredInventorySlot,
                    job.RawItemId,
                    job.ResultItemId,
                    job.Experience,
                    job.Burnt,
                    job.DropObjectId,
                    Clock.Current.ElapsedSeconds));
            if (!transaction.Accepted || transaction.Gameplay is not { } gameplay)
            {
                throw new InvalidOperationException(
                    $"A validated cooking job could not complete: {transaction.Status}.");
            }
            actor.Gameplay.ReplaceWith(gameplay);
            _cookingJobs.Remove(job.ActorId);
            if (!transaction.ObjectDeltas.IsDefaultOrEmpty ||
                !transaction.ChunkDeltas.IsDefaultOrEmpty)
                WorldTransactionCommitted?.Invoke(transaction);
            var interrupted = transaction.Detail == "cooking_interrupted";
            CookingCompleted?.Invoke(new CookingCompletionSnapshot(
                job.CommandId,
                actor.Identity.PlayerId,
                job.RawItemId,
                interrupted ? job.RawItemId : job.ResultItemId,
                !interrupted && job.Burnt,
                interrupted,
                gameplay.ActorRevision,
                gameplay.Inventory.Revision)
            {
                Gameplay = gameplay,
                Transaction = transaction
            });
        }
    }

    private static CookingResult ResolveCookingOutcome(
        Guid sessionId,
        Guid actorId,
        Guid commandId,
        string rawItemId,
        int cookingExperience)
    {
        var level = CookingSkill.LevelForExperience(cookingExperience);
        var roll = DeterministicCookingRoll(sessionId, actorId, commandId);
        return ActorActionService.ResolveCooking(rawItemId, level, roll);
    }

    internal static float DeterministicCookingRoll(
        Guid sessionId, Guid actorId, Guid commandId)
    {
        Span<byte> input = stackalloc byte[48];
        sessionId.TryWriteBytes(input[..16], bigEndian: true, out _);
        actorId.TryWriteBytes(
            input.Slice(16, 16), bigEndian: true, out _);
        commandId.TryWriteBytes(
            input.Slice(32, 16), bigEndian: true, out _);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return BinaryPrimitives.ReadUInt32BigEndian(digest) /
               ((float)uint.MaxValue + 1f);
    }

    internal static Guid DeterministicCookingDropId(
        Guid sessionId, Guid actorId, Guid commandId)
    {
        Span<byte> input = stackalloc byte[49];
        sessionId.TryWriteBytes(input[..16], bigEndian: true, out _);
        actorId.TryWriteBytes(
            input.Slice(16, 16), bigEndian: true, out _);
        commandId.TryWriteBytes(
            input.Slice(32, 16), bigEndian: true, out _);
        input[48] = 0xC0;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16], bigEndian: true);
    }

    private static IntentResult ProcessSwapInventorySlots(
        MutableActor actor,
        SwapInventorySlotsIntent intent)
    {
        var inventory = actor.Gameplay.Inventory;
        if (!IsSlot(inventory, intent.SourceSlot) ||
            !IsSlot(inventory, intent.TargetSlot) ||
            intent.SourceSlot == intent.TargetSlot)
        {
            return Rejected(
                IntentStatus.InvalidInventorySlot,
                actor,
                "Two distinct inventory slots are required.",
                intent.CommandId);
        }

        if (inventory[intent.SourceSlot] is null)
        {
            return Rejected(
                IntentStatus.EmptyInventorySlot,
                actor,
                "The source inventory slot is empty.",
                intent.CommandId);
        }

        var updated = inventory.Clone();
        if (!updated.TrySwap(intent.SourceSlot, intent.TargetSlot))
        {
            return Rejected(
                IntentStatus.InvalidInventorySlot,
                actor,
                "The inventory slots cannot be swapped.",
                intent.CommandId);
        }

        var nextRevision = checked(actor.Gameplay.InventoryRevision + 1);
        actor.Gameplay.Inventory = updated;
        actor.Gameplay.InventoryRevision = nextRevision;
        return Accepted(actor, intent.CommandId);
    }

    private static IntentResult ProcessCombineInventorySlots(
        MutableActor actor,
        CombineInventorySlotsIntent intent)
    {
        var inventory = actor.Gameplay.Inventory;
        if (!IsSlot(inventory, intent.FirstSlot) ||
            !IsSlot(inventory, intent.SecondSlot) ||
            intent.FirstSlot == intent.SecondSlot)
        {
            return Rejected(
                IntentStatus.InvalidInventorySlot,
                actor,
                "Two distinct inventory slots are required.",
                intent.CommandId);
        }

        if (inventory[intent.FirstSlot] is not { } first ||
            inventory[intent.SecondSlot] is not { } second)
        {
            return Rejected(
                IntentStatus.EmptyInventorySlot,
                actor,
                "Both inventory slots must contain an item.",
                intent.CommandId);
        }

        var recipe = ItemCombinationService.FindRecipe(
            first.ItemId,
            second.ItemId);
        if (ToolUpkeepService.TrySharpenStoneTool(
                inventory,
                intent.FirstSlot,
                intent.SecondSlot,
                out var sharpened))
        {
            actor.Gameplay.Inventory = sharpened;
            actor.Gameplay.InventoryRevision = checked(
                actor.Gameplay.InventoryRevision + 1);
            return Accepted(actor, intent.CommandId);
        }
        if (recipe is null)
        {
            return Rejected(
                IntentStatus.NoMatchingRecipe,
                actor,
                "Those items do not match a combination recipe.",
                intent.CommandId);
        }

        return TryCraft(actor, recipe, intent.CommandId);
    }

    private static IntentResult ProcessCraftRecipe(
        MutableActor actor,
        CraftRecipeIntent intent)
    {
        var recipe = string.IsNullOrWhiteSpace(intent.RecipeId)
            ? null
            : CraftingSkill.Recipes.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Id,
                    intent.RecipeId,
                    StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
        {
            return Rejected(
                IntentStatus.UnknownRecipe,
                actor,
                "The stable crafting recipe identifier is unknown.",
                intent.CommandId);
        }

        return TryCraft(actor, recipe, intent.CommandId);
    }

    private static IntentResult TryCraft(
        MutableActor actor,
        CraftingRecipe recipe,
        Guid commandId)
    {
        var gameplay = actor.Gameplay;
        var beforeItems = gameplay.Inventory.ItemIds();
        var craftResult = CraftingService.TryCraftDetailed(
            recipe,
            CraftingSkill.LevelForExperience(gameplay.CraftingExperience),
            gameplay.Inventory,
            out var updated,
            requiredStationAvailable: recipe.RequiredStationItemId is null);
        if (craftResult != CraftingService.CraftResult.Success)
        {
            return Rejected(
                CraftFailureStatus(craftResult),
                actor,
                CraftFailureError(craftResult),
                commandId);
        }

        var experience = CraftingSkill.AwardExperience(
            gameplay.CraftingExperience,
            recipe,
            beforeItems);
        var nextInventoryRevision = checked(gameplay.InventoryRevision + 1);
        var nextActorRevision = experience.Experience ==
            gameplay.CraftingExperience
                ? gameplay.ActorRevision
                : checked(gameplay.ActorRevision + 1);

        gameplay.Inventory = updated;
        gameplay.InventoryRevision = nextInventoryRevision;
        gameplay.CraftingExperience = experience.Experience;
        gameplay.ActorRevision = nextActorRevision;
        return Accepted(actor, commandId);
    }

    private static IntentResult ProcessConsumeFood(
        MutableActor actor,
        ConsumeFoodIntent intent)
    {
        var gameplay = actor.Gameplay;
        if (!IsSlot(gameplay.Inventory, intent.Slot))
        {
            return Rejected(
                IntentStatus.InvalidInventorySlot,
                actor,
                "The inventory slot is outside the carried inventory.",
                intent.CommandId);
        }

        if (gameplay.Inventory[intent.Slot] is not { } stack)
        {
            return Rejected(
                IntentStatus.EmptyInventorySlot,
                actor,
                "The inventory slot is empty.",
                intent.CommandId);
        }

        if (!SurvivalService.TryFoodEffect(stack.ItemId, out var effect))
        {
            return Rejected(
                IntentStatus.ItemNotConsumable,
                actor,
                "The selected item cannot be consumed as food.",
                intent.CommandId);
        }

        if (gameplay.Hunger >= SurvivalService.MaximumHunger &&
            gameplay.Health >= MutablePlayerGameplay.MaximumHealth)
        {
            return Rejected(
                IntentStatus.AlreadyFull,
                actor,
                "The player is already full and healthy.",
                intent.CommandId);
        }

        var updatedInventory = gameplay.Inventory.Clone();
        if (!updatedInventory.TryTake(intent.Slot, 1, out _))
        {
            return Rejected(
                IntentStatus.EmptyInventorySlot,
                actor,
                "The selected food is no longer available.",
                intent.CommandId);
        }

        var survival = SurvivalService.Eat(
            effect,
            gameplay.Hunger,
            gameplay.WellFedSeconds,
            gameplay.Health,
            MutablePlayerGameplay.MaximumHealth);
        var actorChanged = survival.Health != gameplay.Health ||
            survival.Hunger != gameplay.Hunger ||
            survival.WellFedSeconds != gameplay.WellFedSeconds;
        var nextInventoryRevision = checked(gameplay.InventoryRevision + 1);
        var nextActorRevision = actorChanged
            ? checked(gameplay.ActorRevision + 1)
            : gameplay.ActorRevision;

        gameplay.Inventory = updatedInventory;
        gameplay.InventoryRevision = nextInventoryRevision;
        gameplay.Health = survival.Health;
        gameplay.Hunger = survival.Hunger;
        gameplay.WellFedSeconds = survival.WellFedSeconds;
        gameplay.ActorRevision = nextActorRevision;
        return Accepted(actor, intent.CommandId);
    }

    private static bool IsSlot(InventoryContainer inventory, int slot) =>
        (uint)slot < (uint)inventory.Capacity;

    private static IntentStatus CraftFailureStatus(
        CraftingService.CraftResult result) => result switch
        {
            CraftingService.CraftResult.Locked => IntentStatus.CraftingLocked,
            CraftingService.CraftResult.MissingResources =>
                IntentStatus.MissingResources,
            CraftingService.CraftResult.MissingStation =>
                IntentStatus.MissingStation,
            CraftingService.CraftResult.InventoryFull =>
                IntentStatus.InventoryFull,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

    private static string CraftFailureError(
        CraftingService.CraftResult result) => result switch
        {
            CraftingService.CraftResult.Locked =>
                "The player's crafting level does not unlock this recipe.",
            CraftingService.CraftResult.MissingResources =>
                "The authoritative inventory lacks required resources or tools.",
            CraftingService.CraftResult.MissingStation =>
                "The recipe requires a crafting station that is not available.",
            CraftingService.CraftResult.InventoryFull =>
                "The crafted output does not fit in the inventory.",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

    private IntentResult ProcessWalk(MutableActor actor, WalkIntent intent)
    {
        if (!TrySanitizePosition(intent.Destination, out var destination))
        {
            return Rejected(
                IntentStatus.InvalidDestination,
                actor,
                "The destination must contain finite coordinates.");
        }

        if (Vector2.DistanceSquared(actor.Position, destination) >
            _limits.MaximumWalkIntentDistance * _limits.MaximumWalkIntentDistance)
        {
            return Rejected(
                IntentStatus.DestinationTooFar,
                actor,
                "The destination exceeds the maximum command distance.");
        }


        if (intent.WorldLevel != actor.WorldLevel ||
            !_navigation.SupportsWorldLevel(intent.WorldLevel))
        {
            return Rejected(
                IntentStatus.WorldLevelMismatch,
                actor,
                "The destination belongs to a different world level.");
        }

        var route = GridPathfinder.Find(
            _navigation,
            actor.Position,
            destination,
            _limits.MaximumPathSearchVisited,
            worldLevel: actor.WorldLevel,
            obstacles: _obstacles.GetObstacles(actor.WorldLevel));
        if (route.Count == 0 &&
            Vector2.DistanceSquared(actor.Position, destination) >
            _limits.DestinationArrivalDistance *
            _limits.DestinationArrivalDistance)
        {
            return Rejected(
                IntentStatus.PathUnreachable,
                actor,
                "No traversable route reaches that destination.");
        }

        if (route.Count > _limits.MaximumPathWaypoints)
        {
            return Rejected(
                IntentStatus.PathUnreachable,
                actor,
                "The traversable route exceeds the authoritative route limit.");
        }

        actor.ReplaceRoute(route);
        return Accepted(actor);
    }

    private static IntentResult ProcessStop(MutableActor actor)
    {
        actor.ClearRoute();
        return Accepted(actor);
    }

    private IntentResult ProcessChat(MutableActor actor, ChatIntent intent)
    {
        if (!TryNormalizeChat(intent.Message, out var message))
        {
            return Rejected(
                IntentStatus.InvalidChat,
                actor,
                $"Chat must contain 1-{_limits.MaximumChatLength} printable characters.");
        }

        if (_limits.ChatHistoryCapacity > 0)
        {
            while (_chatHistory.Count >= _limits.ChatHistoryCapacity)
            {
                _chatHistory.Dequeue();
            }

            _chatHistory.Enqueue(new ChatMessageSnapshot(
                checked(++_nextChatMessageId),
                Clock.Tick,
                actor.Identity.PlayerId,
                actor.Identity.ActorId,
                actor.DisplayName,
                message));
        }

        return Accepted(actor);
    }

    private void AdvanceActors()
    {
        var arrivalDistanceSquared =
            _limits.DestinationArrivalDistance * _limits.DestinationArrivalDistance;

        foreach (var actor in _actors.Values)
        {
            if (!actor.Connected || actor.CurrentWaypoint is not { } destination)
            {
                actor.Velocity = Vector2.Zero;
                continue;
            }

            var remainingSeconds = 1f / SimulationTiming.TicksPerSecond;
            actor.Velocity = Vector2.Zero;
            while (remainingSeconds > 0 &&
                   actor.CurrentWaypoint is { } waypoint)
            {
                var difference = waypoint - actor.Position;
                var distanceSquared = difference.LengthSquared();
                if (!float.IsFinite(distanceSquared))
                {
                    actor.ClearRoute();
                    break;
                }

                if (distanceSquared <= arrivalDistanceSquared)
                {
                    actor.Position = waypoint;
                    actor.CompleteWaypoint();
                    continue;
                }

                var distance = MathF.Sqrt(distanceSquared);
                var direction = difference / distance;
                var terrainMultiplier = ActorMovementService.TerrainSpeedMultiplier(
                    _navigation.IsWading(actor.Position, actor.WorldLevel),
                    _navigation.HeightAt(actor.Position, actor.WorldLevel),
                    _navigation.HeightAt(waypoint, actor.WorldLevel));
                var speed = _limits.ActorMovementSpeed * terrainMultiplier;
                if (!float.IsFinite(speed) || speed <= 0)
                {
                    actor.ClearRoute();
                    break;
                }
                var availableDistance = speed * remainingSeconds;
                actor.Velocity = direction * speed;
                if (availableDistance + _limits.DestinationArrivalDistance < distance)
                {
                    actor.Position = ClampToWorld(
                        actor.Position + direction * availableDistance);
                    remainingSeconds = 0;
                    continue;
                }

                actor.Position = waypoint;
                remainingSeconds = Math.Max(
                    0,
                    remainingSeconds - distance / speed);
                actor.CompleteWaypoint();
            }

            if (actor.CurrentWaypoint is null)
                actor.Velocity = Vector2.Zero;
        }
    }

    private SessionSnapshot CaptureSnapshotCore(long sequence)
    {
        var actors = _actors.Values
            .OrderBy(static actor => actor.Identity.ActorId.Value)
            .Select(static actor => actor.ToSnapshot())
            .ToImmutableArray();
        return new SessionSnapshot(
            Id,
            sequence,
            Clock.Current,
            actors,
            _chatHistory.ToImmutableArray());
    }

    private PlayerIdentity CreateUniqueIdentity()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var identity = _identitySource.CreatePlayerIdentity();
            if (identity.PlayerId.Value != Guid.Empty &&
                identity.ActorId.Value != Guid.Empty &&
                !_actorsByPlayer.ContainsKey(identity.PlayerId) &&
                !_actors.ContainsKey(identity.ActorId))
            {
                return identity;
            }
        }

        throw new InvalidOperationException("The identity source failed to create a unique player identity.");
    }

    private bool TryGetActor(PlayerId playerId, out MutableActor actor)
    {
        if (_actorsByPlayer.TryGetValue(playerId, out var actorId) &&
            _actors.TryGetValue(actorId, out var existing))
        {
            actor = existing;
            return true;
        }

        actor = null!;
        return false;
    }

    private bool TryNormalizeDisplayName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
            normalized.Length <= _limits.MaximumDisplayNameLength &&
            normalized.All(IsAllowedInlineCharacter);
    }

    private bool TryNormalizeChat(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 &&
            normalized.Length <= _limits.MaximumChatLength &&
            normalized.All(IsAllowedInlineCharacter);
    }

    private bool TrySanitizePosition(Vector2 value, out Vector2 sanitized)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
        {
            sanitized = default;
            return false;
        }

        sanitized = ClampToWorld(value);
        return true;
    }

    private Vector2 ClampToWorld(Vector2 value) => new(
        Math.Clamp(value.X, _limits.MinimumWorldCoordinate, _limits.MaximumWorldCoordinate),
        Math.Clamp(value.Y, _limits.MinimumWorldCoordinate, _limits.MaximumWorldCoordinate));

    private static bool IsAllowedInlineCharacter(char value) =>
        !char.IsControl(value) && !char.IsSurrogate(value);

    private static byte[] HashToken(ReconnectToken token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token.Value));

    private static bool TokenMatches(byte[] expectedHash, ReconnectToken token)
    {
        var actualHash = HashToken(token);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static IntentResult Accepted(
        MutableActor actor,
        Guid commandId = default) =>
        new(IntentStatus.Accepted, actor.LastProcessedCommandSequence, null)
        {
            CommandId = commandId,
            InventoryRevision = actor.Gameplay.InventoryRevision,
            ActorRevision = actor.Gameplay.ActorRevision,
            Gameplay = actor.Gameplay.ToSnapshot()
        };

    private static IntentResult Rejected(
        IntentStatus status,
        MutableActor actor,
        string error,
        Guid commandId = default) =>
        new(status, actor.LastProcessedCommandSequence, error)
        {
            CommandId = commandId,
            InventoryRevision = actor.Gameplay.InventoryRevision,
            ActorRevision = actor.Gameplay.ActorRevision,
            Gameplay = actor.Gameplay.ToSnapshot()
        };

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void EnterOwner()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        if (_ownerThreadId is null)
        {
            _ownerThreadId = currentThreadId;
        }
        else if (_ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException(
                $"Authoritative session belongs to thread {_ownerThreadId}; thread {currentThreadId} cannot mutate it.");
        }

        if (Interlocked.Exchange(ref _executing, 1) != 0)
        {
            throw new InvalidOperationException("Concurrent authoritative session mutation is not allowed.");
        }
    }

    private void ExitOwner() => Volatile.Write(ref _executing, 0);

    private void EnsureOwnerThread()
    {
        if (_ownerThreadId is null || _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Actor registry state is available only on the session owner thread.");
        }
    }

    private abstract record QueuedOperation;

    private sealed record JoinOperation(
        JoinRequest Request,
        TaskCompletionSource<JoinResult> Completion) : QueuedOperation;

    private sealed record ReconnectOperation(
        ReconnectRequest Request,
        TaskCompletionSource<ReconnectResult> Completion) : QueuedOperation;

    private sealed record DisconnectOperation(
        DisconnectRequest Request,
        TaskCompletionSource<DisconnectResult> Completion) : QueuedOperation;

    private sealed record IntentOperation(
        ActorCommand Command,
        TaskCompletionSource<IntentResult>? Completion) : QueuedOperation;

    private sealed record ActiveCookingJob(
        Guid CommandId,
        ActorId ActorId,
        Guid CampfireId,
        WorldChunkKey CampfireChunk,
        Vector2 CampfirePosition,
        int PreferredInventorySlot,
        string RawItemId,
        string ResultItemId,
        int Experience,
        bool Burnt,
        Guid DropObjectId,
        long CompletesAtTick)
    {
        public AuthoritativeCookingJobCheckpoint ToCheckpoint() => new(
            CommandId,
            ActorId,
            CampfireId,
            CampfireChunk,
            CampfirePosition,
            PreferredInventorySlot,
            RawItemId,
            ResultItemId,
            Experience,
            Burnt,
            DropObjectId,
            CompletesAtTick);

        public static ActiveCookingJob FromCheckpoint(
            AuthoritativeCookingJobCheckpoint value) => new(
            value.CommandId,
            value.ActorId,
            value.CampfireId,
            value.CampfireChunk,
            value.CampfirePosition,
            value.PreferredInventorySlot,
            value.RawItemId,
            value.ResultItemId,
            value.Experience,
            value.Burnt,
            value.DropObjectId,
            value.CompletesAtTick);
    }

    private sealed class MutableActor
    {
        private readonly Dictionary<Guid, CommandReceipt> _receipts = [];
        private readonly Queue<Guid> _receiptOrder = [];
        private readonly List<Vector2> _route = [];
        private int _routeIndex;

        public MutableActor(
            PlayerIdentity identity,
            string displayName,
            Vector2 position,
            int worldLevel,
            ClientConnectionId connectionId,
            byte[] reconnectTokenHash)
        {
            Identity = identity;
            DisplayName = displayName;
            Position = position;
            WorldLevel = worldLevel;
            ConnectionId = connectionId;
            ReconnectTokenHash = reconnectTokenHash;
            Connected = true;
            Gameplay = new MutablePlayerGameplay();
        }

        public PlayerIdentity Identity { get; }

        public string DisplayName { get; }

        public Vector2 Position { get; set; }

        public Vector2 Velocity { get; set; }

        public Vector2? Destination { get; set; }

        public int WorldLevel { get; }

        public ClientConnectionId ConnectionId { get; set; }

        public byte[] ReconnectTokenHash { get; }

        public bool Connected { get; set; }

        public long LastProcessedCommandSequence { get; set; }

        public long? DisconnectedAtTick { get; set; }

        public MutablePlayerGameplay Gameplay { get; }

        public Vector2? CurrentWaypoint =>
            _routeIndex < _route.Count ? _route[_routeIndex] : null;

        public void ReplaceRoute(IReadOnlyList<Vector2> route)
        {
            _route.Clear();
            _route.AddRange(route);
            _routeIndex = 0;
            Destination = route.Count == 0 ? null : route[^1];
            Velocity = Vector2.Zero;
        }

        public void CompleteWaypoint()
        {
            if (_routeIndex < _route.Count) _routeIndex++;
            if (_routeIndex < _route.Count) return;
            _route.Clear();
            _routeIndex = 0;
            Destination = null;
        }

        public void ClearRoute()
        {
            _route.Clear();
            _routeIndex = 0;
            Destination = null;
            Velocity = Vector2.Zero;
        }

        public bool TryGetReceipt(Guid commandId, out CommandReceipt receipt) =>
            _receipts.TryGetValue(commandId, out receipt!);

        public void RememberReceipt(
            GameplayIntent intent,
            IntentResult result,
            int capacity)
        {
            if (_receipts.ContainsKey(intent.CommandId))
            {
                return;
            }

            while (_receipts.Count >= capacity)
            {
                var evicted = _receiptOrder.Dequeue();
                _receipts.Remove(evicted);
            }

            _receipts.Add(
                intent.CommandId,
                new CommandReceipt(
                    GameplayIntentFingerprint.Create(intent),
                    result,
                    Restored: false));
            _receiptOrder.Enqueue(intent.CommandId);
        }

        public ImmutableArray<AuthoritativeCommandReceiptCheckpoint>
            CaptureReceipts()
        {
            var result = ImmutableArray.CreateBuilder<
                AuthoritativeCommandReceiptCheckpoint>(_receiptOrder.Count);
            foreach (var commandId in _receiptOrder)
            {
                var receipt = _receipts[commandId];
                result.Add(new AuthoritativeCommandReceiptCheckpoint(
                    commandId,
                    receipt.PayloadFingerprint,
                    receipt.Result.Status,
                    receipt.Result.Error));
            }

            return result.MoveToImmutable();
        }

        public void RestoreReceipts(
            ImmutableArray<AuthoritativeCommandReceiptCheckpoint> receipts,
            int capacity)
        {
            if (receipts.IsDefault || receipts.Length > capacity ||
                _receipts.Count != 0 || _receiptOrder.Count != 0)
            {
                throw new InvalidDataException(
                    "The checkpoint command receipt history is invalid.");
            }

            foreach (var value in receipts)
            {
                if (value.CommandId == Guid.Empty ||
                    !GameplayIntentFingerprint.IsValid(
                        value.PayloadFingerprint) ||
                    !Enum.IsDefined(value.Status) ||
                    value.Error is { Length: > 512 } ||
                    value.Error?.Any(char.IsControl) == true ||
                    !_receipts.TryAdd(value.CommandId, new CommandReceipt(
                        value.PayloadFingerprint,
                        new IntentResult(
                            value.Status,
                            LastProcessedCommandSequence,
                            value.Error)
                        {
                            CommandId = value.CommandId
                        },
                        Restored: true)))
                {
                    throw new InvalidDataException(
                        "The checkpoint contains an invalid command receipt.");
                }

                _receiptOrder.Enqueue(value.CommandId);
            }
        }

        public ActorSnapshot ToSnapshot() => new(
                Identity.ActorId,
                Identity.PlayerId,
                DisplayName,
                Position,
                Velocity,
                Destination,
                WorldLevel,
                Connected,
                LastProcessedCommandSequence,
                DisconnectedAtTick)
            {
                Gameplay = Gameplay.ToSnapshot()
            };
    }

    private sealed class MutablePlayerGameplay
    {
        public const int MaximumHealth = 100;

        public MutablePlayerGameplay()
        {
            Inventory = PlayerInventory.CreateContainer();
        }

        public InventoryContainer Inventory { get; set; }

        public uint InventoryRevision { get; set; } = 1;

        public uint ActorRevision { get; set; } = 1;

        public int Health { get; set; } = MaximumHealth;

        public float Hunger { get; set; } = SurvivalService.MaximumHunger;

        public float WellFedSeconds { get; set; }

        public int CraftingExperience { get; set; }

        public int CookingExperience { get; set; }

        public void ReplaceWith(PlayerGameplaySnapshot snapshot)
        {
            if (snapshot.ActorRevision == 0 ||
                snapshot.Inventory.Revision == 0 ||
                snapshot.Inventory.Capacity != PlayerInventory.Capacity ||
                snapshot.Health is < 0 or > MaximumHealth ||
                !float.IsFinite(snapshot.Hunger) ||
                snapshot.Hunger is < 0 or > SurvivalService.MaximumHunger ||
                !float.IsFinite(snapshot.WellFedSeconds) ||
                snapshot.WellFedSeconds < 0 ||
                snapshot.CraftingExperience < 0 ||
                snapshot.CookingExperience < 0)
            {
                throw new InvalidOperationException(
                    "The world transaction returned invalid actor state.");
            }

            var inventory = PlayerInventory.CreateContainer();
            var seen = new bool[inventory.Capacity];
            foreach (var slot in snapshot.Inventory.Slots)
            {
                if ((uint)slot.Slot >= (uint)inventory.Capacity ||
                    seen[slot.Slot])
                {
                    throw new InvalidOperationException(
                        "The world transaction returned invalid inventory slots.");
                }

                seen[slot.Slot] = true;
                if (slot.ItemId is null && slot.Quantity == 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(slot.ItemId) ||
                    slot.Quantity <= 0 ||
                    !inventory.TrySetSlot(
                        slot.Slot,
                        slot.ItemId,
                        slot.Quantity))
                {
                    throw new InvalidOperationException(
                        "The world transaction returned an invalid inventory item.");
                }
            }

            if (seen.Any(value => !value))
            {
                throw new InvalidOperationException(
                    "The world transaction returned an incomplete inventory.");
            }

            Inventory = inventory;
            InventoryRevision = snapshot.Inventory.Revision;
            ActorRevision = snapshot.ActorRevision;
            Health = snapshot.Health;
            Hunger = snapshot.Hunger;
            WellFedSeconds = snapshot.WellFedSeconds;
            CraftingExperience = snapshot.CraftingExperience;
            CookingExperience = snapshot.CookingExperience;
        }

        public PlayerGameplaySnapshot ToSnapshot()
        {
            var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
                Inventory.Capacity);
            for (var slot = 0; slot < Inventory.Capacity; slot++)
            {
                var stack = Inventory[slot];
                slots.Add(new InventorySlotSnapshot(
                    slot,
                    stack?.ItemId,
                    stack?.Quantity ?? 0));
            }

            return new PlayerGameplaySnapshot(
                ActorRevision,
                Health,
                Hunger,
                WellFedSeconds,
                CraftingExperience,
                CookingExperience,
                new PlayerInventorySnapshot(
                    InventoryRevision,
                    slots.MoveToImmutable()));
        }
    }

    private sealed record CommandReceipt(
        string PayloadFingerprint,
        IntentResult Result,
        bool Restored);
}
