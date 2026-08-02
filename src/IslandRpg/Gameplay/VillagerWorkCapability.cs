namespace IslandRpg.Gameplay;

/// <summary>
/// Keeps role specialization from disabling essential work for survivors who
/// do not belong to a settlement workforce. These villagers are all-rounders:
/// they may use any useful tool and perform each basic survival role.
/// </summary>
internal static class VillagerWorkCapability
{
    public static bool IsAllRounder(VillagerState villager) =>
        villager.WorkRole == VillagerWorkRole.Unassigned &&
        (villager.SettlementGroupId is null || villager.IndependentByChoice);

    public static bool CanPerform(
        VillagerState villager,
        VillagerWorkRole role) =>
        villager.WorkRole == role || IsAllRounder(villager);

    public static bool NeedsTool(VillagerState villager, string itemId) =>
        IsAllRounder(villager) &&
        ItemCatalog.TryGet(itemId, out var item) &&
        item.HasTag(ItemTag.Tool) &&
        VillagerCraftPlanner.Needs(itemId, villager.Inventory);
}
