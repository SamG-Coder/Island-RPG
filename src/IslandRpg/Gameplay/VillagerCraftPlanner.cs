namespace IslandRpg.Gameplay;

internal static class VillagerCraftPlanner
{
    private static readonly string[] Foundation =
    [
        ItemIds.MediumRock,
        ItemIds.SharpenedRock,
        ItemIds.SmallRocks
    ];

    private static readonly string[] IndependentPriorities =
    [
        ItemIds.StoneKnife,
        ItemIds.StoneAxe,
        ItemIds.Campfire,
        ItemIds.Rope,
        ItemIds.GatheringBasket,
        ItemIds.PrimitiveFishingNet,
        ItemIds.StonePickaxe,
        ItemIds.StoneShovel,
        ItemIds.StoneSickle,
        ItemIds.PortableTorch,
        ItemIds.Workbench,
        ItemIds.StorageChest
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
                ItemIds.HerbalPoultice,
                ItemIds.SaltedFish,
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
                ItemIds.HerbalPoultice,
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
            role == VillagerWorkRole.Unassigned
                ? IndependentPriorities
                : RolePriorities.TryGetValue(role, out var roleItems)
                ? roleItems
                : []);

    public static IEnumerable<string> PriorityFor(VillagerState villager)
    {
        var project = villager.ProjectAssignment is { } assignment &&
                      assignment.BuilderId == villager.Id
            ? assignment.ProjectItemId
            : null;
        return (project is null
                ? []
                : CraftingDependencyOrder(project))
            .Concat(PriorityFor(villager.WorkRole))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static bool ConsumesAssignedContribution(
        string resultItemId,
        VillagerState villager)
    {
        if (villager.ProjectAssignment is not { } assignment ||
            assignment.BuilderId == villager.Id)
            return false;
        var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
            string.Equals(value.ResultItemId, resultItemId,
                StringComparison.OrdinalIgnoreCase));
        return recipe?.Ingredients.Any(ingredient =>
            assignment.Requirements.Any(requirement =>
                VillagerSettlementProjectService.MatchesRequirement(
                    ingredient.ItemId, requirement.ItemId) &&
                villager.Inventory.Any(itemId => itemId is not null &&
                    VillagerSettlementProjectService.MatchesRequirement(
                        itemId, requirement.ItemId)))) == true;
    }

    /// <summary>
    /// Returns craftable prerequisites before their dependent result. This
    /// keeps settlement projects and personal crafting on the same recipe
    /// graph instead of maintaining a second, divergent material sequence.
    /// </summary>
    public static IReadOnlyList<string> CraftingDependencyOrder(
        string resultItemId)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(resultItemId, visited, result);
        return result;
    }

    private static void Visit(
        string itemId,
        HashSet<string> visited,
        List<string> result)
    {
        if (!visited.Add(itemId)) return;
        var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
            string.Equals(
                value.ResultItemId, itemId,
                StringComparison.OrdinalIgnoreCase));
        if (recipe is null) return;
        foreach (var ingredient in recipe.Ingredients)
            Visit(ingredient.ItemId, visited, result);
        result.Add(itemId);
    }

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
