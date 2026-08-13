using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Simulation;

namespace IslandRpg.Server.Persistence;

/// <summary>
/// Converts between the simulation's immutable checkpoint and the versioned
/// disk schema. This is the sole boundary where persistence knows simulation
/// types, keeping filesystem concerns out of the 60 Hz authority.
/// </summary>
public static class ServerCheckpointMapper
{
    public static ServerCheckpoint ToDurable(
        AuthoritativeSessionCheckpoint source,
        ServerOptions options,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (source.SessionId.Value != options.WorldId)
            throw new InvalidOperationException(
                "The session identity does not match the configured world.");

        return new ServerCheckpoint(
            ServerCheckpoint.CurrentSchemaVersion,
            revision,
            options.WorldId,
            options.WorldSeed,
            options.BuildVersion,
            options.ContentVersion,
            source.SessionId.Value,
            source.Tick,
            source.SnapshotSequence,
            source.Actors.Select(ToDurable).ToArray(),
            source.World.Objects.Select(ToDurable).ToArray(),
            source.World.ChunkRevisions.Select(static value =>
                new ServerChunkRevisionCheckpoint(
                    value.Chunk.X,
                    value.Chunk.Y,
                    value.Chunk.WorldLevel,
                    value.Revision)).ToArray());
    }

    public static AuthoritativeSessionCheckpoint ToSimulation(
        ServerCheckpoint source,
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ServerCheckpointStore.Validate(source, options.WorldId);
        if (source.WorldSeed != options.WorldSeed ||
            !string.Equals(source.BuildVersion, options.BuildVersion,
                StringComparison.Ordinal) ||
            !string.Equals(source.ContentVersion, options.ContentVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The checkpoint world seed or content identity does not match the server.");
        }

        var chunks = source.ChunkRevisions.ToDictionary(
            static value => new WorldChunkKey(
                value.X,
                value.Y,
                value.WorldLevel),
            static value => value.Revision);
        return new AuthoritativeSessionCheckpoint(
            new SessionId(source.SessionId),
            source.Tick,
            source.SnapshotSequence,
            source.Actors.Select(ToSimulation).ToImmutableArray(),
            new AuthoritativeWorldTransactionsCheckpoint(
                source.WorldObjects.Select(value =>
                    ToSimulation(value, chunks)).ToImmutableArray(),
                source.ChunkRevisions.Select(static value =>
                    new AuthoritativeChunkRevisionSnapshot(
                        new WorldChunkKey(
                            value.X,
                            value.Y,
                            value.WorldLevel),
                        value.Revision)).ToImmutableArray()));
    }

    private static ServerActorCheckpoint ToDurable(
        AuthoritativeActorCheckpoint value) => new(
        value.Identity.PlayerId.Value,
        value.Identity.ActorId.Value,
        value.DisplayName,
        value.Position.X,
        value.Position.Y,
        value.WorldLevel,
        value.LastProcessedCommandSequence,
        value.DisconnectedAtTick,
        value.Gameplay.ActorRevision,
        value.Gameplay.Health,
        value.Gameplay.Hunger,
        value.Gameplay.WellFedSeconds,
        value.Gameplay.CraftingExperience,
        value.Gameplay.CookingExperience,
        value.Gameplay.Inventory.Revision,
        value.Gameplay.Inventory.Slots.Select(static slot =>
            new ServerInventorySlotCheckpoint(
                slot.Slot,
                slot.ItemId,
                slot.Quantity)).ToArray(),
        value.ReconnectTokenHash.ToArray(),
        value.CommandReceipts.Select(static receipt =>
            new ServerCommandReceiptCheckpoint(
                receipt.CommandId,
                receipt.PayloadFingerprint,
                receipt.Status,
                receipt.Error)).ToArray());

    private static AuthoritativeActorCheckpoint ToSimulation(
        ServerActorCheckpoint value) => new(
        new PlayerIdentity(
            new PlayerId(value.PlayerId),
            new ActorId(value.ActorId)),
        value.DisplayName,
        new Vector2(value.X, value.Y),
        value.WorldLevel,
        value.LastProcessedCommandSequence,
        value.DisconnectedAtTick,
        new PlayerGameplaySnapshot(
            value.ActorRevision,
            value.Health,
            value.Hunger,
            value.WellFedSeconds,
            value.CraftingExperience,
            value.CookingExperience,
            new PlayerInventorySnapshot(
                value.InventoryRevision,
                value.Inventory.Select(static slot =>
                    new InventorySlotSnapshot(
                        slot.Slot,
                        slot.ItemId,
                        slot.Quantity)).ToImmutableArray())),
        value.ReconnectTokenHash.ToImmutableArray(),
        value.CommandReceipts.Select(static receipt =>
            new AuthoritativeCommandReceiptCheckpoint(
                receipt.CommandId,
                receipt.PayloadFingerprint,
                receipt.Status,
                receipt.Error)).ToImmutableArray());

    private static ServerWorldObjectCheckpoint ToDurable(
        AuthoritativeWorldObjectCheckpoint value)
    {
        var item = value.Object;
        return new ServerWorldObjectCheckpoint(
            item.ObjectId,
            item.DefinitionId,
            item.Position.X,
            item.Position.Y,
            item.Chunk.X,
            item.Chunk.Y,
            item.Chunk.WorldLevel,
            item.ObjectRevision,
            item.ContainerRevision,
            item.Rotation,
            item.Health,
            item.MaximumHealth,
            item.OwnerId,
            item.GroupOwnerId,
            item.HasContainer,
            item.FuelItemId,
            item.LitUntilGameSeconds,
            item.FiremakingLevel,
            item.GateState,
            value.Container?.AllowsDeposit ?? false,
            value.Container?.Slots.Select(static slot =>
                new ServerContainerSlotCheckpoint(
                    slot.Slot,
                    slot.ItemId,
                    slot.Quantity,
                    slot.OwnerId)).ToArray() ?? []);
    }

    private static AuthoritativeWorldObjectCheckpoint ToSimulation(
        ServerWorldObjectCheckpoint value,
        IReadOnlyDictionary<WorldChunkKey, uint> chunkRevisions)
    {
        var chunk = new WorldChunkKey(
            value.ChunkX,
            value.ChunkY,
            value.WorldLevel);
        var item = new AuthoritativeWorldObjectSnapshot(
            value.ObjectId,
            value.DefinitionId,
            new Vector2(value.X, value.Y),
            chunk,
            value.ObjectRevision,
            value.ContainerRevision,
            value.Rotation,
            value.Health,
            value.MaximumHealth,
            value.OwnerId,
            value.GroupOwnerId,
            value.HasContainer,
            value.FuelItemId,
            value.LitUntilGameSeconds,
            value.FiremakingLevel,
            value.GateState);
        var container = !value.HasContainer
            ? null
            : new WorldContainerSnapshot(
                value.ObjectId,
                chunk,
                chunkRevisions.TryGetValue(chunk, out var chunkRevision)
                    ? chunkRevision
                    : throw new InvalidDataException(
                        "A persisted container has no matching chunk revision."),
                value.ObjectRevision,
                value.ContainerRevision,
                value.DefinitionId,
                value.AllowsDeposit,
                value.Container.Select(static slot =>
                    new WorldContainerSlotSnapshot(
                        slot.Slot,
                        slot.ItemId,
                        slot.Quantity,
                        slot.OwnerId)).ToImmutableArray());
        return new AuthoritativeWorldObjectCheckpoint(item, container);
    }
}
