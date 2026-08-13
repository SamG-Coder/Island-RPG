using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using IslandRpg.Gameplay;

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
    private readonly Channel<QueuedOperation> _inbound;
    private readonly Dictionary<ActorId, MutableActor> _actors = [];
    private readonly Dictionary<PlayerId, ActorId> _actorsByPlayer = [];
    private readonly Dictionary<ClientConnectionId, PlayerId> _playersByConnection = [];
    private readonly Queue<ChatMessageSnapshot> _chatHistory = [];
    private SessionSnapshot _latestSnapshot;
    private int? _ownerThreadId;
    private int _executing;
    private long _nextChatMessageId;

    public AuthoritativeWorldSession(
        SimulationLimits? limits = null,
        ISessionIdentitySource? identitySource = null,
        SessionId? sessionId = null)
    {
        _limits = (limits ?? SimulationLimits.Default).ValidatedCopy();
        _identitySource = identitySource ?? new SecureSessionIdentitySource();
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
            if (!Clock.AdvanceOneTick())
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
            request.InitialHunger is < 0 or > SurvivalService.MaximumHunger)
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
        actor.Destination = null;
        actor.Velocity = Vector2.Zero;
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

            if (!Equals(receipt.Intent, replayed))
            {
                return Rejected(
                    IntentStatus.CommandIdConflict,
                    actor,
                    "The command identifier is already bound to a different gameplay request.",
                    replayed.CommandId);
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

        actor.Destination = destination;
        return Accepted(actor);
    }

    private static IntentResult ProcessStop(MutableActor actor)
    {
        actor.Destination = null;
        actor.Velocity = Vector2.Zero;
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
        var maximumStep = _limits.ActorMovementSpeed / SimulationTiming.TicksPerSecond;
        var arrivalDistanceSquared =
            _limits.DestinationArrivalDistance * _limits.DestinationArrivalDistance;

        foreach (var actor in _actors.Values)
        {
            if (!actor.Connected || actor.Destination is not { } destination)
            {
                actor.Velocity = Vector2.Zero;
                continue;
            }

            var difference = destination - actor.Position;
            var distanceSquared = difference.LengthSquared();
            if (!float.IsFinite(distanceSquared) || distanceSquared <= arrivalDistanceSquared)
            {
                actor.Position = destination;
                actor.Destination = null;
                actor.Velocity = Vector2.Zero;
                continue;
            }

            var distance = MathF.Sqrt(distanceSquared);
            var step = MathF.Min(maximumStep, distance);
            var direction = difference / distance;
            actor.Velocity = direction * _limits.ActorMovementSpeed;
            actor.Position += direction * step;
            actor.Position = ClampToWorld(actor.Position);

            if (step >= distance ||
                Vector2.DistanceSquared(actor.Position, destination) <= arrivalDistanceSquared)
            {
                actor.Position = destination;
                actor.Destination = null;
                actor.Velocity = Vector2.Zero;
            }
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

    private sealed class MutableActor
    {
        private readonly Dictionary<Guid, CommandReceipt> _receipts = [];
        private readonly Queue<Guid> _receiptOrder = [];

        public MutableActor(
            PlayerIdentity identity,
            string displayName,
            Vector2 position,
            ClientConnectionId connectionId,
            byte[] reconnectTokenHash)
        {
            Identity = identity;
            DisplayName = displayName;
            Position = position;
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

        public ClientConnectionId ConnectionId { get; set; }

        public byte[] ReconnectTokenHash { get; }

        public bool Connected { get; set; }

        public long LastProcessedCommandSequence { get; set; }

        public long? DisconnectedAtTick { get; set; }

        public MutablePlayerGameplay Gameplay { get; }

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
                new CommandReceipt(intent, result));
            _receiptOrder.Enqueue(intent.CommandId);
        }

        public ActorSnapshot ToSnapshot() => new(
                Identity.ActorId,
                Identity.PlayerId,
                DisplayName,
                Position,
                Velocity,
                Destination,
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
        GameplayIntent Intent,
        IntentResult Result);
}
