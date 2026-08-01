namespace IslandRpg.Gameplay;

internal static class VillagerCraftPlanner
{
    private static readonly string[] Foundation =
    [
        ItemIds.MediumRock,
        ItemIds.SharpenedRock,
        ItemIds.SmallRocks
    ];

    private static readonly IReadOnlyDictionary<
        VillagerWorkRole, string[]> RolePriorities =
        new Dictionary<VillagerWorkRole, string[]>
        {
            [VillagerWorkRole.Food] =
            [
                ItemIds.Rope,
                ItemIds.PrimitiveFishingNet,
                ItemIds.StoneSickle,
                ItemIds.GatheringBasket,
                ItemIds.Campfire,
                ItemIds.Workbench,
                ItemIds.ReinforcedFishingNet,
                ItemIds.AdvancedFishingNet,
                ItemIds.BronzeSickle,
                ItemIds.IronSickle
            ],
            [VillagerWorkRole.Wood] =
            [ItemIds.StoneAxe, ItemIds.BronzeAxe, ItemIds.IronAxe],
            [VillagerWorkRole.Crafting] =
            [
                ItemIds.Rope,
                ItemIds.PortableTorch,
                ItemIds.GatheringBasket,
                ItemIds.StoneKnife,
                ItemIds.StoneHammer,
                ItemIds.Campfire,
                ItemIds.Plank,
                ItemIds.Workbench,
                ItemIds.StorageChest,
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
                : []);

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

    public static bool Needs(string itemId, VillagerState villager)
    {
        var project = villager.ProjectAssignment?.ProjectItemId;
        if (itemId == ItemIds.SmallRocks && project == ItemIds.Campfire)
            return Count(villager.Inventory, ItemIds.SmallRocks) < 3;
        if (itemId == ItemIds.MediumRock && project == ItemIds.Campfire)
            return Count(villager.Inventory, ItemIds.SmallRocks) < 3 &&
                   Count(villager.Inventory, ItemIds.MediumRock) < 2;
        if (itemId == ItemIds.Plank)
        {
            var target = project switch
            {
                ItemIds.Workbench => 4,
                ItemIds.StorageChest => 6,
                _ => 1
            };
            return Count(villager.Inventory, ItemIds.Plank) < target;
        }
        return Needs(itemId, villager.Inventory);
    }

    private static int Count(string?[] inventory, string itemId) =>
        inventory.Count(value => value == itemId);
}
