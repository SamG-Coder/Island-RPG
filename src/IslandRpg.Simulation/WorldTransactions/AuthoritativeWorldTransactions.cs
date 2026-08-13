using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.World;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner aggregate for atomic authoritative world transactions. The
/// simulation thread is the only writer; callers receive immutable snapshots
/// and deltas, never references to mutable inventories or world collections.
/// </summary>
public sealed class AuthoritativeWorldTransactions
{
    public const float InteractionRange = 3f;
    private const int MaximumRememberedCommands = 4096;

    private int? _ownerThreadId;
    private readonly Dictionary<Guid, ObjectState> _objects = [];
    private readonly Dictionary<WorldChunkKey, uint> _chunkRevisions = [];
    private readonly Dictionary<(ActorId ActorId, Guid CommandId),
        CommandReceipt> _commandResults = [];
    private readonly Queue<(ActorId ActorId, Guid CommandId)> _commandOrder = [];
    private readonly Func<Guid> _newObjectId;

    public AuthoritativeWorldTransactions(Func<Guid>? newObjectId = null)
    {
        _newObjectId = newObjectId ?? Guid.NewGuid;
    }

    public AuthoritativeWorldObjectSnapshot AddObject(WorldObjectSeed seed)
    {
        EnsureOwner();
        if (seed.ObjectId == Guid.Empty || !IsFinite(seed.Position) ||
            seed.ObjectRevision == 0 || seed.ContainerRevision == 0 ||
            string.IsNullOrWhiteSpace(seed.DefinitionId) ||
            !ValidGateState(seed.DefinitionId, seed.GateState))
            throw new ArgumentException("The world-object seed is invalid.", nameof(seed));
        var value = new WorldGroundObject(
            seed.ObjectId,
            seed.DefinitionId,
            seed.Position.X,
            seed.Position.Y,
            seed.FuelItemId,
            seed.LitUntilGameSeconds,
            seed.FiremakingLevel,
            seed.Health,
            seed.MaximumHealth,
            OwnerId: seed.OwnerId,
            GroupOwnerId: seed.GroupOwnerId,
            VisualFrame: seed.Rotation,
            GateState: ToCoreGateState(seed.GateState));
        if (seed.ContainerItems is { Count: > 0 })
        {
            if (!StorageContainerService.IsStorage(seed.DefinitionId))
                throw new ArgumentException(
                    "Only storage objects can be seeded with contents.",
                    nameof(seed));
            var container = StorageContainerService.Open(value);
            foreach (var item in seed.ContainerItems)
                if (item.Quantity <= 0 ||
                    !container.TryAdd(item.ItemId, item.Quantity, item.OwnerId))
                    throw new ArgumentException(
                        "The seeded container contents are invalid or too large.",
                        nameof(seed));
            value = StorageContainerService.Save(value, container);
        }
        var chunk = WorldChunkKey.At(seed.Position, seed.WorldLevel);
        if (!_objects.TryAdd(seed.ObjectId,
                new ObjectState(value, chunk, seed.ObjectRevision,
                    seed.ContainerRevision)))
            throw new InvalidOperationException("The world object already exists.");
        AdvanceChunk(chunk);
        return Snapshot(_objects[seed.ObjectId]);
    }

    public AuthoritativeWorldObjectSnapshot CaptureObject(Guid objectId)
    {
        EnsureOwner();
        if (!_objects.TryGetValue(objectId, out var value))
            throw new KeyNotFoundException("The world object does not exist.");
        return Snapshot(value);
    }

    public uint CaptureChunkRevision(WorldChunkKey chunk)
    {
        EnsureOwner();
        return ChunkRevision(chunk);
    }

    public AuthoritativeWorldTransactionsCheckpoint CaptureCheckpoint()
    {
        EnsureOwner();
        var objects = _objects.Values
            .OrderBy(static value => value.Value.Id)
            .Select(value => new AuthoritativeWorldObjectCheckpoint(
                Snapshot(value),
                StorageContainerService.IsStorage(value.Value.ItemId)
                    ? ContainerSnapshot(value)
                    : null))
            .ToImmutableArray();
        var chunks = _chunkRevisions
            .OrderBy(static value => value.Key.WorldLevel)
            .ThenBy(static value => value.Key.X)
            .ThenBy(static value => value.Key.Y)
            .Select(static value => new AuthoritativeChunkRevisionSnapshot(
                value.Key, value.Value))
            .ToImmutableArray();
        return new(objects, chunks);
    }

    /// <summary>
    /// Replaces an empty aggregate with trusted persisted state. Unlike
    /// AddObject, this path never increments revisions while restoring them.
    /// Validation is completed into temporary collections before committing.
    /// </summary>
    public void RestoreCheckpoint(
        AuthoritativeWorldTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Objects.IsDefault ||
            checkpoint.ChunkRevisions.IsDefault)
        {
            throw new InvalidDataException(
                "The world checkpoint is incomplete.");
        }
        if (_objects.Count != 0 || _chunkRevisions.Count != 0 ||
            _commandResults.Count != 0)
        {
            throw new InvalidOperationException(
                "World state can only be restored into an empty aggregate.");
        }

        var chunks = new Dictionary<WorldChunkKey, uint>();
        foreach (var entry in checkpoint.ChunkRevisions)
        {
            if (entry.Revision == 0 ||
                !chunks.TryAdd(entry.Chunk, entry.Revision))
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid chunk revision.");
            }
        }

        var objects = new Dictionary<Guid, ObjectState>();
        foreach (var entry in checkpoint.Objects)
        {
            var snapshot = entry.Object;
            if (snapshot.ObjectId == Guid.Empty ||
                string.IsNullOrWhiteSpace(snapshot.DefinitionId) ||
                !IsFinite(snapshot.Position) ||
                snapshot.ObjectRevision == 0 ||
                snapshot.ContainerRevision == 0 ||
                snapshot.Health < 0 || snapshot.MaximumHealth < 0 ||
                snapshot.MaximumHealth > 0 &&
                snapshot.Health > snapshot.MaximumHealth ||
                !double.IsFinite(snapshot.LitUntilGameSeconds) ||
                snapshot.LitUntilGameSeconds < 0 ||
                snapshot.Chunk != WorldChunkKey.At(
                    snapshot.Position, snapshot.Chunk.WorldLevel) ||
                !chunks.ContainsKey(snapshot.Chunk) ||
                snapshot.FiremakingLevel is < 1 or > 20 ||
                !ValidGateState(snapshot.DefinitionId, snapshot.GateState) ||
                objects.ContainsKey(snapshot.ObjectId))
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid object.");
            }

            WorldContainerContents? contents = null;
            var isStorage = StorageContainerService.IsStorage(
                snapshot.DefinitionId);
            if (isStorage != snapshot.HasContainer ||
                isStorage != (entry.Container is not null))
            {
                throw new InvalidDataException(
                    "The world checkpoint container metadata is inconsistent.");
            }

            if (entry.Container is { } container)
            {
                contents = RestoreContainer(snapshot, container, chunks);
            }

            var value = new WorldGroundObject(
                snapshot.ObjectId,
                snapshot.DefinitionId,
                snapshot.Position.X,
                snapshot.Position.Y,
                snapshot.FuelItemId,
                snapshot.LitUntilGameSeconds,
                snapshot.FiremakingLevel,
                snapshot.Health,
                snapshot.MaximumHealth,
                contents,
                snapshot.OwnerId,
                snapshot.GroupOwnerId,
                snapshot.Rotation,
                ToCoreGateState(snapshot.GateState));
            objects.Add(snapshot.ObjectId, new ObjectState(
                value,
                snapshot.Chunk,
                snapshot.ObjectRevision,
                snapshot.ContainerRevision));
        }

        foreach (var value in chunks) _chunkRevisions.Add(value.Key, value.Value);
        foreach (var value in objects) _objects.Add(value.Key, value.Value);
    }

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        PickUpWorldObjectTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => PickUp(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        DropInventoryItemTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Drop(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        OpenWorldContainerTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => OpenContainer(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TransferWorldContainerTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TransferContainer(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        AddCampfireFuelTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => AddFuel(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        TakeCampfireFuelTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => TakeFuel(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        LightCampfireTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Light(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        BeginCampfireCookingTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => BeginCooking(state, command));

    /// <summary>
    /// Completes a previously persisted server-owned cooking job. Completion
    /// is driven only by the session clock, never by a second client command.
    /// </summary>
    public WorldTransactionResult CompleteCooking(
        WorldTransactionActorInput input,
        CompleteCampfireCookingTransaction command)
    {
        EnsureOwner();
        var context = new WorldTransactionContext(
            command.OperationId,
            input.ActorId,
            input.Gameplay.ActorRevision,
            input.Gameplay.Inventory.Revision);
        var actor = CreateActor(input);
        if (actor is null)
            return Rejected(context, WorldTransactionStatus.InvalidCommand);
        // Completion is cleanup for an item reserved by an earlier accepted
        // command. A dead actor may not cook or earn XP, but the authority
        // must still return/drop that reserved item.
        var fireStillLit =
            actor.Health > 0 &&
            _objects.TryGetValue(command.CampfireId, out var fire) &&
            fire.Chunk == command.CampfireChunk &&
            CampfireService.IsCampfire(fire.Value) &&
            CampfireService.State(
                fire.Value, command.GameSeconds) == CampfireState.Lit;
        var output = fireStillLit ? command.ResultItemId : command.RawItemId;
        if (!ItemCatalog.TryGet(output, out _))
            return Rejected(context, WorldTransactionStatus.InvalidItem, actor);

        var inventory = actor.Inventory.Clone();
        var objectDeltas = ImmutableArray<WorldObjectTransactionDelta>.Empty;
        var chunkDeltas = ImmutableArray<WorldChunkRevisionDelta>.Empty;
        if (!inventory.TryAddAtPreferredSlot(
                output, command.PreferredInventorySlot))
        {
            if (command.DropObjectId == Guid.Empty ||
                _objects.ContainsKey(command.DropObjectId))
                return Rejected(context, WorldTransactionStatus.InvalidCommand,
                    actor, "The cooking drop identity is invalid.");
            var dropPosition = command.CampfirePosition + new Vector2(.38f, 0);
            var chunk = WorldChunkKey.At(
                dropPosition, command.CampfireChunk.WorldLevel);
            var drop = new ObjectState(new WorldGroundObject(
                command.DropObjectId,
                output,
                dropPosition.X,
                dropPosition.Y,
                OwnerId: actor.ActorId.ToString()), chunk, 1, 1);
            _objects.Add(command.DropObjectId, drop);
            var chunkDelta = AdvanceChunk(chunk);
            objectDeltas =
            [
                new(WorldObjectChangeKind.Added,
                    command.DropObjectId, chunk, 0, 1, Snapshot(drop))
            ];
            chunkDeltas = [chunkDelta];
        }
        else
            CommitInventory(actor, inventory);

        if (fireStillLit && command.Experience > 0)
        {
            actor.CookingExperience = CookingSkill.AwardExperience(
                actor.CookingExperience, command.Experience).Experience;
            AdvanceActor(actor);
        }
        return Accepted(context, actor, objectDeltas, chunkDeltas) with
        {
            Detail = fireStillLit
                ? "cooking_completed"
                : "cooking_interrupted"
        };
    }

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        PlaceConstructionTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => PlaceConstruction(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        BuildConstructionTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => BuildConstruction(state, command));

    public WorldTransactionResult Execute(
        WorldTransactionActorInput actor,
        DemolishWorldObjectTransaction command) =>
        ExecuteCached(actor, command.Context, command,
            state => Demolish(state, command));

    private WorldTransactionResult ExecuteCached(
        WorldTransactionActorInput input,
        WorldTransactionContext context,
        object command,
        Func<ActorState, WorldTransactionResult> operation)
    {
        EnsureOwner();
        if (context.CommandId == Guid.Empty || context.ActorId.Value == Guid.Empty ||
            input.ActorId != context.ActorId || !IsFinite(input.Position))
            return Rejected(context, WorldTransactionStatus.InvalidCommand);
        var key = (context.ActorId, context.CommandId);
        if (_commandResults.TryGetValue(key, out var prior))
            return Equals(prior.Command, command)
                ? prior.Result
                : Rejected(context, WorldTransactionStatus.CommandIdConflict);
        var actor = CreateActor(input);
        WorldTransactionResult result;
        if (actor is null)
            result = Rejected(context, WorldTransactionStatus.InvalidCommand);
        else if (actor.Health <= 0)
            result = Rejected(context, WorldTransactionStatus.DeadActor, actor);
        else if (context.ExpectedActorRevision != actor.ActorRevision)
            result = Rejected(
                context, WorldTransactionStatus.StaleActorRevision, actor);
        else if (context.ExpectedInventoryRevision != actor.InventoryRevision)
            result = Rejected(
                context, WorldTransactionStatus.StaleInventoryRevision, actor);
        else
            result = operation(actor);
        Remember(key, command, result);
        return result;
    }

    private WorldTransactionResult PickUp(
        ActorState actor, PickUpWorldObjectTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Object, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!IsPortable(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotPortable, actor);
        if (!ItemCatalog.TryGet(state.Value.ItemId, out _))
            return Rejected(command.Context, WorldTransactionStatus.InvalidItem, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(state.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);

        var oldObjectRevision = state.ObjectRevision;
        _objects.Remove(state.Value.Id);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Removed, state.Value.Id, state.Chunk,
                oldObjectRevision, checked(oldObjectRevision + 1), null)],
            [chunk]);
    }

    private WorldTransactionResult Drop(
        ActorState actor, DropInventoryItemTransaction command)
    {
        if (command.Quantity <= 0)
            return Rejected(command.Context, WorldTransactionStatus.InvalidQuantity, actor);
        if (!IsFinite(command.Position))
            return Rejected(command.Context, WorldTransactionStatus.InvalidPlacement, actor);
        if (command.WorldLevel != actor.WorldLevel)
            return Rejected(command.Context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (!InRange(actor.Position, command.Position))
            return Rejected(command.Context, WorldTransactionStatus.OutOfRange, actor);
        var chunk = WorldChunkKey.At(command.Position, command.WorldLevel);
        if (ChunkRevision(chunk) != command.ExpectedChunkRevision)
            return Rejected(command.Context, WorldTransactionStatus.StaleChunkRevision, actor);
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } stack ||
            stack.Quantity < command.Quantity)
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        if (!PlayerInventory.CanDrop(stack.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.InvalidItem, actor);

        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, command.Quantity, out var taken))
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        var additions = new List<ObjectState>(command.Quantity);
        var usedIds = new HashSet<Guid>();
        for (var index = 0; index < command.Quantity; index++)
        {
            var id = _newObjectId();
            if (id == Guid.Empty || _objects.ContainsKey(id) || !usedIds.Add(id))
                return Rejected(command.Context,
                    WorldTransactionStatus.InvalidCommand, actor,
                    "The object identity source returned a duplicate ID.");
            additions.Add(new(
                new(id, taken.ItemId, command.Position.X, command.Position.Y,
                    OwnerId: actor.ActorId.ToString()),
                chunk, 1, 1));
        }

        foreach (var addition in additions) _objects.Add(addition.Value.Id, addition);
        var chunkDelta = AdvanceChunk(chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            additions.Select(value => new WorldObjectTransactionDelta(
                WorldObjectChangeKind.Added, value.Value.Id, chunk,
                0, value.ObjectRevision, Snapshot(value))).ToImmutableArray(),
            [chunkDelta]);
    }

    private WorldTransactionResult OpenContainer(
        ActorState actor, OpenWorldContainerTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Container, out var state);
        if (rejected is not null) return rejected;
        if (!StorageContainerService.IsStorage(state!.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.NotContainer, actor);
        if (!CanAccess(actor, state.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        return Accepted(command.Context, actor, [], [],
            container: ContainerSnapshot(state));
    }

    private WorldTransactionResult TransferContainer(
        ActorState actor, TransferWorldContainerTransaction command)
    {
        if (command.Quantity <= 0)
            return Rejected(command.Context, WorldTransactionStatus.InvalidQuantity, actor);
        var rejected = ValidateObject(actor, command.Context,
            command.Container, out var state, requireContainerRevision: true);
        if (rejected is not null) return rejected;
        if (!StorageContainerService.IsStorage(state!.Value.ItemId))
            return Rejected(command.Context, WorldTransactionStatus.NotContainer, actor);
        if (!CanAccess(actor, state.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);

        var inventory = actor.Inventory.Clone();
        var container = StorageContainerService.Open(state.Value);
        if (command.Direction == WorldContainerTransferDirection.Deposit)
        {
            if (!container.Definition.AllowsDeposit)
                return Rejected(command.Context,
                    WorldTransactionStatus.ContainerDepositDenied, actor);
            if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
                return Rejected(command.Context,
                    WorldTransactionStatus.InvalidInventorySlot, actor);
            if (actor.Inventory[command.InventorySlot] is not { } available ||
                available.Quantity < command.Quantity)
                return Rejected(command.Context,
                    WorldTransactionStatus.ItemUnavailable, actor);
        }
        else if (command.Direction == WorldContainerTransferDirection.Withdraw)
        {
            if ((uint)command.ContainerSlot >=
                (uint)container.Definition.Capacity ||
                container.StackAt(command.ContainerSlot) is not { } available ||
                available.Quantity < command.Quantity)
                return Rejected(command.Context,
                    WorldTransactionStatus.ContainerItemUnavailable, actor);
        }
        else
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCommand, actor);
        var moved = command.Direction switch
        {
            WorldContainerTransferDirection.Deposit =>
                ItemContainerTransferService.TryDeposit(
                    inventory, command.InventorySlot, container, command.Quantity),
            WorldContainerTransferDirection.Withdraw =>
                ItemContainerTransferService.TryWithdraw(
                    container, command.ContainerSlot, inventory, command.Quantity),
            _ => false
        };
        if (!moved)
        {
            var status = command.Direction switch
            {
                WorldContainerTransferDirection.Deposit
                    when (uint)command.InventorySlot >=
                         (uint)actor.Inventory.Capacity =>
                    WorldTransactionStatus.InvalidInventorySlot,
                WorldContainerTransferDirection.Deposit =>
                    WorldTransactionStatus.ContainerFull,
                WorldContainerTransferDirection.Withdraw
                    when (uint)command.ContainerSlot >=
                         (uint)container.Definition.Capacity =>
                    WorldTransactionStatus.ContainerItemUnavailable,
                WorldContainerTransferDirection.Withdraw =>
                    WorldTransactionStatus.InventoryFull,
                _ => WorldTransactionStatus.InvalidCommand
            };
            return Rejected(command.Context, status, actor);
        }

        var previous = state.ObjectRevision;
        state.Value = StorageContainerService.Save(state.Value, container);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        state.ContainerRevision = checked(state.ContainerRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk], ContainerSnapshot(state));
    }

    private WorldTransactionResult AddFuel(
        ActorState actor, AddCampfireFuelTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } fuel ||
            !CampfireService.CanAddFuel(
                state.Value, fuel.ItemId, command.GameSeconds))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, 1, out _))
            return Rejected(command.Context, WorldTransactionStatus.ItemUnavailable, actor);
        var previous = state.ObjectRevision;
        state.Value = CampfireService.AddFuel(
            state.Value, fuel.ItemId, command.GameSeconds);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult TakeFuel(
        ActorState actor, TakeCampfireFuelTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        if (!CampfireService.CanRemoveFuel(state.Value, command.GameSeconds) ||
            string.IsNullOrWhiteSpace(state.Value.FuelItemId))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(state.Value.FuelItemId))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);
        var previous = state.ObjectRevision;
        state.Value = CampfireService.RemoveFuel(state.Value, command.GameSeconds);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult Light(
        ActorState actor, LightCampfireTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context, WorldTransactionStatus.NotCampfire, actor);
        var failure = CampfireService.LightFailure(
            state.Value, actor.Inventory.ItemIds(), command.GameSeconds);
        if (failure != CampfireLightFailure.None)
            return Rejected(command.Context,
                WorldTransactionStatus.CampfireLightingRequirementsMissing,
                actor, CampfireService.LightFailureCode(failure));
        var previous = state.ObjectRevision;
        state.Value = CampfireService.Light(
            state.Value, command.GameSeconds, actor.FiremakingLevel);
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        var chunk = AdvanceChunk(state.Chunk);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult BeginCooking(
        ActorState actor, BeginCampfireCookingTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Campfire, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.AccessDenied, actor);
        if (!CampfireService.IsCampfire(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotCampfire, actor);
        if (CampfireService.State(state.Value, command.GameSeconds) !=
            CampfireState.Lit)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidCampfireState, actor,
                "The campfire must be lit before cooking.");
        if ((uint)command.InventorySlot >= (uint)actor.Inventory.Capacity)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidInventorySlot, actor);
        if (actor.Inventory[command.InventorySlot] is not { } raw)
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        if (!CookingSkill.TryProfile(raw.ItemId, out var profile))
            return Rejected(command.Context,
                WorldTransactionStatus.NotCookable, actor);
        if (CookingSkill.LevelForExperience(actor.CookingExperience) <
            profile.RequiredLevel)
            return Rejected(command.Context,
                WorldTransactionStatus.CookingLocked, actor,
                $"Cooking level {profile.RequiredLevel} is required.");

        var inventory = actor.Inventory.Clone();
        if (!inventory.TryTake(command.InventorySlot, 1, out _))
            return Rejected(command.Context,
                WorldTransactionStatus.ItemUnavailable, actor);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor, [], []);
    }

    private WorldTransactionResult PlaceConstruction(
        ActorState actor, PlaceConstructionTransaction command)
    {
        if (!IsFinite(command.Position) || command.Rotation is < 0 or > 3)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidPlacement, actor);
        if (command.WorldLevel != actor.WorldLevel)
            return Rejected(command.Context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (!InRange(actor.Position, command.Position))
            return Rejected(command.Context, WorldTransactionStatus.OutOfRange, actor);
        if (!ConstructionService.IsConstructible(command.DefinitionId))
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidConstruction, actor);
        var chunkKey = WorldChunkKey.At(command.Position, command.WorldLevel);
        if (ChunkRevision(chunkKey) != command.ExpectedChunkRevision)
            return Rejected(command.Context,
                WorldTransactionStatus.StaleChunkRevision, actor);
        var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
            value.ResultItemId.Equals(command.DefinitionId,
                StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
            return Rejected(command.Context,
                WorldTransactionStatus.InvalidConstruction, actor);
        var consume = CraftingService.TryConsumeForPlacement(
            recipe, actor.CraftingLevel, actor.Inventory, out var inventory);
        if (consume != CraftingService.CraftResult.Success)
            return Rejected(command.Context, consume switch
            {
                CraftingService.CraftResult.Locked =>
                    WorldTransactionStatus.ConstructionLocked,
                CraftingService.CraftResult.InventoryFull =>
                    WorldTransactionStatus.InventoryFull,
                _ => WorldTransactionStatus.MissingConstructionResources
            }, actor);
        var id = _newObjectId();
        if (id == Guid.Empty || _objects.ContainsKey(id))
            return Rejected(command.Context, WorldTransactionStatus.InvalidCommand,
                actor, "The object identity source returned a duplicate ID.");
        var value = ConstructionService.Begin(new(
            id, command.DefinitionId, command.Position.X, command.Position.Y,
            OwnerId: actor.ActorId.ToString(), VisualFrame: command.Rotation));
        var state = new ObjectState(value, chunkKey, 1, 1);
        _objects.Add(id, state);
        var chunk = AdvanceChunk(chunkKey);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Added, id, chunkKey, 0, 1,
                Snapshot(state))], [chunk]);
    }

    private WorldTransactionResult BuildConstruction(
        ActorState actor, BuildConstructionTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Construction, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!ConstructionService.IsConstructionSite(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotConstructionSite, actor);
        if (actor.Inventory.Count(itemId =>
                ItemCatalog.Get(itemId).HasTag(ItemTag.Hammer)) < 1)
            return Rejected(command.Context,
                WorldTransactionStatus.MissingConstructionResources, actor);
        var previous = state.ObjectRevision;
        state.Value = ConstructionService.AddWork(state.Value,
            ConstructionService.WorkHealth(actor.CraftingLevel, actor.Energy));
        state.ObjectRevision = checked(state.ObjectRevision + 1);
        actor.CraftingExperience = SkillService.AwardExperience(
            actor.CraftingExperience, 6).Experience;
        var chunk = AdvanceChunk(state.Chunk);
        AdvanceActor(actor);
        return Accepted(command.Context, actor,
            [UpdatedDelta(state, previous)], [chunk]);
    }

    private WorldTransactionResult Demolish(
        ActorState actor, DemolishWorldObjectTransaction command)
    {
        var rejected = ValidateObject(actor, command.Context,
            command.Object, out var state);
        if (rejected is not null) return rejected;
        if (!CanAccess(actor, state!.Value))
            return Rejected(command.Context, WorldTransactionStatus.AccessDenied, actor);
        if (!ConstructionService.IsConstructionSite(state.Value))
            return Rejected(command.Context,
                WorldTransactionStatus.NotConstructionSite, actor);
        var refund = ConstructionService.DemolitionRefund(state.Value);
        if (refund is null)
            return Rejected(command.Context,
                WorldTransactionStatus.NoDemolitionRefund, actor);
        var inventory = actor.Inventory.Clone();
        if (!inventory.TryAdd(refund))
            return Rejected(command.Context, WorldTransactionStatus.InventoryFull, actor);
        var previous = state.ObjectRevision;
        _objects.Remove(state.Value.Id);
        var chunk = AdvanceChunk(state.Chunk);
        CommitInventory(actor, inventory);
        return Accepted(command.Context, actor,
            [new(WorldObjectChangeKind.Removed, state.Value.Id, state.Chunk,
                previous, checked(previous + 1), null)], [chunk]);
    }

    private WorldTransactionResult? ValidateObject(
        ActorState actor,
        WorldTransactionContext context,
        WorldObjectHandle handle,
        out ObjectState? state,
        bool requireContainerRevision = false)
    {
        state = null;
        if (!_objects.TryGetValue(handle.ObjectId, out var found))
            return Rejected(context, WorldTransactionStatus.ObjectNotFound, actor);
        if (found.Chunk != handle.Chunk)
            return Rejected(context,
                WorldTransactionStatus.ObjectLocationMismatch, actor);
        if (handle.Chunk.WorldLevel != actor.WorldLevel)
            return Rejected(context, WorldTransactionStatus.WrongWorldLevel, actor);
        if (found.ObjectRevision != handle.ExpectedObjectRevision)
            return Rejected(context,
                WorldTransactionStatus.StaleObjectRevision, actor);
        if (ChunkRevision(found.Chunk) != handle.ExpectedChunkRevision)
            return Rejected(context, WorldTransactionStatus.StaleChunkRevision, actor);
        if (requireContainerRevision &&
            found.ContainerRevision != handle.ExpectedContainerRevision)
            return Rejected(context,
                WorldTransactionStatus.StaleContainerRevision, actor);
        if (!InRange(actor.Position, new(found.Value.X, found.Value.Y)))
            return Rejected(context, WorldTransactionStatus.OutOfRange, actor);
        state = found;
        return null;
    }

    private static bool IsPortable(WorldGroundObject value) =>
        !StorageContainerService.IsStorage(value.ItemId) &&
        !CampfireService.IsCampfire(value) &&
        !ConstructionService.IsConstructible(value.ItemId);

    private static bool CanAccess(ActorState actor, WorldGroundObject value) =>
        string.IsNullOrWhiteSpace(value.OwnerId) &&
        string.IsNullOrWhiteSpace(value.GroupOwnerId) ||
        string.Equals(value.OwnerId, actor.ActorId.ToString(),
            StringComparison.Ordinal) ||
        !string.IsNullOrWhiteSpace(actor.GroupId) &&
        string.Equals(value.GroupOwnerId, actor.GroupId,
            StringComparison.Ordinal);

    private static bool InRange(Vector2 actor, Vector2 target) =>
        Vector2.DistanceSquared(actor, target) <=
        InteractionRange * InteractionRange;

    private WorldTransactionResult Accepted(
        WorldTransactionContext context,
        ActorState actor,
        IEnumerable<WorldObjectTransactionDelta> objects,
        IEnumerable<WorldChunkRevisionDelta> chunks,
        WorldContainerSnapshot? container = null) =>
        new(context.CommandId, WorldTransactionStatus.Accepted,
            actor.ActorRevision, actor.InventoryRevision,
            objects.ToImmutableArray(), chunks.ToImmutableArray(),
            actor.GameplaySnapshot(), container);

    private WorldTransactionResult Rejected(
        WorldTransactionContext context,
        WorldTransactionStatus status,
        ActorState? actor = null,
        string detail = "") =>
        new(context.CommandId, status,
            actor?.ActorRevision ?? 0,
            actor?.InventoryRevision ?? 0,
            [], [], actor?.GameplaySnapshot(), null, detail);

    private static WorldObjectTransactionDelta UpdatedDelta(
        ObjectState state, uint previous) => new(
            WorldObjectChangeKind.Updated, state.Value.Id, state.Chunk,
            previous, state.ObjectRevision, Snapshot(state));

    private static AuthoritativeWorldObjectSnapshot Snapshot(ObjectState state) =>
        new(state.Value.Id, state.Value.ItemId,
            new(state.Value.X, state.Value.Y), state.Chunk,
            state.ObjectRevision, state.ContainerRevision,
            state.Value.VisualFrame, state.Value.Health, state.Value.MaxHealth,
            state.Value.OwnerId, state.Value.GroupOwnerId,
            state.Value.Container is not null ||
            StorageContainerService.IsStorage(state.Value.ItemId),
            state.Value.FuelItemId, state.Value.LitUntilGameSeconds,
            state.Value.FiremakingLevel,
            FromCoreGateState(state.Value));

    private static WorldContainerContents RestoreContainer(
        AuthoritativeWorldObjectSnapshot snapshot,
        WorldContainerSnapshot container,
        IReadOnlyDictionary<WorldChunkKey, uint> chunks)
    {
        var definition = StorageContainerService.Definition(
            snapshot.ObjectId, snapshot.DefinitionId);
        if (container.ObjectId != snapshot.ObjectId ||
            container.Chunk != snapshot.Chunk ||
            container.ChunkRevision != chunks[snapshot.Chunk] ||
            container.ObjectRevision != snapshot.ObjectRevision ||
            container.ContainerRevision != snapshot.ContainerRevision ||
            !string.Equals(container.DefinitionId, snapshot.DefinitionId,
                StringComparison.OrdinalIgnoreCase) ||
            container.AllowsDeposit != definition.AllowsDeposit ||
            container.Slots.Length != definition.Capacity)
        {
            throw new InvalidDataException(
                "The world checkpoint contains invalid container metadata.");
        }

        var items = new string?[definition.Capacity];
        var quantities = new int[definition.Capacity];
        var owners = new string?[definition.Capacity];
        var seen = new bool[definition.Capacity];
        foreach (var slot in container.Slots)
        {
            if ((uint)slot.Slot >= (uint)definition.Capacity ||
                seen[slot.Slot])
            {
                throw new InvalidDataException(
                    "The world checkpoint contains invalid container slots.");
            }

            seen[slot.Slot] = true;
            if (slot.ItemId is null && slot.Quantity == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(slot.ItemId) ||
                slot.Quantity <= 0 ||
                !ItemCatalog.TryGet(slot.ItemId, out var item) ||
                slot.Quantity > 1 && !item.CanStack)
            {
                throw new InvalidDataException(
                    "The world checkpoint contains an invalid container item.");
            }

            items[slot.Slot] = slot.ItemId;
            quantities[slot.Slot] = slot.Quantity;
            owners[slot.Slot] = slot.OwnerId;
        }
        if (seen.Any(value => !value))
        {
            throw new InvalidDataException(
                "The world checkpoint container baseline is incomplete.");
        }
        return new WorldContainerContents(items, quantities, owners);
    }

    private static bool ValidGateState(
        string definitionId,
        WorldGateAccessState state) => GateCatalog.IsGate(definitionId)
        ? state != WorldGateAccessState.None &&
          Enum.IsDefined(state)
        : state == WorldGateAccessState.None;

    private static GateAccessState ToCoreGateState(WorldGateAccessState value) =>
        value switch
        {
            WorldGateAccessState.None or WorldGateAccessState.Unlocked =>
                GateAccessState.Unlocked,
            WorldGateAccessState.Opened => GateAccessState.Opened,
            WorldGateAccessState.Locked => GateAccessState.Locked,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static WorldGateAccessState FromCoreGateState(
        WorldGroundObject value)
    {
        if (!GateCatalog.IsGate(value.ItemId))
        {
            return WorldGateAccessState.None;
        }

        return value.GateState switch
        {
            GateAccessState.Unlocked => WorldGateAccessState.Unlocked,
            GateAccessState.Opened => WorldGateAccessState.Opened,
            GateAccessState.Locked => WorldGateAccessState.Locked,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private WorldContainerSnapshot ContainerSnapshot(ObjectState state)
    {
        var container = StorageContainerService.Open(state.Value);
        var items = container.Items;
        var quantities = container.Quantities;
        var owners = container.OwnerIds;
        var slots = ImmutableArray.CreateBuilder<WorldContainerSlotSnapshot>(
            container.Definition.Capacity);
        for (var slot = 0; slot < container.Definition.Capacity; slot++)
            slots.Add(new(slot, items[slot], quantities[slot], owners[slot]));
        return new(state.Value.Id, state.Chunk, ChunkRevision(state.Chunk),
            state.ObjectRevision,
            state.ContainerRevision, state.Value.ItemId,
            container.Definition.AllowsDeposit, slots.MoveToImmutable());
    }

    private WorldChunkRevisionDelta AdvanceChunk(WorldChunkKey chunk)
    {
        var previous = ChunkRevision(chunk);
        var current = checked(previous + 1);
        _chunkRevisions[chunk] = current;
        return new(chunk, previous, current);
    }

    private uint ChunkRevision(WorldChunkKey chunk) =>
        _chunkRevisions.GetValueOrDefault(chunk);

    private static void AdvanceActor(ActorState actor) =>
        actor.ActorRevision = checked(actor.ActorRevision + 1);

    private static void CommitInventory(
        ActorState actor, InventoryContainer inventory)
    {
        actor.Inventory = inventory;
        actor.InventoryRevision = checked(actor.InventoryRevision + 1);
        AdvanceActor(actor);
    }

    private static ActorState? CreateActor(WorldTransactionActorInput input)
    {
        if (input.ActorId.Value == Guid.Empty ||
            input.Gameplay.ActorRevision == 0 ||
            input.Gameplay.Inventory.Revision == 0 ||
            input.Gameplay.Health < 0 ||
            input.Gameplay.WoodcuttingExperience < 0 ||
            input.Gameplay.FarmingExperience < 0 ||
            input.Gameplay.MiningExperience < 0 ||
            input.Gameplay.AdventureExperience < 0 ||
            input.Gameplay.Inventory.Capacity != PlayerInventory.Capacity)
            return null;
        var inventory = PlayerInventory.CreateContainer();
        var encounteredSlots = new bool[inventory.Capacity];
        foreach (var slot in input.Gameplay.Inventory.Slots)
        {
            if (slot.Slot < 0 || slot.Slot >= inventory.Capacity)
                return null;
            if (encounteredSlots[slot.Slot]) return null;
            encounteredSlots[slot.Slot] = true;
            if (slot.ItemId is null && slot.Quantity == 0) continue;
            if (string.IsNullOrWhiteSpace(slot.ItemId) || slot.Quantity <= 0 ||
                !inventory.TrySetSlot(slot.Slot, slot.ItemId, slot.Quantity))
                return null;
        }
        if (encounteredSlots.Any(value => !value)) return null;
        return new(input, inventory);
    }

    private void Remember(
        (ActorId ActorId, Guid CommandId) key,
        object command,
        WorldTransactionResult result)
    {
        _commandResults.Add(key, new(command, result));
        _commandOrder.Enqueue(key);
        while (_commandOrder.Count > MaximumRememberedCommands)
            _commandResults.Remove(_commandOrder.Dequeue());
    }

    private void EnsureOwner()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        _ownerThreadId ??= currentThreadId;
        if (currentThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "World transactions must execute on their owning simulation thread.");
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private sealed class ActorState
    {
        private readonly PlayerGameplaySnapshot _source;

        public ActorState(
            WorldTransactionActorInput input, InventoryContainer inventory)
        {
            _source = input.Gameplay;
            ActorId = input.ActorId;
            Position = input.Position;
            WorldLevel = input.WorldLevel;
            Health = input.Gameplay.Health;
            ActorRevision = input.Gameplay.ActorRevision;
            InventoryRevision = input.Gameplay.Inventory.Revision;
            CraftingLevel = CraftingSkill.LevelForExperience(
                input.Gameplay.CraftingExperience);
            CraftingExperience = input.Gameplay.CraftingExperience;
            CookingExperience = input.Gameplay.CookingExperience;
            FiremakingLevel = Math.Clamp(input.FiremakingLevel, 1, 20);
            Energy = Math.Clamp(input.Energy, 0, 100);
            GroupId = input.GroupId;
            Inventory = inventory;
        }

        public ActorId ActorId { get; }
        public Vector2 Position { get; }
        public int WorldLevel { get; }
        public int Health { get; }
        public uint ActorRevision { get; set; }
        public uint InventoryRevision { get; set; }
        public int CraftingLevel { get; }
        public int CraftingExperience { get; set; }
        public int CookingExperience { get; set; }
        public int FiremakingLevel { get; }
        public float Energy { get; }
        public string? GroupId { get; }
        public InventoryContainer Inventory { get; set; }

        public PlayerGameplaySnapshot GameplaySnapshot()
        {
            var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
                Inventory.Capacity);
            for (var slot = 0; slot < Inventory.Capacity; slot++)
            {
                var value = Inventory[slot];
                slots.Add(new(slot, value?.ItemId, value?.Quantity ?? 0));
            }
            return _source with
            {
                ActorRevision = ActorRevision,
                CraftingExperience = CraftingExperience,
                CookingExperience = CookingExperience,
                Inventory = new(InventoryRevision, slots.MoveToImmutable())
            };
        }
    }

    private sealed class ObjectState(
        WorldGroundObject value,
        WorldChunkKey chunk,
        uint objectRevision,
        uint containerRevision)
    {
        public WorldGroundObject Value { get; set; } = value;
        public WorldChunkKey Chunk { get; } = chunk;
        public uint ObjectRevision { get; set; } = objectRevision;
        public uint ContainerRevision { get; set; } = containerRevision;
    }

    private sealed record CommandReceipt(
        object Command,
        WorldTransactionResult Result);
}
