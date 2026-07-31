namespace IslandRpg.Gameplay;

internal static class VillagerCraftPlanner
{
    private static readonly string[] Foundation =
    [
        ItemIds.MediumRock,
        ItemIds.SharpenedRock,
        ItemIds.SmallRocks
    ];

    private static readonly string[] Infrastructure =
    [
        ItemIds.Rope,
        ItemIds.Campfire,
        ItemIds.Workbench,
        ItemIds.StorageChest
    ];

    private static readonly IReadOnlyDictionary<
        VillagerWorkRole, string[]> RolePriorities =
        new Dictionary<VillagerWorkRole, string[]>
        {
            [VillagerWorkRole.Food] =
            [
                ItemIds.PrimitiveFishingNet,
                ItemIds.ReinforcedFishingNet,
                ItemIds.AdvancedFishingNet,
                ItemIds.BronzeSickle,
                ItemIds.IronSickle
            ],
            [VillagerWorkRole.Wood] =
            [ItemIds.StoneAxe, ItemIds.BronzeAxe, ItemIds.IronAxe],
            [VillagerWorkRole.Crafting] =
            [
                ItemIds.StoneKnife,
                ItemIds.StoneHammer,
                ItemIds.Bloomery,
                ItemIds.BronzeBar,
                ItemIds.SmithingAnvil,
                ItemIds.BronzeHammer,
                ItemIds.BronzeKnife,
                ItemIds.IronBloom,
                ItemIds.IronBar,
                ItemIds.IronHammer,
                ItemIds.IronKnife
            ],
            [VillagerWorkRole.Exploration] =
            [
                ItemIds.StonePickaxe,
                ItemIds.StoneShovel,
                ItemIds.BronzePickaxe,
                ItemIds.BronzeShovel,
                ItemIds.IronPickaxe,
                ItemIds.IronShovel
            ]
        };

    public static IEnumerable<string> PriorityFor(VillagerWorkRole role) =>
        Foundation.Concat(
            RolePriorities.TryGetValue(role, out var roleItems)
                ? roleItems
                : [])
            .Concat(Infrastructure);

    public static bool Needs(string itemId, string?[] inventory)
    {
        var candidate = ItemCatalog.Get(itemId);
        if (!candidate.HasTag(ItemTag.Tool))
            return !inventory.Contains(itemId);
        if (candidate.HasTag(ItemTag.Axe))
            return (PlayerInventory.BestAxe(inventory)?.WoodcuttingPower ?? 0) <
                   candidate.WoodcuttingPower;
        if (candidate.HasTag(ItemTag.Pickaxe))
            return (PlayerInventory.BestPickaxe(inventory)?.MiningPower ?? 0) <
                   candidate.MiningPower;
        if (candidate.HasTag(ItemTag.Sickle))
            return (PlayerInventory.BestSickle(inventory)?.FarmingPower ?? 0) <
                   candidate.FarmingPower;
        if (candidate.HasTag(ItemTag.Shovel))
            return (PlayerInventory.BestShovel(inventory)?.DiggingPower ?? 0) <
                   candidate.DiggingPower;
        if (candidate.HasTag(ItemTag.FishingNet))
            return (PlayerInventory.BestFishingNet(inventory)?.FishingPower ?? 0) <
                   candidate.FishingPower;
        if (candidate.HasTag(ItemTag.Hammer))
            return (PlayerInventory.BestHammer(inventory)?.HammerPower ?? 0) <
                   candidate.HammerPower;
        if (candidate.HasTag(ItemTag.Knife))
            return (PlayerInventory.BestKnife(inventory)?.KnifePower ?? 0) <
                   candidate.KnifePower;
        return !inventory.Contains(itemId);
    }
}
