using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Resources;
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
                    value.Revision)).ToArray(),
            (source.CookingJobs.IsDefault
                    ? ImmutableArray<AuthoritativeCookingJobCheckpoint>.Empty
                    : source.CookingJobs)
                .Select(static value => new ServerCookingJobCheckpoint(
                    value.CommandId,
                    value.ActorId.Value,
                    value.CampfireId,
                    value.CampfireChunk.X,
                    value.CampfireChunk.Y,
                    value.CampfireChunk.WorldLevel,
                    value.CampfirePosition.X,
                    value.CampfirePosition.Y,
                    value.PreferredInventorySlot,
                    value.RawItemId,
                    value.ResultItemId,
                    value.Experience,
                    value.Burnt,
                    value.DropObjectId,
                    value.CompletesAtTick)).ToArray(),
            ToDurable(source.Resources ??
                AuthoritativeResourceTransactionsCheckpoint.Empty),
            (source.World.ExcavationCadences.IsDefault
                    ? ImmutableArray<AuthoritativeExcavationCadenceCheckpoint>.Empty
                    : source.World.ExcavationCadences)
                .Select(static value =>
                    new ServerExcavationCadenceCheckpoint(
                        value.ActorId.Value,
                        value.ExcavationId,
                        value.NextAllowedGameSeconds))
                .ToArray(),
            options.IslandStart,
            ToDurable(source.Boats ??
                AuthoritativeBoatTransactionsCheckpoint.Empty),
            ToDurable(source.Combat ??
                AuthoritativeCombatCheckpoint.Empty(options.WorldSeed)));
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
                StringComparison.Ordinal) ||
            source.IslandStart != options.IslandStart)
        {
            throw new InvalidDataException(
                "The checkpoint world seed, profile, or content identity does not match the server.");
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
                        value.Revision)).ToImmutableArray(),
                (source.ExcavationCadences ?? [])
                    .Select(static value =>
                        new AuthoritativeExcavationCadenceCheckpoint(
                            new ActorId(value.ActorId),
                            value.ExcavationId,
                            value.NextAllowedGameSeconds))
                    .ToImmutableArray()),
            (source.CookingJobs ?? [])
                .Select(static value => new AuthoritativeCookingJobCheckpoint(
                    value.CommandId,
                    new ActorId(value.ActorId),
                    value.CampfireId,
                    new WorldChunkKey(
                        value.CampfireChunkX,
                        value.CampfireChunkY,
                        value.WorldLevel),
                    new Vector2(value.CampfireX, value.CampfireY),
                    value.PreferredInventorySlot,
                    value.RawItemId,
                    value.ResultItemId,
                    value.Experience,
                    value.Burnt,
                    value.DropObjectId,
                    value.CompletesAtTick)).ToImmutableArray(),
            ToSimulation(source.Resources),
            ToSimulation(source.Boats),
            ToSimulation(source.Combat, source.WorldSeed));
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
                receipt.Error)).ToArray(),
        value.Gameplay.WoodcuttingExperience,
        value.Gameplay.FarmingExperience,
        value.Gameplay.MiningExperience,
        value.Gameplay.AdventureExperience,
        value.Gameplay.DiggingExperience,
        value.Gameplay.FishingExperience,
        value.Gameplay.MaximumHealth,
        value.Gameplay.AttackExperience,
        value.Gameplay.StrengthExperience,
        value.Gameplay.DefenceExperience,
        value.Gameplay.CombatStance,
        value.Gameplay.LifeState,
        value.Gameplay.RespawnAvailableTick,
        value.Gameplay.CombatStatus.SlowedUntil,
        value.Gameplay.CombatStatus.RootedUntil,
        value.Gameplay.CombatStatus.PoisonedUntil,
        value.Gameplay.CombatStatus.NextPoisonTickAt,
        value.Gameplay.CombatStatus.PoisonDamage,
        value.Gameplay.CombatTargetEnemyId?.Value,
        value.Gameplay.CombatAttackSequence,
        value.Gameplay.NextCombatAttackTick);

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
                        slot.Quantity)).ToImmutableArray()),
            value.WoodcuttingExperience,
            value.FarmingExperience,
            value.MiningExperience,
            value.AdventureExperience,
            value.DiggingExperience,
            value.FishingExperience,
            value.MaximumHealth,
            value.AttackExperience,
            value.StrengthExperience,
            value.DefenceExperience,
            value.CombatStance,
            value.LifeState,
            value.RespawnAvailableTick,
            new IslandRpg.Gameplay.SlimeVictimStatus(
                value.SlowedUntil,
                value.RootedUntil,
                value.PoisonedUntil,
                value.NextPoisonTickAt,
                value.PoisonDamage),
            value.CombatTargetEnemyId is { } enemyId
                ? new EnemyId(enemyId)
                : null,
            value.CombatAttackSequence,
            value.NextCombatAttackTick),
        value.ReconnectTokenHash.ToImmutableArray(),
        value.CommandReceipts.Select(static receipt =>
            new AuthoritativeCommandReceiptCheckpoint(
                receipt.CommandId,
                receipt.PayloadFingerprint,
                receipt.Status,
                receipt.Error)).ToImmutableArray());

    private static IReadOnlyList<ServerBoatCheckpoint> ToDurable(
        AuthoritativeBoatTransactionsCheckpoint value) =>
        (value.Boats.IsDefault
                ? ImmutableArray<AuthoritativeBoatCheckpoint>.Empty
                : value.Boats)
            .Select(static boat => new ServerBoatCheckpoint(
                boat.BoatId.Value,
                boat.OwnerPlayerId.Value,
                boat.GroupId,
                boat.OccupantActorId?.Value,
                boat.OccupantPlayerId?.Value,
                boat.Position.X,
                boat.Position.Y,
                boat.Facing.X,
                boat.Facing.Y,
                boat.WorldLevel,
                boat.Revision,
                boat.RemainingRoute.Select(static point =>
                    new ServerBoatRoutePointCheckpoint(
                        point.X, point.Y)).ToArray(),
                boat.PlanningCooldownSeconds))
            .ToArray();

    private static AuthoritativeBoatTransactionsCheckpoint ToSimulation(
        IReadOnlyList<ServerBoatCheckpoint>? value) => new(
        (value ?? [])
            .Select(static boat => new AuthoritativeBoatCheckpoint(
                new BoatId(boat.BoatId),
                new PlayerId(boat.OwnerPlayerId),
                boat.GroupId,
                boat.OccupantActorId is { } actor
                    ? new ActorId(actor)
                    : null,
                boat.OccupantPlayerId is { } player
                    ? new PlayerId(player)
                    : null,
                new Vector2(boat.X, boat.Y),
                new Vector2(boat.FacingX, boat.FacingY),
                boat.WorldLevel,
                boat.Revision,
                boat.RemainingRoute.Select(static point =>
                    new Vector2(point.X, point.Y)).ToImmutableArray(),
                boat.PlanningCooldownSeconds))
            .ToImmutableArray());

    private static ServerResourceCheckpoint ToDurable(
        AuthoritativeResourceTransactionsCheckpoint value) => new(
        value.Chunks.Select(static chunk =>
            new ServerResourceChunkCheckpoint(
                chunk.Chunk.X,
                chunk.Chunk.Y,
                chunk.Chunk.WorldLevel,
                chunk.ResourceChunkRevision,
                chunk.Nodes.Select(static node =>
                    new ServerResourceNodeCheckpoint(
                        node.Id.Value,
                        node.Kind,
                        node.NodeRevision,
                        node.Health,
                        node.Remaining,
                        node.ReadyAtGameSeconds,
                        node.Depleted)).ToArray())).ToArray(),
        value.ActorCadences.Select(static cadence =>
            new ServerResourceCadenceCheckpoint(
                cadence.ActorId.Value,
                cadence.Action,
                cadence.ReadyAtGameSeconds,
                cadence.ActionOrdinal)).ToArray());

    private static AuthoritativeResourceTransactionsCheckpoint ToSimulation(
        ServerResourceCheckpoint? value)
    {
        value ??= new ServerResourceCheckpoint([], []);
        return new AuthoritativeResourceTransactionsCheckpoint(
            value.Chunks.Select(static chunk =>
                new ResourceChunkSparseState(
                    new WorldChunkKey(chunk.X, chunk.Y, chunk.WorldLevel),
                    chunk.Revision,
                    chunk.Nodes.Select(node =>
                        new ResourceNodeSparseState(
                            new ResourceNodeId(node.NodeId),
                            node.Kind,
                            new WorldChunkKey(
                                chunk.X, chunk.Y, chunk.WorldLevel),
                            node.NodeRevision,
                            node.Health,
                            node.Remaining,
                            node.ReadyAtGameSeconds,
                            node.Depleted)).ToImmutableArray()))
                .ToImmutableArray(),
            value.ActorCadences.Select(static cadence =>
                new ResourceActorCadenceCheckpoint(
                    new ActorId(cadence.ActorId),
                    cadence.Action,
                    cadence.ReadyAtGameSeconds,
                    cadence.ActionOrdinal)).ToImmutableArray());
    }

    private static ServerCombatCheckpoint ToDurable(
        AuthoritativeCombatCheckpoint value) => new(
        value.WorldSeed,
        value.NextEventOrdinal,
        value.NextSpawnOrdinal,
        value.Enemies.Select(static enemy => new ServerEnemyCheckpoint(
            enemy.EnemyId.Value,
            enemy.Revision,
            enemy.Kind,
            enemy.Behavior,
            enemy.SpawnPosition.X,
            enemy.SpawnPosition.Y,
            enemy.Position.X,
            enemy.Position.Y,
            enemy.Velocity.X,
            enemy.Velocity.Y,
            enemy.WorldLevel,
            enemy.PowerLevel,
            enemy.Health,
            enemy.MaximumHealth,
            enemy.SizeScale,
            enemy.Status.SlowedUntil,
            enemy.Status.RootedUntil,
            enemy.Status.PoisonedUntil,
            enemy.Status.NextPoisonTickAt,
            enemy.Status.PoisonDamage,
            enemy.TargetActorId?.Value,
            enemy.ParentEnemyId?.Value,
            enemy.SpawnOrdinal,
            enemy.AttackSequence,
            enemy.NextAttackTick,
            enemy.SplitGeneration,
            enemy.DeathRemovalTick,
            enemy.ReactionReadyTick,
            enemy.BurrowEmergeTick)).ToArray());

    private static AuthoritativeCombatCheckpoint ToSimulation(
        ServerCombatCheckpoint? value,
        long worldSeed)
    {
        value ??= new ServerCombatCheckpoint(worldSeed, 1, 1, []);
        return new AuthoritativeCombatCheckpoint(
            value.WorldSeed,
            value.NextEventOrdinal,
            value.NextSpawnOrdinal,
            value.Enemies.Select(static enemy =>
                new AuthoritativeEnemyCheckpoint(
                    new EnemyId(enemy.EnemyId),
                    enemy.Revision,
                    enemy.Kind,
                    enemy.Behavior,
                    new Vector2(enemy.SpawnX, enemy.SpawnY),
                    new Vector2(enemy.X, enemy.Y),
                    new Vector2(enemy.VelocityX, enemy.VelocityY),
                    enemy.WorldLevel,
                    enemy.PowerLevel,
                    enemy.Health,
                    enemy.MaximumHealth,
                    enemy.SizeScale,
                    new IslandRpg.Gameplay.SlimeVictimStatus(
                        enemy.SlowedUntil,
                        enemy.RootedUntil,
                        enemy.PoisonedUntil,
                        enemy.NextPoisonTickAt,
                        enemy.PoisonDamage),
                    enemy.TargetActorId is { } target
                        ? new ActorId(target)
                        : null,
                    enemy.ParentEnemyId is { } parent
                        ? new EnemyId(parent)
                        : null,
                    enemy.SpawnOrdinal,
                    enemy.AttackSequence,
                    enemy.NextAttackTick,
                    enemy.SplitGeneration,
                    enemy.DeathRemovalTick,
                    enemy.ReactionReadyTick,
                    enemy.BurrowEmergeTick)).ToImmutableArray());
    }

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
                    slot.OwnerId)).ToArray() ?? [],
            item.LinkedObjectId);
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
            value.GateState,
            value.LinkedObjectId);
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
