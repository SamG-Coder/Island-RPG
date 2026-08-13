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

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
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
            string.IsNullOrWhiteSpace(seed.DefinitionId))
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
            VisualFrame: seed.Rotation);
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
            state.Value.FuelItemId, state.Value.LitUntilGameSeconds);

    private static WorldContainerSnapshot ContainerSnapshot(ObjectState state)
    {
        var container = StorageContainerService.Open(state.Value);
        var items = container.Items;
        var quantities = container.Quantities;
        var owners = container.OwnerIds;
        var slots = ImmutableArray.CreateBuilder<WorldContainerSlotSnapshot>(
            container.Definition.Capacity);
        for (var slot = 0; slot < container.Definition.Capacity; slot++)
            slots.Add(new(slot, items[slot], quantities[slot], owners[slot]));
        return new(state.Value.Id, state.ObjectRevision,
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
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
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
