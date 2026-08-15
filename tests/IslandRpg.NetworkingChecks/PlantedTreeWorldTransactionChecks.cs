using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class PlantedTreeWorldTransactionChecks
{
    private const string OakSeed = "oak_seeds";
    private const string OakLogs = "oak_logs";
    private const string Axe = "stone_axe";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "planted tree world transactions plant grow fell fade and cap",
            PlantGrowFellFadeAndCap);
        checks.Add(
            "planted tree strikes are authoritative woodcutting",
            StrikesAreAuthoritative);
    }

    private static void PlantGrowFellFadeAndCap()
    {
        var actorId = new ActorId(Guid.Parse(
            "51000000-0000-0000-0000-000000000001"));
        var commandId = Guid.Parse(
            "52000000-0000-0000-0000-000000000001");
        var treeId = AuthoritativeWorldTransactions.DerivePlantedTreeObjectId(
            actorId, commandId, 1);
        CheckAssert.Equal(treeId,
            AuthoritativeWorldTransactions.DerivePlantedTreeObjectId(
                actorId, commandId, 1),
            "planted-tree identity should be deterministic");

        var authority = new AuthoritativeWorldTransactions();
        var actor = Actor(actorId, [OakSeed]);
        var position = new Vector2(3.5f, 1.5f);
        var plantedAt = 1_000d;
        var planted = authority.Execute(actor, new PlantTreeTransaction(
            Context(actor, commandId), treeId, 0, position, 0,
            authority.CaptureChunkRevision(
                WorldChunkKey.At(position, 0)),
            plantedAt, "Mira"));
        CheckAssert.True(planted.Accepted, "tree planting should succeed");
        CheckAssert.Equal(0, Count(planted.Gameplay!.Value, OakSeed),
            "planting should consume the seed");
        CheckAssert.Equal(25, planted.Gameplay.Value.FarmingExperience,
            "planting should award Farming XP");
        var tree = planted.ObjectDeltas.Single().Object!;
        CheckAssert.Equal(ItemIds.PlantedTree, tree.DefinitionId,
            "planted trees use the shared planted-tree definition");
        CheckAssert.Equal("FOAK_NN|Mira", tree.FuelItemId,
            "fuel should encode tree type and planter name");
        CheckAssert.Equal(plantedAt, tree.LitUntilGameSeconds,
            "standing trees persist planted-at time");
        CheckAssert.Equal(actorId.ToString(), tree.OwnerId,
            "planted trees keep their planter identity");
        CheckAssert.Equal(150, tree.Health,
            "oak planted trees start at full oak health");

        var value = PlantedTreeService.Plant(
            treeId, OakSeed, position.X, position.Y, plantedAt, "Mira",
            actorId.ToString());
        CheckAssert.True(PlantedTreeService.IsLiving(value),
            "a freshly planted tree is a living user tree");
        CheckAssert.Equal(
            PlantedTreeService.ShrubScale,
            PlantedTreeService.GrowthScale(value, plantedAt),
            "a planted tree starts as a scaled shrub");
        CheckAssert.True(
            PlantedTreeService.GrowthScale(
                value, plantedAt + PlantedTreeService.GrowthGameSeconds / 2)
            is > PlantedTreeService.ShrubScale and < 1,
            "a planted tree grows continuously toward full size");
        CheckAssert.Equal(
            1f,
            PlantedTreeService.GrowthScale(
                value,
                plantedAt + PlantedTreeService.GrowthGameSeconds +
                PlantedTreeService.CompactGameSeconds),
            "a mature planted tree compacts to full scale");
        CheckAssert.True(
            PlantedTreeService.IsCompacted(
                value,
                plantedAt + PlantedTreeService.GrowthGameSeconds +
                PlantedTreeService.CompactGameSeconds),
            "growth then compact marks the user tree mature");
        CheckAssert.Equal("Planted by Mira", PlantedTreeService.Title(value),
            "user trees always carry a planter title");

        for (var extra = 1;
             extra < PlantedTreeService.MaximumLivingTreesPerPlanter;
             extra++)
        {
            var extraPosition = new Vector2(3.5f + extra, 1.5f);
            authority.AddObject(new WorldObjectSeed(
                Guid.Parse($"53000000-0000-0000-0000-00000000{extra:D4}"),
                ItemIds.PlantedTree,
                extraPosition,
                FuelItemId: "FOAK_NN|Mira",
                LitUntilGameSeconds: plantedAt,
                Health: 150,
                MaximumHealth: 150,
                OwnerId: actorId.ToString()));
        }

        var overflowActor = Actor(actorId, [OakSeed]);
        var overflow = authority.Execute(overflowActor, new PlantTreeTransaction(
            Context(overflowActor, Guid.Parse(
                "55000000-0000-0000-0000-000000000099")),
            Guid.Parse("56000000-0000-0000-0000-000000000099"),
            0, new Vector2(2.5f, 1.5f), 0,
            authority.CaptureChunkRevision(
                WorldChunkKey.At(new Vector2(2.5f, 1.5f), 0)),
            plantedAt, "Mira"));
        CheckAssert.Equal(
            WorldTransactionStatus.PlantLimitReached, overflow.Status,
            "the shared planter cap must reject further living trees");

        var felled = PlantedTreeService.ApplyStrike(
            value, 0, plantedAt + 10);
        CheckAssert.True(PlantedTreeService.IsFelled(felled),
            "zero health converts the user tree into a fading trunk");
        CheckAssert.Equal(
            1f,
            PlantedTreeService.FadeOpacity(felled, felled.LitUntilGameSeconds),
            "a freshly felled trunk is fully visible");
        CheckAssert.True(
            PlantedTreeService.FadeOpacity(
                felled,
                felled.LitUntilGameSeconds +
                PlantedTreeService.FadeGameSeconds / 2) is > 0 and < 1,
            "the trunk fades over time");
        CheckAssert.True(
            PlantedTreeService.IsExpired(
                felled,
                felled.LitUntilGameSeconds +
                PlantedTreeService.FadeGameSeconds),
            "the faded trunk is removed when its fade completes");
        CheckAssert.Equal(OakLogs, PlantedTreeService.LogItemId("FOAK_NN"),
            "felled oaks still grant oak logs");
    }

    private static void StrikesAreAuthoritative()
    {
        var actorId = new ActorId(Guid.Parse(
            "61000000-0000-0000-0000-000000000001"));
        var commandId = Guid.Parse(
            "62000000-0000-0000-0000-000000000001");
        var treeId = AuthoritativeWorldTransactions.DerivePlantedTreeObjectId(
            actorId, commandId, 1);
        var authority = new AuthoritativeWorldTransactions(worldSeed: 2187);
        var actor = Actor(actorId, [OakSeed, Axe]);
        var position = new Vector2(5.5f, 2.5f);
        var plantedAt = 100d;
        var planted = authority.Execute(actor, new PlantTreeTransaction(
            Context(actor, commandId), treeId, 0, position, 0,
            authority.CaptureChunkRevision(
                WorldChunkKey.At(position, 0)),
            plantedAt, "Reed"));
        CheckAssert.True(planted.Accepted, "the tree must exist before it is struck");

        var striker = Actor(actorId, [null, Axe]);
        var handle = new WorldObjectHandle(
            treeId,
            WorldChunkKey.At(position, 0),
            planted.ObjectDeltas.Single().CurrentObjectRevision,
            planted.ChunkDeltas.Single().CurrentRevision);
        var strike = authority.Execute(striker, new StrikePlantedTreeTransaction(
            Context(striker, Guid.Parse(
                "63000000-0000-0000-0000-000000000001")),
            handle, 1, 2, plantedAt, 2187, 1));
        CheckAssert.True(strike.Accepted, "a planted-tree strike should commit");
        CheckAssert.True(
            strike.Detail.StartsWith("planted_tree_hit", StringComparison.Ordinal) ||
            strike.Detail == "planted_tree_miss",
            "the first strike should be a hit or miss receipt");
        if (strike.ObjectDeltas.Length > 0)
        {
            var after = strike.ObjectDeltas.Single().Object!;
            CheckAssert.True(after.Health < after.MaximumHealth,
                "a hit must reduce planted-tree health");
        }
    }

    private static WorldTransactionActorInput Actor(
        ActorId id, IReadOnlyList<string?> items)
    {
        if (items.Count > PlayerInventory.Capacity)
            throw new ArgumentOutOfRangeException(nameof(items));
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
            PlayerInventory.Capacity);
        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
            slots.Add(slot < items.Count && items[slot] is { } itemId
                ? new(slot, itemId, 1)
                : new(slot, null, 0));
        return new(id, new Vector2(3.5f, 1.5f), 0,
            new PlayerGameplaySnapshot(
                1, 100, 100, 0, 0, 0,
                new(1, slots.MoveToImmutable())));
    }

    private static WorldTransactionContext Context(
        WorldTransactionActorInput actor, Guid commandId) =>
        new(commandId, actor.ActorId, actor.Gameplay.ActorRevision,
            actor.Gameplay.Inventory.Revision, "planted-tree-check");

    private static int Count(PlayerGameplaySnapshot gameplay, string itemId)
    {
        var total = 0;
        foreach (var slot in gameplay.Inventory.Slots)
            if (slot.ItemId == itemId)
                total += slot.Quantity;
        return total;
    }
}
