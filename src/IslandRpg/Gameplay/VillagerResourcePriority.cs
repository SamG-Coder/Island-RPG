namespace IslandRpg.Gameplay;

internal static class VillagerResourcePriority
{
    public static int Score(VillagerState villager, string itemId)
    {
        if (!ItemCatalog.TryGet(itemId, out var item)) return 0;
        if (villager.LastDeliberation is
            { Action: "seek_trade", ItemId: { Length: > 0 } tradeItem } &&
            !villager.Inventory.Contains(tradeItem) &&
            string.Equals(
                tradeItem, itemId,
                StringComparison.OrdinalIgnoreCase))
            return 95;
        if (IsPromised(villager, itemId)) return 100;
        if (VillagerSettlementProjectService.NeedsItem(villager, itemId))
            return 90;
        if (MatchesActiveGoal(villager, item)) return 80;
        if (VillagerWorkCapability.NeedsTool(villager, itemId))
            return 75;
        if (VillagerWorkSupplyPlanner.NeedsItem(villager, itemId))
            return 60;
        if (SurvivalService.TryFoodEffect(itemId, out _))
            return villager.Hunger <= 82 &&
                   VillagerSimulation.CountFood(villager.Inventory) < 2
                ? 50
                : 0;
        return 0;
    }

    public static bool MatchesActiveGoal(
        VillagerState villager, ItemDefinition item) =>
        villager.Goals?.Any(goal =>
            goal.Status == CommitmentStatus.Active &&
            goal.Progress < goal.TargetQuantity &&
            goal.Kind switch
            {
                VillagerGoalKind.StockpileFood =>
                    SurvivalService.TryFoodEffect(item.Id, out _),
                VillagerGoalKind.StockpileWood => item.HasTag(ItemTag.Log),
                _ => string.Equals(
                    goal.ItemId, item.Id,
                    StringComparison.OrdinalIgnoreCase)
            }) == true;

    private static bool IsPromised(VillagerState villager, string itemId) =>
        VillagerPromisePlanService.NeedsItem(villager, itemId);
}
