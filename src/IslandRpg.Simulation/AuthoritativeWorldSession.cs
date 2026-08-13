using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using IslandRpg.Gameplay;
using IslandRpg.Caves;
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
    private readonly AuthoritativeResourceTransactions? _resourceTransactions;
    private readonly AuthoritativeBoatTransactions? _boatTransactions;
    private readonly AuthoritativeCombatTransactions? _combatTransactions;
    private readonly Channel<QueuedOperation> _inbound;
    private readonly Dictionary<ActorId, MutableActor> _actors = [];
    private readonly Dictionary<PlayerId, ActorId> _actorsByPlayer = [];
    private readonly Dictionary<ClientConnectionId, PlayerId> _playersByConnection = [];
    private readonly HashSet<PlayerId> _expiredPlayers = [];
    private readonly Queue<PlayerId> _expiredPlayerOrder = [];
    private readonly Queue<ChatMessageSnapshot> _chatHistory = [];
    private readonly Dictionary<ActorId, ActiveCookingJob> _cookingJobs = [];
    private SessionSnapshot _latestSnapshot;
    private int? _ownerThreadId;
    private int _executing;
    private long _nextChatMessageId;
    private long _nextActorRetentionOrdinal;

    public AuthoritativeWorldSession(
        SimulationLimits? limits = null,
        ISessionIdentitySource? identitySource = null,
        SessionId? sessionId = null,
        IWorldNavigationQuery? navigation = null,
        IWorldNavigationObstacleSource? obstacles = null,
        AuthoritativeWorldTransactions? worldTransactions = null,
        AuthoritativeResourceTransactions? resourceTransactions = null,
        AuthoritativeBoatTransactions? boatTransactions = null,
        AuthoritativeCombatTransactions? combatTransactions = null)
    {
        _limits = (limits ?? SimulationLimits.Default).ValidatedCopy();
        _identitySource = identitySource ?? new SecureSessionIdentitySource();
        _navigation = navigation ?? OpenWorldNavigationQuery.Instance;
        _worldTransactions = worldTransactions ??
            new AuthoritativeWorldTransactions();
        var staticObstacles = obstacles ??
            EmptyWorldNavigationObstacleSource.Instance;
        _obstacles = ReferenceEquals(staticObstacles, _worldTransactions)
            ? staticObstacles
            : ReferenceEquals(
                staticObstacles, EmptyWorldNavigationObstacleSource.Instance)
                ? _worldTransactions
                : new CompositeWorldNavigationObstacleSource(
                    staticObstacles, _worldTransactions);
        _resourceTransactions = resourceTransactions;
        _boatTransactions = boatTransactions;
        _combatTransactions = combatTransactions;
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

    public event Action<ResourceTransactionResult>? ResourceTransactionCommitted;

    public event Action<BoatTransactionResult>? BoatTransactionCommitted;

    public event Action<BoatStateDelta>? BoatStateCommitted;

    /// <summary>
    /// Specialized observer hook for an autonomous route arrival. General
    /// public replication subscribes to <see cref="BoatAutonomousStateCommitted"/>.
    /// </summary>
    public event Action<BoatStateDelta>? BoatRouteCompleted;

    /// <summary>
    /// Raised for autonomous semantic boat transitions that must be published
    /// independently of a command outcome, including route arrival and an
    /// occupant dying. Command-bound transitions are returned on their result
    /// so requester-private state can retain precedence.
    /// </summary>
    public event Action<BoatStateDelta>? BoatAutonomousStateCommitted;

    public event Action<EnemyStateDelta>? EnemyStateCommitted;

    public event Action<CombatEventSnapshot>? CombatEventCommitted;

    public event Action<CookingCompletionSnapshot>? CookingCompleted;

    /// <summary>
    /// Raised synchronously on the owner thread after a gameplay intent has
    /// finished committing, but before its acknowledgement task is completed.
    /// Transport adapters use this boundary to reserve publication order
    /// without moving private requester state onto the simulation thread.
    /// </summary>
    public event Action<ActorCommand, IntentResult>? GameplayIntentCommitted;

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
    /// Trusted host-only queue seam for island-start boat provisioning. It
    /// preserves the session's single-owner mutation rule during handshake.
    /// </summary>
    public Task<AuthoritativeBoatSnapshot> EnqueueProvisionPlayerBoatAsync(
        PlayerId playerId,
        string? groupId = null)
    {
        var completion = NewCompletion<AuthoritativeBoatSnapshot>();
        if (!_inbound.Writer.TryWrite(new ProvisionPlayerBoatOperation(
                playerId, groupId, completion)))
            completion.SetException(new InvalidOperationException(
                "The authoritative command queue is full."));
        return completion.Task;
    }

    /// <summary>
    /// Enqueues a non-gameplay command without allocating an acknowledgement
    /// task. This is the preferred path for high-frequency movement input.
    /// Revision-checked gameplay always requires an acknowledgement so transport
    /// adapters can publish its private receipt and ordered public effects.
    /// </summary>
    public bool TryEnqueueIntent(ActorCommand command) =>
        command.Intent is not GameplayIntent &&
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
            if (_boatTransactions is not null)
                foreach (var delta in _boatTransactions.Advance(
                             SimulationTiming.FixedDeltaSeconds))
                {
                    BoatStateCommitted?.Invoke(delta);
                    BoatRouteCompleted?.Invoke(delta);
                    BoatAutonomousStateCommitted?.Invoke(delta);
                }
            SynchronizeBoatOccupants();
            AdvanceCombat();
            AdvanceSurvival();
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
    /// Trusted server seam for explicit boat seeds. Network input cannot
    /// create entities or select their identities.
    /// </summary>
    public AuthoritativeBoatSnapshot SeedBoat(AuthoritativeBoatSeed seed)
    {
        EnterOwner();
        try
        {
            var boat = _boatTransactions?.Seed(seed) ??
                   throw new InvalidOperationException(
                       "This session has no authoritative boat aggregate.");
            BoatStateCommitted?.Invoke(new(
                BoatChangeKind.Added, null, boat));
            return boat;
        }
        finally
        {
            ExitOwner();
        }
    }

    /// <summary>
    /// Trusted island-start provisioning seam. The host decides whether this
    /// is called; ordinary joins never implicitly create a raft.
    /// </summary>
    public AuthoritativeBoatSnapshot ProvisionPlayerBoat(
        PlayerId playerId,
        string? groupId = null)
    {
        EnterOwner();
        try
        {
            return ProvisionPlayerBoatCore(playerId, groupId);
        }
        finally
        {
            ExitOwner();
        }
    }

    private AuthoritativeBoatSnapshot ProvisionPlayerBoatCore(
        PlayerId playerId,
        string? groupId)
    {
        if (!TryGetActor(playerId, out var actor))
            throw new KeyNotFoundException("The player does not exist.");
        if (_boatTransactions is null)
            throw new InvalidOperationException(
                "This session has no authoritative boat aggregate.");
        var id = AuthoritativeBoatTransactions.DerivePlayerBoatId(playerId);
        var existed = _boatTransactions.CaptureBoats().Any(
            value => value.BoatId == id);
        var boat = _boatTransactions.ProvisionPlayerBoat(
            playerId, actor.Position, groupId);
        if (!existed)
            BoatStateCommitted?.Invoke(new(
                BoatChangeKind.Added, null, boat));
        return boat;
    }

    public ImmutableArray<AuthoritativeBoatSnapshot> CaptureBoats()
    {
        EnterOwner();
        try
        {
            return _boatTransactions?.CaptureBoats() ?? [];
        }
        finally
        {
            ExitOwner();
        }
    }

    public AuthoritativeEnemySnapshot SeedEnemy(AuthoritativeEnemySeed seed)
    {
        EnterOwner();
        try
        {
            var enemy = _combatTransactions?.Seed(seed) ??
                throw new InvalidOperationException(
                    "This session has no authoritative combat aggregate.");
            EnemyStateCommitted?.Invoke(new(EnemyChangeKind.Added, null, enemy));
            return enemy;
        }
        finally
        {
            ExitOwner();
        }
    }

    public ImmutableArray<AuthoritativeEnemySnapshot> CaptureEnemies()
    {
        EnterOwner();
        try
        {
            return _combatTransactions?.CaptureEnemies(
                ActorNetworkIds(), Clock.Current.ElapsedSeconds) ?? [];
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
                cooking,
                _resourceTransactions?.CaptureCheckpoint(),
                _boatTransactions?.CaptureCheckpoint(),
                _combatTransactions?.CaptureCheckpoint());
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
                _expiredPlayers.Count != 0 ||
                _expiredPlayerOrder.Count != 0 ||
                _chatHistory.Count != 0 || _nextChatMessageId != 0 ||
                _cookingJobs.Count != 0 || _nextActorRetentionOrdinal != 0)
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
            if (_resourceTransactions is not null &&
                checkpoint.Resources is null)
            {
                throw new InvalidDataException(
                    "A resource-enabled session requires resource checkpoint state.");
            }
            if (checkpoint.Resources is { } pendingResources)
            {
                if (_resourceTransactions is null)
                    throw new InvalidDataException(
                        "The checkpoint has resource state but this session has no resource authority.");
                _resourceTransactions.ValidateCheckpoint(pendingResources);
            }
            if (checkpoint.Boats is { } pendingBoats)
            {
                if (_boatTransactions is null)
                {
                    if (!pendingBoats.Boats.IsDefaultOrEmpty)
                        throw new InvalidDataException(
                            "The checkpoint has boats but this session has no boat authority.");
                }
                else
                {
                _boatTransactions.ValidateCheckpoint(pendingBoats);
                var persistedActors = actors.Values.ToDictionary(
                    static value => value.Identity.ActorId);
                foreach (var boat in pendingBoats.Boats)
                {
                    if (boat.OccupantActorId is not { } actorId) continue;
                    if (!persistedActors.TryGetValue(actorId, out var occupant) ||
                        boat.OccupantPlayerId != occupant.Identity.PlayerId ||
                        boat.Position != occupant.Position ||
                        boat.WorldLevel != occupant.WorldLevel)
                        throw new InvalidDataException(
                            "A persisted boat occupant does not match its actor.");
                }
                }
            }
            if (_combatTransactions is not null && checkpoint.Combat is null)
                throw new InvalidDataException(
                    "A combat-enabled session requires combat checkpoint state.");
            if (checkpoint.Combat is { } pendingCombat)
            {
                if (_combatTransactions is null)
                {
                    if (!pendingCombat.Enemies.IsDefaultOrEmpty)
                        throw new InvalidDataException(
                            "The checkpoint has enemies but this session has no combat authority.");
                }
                else
                {
                    _combatTransactions.ValidateCheckpoint(pendingCombat);
                    var actorIds = actors.Keys.ToHashSet();
                    if (pendingCombat.Enemies.Any(value =>
                            value.TargetActorId is { } target &&
                            !actorIds.Contains(target)))
                        throw new InvalidDataException(
                            "A persisted enemy targets an unknown actor.");
                }
            }
            // Resource validation precedes the world commit. Each restorer
            // then validates completely before mutating its own aggregate.
            _worldTransactions.RestoreCheckpoint(checkpoint.World);
            if (checkpoint.Resources is { } resources)
            {
                _resourceTransactions!.RestoreCheckpoint(resources);
            }
            if (checkpoint.Boats is { } boats)
                if (_boatTransactions is not null)
                    _boatTransactions.RestoreCheckpoint(boats);
            if (checkpoint.Combat is { } combat)
                if (_combatTransactions is not null)
                    _combatTransactions.RestoreCheckpoint(combat);
            Clock.Restore(checkpoint.Tick, checkpoint.SnapshotSequence);
            foreach (var value in actors) _actors.Add(value.Key, value.Value);
            foreach (var value in actorsByPlayer)
                _actorsByPlayer.Add(value.Key, value.Value);
            foreach (var actor in _actors.Values
                         .OrderBy(static value => value.DisconnectedAtTick)
                         .ThenBy(static value =>
                             value.Identity.PlayerId.Value))
                actor.RetentionOrdinal =
                    checked(++_nextActorRetentionOrdinal);
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
                var result = ProcessIntent(intent.Command);
                if (intent.Command.Intent is GameplayIntent)
                    GameplayIntentCommitted?.Invoke(intent.Command, result);
                intent.Completion?.SetResult(result);
                break;
            case ProvisionPlayerBoatOperation provision:
                try
                {
                    provision.Completion.SetResult(ProvisionPlayerBoatCore(
                        provision.PlayerId, provision.GroupId));
                }
                catch (Exception error)
                {
                    provision.Completion.SetException(error);
                }
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

        if (_playersByConnection.Count >= _limits.MaximumConnectedActors)
        {
            return new JoinResult(
                JoinStatus.SessionFull,
                default,
                default,
                0,
                "The session has reached its concurrent player limit.");
        }

        if (request.ProvisionBoat && _boatTransactions is null)
        {
            return new JoinResult(
                JoinStatus.InvalidRequest,
                default,
                default,
                0,
                "This session cannot provision an island-start boat.");
        }

        MutableActor? expiringActor = null;
        if (request.ProvisionBoat &&
            _boatTransactions!.RequiresPlayerBoatReclamation)
        {
            expiringActor = _actors.Values
                .Where(value => !value.Connected &&
                    _boatTransactions.HasBoatOwnedBy(
                        value.Identity.PlayerId))
                .OrderBy(static value => value.RetentionOrdinal)
                .ThenBy(static value => value.Identity.PlayerId.Value)
                .FirstOrDefault();
            if (expiringActor is null)
            {
                return new JoinResult(
                    JoinStatus.SessionFull,
                    default,
                    default,
                    0,
                    "The island fleet limit has no disconnected owner available for expiry.");
            }
        }

        if (_actors.Count >= _limits.MaximumActors && expiringActor is null)
        {
            expiringActor = _actors.Values
                .Where(static value => !value.Connected)
                .OrderBy(static value => value.RetentionOrdinal)
                .ThenBy(static value => value.Identity.PlayerId.Value)
                .FirstOrDefault();
            if (expiringActor is null)
            {
                return new JoinResult(
                    JoinStatus.SessionFull,
                    default,
                    default,
                    0,
                    "The session actor limit is occupied by connected players.");
            }
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

        AuthoritativeBoatSnapshot? boat = null;
        ImmutableArray<BoatStateDelta> replacedBoats = [];
        var replacedExpiredBoats = false;
        if (request.ProvisionBoat)
        {
            try
            {
                // Provision before publishing any actor lookup. The boat
                // aggregate either commits a complete stable entity or
                // throws without mutation, so a failed island join cannot
                // leak actor/player/connection mappings.
                if (expiringActor is null)
                {
                    boat = _boatTransactions!.ProvisionPlayerBoat(
                        identity.PlayerId, actor.Position);
                }
                else
                {
                    boat = _boatTransactions!.ReplacePlayerBoat(
                        expiringActor.Identity.PlayerId,
                        identity.PlayerId,
                        actor.Position,
                        out replacedBoats);
                    replacedExpiredBoats = true;
                }
            }
            catch (Exception error) when (error is ArgumentException or
                                          InvalidOperationException)
            {
                return new JoinResult(
                    JoinStatus.InvalidRequest,
                    default,
                    default,
                    0,
                "No valid island-start boat mooring is available.");
            }
        }
        if (expiringActor is not null)
            ExpireActor(
                expiringActor,
                replacedBoats,
                replacedExpiredBoats);
        actor.RetentionOrdinal = checked(++_nextActorRetentionOrdinal);
        _actors.Add(identity.ActorId, actor);
        _actorsByPlayer.Add(identity.PlayerId, identity.ActorId);
        _playersByConnection.Add(request.ConnectionId, identity.PlayerId);
        if (boat is not null)
            BoatStateCommitted?.Invoke(new(
                BoatChangeKind.Added, null, boat));

        return new JoinResult(
            JoinStatus.Accepted,
            identity,
            reconnectToken,
            1,
            null)
        {
            Gameplay = actor.Gameplay.ToSnapshot(),
            Position = actor.Position,
            WorldLevel = actor.WorldLevel,
            Boat = boat
        };
    }

    private void ExpireActor(
        MutableActor actor,
        ImmutableArray<BoatStateDelta> alreadyRemovedBoats,
        bool boatsAlreadyRemoved)
    {
        if (actor.Connected ||
            _playersByConnection.ContainsValue(actor.Identity.PlayerId))
            throw new InvalidOperationException(
                "A connected actor cannot expire from retained history.");

        _cookingJobs.Remove(actor.Identity.ActorId);
        _worldTransactions.ForgetActor(actor.Identity.ActorId);
        _resourceTransactions?.ForgetActor(actor.Identity.ActorId);
        var enemyDeltas = _combatTransactions?.ForgetActor(
            actor.Identity.ActorId) ?? [];
        var remainingBoatDeltas = _boatTransactions?.ForgetActor(
            actor.Identity.PlayerId,
            actor.Identity.ActorId) ?? [];
        var boatDeltas = boatsAlreadyRemoved
            ? alreadyRemovedBoats.AddRange(remainingBoatDeltas)
            : remainingBoatDeltas;

        if (!_actorsByPlayer.Remove(actor.Identity.PlayerId) ||
            !_actors.Remove(actor.Identity.ActorId))
            throw new InvalidOperationException(
                "The authoritative actor registry is inconsistent.");
        RememberExpiredPlayer(actor.Identity.PlayerId);

        foreach (var delta in boatDeltas)
        {
            BoatStateCommitted?.Invoke(delta);
            BoatAutonomousStateCommitted?.Invoke(delta);
        }
        foreach (var delta in enemyDeltas)
            EnemyStateCommitted?.Invoke(delta);
    }

    private void RememberExpiredPlayer(PlayerId playerId)
    {
        if (_limits.ExpiredPlayerTombstoneCapacity == 0) return;
        if (!_expiredPlayers.Add(playerId)) return;
        _expiredPlayerOrder.Enqueue(playerId);
        while (_expiredPlayerOrder.Count >
               _limits.ExpiredPlayerTombstoneCapacity)
            _expiredPlayers.Remove(_expiredPlayerOrder.Dequeue());
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
            if (_expiredPlayers.Contains(request.PlayerId))
            {
                return new ReconnectResult(
                    ReconnectStatus.ExpiredPlayer,
                    default,
                    0,
                    "This disconnected player expired from the bounded session history.");
            }
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

        if (_playersByConnection.Count >= _limits.MaximumConnectedActors)
        {
            return new ReconnectResult(
                ReconnectStatus.SessionFull,
                actor.Identity,
                checked(actor.LastProcessedCommandSequence + 1),
                "The session has reached its concurrent player limit.");
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
            Gameplay = actor.Gameplay.ToSnapshot(),
            Position = actor.Position,
            WorldLevel = actor.WorldLevel,
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
        BoatStateDelta? stoppedBoat = null;
        if (_boatTransactions?.StopForOccupant(
                actor.Identity.ActorId) is { } boatDelta)
        {
            stoppedBoat = boatDelta;
            BoatStateCommitted?.Invoke(boatDelta);
        }
        actor.DisconnectedAtTick = Clock.Tick;
        actor.RetentionOrdinal = checked(++_nextActorRetentionOrdinal);
        _playersByConnection.Remove(request.ConnectionId);
        return new DisconnectResult(DisconnectStatus.Accepted, null)
        {
            BoatDelta = stoppedBoat
        };
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
            if (gameplay.CommandId != Guid.Empty &&
                result.Status != IntentStatus.CommandIdConflict)
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
            WalkIntent walk when _boatTransactions?.FindByOccupant(
                actor.Identity.ActorId) is null => ProcessWalk(actor, walk),
            WalkIntent => Rejected(
                IntentStatus.AlreadyAboard, actor,
                "Use boat movement while aboard."),
            StopIntent when _boatTransactions?.FindByOccupant(
                actor.Identity.ActorId) is not null => Rejected(
                    IntentStatus.AlreadyAboard, actor,
                    "Use an exact revisioned boat stop while aboard."),
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

        if (intent is ResourceGameplayIntent resource)
        {
            return ProcessResourceIntent(actor, resource);
        }

        if (intent is BoatGameplayIntent boat)
            return ProcessBoatIntent(actor, boat);

        if (intent is CombatGameplayIntent combat)
            return ProcessCombatIntent(actor, combat);

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

        if (actor.Gameplay.LifeState == ActorLifeState.Dead ||
            actor.Gameplay.Health <= 0)
        {
            return Rejected(
                IntentStatus.DeadActor,
                actor,
                "A dead actor cannot change inventory, craft, or consume items.",
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
        var context = new WorldTransactionContext(
            intent.CommandId,
            actor.Identity.ActorId,
            intent.ExpectedActorRevision,
            intent.ExpectedInventoryRevision,
            GameplayIntentFingerprint.Create(intent));
        var cachedResolution = _worldTransactions.ResolveCached(
            context, out var cachedTransaction);
        if (cachedResolution != CachedWorldTransactionResolution.Missing)
        {
            return ResolveCachedWorldIntent(
                actor, intent, cachedResolution, cachedTransaction);
        }

        if (intent is PlantCropIntent crop &&
            (crop.WorldLevel != 0 ||
             !CropService.IsTileCenter(crop.Position) ||
             !_navigation.SupportsWorldLevel(crop.WorldLevel) ||
             !_navigation.CanStandAt(crop.Position, crop.WorldLevel) ||
             _resourceTransactions?.HasBlockingTreeAt(
                 crop.Position, crop.WorldLevel) == true ||
             _obstacles.GetObstacles(
                 crop.WorldLevel,
                 crop.Position - new Vector2(.25f),
                 crop.Position + new Vector2(.25f)).Any(value =>
                 value.Contains(crop.Position))))
        {
            return Rejected(
                IntentStatus.InvalidPlacement,
                actor,
                "Crops must be planted at the centre of a clear traversable surface tile.",
                intent.CommandId);
        }

        if (intent is PlaceConstructionIntent placement &&
            (!_navigation.SupportsWorldLevel(placement.WorldLevel) ||
             (PlaceableWorldObjectRules.TryGetCollision(
                  placement.DefinitionId, out var constructionDefinition) &&
              !IsClearConstructionFootprint(
                  placement, constructionDefinition))))
        {
            return Rejected(
                IntentStatus.InvalidPlacement,
                actor,
                "Construction must be placed on clear, level, dry terrain.",
                intent.CommandId);
        }

        if (intent is PlaceInventoryWorldObjectIntent furniture &&
            (!PlaceableWorldObjectRules.TryGet(
                 furniture.DefinitionId, out var furnitureDefinition) ||
             !PlaceableWorldObjectRules.IsSupportedTerrain(
                 furnitureDefinition,
                 furniture.Position,
                 furniture.Rotation,
                 furniture.WorldLevel,
                 _navigation) ||
             _resourceTransactions?.HasBlockingResourceInFootprint(
                 furniture.Position,
                 furniture.WorldLevel,
                 PlaceableWorldObjectRules.PlacementFootprint(
                     furnitureDefinition, furniture.Rotation)) == true ||
             _obstacles.GetObstacles(
                 furniture.WorldLevel,
                 PlaceableWorldObjectRules.CollisionBounds(
                     furnitureDefinition,
                     furniture.Position,
                     furniture.Rotation).Minimum - new Vector2(.18f),
                 PlaceableWorldObjectRules.CollisionBounds(
                     furnitureDefinition,
                     furniture.Position,
                     furniture.Rotation).Maximum + new Vector2(.18f)).Any(value =>
                 PlacementOverlapsObstacle(
                     furnitureDefinition,
                     furniture.Position,
                     furniture.Rotation,
                     value))))
        {
            return Rejected(
                IntentStatus.InvalidPlacement,
                actor,
                "Furniture must be placed on clear, level, traversable terrain.",
                intent.CommandId);
        }

        if (intent is StartExcavationIntent excavation &&
            (!float.IsFinite(excavation.Position.X) ||
             !float.IsFinite(excavation.Position.Y) ||
             !_navigation.SupportsWorldLevel(excavation.WorldLevel) ||
             !_navigation.CanStandAt(
                 CaveExcavationRules.Snap(excavation.Position),
                 excavation.WorldLevel) ||
             _resourceTransactions?.HasBlockingTreeAt(
                 CaveExcavationRules.Snap(excavation.Position),
                 excavation.WorldLevel) == true ||
             _obstacles.GetObstacles(
                 excavation.WorldLevel,
                 CaveExcavationRules.Snap(excavation.Position) -
                 new Vector2(.25f),
                 CaveExcavationRules.Snap(excavation.Position) +
                 new Vector2(.25f)).Any(value =>
                 value.Contains(CaveExcavationRules.Snap(
                     excavation.Position)))))
        {
            return Rejected(
                IntentStatus.InvalidPlacement,
                actor,
                "Excavation must begin on clear traversable ground.",
                intent.CommandId);
        }

        var gameSeconds = Clock.Current.ElapsedSeconds;
        var worldGameSeconds = AuthoritativeWorldTime
            .FromElapsedRealSeconds(gameSeconds);
        var beforeGameplay = actor.Gameplay.ToSnapshot();
        var input = new WorldTransactionActorInput(
            actor.Identity.ActorId,
            actor.Position,
            actor.WorldLevel,
            beforeGameplay);
        var questTarget = intent switch
        {
            PickUpWorldObjectIntent pickUp =>
                TryCaptureWorldObject(pickUp.Object.ObjectId),
            HarvestCropIntent harvest =>
                TryCaptureWorldObject(harvest.Crop.ObjectId),
            _ => null
        };
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
            PlaceInventoryWorldObjectIntent placeFurniture =>
                _worldTransactions.Execute(
                    input,
                    new PlaceInventoryWorldObjectTransaction(
                        context,
                        placeFurniture.DefinitionId,
                        placeFurniture.InventorySlot,
                        placeFurniture.Position,
                        placeFurniture.WorldLevel,
                        placeFurniture.Rotation,
                        placeFurniture.ExpectedChunkRevision)),
            PlantCropIntent plant => _worldTransactions.Execute(
                input,
                new PlantCropTransaction(
                    context,
                    AuthoritativeWorldTransactions.DeriveCropObjectId(
                        actor.Identity.ActorId,
                        plant.CommandId,
                        plant.ExpectedActorRevision),
                    plant.SeedInventorySlot,
                    plant.Position,
                    plant.WorldLevel,
                    plant.ExpectedChunkRevision,
                    worldGameSeconds)),
            HarvestCropIntent harvest => _worldTransactions.Execute(
                input,
                new HarvestCropTransaction(
                    context, harvest.Crop, worldGameSeconds)),
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
                    worldGameSeconds)),
            TakeCampfireFuelIntent takeFuel => _worldTransactions.Execute(
                input,
                new TakeCampfireFuelTransaction(
                    context,
                    takeFuel.Campfire,
                    worldGameSeconds)),
            LightCampfireIntent light => _worldTransactions.Execute(
                input,
                new LightCampfireTransaction(
                    context,
                    light.Campfire,
                    worldGameSeconds)),
            CookOnCampfireIntent cook => BeginCooking(
                actor, input, context, cook, worldGameSeconds),
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
            StartExcavationIntent start => _worldTransactions.Execute(
                input,
                new StartExcavationTransaction(
                    context, start.Position, start.WorldLevel,
                    start.ShovelInventorySlot,
                    start.ExpectedChunkRevision, gameSeconds)),
            WorkExcavationIntent work => _worldTransactions.Execute(
                input,
                new WorkExcavationTransaction(
                    context, work.Excavation,
                    work.ShovelInventorySlot, gameSeconds)),
            RestoreExcavationIntent restore => _worldTransactions.Execute(
                input,
                new RestoreExcavationTransaction(
                    context, restore.Excavation)),
            InstallCaveRopeIntent install => _worldTransactions.Execute(
                input,
                new InstallCaveRopeTransaction(
                    context, install.Shaft,
                    install.RopeInventorySlot)),
            TakeCaveRopeIntent take => _worldTransactions.Execute(
                input,
                new TakeCaveRopeTransaction(context, take.Entrance)),
            FillExcavationIntent fill => _worldTransactions.Execute(
                input,
                new FillExcavationTransaction(
                    context, fill.Excavation,
                    fill.MaterialInventorySlot)),
            TraverseCaveIntent traverse => _worldTransactions.Execute(
                input,
                new TraverseCaveTransaction(context, traverse.Entrance)),
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
            gameplay = ReconcileAdventureHealth(
                beforeGameplay, gameplay);
            actor.Gameplay.ReplaceWith(gameplay);
        }

        if (transaction.Accepted &&
            transaction.ActorTransition is { } transition)
        {
            actor.Position = transition.Position;
            actor.WorldLevel = transition.WorldLevel;
            actor.ClearRoute();
        }

        if (transaction.Accepted)
        {
            ApplyQuestEvents(
                actor,
                CommittedWorldQuestEvents(
                    intent,
                    questTarget,
                    transaction,
                    beforeGameplay,
                    actor.Gameplay.ToSnapshot()),
                beforeGameplay.ActorRevision);
            transaction = RebaseTransaction(
                transaction, actor.Gameplay.ToSnapshot());
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

    private IntentResult ResolveCachedWorldIntent(
        MutableActor actor,
        WorldGameplayIntent intent,
        CachedWorldTransactionResolution resolution,
        WorldTransactionResult transaction)
    {
        var gameplay = actor.Gameplay.ToSnapshot();
        var duplicate =
            resolution == CachedWorldTransactionResolution.Duplicate;
        var status = MapWorldStatus(transaction.Status);
        return new IntentResult(
            status,
            actor.LastProcessedCommandSequence,
            transaction.Accepted
                ? null
                : string.IsNullOrWhiteSpace(transaction.Detail)
                    ? transaction.Status.ToString()
                    : transaction.Detail)
        {
            CommandId = intent.CommandId,
            InventoryRevision = gameplay.Inventory.Revision,
            ActorRevision = gameplay.ActorRevision,
            Duplicate = duplicate,
            Gameplay = gameplay,
            // The aggregate receipt can outlive the session's private receipt.
            // Never expose its stale gameplay, container, cave transition, or
            // public deltas. A conflict has no cached effects and remains useful
            // as a typed world rejection; an exact retry is a safe tombstone.
            WorldTransaction = duplicate
                ? null
                : RebaseTransaction(transaction, gameplay)
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
            WorldTransactionStatus.InvalidExcavation =>
                IntentStatus.InvalidExcavation,
            WorldTransactionStatus.MissingExcavationTool =>
                IntentStatus.MissingExcavationTool,
            WorldTransactionStatus.ExcavationCadenceLocked =>
                IntentStatus.ExcavationCadenceLocked,
            WorldTransactionStatus.InvalidCaveLink =>
                IntentStatus.InvalidCaveLink,
            WorldTransactionStatus.NotCrop => IntentStatus.NotCrop,
            WorldTransactionStatus.CropNotReady =>
                IntentStatus.CropNotReady,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private IntentResult ProcessResourceIntent(
        MutableActor actor,
        ResourceGameplayIntent intent)
    {
        if (_resourceTransactions is null)
        {
            return Rejected(
                IntentStatus.InvalidIntent,
                actor,
                "This session has no authoritative resource catalog.",
                intent.CommandId);
        }

        var context = new WorldTransactionContext(
            intent.CommandId,
            actor.Identity.ActorId,
            intent.ExpectedActorRevision,
            intent.ExpectedInventoryRevision);
        var inputGameplay = actor.Gameplay.ToSnapshot();
        var input = new WorldTransactionActorInput(
            actor.Identity.ActorId,
            actor.Position,
            actor.WorldLevel,
            inputGameplay);
        var occupiedBoat = intent is CatchFishIntent
            ? _boatTransactions?.FindByOccupant(actor.Identity.ActorId)
            : null;
        var realSeconds = Clock.Current.ElapsedSeconds;
        var worldGameSeconds = AuthoritativeWorldTime
            .FromElapsedRealSeconds(realSeconds);
        var transaction = intent switch
        {
            GatherTreeStickIntent gather => _resourceTransactions.Execute(
                input,
                new GatherTreeStickTransaction(
                    context, gather.Node, realSeconds, worldGameSeconds)),
            StrikeTreeIntent strike => _resourceTransactions.Execute(
                input,
                new StrikeTreeTransaction(
                    context, strike.Node, strike.ToolInventorySlot,
                    realSeconds, worldGameSeconds)),
            GatherFibreIntent fibre => _resourceTransactions.Execute(
                input,
                new GatherFibreTransaction(
                    context, fibre.Node, realSeconds, worldGameSeconds)),
            GatherBerriesIntent berries => _resourceTransactions.Execute(
                input,
                new GatherBerriesTransaction(
                    context, berries.Node, berries.ToolInventorySlot,
                    realSeconds, worldGameSeconds)),
            MineResourceIntent mining => _resourceTransactions.Execute(
                input,
                new MineResourceTransaction(
                    context, mining.Node, mining.ToolInventorySlot,
                    realSeconds, worldGameSeconds)),
            CatchFishIntent fishing => _resourceTransactions.Execute(
                input with
                {
                    Position = occupiedBoat?.Position ?? actor.Position
                },
                new CatchFishTransaction(
                    context, fishing.Node,
                    fishing.FishingNetInventorySlot,
                    occupiedBoat is null ? 2.4f : 2.85f,
                    realSeconds, worldGameSeconds)),
            _ => throw new InvalidOperationException(
                "The resource gameplay intent type is unsupported.")
        };

        if (transaction.Accepted && transaction.Gameplay is { } gameplay &&
            (gameplay.ActorRevision != actor.Gameplay.ActorRevision ||
             gameplay.Inventory.Revision != actor.Gameplay.InventoryRevision ||
             gameplay.WoodcuttingExperience !=
             actor.Gameplay.WoodcuttingExperience ||
             gameplay.FarmingExperience != actor.Gameplay.FarmingExperience ||
             gameplay.MiningExperience != actor.Gameplay.MiningExperience ||
             gameplay.AdventureExperience !=
             actor.Gameplay.AdventureExperience ||
             gameplay.FishingExperience !=
             actor.Gameplay.FishingExperience))
        {
            gameplay = ReconcileAdventureHealth(
                inputGameplay, gameplay);
            actor.Gameplay.ReplaceWith(gameplay);
        }
        if (transaction.Accepted)
        {
            ApplyQuestEvents(
                actor,
                CommittedResourceQuestEvents(intent, transaction),
                inputGameplay.ActorRevision);
            transaction = RebaseTransaction(
                transaction, actor.Gameplay.ToSnapshot());
        }
        // Accepted misses still carry authoritative hit/damage feedback and
        // cadence progression even though no node revision changed.
        if (transaction.Accepted)
            ResourceTransactionCommitted?.Invoke(transaction);

        return new IntentResult(
            MapResourceStatus(transaction.Status),
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
            ResourceTransaction = transaction
        };
    }

    private static IntentStatus MapResourceStatus(
        ResourceTransactionStatus status) => status switch
        {
            ResourceTransactionStatus.Accepted => IntentStatus.Accepted,
            ResourceTransactionStatus.InvalidCommand =>
                IntentStatus.WorldCommandInvalid,
            ResourceTransactionStatus.ActorNotFound =>
                IntentStatus.ActorNotFound,
            ResourceTransactionStatus.DeadActor => IntentStatus.DeadActor,
            ResourceTransactionStatus.StaleActorRevision =>
                IntentStatus.StaleActorRevision,
            ResourceTransactionStatus.StaleInventoryRevision =>
                IntentStatus.StaleInventoryRevision,
            ResourceTransactionStatus.ResourceNotFound =>
                IntentStatus.ResourceNotFound,
            ResourceTransactionStatus.WrongResourceKind =>
                IntentStatus.WrongResourceKind,
            ResourceTransactionStatus.StaleNodeRevision =>
                IntentStatus.StaleNodeRevision,
            ResourceTransactionStatus.StaleResourceChunkRevision =>
                IntentStatus.StaleResourceChunkRevision,
            ResourceTransactionStatus.WrongWorldLevel =>
                IntentStatus.WrongWorldLevel,
            ResourceTransactionStatus.OutOfRange => IntentStatus.OutOfRange,
            ResourceTransactionStatus.InventoryFull =>
                IntentStatus.InventoryFull,
            ResourceTransactionStatus.MissingTool => IntentStatus.MissingTool,
            ResourceTransactionStatus.CadenceLocked =>
                IntentStatus.ResourceCadenceLocked,
            ResourceTransactionStatus.Depleted => IntentStatus.ResourceDepleted,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private void ApplyQuestEvents(
        MutableActor actor,
        IEnumerable<QuestEvent> events,
        uint baselineActorRevision)
    {
        var gameplay = actor.Gameplay;
        var quests = gameplay.Quests;
        var adventureExperience = gameplay.AdventureExperience;
        var changed = false;
        foreach (var questEvent in events)
        {
            var update = QuestService.Apply(
                quests,
                adventureExperience,
                questEvent,
                Clock.Tick);
            quests = update.Progress;
            adventureExperience = update.AdventureExperience;
            changed |= update.Changed;
        }

        // A newly unlocked objective may already be satisfied by items the
        // server owns in this inventory. Reconcile those facts from the
        // authoritative slots, never from client-provided totals. The pass is
        // bounded by the quest chain, and canonical counters make repeats
        // idempotent.
        for (var pass = 0; pass < QuestService.Definitions.Count; pass++)
        {
            var activeBefore = QuestService.ActiveQuest(quests)?
                .Definition.Id;
            var inventoryEvents = QuestService.InventoryProgressEvents(
                quests,
                gameplay.Inventory.ItemIds(),
                gameplay.Inventory.Quantities());
            if (inventoryEvents.IsDefaultOrEmpty) break;

            foreach (var inventoryEvent in inventoryEvents)
            {
                var update = QuestService.Apply(
                    quests,
                    adventureExperience,
                    inventoryEvent,
                    Clock.Tick);
                quests = update.Progress;
                adventureExperience = update.AdventureExperience;
                changed |= update.Changed;

                var activeAfter = QuestService.ActiveQuest(quests)?
                    .Definition.Id;
                if (!string.Equals(
                        activeBefore, activeAfter, StringComparison.Ordinal))
                    break;
            }

            if (string.Equals(
                    activeBefore,
                    QuestService.ActiveQuest(quests)?.Definition.Id,
                    StringComparison.Ordinal))
                break;
        }

        if (!changed) return;

        var previousMaximumHealth = gameplay.MaximumHealth;
        var maximumHealth = AdventureService.MaximumHealth(
            adventureExperience);
        var health = Math.Clamp(
            checked(gameplay.Health +
                    (maximumHealth - previousMaximumHealth)),
            0,
            maximumHealth);
        var actorRevision = gameplay.ActorRevision == baselineActorRevision
            ? checked(gameplay.ActorRevision + 1)
            : gameplay.ActorRevision;
        gameplay.Quests = quests;
        gameplay.AdventureExperience = adventureExperience;
        gameplay.MaximumHealth = maximumHealth;
        gameplay.Health = health;
        gameplay.ActorRevision = actorRevision;
    }

    private static PlayerGameplaySnapshot ReconcileAdventureHealth(
        PlayerGameplaySnapshot before,
        PlayerGameplaySnapshot after)
    {
        if (after.AdventureExperience == before.AdventureExperience)
            return after;
        var maximumHealth = AdventureService.MaximumHealth(
            after.AdventureExperience);
        var gainedMaximumHealth = maximumHealth - before.MaximumHealth;
        return after with
        {
            MaximumHealth = maximumHealth,
            Health = Math.Clamp(
                checked(after.Health + gainedMaximumHealth),
                0,
                maximumHealth)
        };
    }

    private AuthoritativeWorldObjectSnapshot? TryCaptureWorldObject(
        Guid objectId)
    {
        try
        {
            return _worldTransactions.CaptureObject(objectId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    internal static ImmutableArray<QuestEvent> CommittedWorldQuestEvents(
        WorldGameplayIntent intent,
        AuthoritativeWorldObjectSnapshot? target,
        WorldTransactionResult transaction,
        PlayerGameplaySnapshot before,
        PlayerGameplaySnapshot after)
    {
        switch (intent)
        {
            case PickUpWorldObjectIntent when target is { } pickedUp:
                return [new QuestEvent(
                    QuestEventType.GatherItem,
                    pickedUp.DefinitionId)];
            case HarvestCropIntent when
                target?.FuelItemId is { } harvestItemId:
            {
                var quantity = InventoryItemCount(after, harvestItemId) -
                               InventoryItemCount(before, harvestItemId);
                return quantity > 0
                    ? [new QuestEvent(
                        QuestEventType.GatherItem,
                        harvestItemId,
                        quantity)]
                    : [];
            }
            case LightCampfireIntent:
                return [new QuestEvent(QuestEventType.LightCampfire)];
            case PlaceConstructionIntent placement:
                return [new QuestEvent(
                    QuestEventType.BuildObject,
                    placement.DefinitionId)];
            case PlaceInventoryWorldObjectIntent furniture when
                PlaceableWorldObjectRules.TryGet(
                    furniture.DefinitionId, out var furnitureDefinition):
                return [new QuestEvent(
                    QuestEventType.BuildObject,
                    furnitureDefinition.ItemId)];
            case TraverseCaveIntent when
                transaction.ActorTransition?.WorldLevel ==
                    CaveExcavationRules.UndergroundWorldLevel:
                return [new QuestEvent(QuestEventType.EnterCave)];
            default:
                return [];
        }
    }

    internal static ImmutableArray<QuestEvent> CommittedResourceQuestEvents(
        ResourceGameplayIntent intent,
        ResourceTransactionResult transaction)
    {
        if (intent is CatchFishIntent)
        {
            if (transaction.FishingOutcome is not { Caught: true })
                return [];
            var catchReward = transaction.Rewards.IsDefaultOrEmpty
                ? null
                : transaction.Rewards[0].ItemId;
            return [new QuestEvent(
                QuestEventType.CatchFish,
                catchReward)];
        }

        if (transaction.Rewards.IsDefaultOrEmpty) return [];
        var type = intent is MineResourceIntent
            ? QuestEventType.MineOre
            : QuestEventType.GatherItem;
        return transaction.Rewards
            .Where(static reward => reward.Quantity > 0)
            .Select(reward => new QuestEvent(
                type,
                reward.ItemId,
                reward.Quantity))
            .ToImmutableArray();
    }

    private static int InventoryItemCount(
        PlayerGameplaySnapshot gameplay,
        string itemId) => gameplay.Inventory.Slots
        .Where(slot => string.Equals(
            slot.ItemId,
            itemId,
            StringComparison.OrdinalIgnoreCase))
        .Sum(static slot => slot.Quantity);

    internal static ImmutableArray<QuestEvent> CommittedCookingQuestEvents(
        bool interrupted,
        bool burnt,
        string resultItemId) => !interrupted && !burnt
        ? [new QuestEvent(QuestEventType.CookFood, resultItemId)]
        : [];

    private static WorldTransactionResult RebaseTransaction(
        WorldTransactionResult transaction,
        PlayerGameplaySnapshot gameplay) => transaction with
    {
        ActorRevision = gameplay.ActorRevision,
        InventoryRevision = gameplay.Inventory.Revision,
        Gameplay = gameplay
    };

    private static ResourceTransactionResult RebaseTransaction(
        ResourceTransactionResult transaction,
        PlayerGameplaySnapshot gameplay) => transaction with
    {
        ActorRevision = gameplay.ActorRevision,
        InventoryRevision = gameplay.Inventory.Revision,
        Gameplay = gameplay
    };

    private IntentResult ProcessBoatIntent(
        MutableActor actor,
        BoatGameplayIntent intent)
    {
        if (_boatTransactions is null)
            return Rejected(IntentStatus.InvalidIntent, actor,
                "This session has no authoritative boat authority.",
                intent.CommandId);

        var context = new WorldTransactionContext(
            intent.CommandId,
            actor.Identity.ActorId,
            intent.ExpectedActorRevision,
            intent.ExpectedInventoryRevision);
        var input = new BoatTransactionActorInput(
            actor.Identity.ActorId,
            actor.Identity.PlayerId,
            actor.Position,
            actor.WorldLevel,
            actor.Gameplay.ToSnapshot());
        var transaction = intent switch
        {
            BoardBoatIntent board => _boatTransactions.Execute(
                input, new BoardBoatTransaction(context, board.Boat)),
            MoveBoatIntent move => _boatTransactions.Execute(
                input, new MoveBoatTransaction(
                    context, move.Boat, move.Target)),
            StopBoatIntent stop => _boatTransactions.Execute(
                input, new StopBoatTransaction(context, stop.Boat)),
            DisembarkBoatIntent disembark => _boatTransactions.Execute(
                input, new DisembarkBoatTransaction(
                    context, disembark.Boat,
                    disembark.RequestedLanding)),
            _ => throw new InvalidOperationException(
                "The boat gameplay intent type is unsupported.")
        };

        if (transaction.Accepted &&
            transaction.Gameplay.ActorRevision !=
            actor.Gameplay.ActorRevision)
            actor.Gameplay.ReplaceWith(transaction.Gameplay);
        if (transaction.Accepted &&
            transaction.ActorTransition is { } transition)
        {
            actor.Position = transition.Position;
            actor.WorldLevel = transition.WorldLevel;
            actor.ClearRoute();
        }
        if (transaction.Accepted && transaction.BoatDelta is { } delta)
        {
            BoatTransactionCommitted?.Invoke(transaction);
            BoatStateCommitted?.Invoke(delta);
        }

        return new IntentResult(
            MapBoatStatus(transaction.Status),
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
            BoatTransaction = transaction
        };
    }

    private static IntentStatus MapBoatStatus(
        BoatTransactionStatus status) => status switch
        {
            BoatTransactionStatus.Accepted => IntentStatus.Accepted,
            BoatTransactionStatus.InvalidCommand =>
                IntentStatus.WorldCommandInvalid,
            BoatTransactionStatus.ActorNotFound => IntentStatus.ActorNotFound,
            BoatTransactionStatus.DeadActor => IntentStatus.DeadActor,
            BoatTransactionStatus.StaleActorRevision =>
                IntentStatus.StaleActorRevision,
            BoatTransactionStatus.StaleInventoryRevision =>
                IntentStatus.StaleInventoryRevision,
            BoatTransactionStatus.BoatNotFound => IntentStatus.BoatNotFound,
            BoatTransactionStatus.StaleBoatRevision =>
                IntentStatus.StaleBoatRevision,
            BoatTransactionStatus.WrongWorldLevel =>
                IntentStatus.WrongWorldLevel,
            BoatTransactionStatus.OutOfRange => IntentStatus.OutOfRange,
            BoatTransactionStatus.AccessDenied => IntentStatus.AccessDenied,
            BoatTransactionStatus.AlreadyAboard => IntentStatus.AlreadyAboard,
            BoatTransactionStatus.BoatOccupied => IntentStatus.BoatOccupied,
            BoatTransactionStatus.NotAboard => IntentStatus.NotAboard,
            BoatTransactionStatus.InvalidDestination =>
                IntentStatus.InvalidBoatDestination,
            BoatTransactionStatus.DestinationTooFar =>
                IntentStatus.BoatDestinationTooFar,
            BoatTransactionStatus.RouteUnreachable =>
                IntentStatus.BoatRouteUnreachable,
            BoatTransactionStatus.InvalidLanding =>
                IntentStatus.InvalidBoatLanding,
            BoatTransactionStatus.PlanningCadenceLocked =>
                IntentStatus.BoatPlanningLocked,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private IntentResult ProcessCombatIntent(
        MutableActor actor,
        CombatGameplayIntent intent)
    {
        if (_combatTransactions is null)
        {
            if (intent is RespawnIntent respawn)
                return ProcessRespawnWithoutCombat(actor, respawn);
            return Rejected(IntentStatus.CombatUnavailable, actor,
                "This session has no authoritative combat authority.",
                intent.CommandId);
        }

        if (intent is SetCombatTargetIntent &&
            _boatTransactions?.FindByOccupant(
                actor.Identity.ActorId) is not null)
            return Rejected(IntentStatus.AlreadyAboard, actor,
                "Disembark before entering combat.",
                intent.CommandId);
        var context = new WorldTransactionContext(
            intent.CommandId, actor.Identity.ActorId,
            intent.ExpectedActorRevision, intent.ExpectedInventoryRevision);
        var input = CombatInput(actor);
        var transaction = intent switch
        {
            SetCombatTargetIntent target => _combatTransactions.SetTarget(
                input, context, target.Enemy, Clock.Tick),
            CancelCombatIntent => _combatTransactions.CancelTarget(
                input, context, Clock.Tick),
            SetCombatStanceIntent stance => _combatTransactions.SetStance(
                input, context, stance.Stance),
            RespawnIntent => _combatTransactions.Respawn(
                input, context, Clock.Tick),
            _ => throw new InvalidOperationException(
                "The combat gameplay intent type is unsupported.")
        };
        BoatStateDelta? detachedBoat = null;
        if (transaction.Accepted)
        {
            actor.Gameplay.ReplaceWith(transaction.Gameplay);
            if (intent is SetCombatTargetIntent or CancelCombatIntent)
                actor.ClearRoute();
            if (intent is RespawnIntent)
            {
                detachedBoat = _boatTransactions?.DetachOccupant(
                    actor.Identity.ActorId);
                if (detachedBoat is not null)
                    BoatStateCommitted?.Invoke(detachedBoat);
                actor.Position = _combatTransactions.RespawnPosition;
                actor.WorldLevel = 0;
                actor.ClearRoute();
            }
            // Command deltas/events remain on the private result. The server
            // sends that receipt/player state first, then publishes them so a
            // reconnecting requester can never observe public combat ahead of
            // its own authoritative outcome. Autonomous tick transitions use
            // the session events below.
        }
        return new IntentResult(
            MapCombatStatus(transaction.Status),
            actor.LastProcessedCommandSequence,
            transaction.Accepted ? null : transaction.Detail)
        {
            CommandId = intent.CommandId,
            InventoryRevision = actor.Gameplay.InventoryRevision,
            ActorRevision = actor.Gameplay.ActorRevision,
            Gameplay = actor.Gameplay.ToSnapshot(),
            CombatTransaction = transaction,
            BoatDelta = detachedBoat
        };
    }

    private IntentResult ProcessRespawnWithoutCombat(
        MutableActor actor,
        RespawnIntent intent)
    {
        CombatTransactionStatus status;
        string detail;
        if (intent.ExpectedInventoryRevision !=
            actor.Gameplay.InventoryRevision)
        {
            status = CombatTransactionStatus.StaleInventoryRevision;
            detail = "The inventory revision is stale.";
        }
        else if (intent.ExpectedActorRevision != actor.Gameplay.ActorRevision)
        {
            status = CombatTransactionStatus.StaleActorRevision;
            detail = "The actor revision is stale.";
        }
        else if (actor.Gameplay.LifeState != ActorLifeState.Dead)
        {
            status = CombatTransactionStatus.ActorAlive;
            detail = "The actor is already alive.";
        }
        else if (Clock.Tick < actor.Gameplay.RespawnAvailableTick)
        {
            status = CombatTransactionStatus.RespawnLocked;
            detail = "The respawn delay has not elapsed.";
        }
        else
        {
            status = CombatTransactionStatus.Accepted;
            detail = string.Empty;
        }

        BoatStateDelta? detachedBoat = null;
        if (status == CombatTransactionStatus.Accepted)
        {
            var gameplay = AuthoritativeCombatTransactions.RespawnGameplay(
                actor.Gameplay.ToSnapshot());
            actor.Gameplay.ReplaceWith(gameplay);
            detachedBoat = _boatTransactions?.DetachOccupant(
                actor.Identity.ActorId);
            if (detachedBoat is not null)
                BoatStateCommitted?.Invoke(detachedBoat);
            actor.Position = Vector2.Zero;
            actor.WorldLevel = 0;
            actor.ClearRoute();
        }

        var transaction = new CombatTransactionResult(
            intent.CommandId,
            status,
            actor.Gameplay.ToSnapshot(),
            Detail: detail);
        return new IntentResult(
            MapCombatStatus(status),
            actor.LastProcessedCommandSequence,
            status == CombatTransactionStatus.Accepted ? null : detail)
        {
            CommandId = intent.CommandId,
            InventoryRevision = actor.Gameplay.InventoryRevision,
            ActorRevision = actor.Gameplay.ActorRevision,
            Gameplay = actor.Gameplay.ToSnapshot(),
            CombatTransaction = transaction,
            BoatDelta = detachedBoat
        };
    }

    private static IntentStatus MapCombatStatus(
        CombatTransactionStatus status) => status switch
        {
            CombatTransactionStatus.Accepted => IntentStatus.Accepted,
            CombatTransactionStatus.DeadActor => IntentStatus.DeadActor,
            CombatTransactionStatus.ActorAlive => IntentStatus.ActorAlreadyAlive,
            CombatTransactionStatus.StaleActorRevision =>
                IntentStatus.StaleActorRevision,
            CombatTransactionStatus.StaleInventoryRevision =>
                IntentStatus.StaleInventoryRevision,
            CombatTransactionStatus.EnemyNotFound => IntentStatus.EnemyNotFound,
            CombatTransactionStatus.EnemyDead => IntentStatus.EnemyDead,
            CombatTransactionStatus.StaleEnemyRevision =>
                IntentStatus.StaleEnemyRevision,
            CombatTransactionStatus.WrongWorldLevel =>
                IntentStatus.WorldLevelMismatch,
            CombatTransactionStatus.InvalidStance =>
                IntentStatus.InvalidCombatStance,
            CombatTransactionStatus.RespawnLocked => IntentStatus.RespawnLocked,
            _ => IntentStatus.InvalidIntent
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
                    AuthoritativeWorldTime.FromElapsedRealSeconds(
                        Clock.Current.ElapsedSeconds)));
            if (!transaction.Accepted || transaction.Gameplay is not { } gameplay)
            {
                throw new InvalidOperationException(
                    $"A validated cooking job could not complete: {transaction.Status}.");
            }
            gameplay = ReconcileAdventureHealth(input.Gameplay, gameplay);
            actor.Gameplay.ReplaceWith(gameplay);
            _cookingJobs.Remove(job.ActorId);
            var interrupted = transaction.Detail == "cooking_interrupted";
            ApplyQuestEvents(
                actor,
                CommittedCookingQuestEvents(
                    interrupted, job.Burnt, job.ResultItemId),
                input.Gameplay.ActorRevision);
            gameplay = actor.Gameplay.ToSnapshot();
            transaction = RebaseTransaction(transaction, gameplay);
            if (!transaction.ObjectDeltas.IsDefaultOrEmpty ||
                !transaction.ChunkDeltas.IsDefaultOrEmpty)
                WorldTransactionCommitted?.Invoke(transaction);
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

    private IntentResult ProcessCombineInventorySlots(
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

    private IntentResult ProcessCraftRecipe(
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

    private IntentResult TryCraft(
        MutableActor actor,
        CraftingRecipe recipe,
        Guid commandId)
    {
        var gameplay = actor.Gameplay;
        var baselineActorRevision = gameplay.ActorRevision;
        var beforeItems = gameplay.Inventory.ItemIds();
        var beforeResultCount = gameplay.Inventory.Count(recipe.ResultItemId);
        var craftResult = CraftingService.TryCraftDetailed(
            recipe,
            CraftingSkill.LevelForExperience(gameplay.CraftingExperience),
            gameplay.Inventory,
            out var updated,
            requiredStationAvailable:
                recipe.RequiredStationItemId is null ||
                _worldTransactions.HasCraftingStationWithin(
                    actor.Position,
                    actor.WorldLevel,
                    recipe.RequiredStationItemId,
                    PlaceableWorldObjectRules.CraftingStationInteractionRange));
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
        var adventure = AdventureService.AwardFromAction(
            gameplay.AdventureExperience,
            experience.Gained);
        var nextInventoryRevision = checked(gameplay.InventoryRevision + 1);
        var nextActorRevision = experience.Gained == 0 &&
            adventure.Gained == 0
                ? gameplay.ActorRevision
                : checked(gameplay.ActorRevision + 1);

        gameplay.Inventory = updated;
        gameplay.InventoryRevision = nextInventoryRevision;
        gameplay.CraftingExperience = experience.Experience;
        gameplay.AdventureExperience = adventure.Experience;
        var maximumHealth = AdventureService.MaximumHealth(
            gameplay.AdventureExperience);
        gameplay.Health = Math.Clamp(
            checked(gameplay.Health + maximumHealth - gameplay.MaximumHealth),
            0,
            maximumHealth);
        gameplay.MaximumHealth = maximumHealth;
        gameplay.ActorRevision = nextActorRevision;
        var crafted = gameplay.Inventory.Count(recipe.ResultItemId) -
                      beforeResultCount;
        if (crafted > 0)
            ApplyQuestEvents(
                actor,
                [new QuestEvent(
                    QuestEventType.CraftItem,
                    recipe.ResultItemId,
                    crafted)],
                baselineActorRevision);
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
            gameplay.Health >= gameplay.MaximumHealth)
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
            gameplay.MaximumHealth);
        var timedHealing = effect.TimedHealing > 0
            ? TimedHealingService.Start(effect)
            : gameplay.TimedHealing;
        if (survival.Health >= gameplay.MaximumHealth)
            timedHealing = default;
        var actorChanged = survival.Health != gameplay.Health ||
            survival.Hunger != gameplay.Hunger ||
            survival.WellFedSeconds != gameplay.WellFedSeconds ||
            timedHealing != gameplay.TimedHealing;
        var nextInventoryRevision = checked(gameplay.InventoryRevision + 1);
        var nextActorRevision = actorChanged
            ? checked(gameplay.ActorRevision + 1)
            : gameplay.ActorRevision;

        gameplay.Inventory = updatedInventory;
        gameplay.InventoryRevision = nextInventoryRevision;
        gameplay.Health = survival.Health;
        gameplay.Hunger = survival.Hunger;
        gameplay.WellFedSeconds = survival.WellFedSeconds;
        gameplay.TimedHealing = timedHealing;
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
        if (actor.Gameplay.LifeState == ActorLifeState.Dead ||
            actor.Gameplay.Health <= 0)
            return Rejected(IntentStatus.DeadActor, actor,
                "A dead actor cannot move.");
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
            obstacleSource: _obstacles);
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

        // Ordinary movement is an authoritative decision to leave combat.
        // Commit it only after route validation so a rejected replacement
        // cannot discard the actor's live target. Both mutations occur on
        // this single owner thread before the fixed step can observe either.
        CancelCombatForMovement(actor);
        actor.ReplaceRoute(route);
        return Accepted(actor);
    }

    private static bool PlacementOverlapsObstacle(
        PlaceableWorldObjectDefinition definition,
        Vector2 center,
        int rotation,
        NavigationObstacle obstacle)
    {
        var placementObstacles = PlaceableWorldObjectRules.CollisionObstacles(
            definition, center, rotation);
        return placementObstacles.Any(candidate =>
            PlaceableWorldObjectRules.Overlaps(
                candidate, obstacle, .18f));
    }

    private bool IsClearConstructionFootprint(
        PlaceConstructionIntent placement,
        PlaceableWorldObjectDefinition definition)
    {
        var placementObstacles = PlaceableWorldObjectRules.CollisionObstacles(
            definition,
            placement.Position,
            placement.Rotation);
        if (placementObstacles.Count == 0) return false;

        var collisionBounds = PlaceableWorldObjectRules.CollisionBounds(
            placementObstacles);
        var placementFootprint =
            PlaceableWorldObjectRules.PlacementFootprint(
                definition, placement.Rotation);
        if (!PlaceableWorldObjectRules.IsSupportedTerrain(
                definition,
                placement.Position,
                placement.Rotation,
                placement.WorldLevel,
                _navigation) ||
            !_navigation.CanStandAt(
                placement.Position, placement.WorldLevel) ||
            _resourceTransactions?.HasBlockingResourceInFootprint(
                placement.Position,
                placement.WorldLevel,
                placementFootprint) == true)
        {
            return false;
        }

        var existingObstacles = _obstacles.GetObstacles(
            placement.WorldLevel,
            collisionBounds.Minimum - new Vector2(.18f),
            collisionBounds.Maximum + new Vector2(.18f));
        return !placementObstacles.Any(candidate =>
            existingObstacles.Any(existing =>
                PlaceableWorldObjectRules.Overlaps(
                    candidate, existing, .18f)));
    }

    private static IntentResult ProcessStop(MutableActor actor)
    {
        if (actor.Gameplay.LifeState == ActorLifeState.Dead ||
            actor.Gameplay.Health <= 0)
            return Rejected(IntentStatus.DeadActor, actor,
                "A dead actor cannot move.");
        CancelCombatForMovement(actor);
        actor.ClearRoute();
        return Accepted(actor);
    }

    private static void CancelCombatForMovement(MutableActor actor)
    {
        if (actor.Gameplay.CombatTargetEnemyId is null &&
            actor.Gameplay.NextCombatAttackTick == 0)
            return;
        actor.Gameplay.CombatTargetEnemyId = null;
        actor.Gameplay.NextCombatAttackTick = 0;
        actor.Gameplay.ActorRevision = checked(
            actor.Gameplay.ActorRevision + 1);
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
            if (actor.Gameplay.LifeState == ActorLifeState.Dead ||
                actor.Gameplay.Health <= 0)
            {
                actor.ClearRoute();
                continue;
            }
            if (_boatTransactions?.FindByOccupant(
                    actor.Identity.ActorId) is not null)
            {
                actor.ClearRoute();
                continue;
            }
            if (!actor.Connected || actor.CurrentWaypoint is not { } destination)
            {
                actor.Velocity = Vector2.Zero;
                continue;
            }

            var combatMovementMultiplier =
                actor.Gameplay.CombatStatus.MovementMultiplier(
                    Clock.Current.ElapsedSeconds);
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
                var speed = _limits.ActorMovementSpeed * terrainMultiplier *
                            combatMovementMultiplier;
                if (combatMovementMultiplier == 0)
                {
                    // Rooting pauses an accepted server route. The route must
                    // remain installed so movement resumes on the exact fixed
                    // step at which the absolute deadline expires.
                    actor.Velocity = Vector2.Zero;
                    break;
                }
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

    private void SynchronizeBoatOccupants()
    {
        if (_boatTransactions is null) return;
        foreach (var actor in _actors.Values)
        {
            var boat = _boatTransactions.FindByOccupant(
                actor.Identity.ActorId);
            if (boat is null) continue;
            actor.Position = boat.Position;
            actor.WorldLevel = boat.WorldLevel;
            actor.Velocity = boat.Velocity;
            actor.Destination = boat.Destination;
        }
    }

    private void AdvanceCombat()
    {
        if (_combatTransactions is null) return;
        // A restored or legacy checkpoint may predate the atomic boarding
        // cancellation rule. Canonicalize any retained target before combat
        // receives transforms, guaranteeing that it cannot chase an occupant
        // away from the position synchronized from the boat above.
        if (_boatTransactions is not null)
            foreach (var actor in _actors.Values)
                if (_boatTransactions.FindByOccupant(
                        actor.Identity.ActorId) is not null)
                    CancelCombatForMovement(actor);
        var actors = _actors.Values
            .OrderBy(static value => value.Identity.ActorId.Value)
            .Select(CombatInput)
            .ToArray();
        var update = _combatTransactions.Advance(
            SimulationTiming.FixedDeltaSeconds, Clock.Tick, actors);
        foreach (var mutation in update.ActorMutations)
        {
            if (!_actors.TryGetValue(mutation.ActorId, out var actor))
                throw new InvalidOperationException(
                    "Combat mutated an actor outside the session registry.");
            var died = actor.Gameplay.LifeState != ActorLifeState.Dead &&
                       mutation.Gameplay.LifeState == ActorLifeState.Dead;
            var gameplay = mutation.Gameplay;
            if (gameplay.Health < actor.Gameplay.Health ||
                gameplay.LifeState == ActorLifeState.Dead)
            {
                gameplay = gameplay with
                {
                    TimedHealingRemainingHealth = 0,
                    TimedHealingRemainingSeconds = 0,
                    TimedHealingFractionalHealth = 0
                };
            }
            actor.Gameplay.ReplaceWith(gameplay);
            if (mutation.Position is { } position) actor.Position = position;
            if (mutation.WorldLevel is { } level) actor.WorldLevel = level;
            if (mutation.ClearMovement) actor.ClearRoute();
            if (died && _boatTransactions?.DetachOccupant(
                    actor.Identity.ActorId) is { } detachedBoat)
            {
                BoatStateCommitted?.Invoke(detachedBoat);
                BoatAutonomousStateCommitted?.Invoke(detachedBoat);
            }
        }
        foreach (var drop in update.LootDrops)
        {
            if (drop.Items.IsDefaultOrEmpty) continue;
            var owner = drop.OwnerActorId?.ToString();
            var transaction = _worldTransactions.AddObjectCommitted(
                drop.ObjectId,
                new WorldObjectSeed(
                    drop.ObjectId,
                    ItemIds.LootBag,
                    drop.Position,
                    drop.WorldLevel,
                    OwnerId: owner,
                    ContainerItems: drop.Items.Select(value =>
                        (value.ItemId, value.Quantity,
                            (string?)null)).ToArray()));
            if (!transaction.Accepted)
                throw new InvalidOperationException(
                    "A validated deterministic combat loot bag failed to commit.");
            WorldTransactionCommitted?.Invoke(transaction);
        }
        foreach (var delta in update.EnemyDeltas)
            EnemyStateCommitted?.Invoke(delta);
        foreach (var combatEvent in update.Events)
            CombatEventCommitted?.Invoke(combatEvent);
    }

    private void AdvanceSurvival()
    {
        // Survival is semantically observable at a one-second cadence. This
        // keeps private gameplay revisions stable between meaningful updates
        // while preserving fixed-authority elapsed time.
        if ((Clock.Tick + 1) % SimulationTiming.TicksPerSecond != 0) return;
        foreach (var actor in _actors.Values)
        {
            if (!actor.Connected || actor.Gameplay.LifeState != ActorLifeState.Alive ||
                actor.Gameplay.Health <= 0)
                continue;
            var gameplay = actor.Gameplay;
            var survival = SurvivalService.Advance(
                gameplay.Hunger, gameplay.WellFedSeconds, gameplay.Health,
                elapsed: 1,
                starvationDamageRemainder: gameplay.StarvationDamageRemainder);
            var activeHealing = survival.Health < gameplay.Health
                ? default
                : gameplay.TimedHealing;
            var regeneration = EntityHealthRegenerationService.Advance(
                survival.Health, gameplay.MaximumHealth,
                elapsedRealSeconds: 1,
                multiplier: _worldTransactions.HasLitCampfireWithin(
                    actor.Position,
                    actor.WorldLevel,
                    AuthoritativeWorldTime.FromElapsedRealSeconds(
                        Clock.Current.ElapsedSeconds),
                    EntityHealthRegenerationService.LitCampfireRange)
                    ? EntityHealthRegenerationService
                        .LitCampfireHumanMultiplier
                    : 1,
                remainder: gameplay.HealthRegenerationRemainder);
            var healing = TimedHealingService.Advance(
                regeneration.Health,
                gameplay.MaximumHealth,
                elapsed: 1,
                activeHealing);
            if (survival.Hunger == gameplay.Hunger &&
                survival.WellFedSeconds == gameplay.WellFedSeconds &&
                healing.Health == gameplay.Health &&
                survival.StarvationDamageRemainder == gameplay.StarvationDamageRemainder &&
                regeneration.Remainder == gameplay.HealthRegenerationRemainder &&
                healing.State == gameplay.TimedHealing)
                continue;
            var next = gameplay.ToSnapshot() with
            {
                Hunger = survival.Hunger,
                WellFedSeconds = survival.WellFedSeconds,
                Health = healing.Health,
                StarvationDamageRemainder =
                    survival.StarvationDamageRemainder,
                HealthRegenerationRemainder = regeneration.Remainder,
                TimedHealingRemainingHealth = healing.State.RemainingHealth,
                TimedHealingRemainingSeconds = healing.State.RemainingSeconds,
                TimedHealingFractionalHealth = healing.State.FractionalHealth
            };
            if (next.Health <= 0)
            {
                if (_combatTransactions is not null)
                {
                    var mutation = _combatTransactions.ApplyEnvironmentalDeath(
                        CombatInput(actor), next, Clock.Tick,
                        out var combatEvent);
                    if (mutation is { } value)
                    {
                        actor.Gameplay.ReplaceWith(value.Gameplay);
                        if (value.ClearMovement) actor.ClearRoute();
                        if (_boatTransactions?.DetachOccupant(
                                actor.Identity.ActorId) is { } detachedBoat)
                        {
                            BoatStateCommitted?.Invoke(detachedBoat);
                            BoatAutonomousStateCommitted?.Invoke(detachedBoat);
                        }
                        if (combatEvent is { } emitted)
                            CombatEventCommitted?.Invoke(emitted);
                        continue;
                    }
                }
                next = AuthoritativeCombatTransactions.DeathGameplay(
                    next,
                    Clock.Tick,
                    AuthoritativeCombatTransactions.DefaultRespawnDelayTicks);
                gameplay.ReplaceWith(next);
                actor.ClearRoute();
                if (_boatTransactions?.DetachOccupant(
                        actor.Identity.ActorId) is { } fallbackDetachedBoat)
                {
                    BoatStateCommitted?.Invoke(fallbackDetachedBoat);
                    BoatAutonomousStateCommitted?.Invoke(fallbackDetachedBoat);
                }
                continue;
            }
            next = next with
            {
                ActorRevision = checked(next.ActorRevision + 1)
            };
            gameplay.ReplaceWith(next);
        }
    }

    private CombatActorInput CombatInput(MutableActor actor) => new(
        actor.Identity.ActorId,
        ActorNetworkEntityIdentity.Derive(actor.Identity.ActorId),
        actor.Position,
        actor.WorldLevel,
        actor.Connected,
        actor.Gameplay.ToSnapshot());

    private Dictionary<ActorId, ulong> ActorNetworkIds() =>
        _actors.Keys.ToDictionary(static value => value,
            static value => ActorNetworkEntityIdentity.Derive(value));

    private SessionSnapshot CaptureSnapshotCore(long sequence)
    {
        var actors = _actors.Values
            .OrderBy(static actor => actor.Identity.ActorId.Value)
            .Select(actor => actor.ToSnapshot() with
            {
                BoardedBoatId = _boatTransactions?.FindByOccupant(
                    actor.Identity.ActorId)?.BoatId
            })
            .ToImmutableArray();
        return new SessionSnapshot(
            Id,
            sequence,
            Clock.Current,
            actors,
            _chatHistory.ToImmutableArray(),
            _boatTransactions?.CaptureBoats() ?? [],
            _combatTransactions?.CaptureEnemies(
                ActorNetworkIds(), Clock.Current.ElapsedSeconds) ?? [],
            []);
    }

    private PlayerIdentity CreateUniqueIdentity()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var identity = _identitySource.CreatePlayerIdentity();
            if (identity.PlayerId.Value != Guid.Empty &&
                identity.ActorId.Value != Guid.Empty &&
                !_actorsByPlayer.ContainsKey(identity.PlayerId) &&
                !_expiredPlayers.Contains(identity.PlayerId) &&
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

    private sealed record ProvisionPlayerBoatOperation(
        PlayerId PlayerId,
        string? GroupId,
        TaskCompletionSource<AuthoritativeBoatSnapshot> Completion) :
        QueuedOperation;

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

        public int WorldLevel { get; set; }

        public ClientConnectionId ConnectionId { get; set; }

        public byte[] ReconnectTokenHash { get; }

        public bool Connected { get; set; }

        public long LastProcessedCommandSequence { get; set; }

        public long? DisconnectedAtTick { get; set; }

        /// <summary>
        /// Monotonic in-memory recency used only for bounded offline retention.
        /// Checkpoint restore reconstructs a deterministic order from the
        /// durable disconnect tick and player identity.
        /// </summary>
        public long RetentionOrdinal { get; set; }

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
                    // Aggregate-level duplicates deliberately carry no stale
                    // transaction effects. Keep their reinserted session
                    // receipt as a current-state tombstone too, so later
                    // retries cannot report revisions from this retry point.
                    Restored: result.Duplicate));
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
        public const int BaseMaximumHealth = 100;

        public MutablePlayerGameplay()
        {
            Inventory = PlayerInventory.CreateContainer();
            Quests = QuestService.Normalize(null);
        }

        public InventoryContainer Inventory { get; set; }

        public uint InventoryRevision { get; set; } = 1;

        public uint ActorRevision { get; set; } = 1;

        public int Health { get; set; } = BaseMaximumHealth;

        public float Hunger { get; set; } = SurvivalService.MaximumHunger;

        public float WellFedSeconds { get; set; }

        public float StarvationDamageRemainder { get; set; }

        public float HealthRegenerationRemainder { get; set; }

        public TimedHealingState TimedHealing { get; set; }

        public int CraftingExperience { get; set; }

        public int CookingExperience { get; set; }

        public int WoodcuttingExperience { get; set; }

        public int FarmingExperience { get; set; }

        public int MiningExperience { get; set; }

        public int AdventureExperience { get; set; }

        public int DiggingExperience { get; set; }

        public int FishingExperience { get; set; }

        public int MaximumHealth { get; set; } = BaseMaximumHealth;

        public int AttackExperience { get; set; }

        public int StrengthExperience { get; set; }

        public int DefenceExperience { get; set; }

        public MeleeCombatStance CombatStance { get; set; } =
            MeleeCombatStance.Accurate;

        public ActorLifeState LifeState { get; set; } = ActorLifeState.Alive;

        public long RespawnAvailableTick { get; set; }

        public SlimeVictimStatus CombatStatus { get; set; }

        public EnemyId? CombatTargetEnemyId { get; set; }

        public ulong CombatAttackSequence { get; set; }

        public long NextCombatAttackTick { get; set; }

        public ImmutableArray<QuestProgress> Quests { get; set; }

        public void ReplaceWith(PlayerGameplaySnapshot snapshot)
        {
            ImmutableArray<QuestProgress> quests;
            try
            {
                if (snapshot.Quests.IsDefault)
                {
                    quests = QuestService.Normalize(null);
                }
                else
                {
                    QuestService.Validate(snapshot.Quests);
                    quests = snapshot.Quests;
                }
            }
            catch (Exception error) when (error is InvalidDataException or
                                          ArgumentException)
            {
                throw new InvalidOperationException(
                    "The world transaction returned invalid quest state.",
                    error);
            }

            if (snapshot.ActorRevision is 0 or uint.MaxValue ||
                snapshot.Inventory.Revision == 0 ||
                snapshot.Inventory.Capacity != PlayerInventory.Capacity ||
                snapshot.Health < 0 ||
                !float.IsFinite(snapshot.Hunger) ||
                snapshot.Hunger is < 0 or > SurvivalService.MaximumHunger ||
                !float.IsFinite(snapshot.WellFedSeconds) ||
                snapshot.WellFedSeconds < 0 ||
                !float.IsFinite(snapshot.StarvationDamageRemainder) ||
                snapshot.StarvationDamageRemainder is < 0 or >= 1 ||
                !float.IsFinite(snapshot.HealthRegenerationRemainder) ||
                snapshot.HealthRegenerationRemainder is < 0 or >= 1 ||
                !TimedHealingService.IsCanonical(new TimedHealingState(
                    snapshot.TimedHealingRemainingHealth,
                    snapshot.TimedHealingRemainingSeconds,
                    snapshot.TimedHealingFractionalHealth)) ||
                (snapshot.TimedHealingRemainingHealth > 0 &&
                 (snapshot.Health <= 0 ||
                  snapshot.Health >= snapshot.MaximumHealth ||
                  snapshot.LifeState != ActorLifeState.Alive)) ||
                snapshot.CraftingExperience < 0 ||
                snapshot.CookingExperience < 0 ||
                snapshot.WoodcuttingExperience < 0 ||
                snapshot.FarmingExperience < 0 ||
                snapshot.MiningExperience < 0 ||
                snapshot.AdventureExperience < 0 ||
                snapshot.AdventureExperience > AdventureService
                    .ExperienceForLevel(AdventureService.MaximumLevel) ||
                snapshot.DiggingExperience < 0 ||
                snapshot.FishingExperience < 0 ||
                snapshot.MaximumHealth <= 0 ||
                snapshot.MaximumHealth != AdventureService.MaximumHealth(
                    snapshot.AdventureExperience) ||
                snapshot.Health > snapshot.MaximumHealth ||
                snapshot.AttackExperience < 0 ||
                snapshot.StrengthExperience < 0 ||
                snapshot.DefenceExperience < 0 ||
                !Enum.IsDefined(snapshot.CombatStance) ||
                !Enum.IsDefined(snapshot.LifeState) ||
                snapshot.RespawnAvailableTick < 0 ||
                !ValidCombatStatus(snapshot.CombatStatus) ||
                snapshot.CombatTargetEnemyId is { IsEmpty: true } ||
                (snapshot.LifeState == ActorLifeState.Dead &&
                 (snapshot.Health != 0 || snapshot.RespawnAvailableTick == 0)) ||
                (snapshot.LifeState == ActorLifeState.Alive &&
                 (snapshot.Health <= 0 || snapshot.RespawnAvailableTick != 0)))
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
            StarvationDamageRemainder = snapshot.StarvationDamageRemainder;
            HealthRegenerationRemainder = snapshot.HealthRegenerationRemainder;
            TimedHealing = new TimedHealingState(
                snapshot.TimedHealingRemainingHealth,
                snapshot.TimedHealingRemainingSeconds,
                snapshot.TimedHealingFractionalHealth);
            CraftingExperience = snapshot.CraftingExperience;
            CookingExperience = snapshot.CookingExperience;
            WoodcuttingExperience = snapshot.WoodcuttingExperience;
            FarmingExperience = snapshot.FarmingExperience;
            MiningExperience = snapshot.MiningExperience;
            AdventureExperience = snapshot.AdventureExperience;
            DiggingExperience = snapshot.DiggingExperience;
            FishingExperience = snapshot.FishingExperience;
            MaximumHealth = snapshot.MaximumHealth;
            AttackExperience = snapshot.AttackExperience;
            StrengthExperience = snapshot.StrengthExperience;
            DefenceExperience = snapshot.DefenceExperience;
            CombatStance = snapshot.CombatStance;
            LifeState = snapshot.LifeState;
            RespawnAvailableTick = snapshot.RespawnAvailableTick;
            CombatStatus = snapshot.CombatStatus;
            CombatTargetEnemyId = snapshot.CombatTargetEnemyId;
            CombatAttackSequence = snapshot.CombatAttackSequence;
            NextCombatAttackTick = snapshot.NextCombatAttackTick;
            Quests = quests;
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
                    slots.MoveToImmutable()),
                WoodcuttingExperience,
                FarmingExperience,
                MiningExperience,
                AdventureExperience,
                DiggingExperience,
                FishingExperience,
                MaximumHealth,
                AttackExperience,
                StrengthExperience,
                DefenceExperience,
                CombatStance,
                LifeState,
                RespawnAvailableTick,
                CombatStatus,
                CombatTargetEnemyId,
                CombatAttackSequence,
                NextCombatAttackTick,
                StarvationDamageRemainder,
                HealthRegenerationRemainder,
                Quests,
                TimedHealing.RemainingHealth,
                TimedHealing.RemainingSeconds,
                TimedHealing.FractionalHealth);
        }

        private static bool ValidCombatStatus(SlimeVictimStatus value) =>
            double.IsFinite(value.SlowedUntil) && value.SlowedUntil >= 0 &&
            double.IsFinite(value.RootedUntil) && value.RootedUntil >= 0 &&
            double.IsFinite(value.PoisonedUntil) && value.PoisonedUntil >= 0 &&
            double.IsFinite(value.NextPoisonTickAt) &&
            value.NextPoisonTickAt >= 0 && value.PoisonDamage >= 0;
    }

    private sealed record CommandReceipt(
        string PayloadFingerprint,
        IntentResult Result,
        bool Restored);

    private sealed class CompositeWorldNavigationObstacleSource(
        IWorldNavigationObstacleSource first,
        IWorldNavigationObstacleSource second) :
        IWorldNavigationObstacleSource
    {
        public IReadOnlyList<NavigationObstacle> GetObstacles(int worldLevel)
        {
            var firstValues = first.GetObstacles(worldLevel);
            var secondValues = second.GetObstacles(worldLevel);
            if (firstValues.Count == 0) return secondValues;
            if (secondValues.Count == 0) return firstValues;
            var combined = new NavigationObstacle[
                firstValues.Count + secondValues.Count];
            for (var index = 0; index < firstValues.Count; index++)
                combined[index] = firstValues[index];
            for (var index = 0; index < secondValues.Count; index++)
                combined[firstValues.Count + index] = secondValues[index];
            return combined;
        }

        public IReadOnlyList<NavigationObstacle> GetObstacles(
            int worldLevel,
            Vector2 minimum,
            Vector2 maximum)
        {
            var firstValues = first.GetObstacles(
                worldLevel, minimum, maximum);
            var secondValues = second.GetObstacles(
                worldLevel, minimum, maximum);
            if (firstValues.Count == 0) return secondValues;
            if (secondValues.Count == 0) return firstValues;
            var combined = new NavigationObstacle[
                firstValues.Count + secondValues.Count];
            for (var index = 0; index < firstValues.Count; index++)
                combined[index] = firstValues[index];
            for (var index = 0; index < secondValues.Count; index++)
                combined[firstValues.Count + index] = secondValues[index];
            return combined;
        }
    }
}
