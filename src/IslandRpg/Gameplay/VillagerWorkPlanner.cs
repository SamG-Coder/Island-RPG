namespace IslandRpg.Gameplay;

internal readonly record struct VillagerResourceForecast(
    int LivingPeople,
    int Food,
    int Wood,
    int CraftingMaterials,
    int FoodDeficit,
    int WoodDeficit);

internal static class VillagerWorkPlanner
{
    public static VillagerResourceForecast Forecast(
        IReadOnlyList<VillagerState> villagers)
    {
        var living = villagers.Where(value => value.Health > 0).ToArray();
        var food = living.Sum(value =>
            VillagerSimulation.CountFood(value.Inventory));
        var wood = living.Sum(value => value.Inventory.Count(item =>
            item is ItemIds.Logs or ItemIds.OakLogs or ItemIds.PineLogs or
                ItemIds.PalmLogs or ItemIds.Bamboo));
        var materials = living.Sum(value => value.Inventory.Count(item =>
            item is ItemIds.Sticks or ItemIds.PlantFibres or
                ItemIds.LargeRock or ItemIds.MediumRock));
        return new(
            living.Length,
            food,
            wood,
            materials,
            Math.Max(0, living.Length * 2 - food),
            Math.Max(0, living.Length * 4 - wood));
    }

    public static int Suitability(
        VillagerState villager,
        VillagerWorkRole role,
        VillagerResourceForecast forecast) => role switch
    {
        VillagerWorkRole.Food =>
            (int)(100 - villager.Hunger) * 3 +
            SkillLevel(villager.FishingExperience) * 4 +
            SkillLevel(villager.CookingExperience) * 3 +
            SkillLevel(villager.FarmingExperience) * 2 +
            forecast.FoodDeficit * 20,
        VillagerWorkRole.Wood =>
            SkillLevel(villager.WoodcuttingExperience) * 5 +
            (PlayerInventory.BestAxe(villager.Inventory)?.WoodcuttingPower ?? 0) * 40 +
            forecast.WoodDeficit * 8,
        VillagerWorkRole.Crafting =>
            SkillLevel(villager.CraftingExperience) * 5 +
            (PlayerInventory.BestKnife(villager.Inventory)?.KnifePower ?? 0) * 25 +
            (PlayerInventory.BestHammer(villager.Inventory)?.HammerPower ?? 0) * 25 +
            forecast.CraftingMaterials * 2,
        VillagerWorkRole.Exploration =>
            SkillLevel(villager.MiningExperience) * 3 +
            SkillLevel(villager.DiggingExperience) * 3 +
            (PlayerInventory.BestPickaxe(villager.Inventory)?.MiningPower ?? 0) * 30,
        _ => 0
    };

    private static int SkillLevel(int experience) =>
        SkillService.LevelForExperience(experience);
}
