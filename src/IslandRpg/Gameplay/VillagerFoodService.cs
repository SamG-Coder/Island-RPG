namespace IslandRpg.Gameplay;

internal static class VillagerFoodService
{
    public const float UrgentHungerThreshold = 35;

    public static bool IsMeal(string? itemId) =>
        itemId is not null &&
        SurvivalService.TryFoodEffect(itemId, out var effect) &&
        effect.HungerRestored > 0;

    public static int FindMealSlot(string?[] inventory)
    {
        for (var slot = 0; slot < inventory.Length; slot++)
            if (IsMeal(inventory[slot]))
                return slot;
        return -1;
    }

    public static VillagerState EatCarriedMeal(
        VillagerState state,
        double gameSeconds)
    {
        if (state.Health <= 0) return state;
        var slot = FindMealSlot(state.Inventory);
        if (slot < 0) return state;
        var eaten = EntityInteractionService.Eat(
            state.Inventory,
            slot,
            state.Hunger,
            state.WellFedSeconds,
            state.Health,
            AdventureService.BaseMaximumHealth);
        if (!eaten.Succeeded) return state;
        var updated = state with
        {
            Inventory = eaten.Inventory,
            Hunger = eaten.Survival.Hunger,
            Health = eaten.Survival.Health,
            WellFedSeconds = eaten.Survival.WellFedSeconds
        };
        return VillagerNeedPatternMemory.RecordMeal(updated, gameSeconds);
    }
}
