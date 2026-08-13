using System.Numerics;
using IslandRpg.Protocol;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

/// <summary>
/// Pure transport projection for authoritative world transactions. This type
/// deliberately owns no world state: the simulation validates and commits the
/// transaction first, then the server projects its immutable receipt here.
/// </summary>
public static class WorldActionProtocolAdapter
{
    /// <summary>
    /// Converts an untrusted, already-decoded wire payload into a
    /// transport-independent session intent. Returns false for the four
    /// non-world gameplay actions, allowing their existing path to handle them.
    /// </summary>
    public static bool TryToWorldIntent(
        ActionCommandMessage command,
        out WorldGameplayIntent? intent)
    {
        ArgumentNullException.ThrowIfNull(command);
        intent = command.Payload switch
        {
            PickUpWorldObjectAction value => new PickUpWorldObjectIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Object)),
            DropInventoryItemAction value => new DropInventoryItemIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                value.InventorySlot,
                value.Quantity,
                new Vector2(value.X, value.Y),
                value.WorldLevel,
                value.ExpectedChunkRevision),
            OpenContainerAction value => new OpenWorldContainerIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Object)),
            ContainerTransferAction value => new TransferWorldContainerIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Container, value.ExpectedContainerRevision),
                value.Direction switch
                {
                    ContainerTransferDirection.Deposit =>
                        WorldContainerTransferDirection.Deposit,
                    ContainerTransferDirection.Withdraw =>
                        WorldContainerTransferDirection.Withdraw,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(command), "The container direction is invalid."),
                },
                value.InventorySlot,
                value.ContainerSlot,
                value.Quantity),
            AddCampfireFuelAction value => new AddCampfireFuelIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Campfire),
                value.InventorySlot),
            TakeCampfireFuelAction value => new TakeCampfireFuelIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Campfire)),
            LightCampfireAction value => new LightCampfireIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Campfire)),
            CookOnCampfireAction value => new CookOnCampfireIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Campfire),
                value.InventorySlot),
            PlaceConstructionAction value => new PlaceConstructionIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                value.DefinitionId,
                new Vector2(value.X, value.Y),
                value.WorldLevel,
                value.Rotation,
                value.ExpectedChunkRevision),
            BuildConstructionAction value => new BuildConstructionIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Construction)),
            DemolishWorldObjectAction value => new DemolishWorldObjectIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                Handle(value.Object)),
            _ => null,
        };
        return intent is not null;
    }

    public static ActionResultMessage ToActionResult(
        ulong sequence,
        ulong tick,
        WorldTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rejection = MapRejection(result.Status);
        return new ActionResultMessage(
            sequence,
            tick,
            result.CommandId,
            result.Accepted,
            rejection,
            result.Detail,
            result.ActorRevision,
            result.InventoryRevision);
    }

    public static WorldObjectStateMessage ToPublicWorldState(
        ulong sequence,
        ulong tick,
        AuthoritativeWorldObjectSnapshot value,
        uint chunkRevision)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new WorldObjectStateMessage(
            sequence,
            tick,
            ToPublicState(value, chunkRevision));
    }

    /// <summary>
    /// Projects the requester-owned gameplay sections that changed relative to
    /// the command's optimistic baseline. Inventory changes carry all 28 slots;
    /// this remains a legal delta and avoids reconstructing pre-transaction
    /// inventory merely to calculate sparse slots.
    /// </summary>
    public static PlayerStateMessage? ToPrivatePlayerState(
        ulong sequence,
        ulong tick,
        Guid playerId,
        ulong playerEntityId,
        ActionCommandMessage command,
        WorldTransactionResult result,
        bool forceBaseline = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Gameplay is not { } gameplay) return null;

        var actorChanged = forceBaseline ||
            gameplay.ActorRevision != command.ActorRevision;
        var inventoryChanged = forceBaseline ||
            gameplay.Inventory.Revision != command.InventoryRevision;
        if (!actorChanged && !inventoryChanged) return null;

        var flags = forceBaseline ? PlayerStateFlags.Baseline : PlayerStateFlags.None;
        if (actorChanged) flags |= PlayerStateFlags.Actor;
        if (inventoryChanged) flags |= PlayerStateFlags.Inventory;
        var slots = inventoryChanged
            ? gameplay.Inventory.Slots.Select(static value =>
                new InventorySlotState(
                    value.Slot,
                    value.ItemId ?? string.Empty,
                    value.Quantity)).ToArray()
            : [];
        if (inventoryChanged && slots.Length != ProtocolLimits.PlayerInventorySlots)
        {
            throw new InvalidOperationException(
                "The authoritative player inventory is not protocol-sized.");
        }
        return new PlayerStateMessage(
            sequence,
            tick,
            playerId,
            playerEntityId,
            flags,
            forceBaseline ? 0 : command.ActorRevision,
            forceBaseline ? 0 : command.InventoryRevision,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay.Health,
            gameplay.Hunger,
            gameplay.WellFedSeconds,
            gameplay.CraftingExperience,
            gameplay.CookingExperience,
            slots,
            gameplay.WoodcuttingExperience,
            gameplay.FarmingExperience,
            gameplay.MiningExperience,
            gameplay.AdventureExperience,
            gameplay.DiggingExperience,
            gameplay.FishingExperience);
    }

    /// <summary>
    /// Creates the public, broadcast-safe world delta. Visual fuel/burn and
    /// gate state are public; container slots, ownership and requester state
    /// are intentionally absent from <see cref="WorldObjectState"/>.
    /// </summary>
    public static WorldObjectDeltaBatchMessage? ToPublicWorldDeltaBatch(
        ulong sequence,
        ulong tick,
        WorldTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted || result.ObjectDeltas.IsDefaultOrEmpty) return null;
        if (result.ObjectDeltas.Length > ProtocolLimits.MaxWorldObjectsPerBatch)
        {
            throw new InvalidOperationException(
                "The world transaction exceeds one bounded protocol batch.");
        }

        var chunkDeltas = IndexChunkDeltas(result);
        var projected = new WorldObjectDelta[result.ObjectDeltas.Length];
        for (var index = 0; index < projected.Length; index++)
        {
            var value = result.ObjectDeltas[index];
            if (!chunkDeltas.TryGetValue(value.Chunk, out var chunk))
            {
                throw new InvalidOperationException(
                    "A world-object delta has no matching chunk revision delta.");
            }

            ValidateDelta(value, chunk);
            var reference = new WorldObjectReference(
                value.ObjectId,
                value.Chunk.X,
                value.Chunk.Y,
                ToWireWorldLevel(value.Chunk.WorldLevel),
                value.PreviousObjectRevision,
                chunk.PreviousRevision);
            projected[index] = value.Kind switch
            {
                WorldObjectChangeKind.Added or WorldObjectChangeKind.Updated =>
                    new WorldObjectDelta(
                        WorldObjectDeltaKind.Upsert,
                        reference,
                        chunk.CurrentRevision,
                        ToPublicState(value.Object!, chunk.CurrentRevision)),
                WorldObjectChangeKind.Removed => new WorldObjectDelta(
                    WorldObjectDeltaKind.Remove,
                    reference,
                    chunk.CurrentRevision,
                    null),
                _ => throw new InvalidOperationException(
                    "The world-object change kind is invalid."),
            };
        }

        return new WorldObjectDeltaBatchMessage(sequence, tick, projected);
    }

    /// <summary>
    /// Creates a complete requester-only container baseline. Callers must queue
    /// this message only on the requesting connection; it must never enter the
    /// public broadcast path.
    /// </summary>
    public static ContainerStateMessage? ToPrivateContainerBaseline(
        ulong sequence,
        ulong tick,
        ActionCommandMessage command,
        WorldTransactionResult result)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted || result.Container is not { } container) return null;

        var reference = ContainerReference(command, result, container);
        var slots = container.Slots
            .OrderBy(static value => value.Slot)
            .Select(static value => new ContainerSlotState(
                value.Slot,
                value.ItemId ?? string.Empty,
                value.Quantity))
            .ToArray();
        if (slots.Length == 0 ||
            slots.Select(static value => value.Slot)
                .Where((slot, index) => slot != index)
                .Any())
        {
            throw new InvalidOperationException(
                "An authoritative container baseline must contain dense slots.");
        }

        return new ContainerStateMessage(
            sequence,
            tick,
            reference,
            0,
            container.ContainerRevision,
            container.DefinitionId,
            container.AllowsDeposit
                ? ContainerAccessMode.DepositAndWithdraw
                : ContainerAccessMode.WithdrawOnly,
            slots.Length,
            true,
            slots);
    }

    public static CommandRejectionCode MapRejection(
        WorldTransactionStatus status) => status switch
        {
            WorldTransactionStatus.Accepted => CommandRejectionCode.None,
            WorldTransactionStatus.CommandIdConflict or
                WorldTransactionStatus.StaleActorRevision or
                WorldTransactionStatus.StaleInventoryRevision or
                WorldTransactionStatus.StaleObjectRevision or
                WorldTransactionStatus.StaleChunkRevision or
                WorldTransactionStatus.StaleContainerRevision =>
                CommandRejectionCode.OutOfOrder,
            WorldTransactionStatus.ActorNotFound or
                WorldTransactionStatus.AccessDenied =>
                CommandRejectionCode.NotAuthorized,
            WorldTransactionStatus.InvalidCommand or
                WorldTransactionStatus.ObjectLocationMismatch or
                WorldTransactionStatus.InvalidItem or
                WorldTransactionStatus.InvalidQuantity or
                WorldTransactionStatus.InvalidInventorySlot or
            WorldTransactionStatus.InvalidPlacement =>
                CommandRejectionCode.Invalid,
            WorldTransactionStatus.InvalidExcavation or
                WorldTransactionStatus.MissingExcavationTool or
                WorldTransactionStatus.InvalidCaveLink =>
                CommandRejectionCode.Impossible,
            WorldTransactionStatus.ExcavationCadenceLocked =>
                CommandRejectionCode.RateLimited,
            _ => CommandRejectionCode.Impossible,
        };

    private static WorldObjectHandle Handle(
        WorldObjectReference value,
        uint expectedContainerRevision = 0) => new(
        value.ObjectId,
        new WorldChunkKey(value.ChunkX, value.ChunkY, value.WorldLevel),
        value.ExpectedObjectRevision,
        value.ExpectedChunkRevision,
        expectedContainerRevision);

    private static Dictionary<WorldChunkKey, WorldChunkRevisionDelta>
        IndexChunkDeltas(WorldTransactionResult result)
    {
        var indexed = new Dictionary<WorldChunkKey, WorldChunkRevisionDelta>();
        foreach (var value in result.ChunkDeltas)
        {
            if (value.CurrentRevision <= value.PreviousRevision ||
                !indexed.TryAdd(value.Chunk, value))
            {
                throw new InvalidOperationException(
                    "World chunk revisions must advance exactly once per receipt.");
            }
        }

        return indexed;
    }

    private static void ValidateDelta(
        WorldObjectTransactionDelta value,
        WorldChunkRevisionDelta chunk)
    {
        if (value.ObjectId == Guid.Empty ||
            value.CurrentObjectRevision <= value.PreviousObjectRevision)
        {
            throw new InvalidOperationException(
                "World-object revisions must advance monotonically.");
        }

        if (value.Kind == WorldObjectChangeKind.Removed)
        {
            if (value.Object is not null)
                throw new InvalidOperationException(
                    "A removed world object cannot carry current state.");
            return;
        }

        if (value.Object is not { } state ||
            state.ObjectId != value.ObjectId ||
            state.Chunk != value.Chunk ||
            state.ObjectRevision != value.CurrentObjectRevision ||
            chunk.Chunk != state.Chunk)
        {
            throw new InvalidOperationException(
                "The authoritative world-object state does not match its delta.");
        }
    }

    private static WorldObjectState ToPublicState(
        AuthoritativeWorldObjectSnapshot value,
        uint chunkRevision) => new(
        value.ObjectId,
        value.Chunk.X,
        value.Chunk.Y,
        ToWireWorldLevel(value.Chunk.WorldLevel),
        chunkRevision,
        value.ObjectRevision,
        value.DefinitionId,
        value.Position.X,
        value.Position.Y,
        ToWireRotation(value.Rotation),
        value.Health,
        value.MaximumHealth,
        value.HasContainer,
        value.FuelItemId ?? string.Empty,
        value.LitUntilGameSeconds,
        value.GateState switch
        {
            WorldGateAccessState.None => WorldObjectGateState.None,
            WorldGateAccessState.Unlocked => WorldObjectGateState.Unlocked,
            WorldGateAccessState.Opened => WorldObjectGateState.Opened,
            WorldGateAccessState.Locked => WorldObjectGateState.Locked,
            _ => throw new InvalidOperationException(
                "The authoritative gate state is invalid."),
        },
        value.LinkedObjectId ?? Guid.Empty);

    private static WorldObjectReference ContainerReference(
        ActionCommandMessage command,
        WorldTransactionResult result,
        WorldContainerSnapshot container)
    {
        if (container.ObjectRevision != container.ContainerRevision)
        {
            // Protocol v2 uses the reference revision as the private container
            // revision. Storage mutations advance both revisions together, so
            // divergence signals a broken authority invariant rather than a
            // value that can be represented safely on the wire.
            throw new InvalidOperationException(
                "Container and object revisions cannot diverge on protocol v2.");
        }

        var changed = result.ObjectDeltas.FirstOrDefault(value =>
            value.ObjectId == container.ObjectId && value.Object is not null);
        if (changed is not null)
        {
            var chunk = result.ChunkDeltas.SingleOrDefault(value =>
                value.Chunk == changed.Chunk);
            if (chunk.CurrentRevision == 0)
                throw new InvalidOperationException(
                    "The changed container has no current chunk revision.");
            return new WorldObjectReference(
                container.ObjectId,
                changed.Chunk.X,
                changed.Chunk.Y,
                ToWireWorldLevel(changed.Chunk.WorldLevel),
                container.ContainerRevision,
                chunk.CurrentRevision);
        }

        var original = command.Payload switch
        {
            OpenContainerAction value => value.Object,
            ContainerTransferAction value => value.Container,
            _ => throw new InvalidOperationException(
                "Only container actions can return private container state."),
        };
        if (original.ObjectId != container.ObjectId ||
            original.ChunkX != container.Chunk.X ||
            original.ChunkY != container.Chunk.Y ||
            original.WorldLevel != container.Chunk.WorldLevel ||
            original.ExpectedObjectRevision != container.ObjectRevision ||
            original.ExpectedChunkRevision != container.ChunkRevision)
        {
            throw new InvalidOperationException(
                "The opened container does not match the authoritative result.");
        }

        return new WorldObjectReference(
            container.ObjectId,
            container.Chunk.X,
            container.Chunk.Y,
            ToWireWorldLevel(container.Chunk.WorldLevel),
            container.ContainerRevision,
            container.ChunkRevision);
    }

    private static short ToWireWorldLevel(int value)
    {
        if (value is < short.MinValue or > short.MaxValue)
            throw new InvalidOperationException(
                "The authoritative world level is outside protocol bounds.");
        return (short)value;
    }

    private static int ToWireRotation(int value) => value switch
    {
        -1 => 0,
        >= 0 and <= 3 => value,
        _ => throw new InvalidOperationException(
            "The authoritative object rotation is outside protocol bounds."),
    };
}
