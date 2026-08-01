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
            Math.Max(0, villager.Health) * 2 +
            (int)Math.Clamp(villager.Hunger - 25, 0, 75) +
            VillagerSimulation.CountFood(villager.Inventory) * 6 +
            SkillLevel(villager.FishingExperience) * 8 +
            SkillLevel(villager.CookingExperience) * 6 +
            SkillLevel(villager.FarmingExperience) * 5 +
            (PlayerInventory.BestFishingNet(villager.Inventory)?.FishingPower ?? 0) * 20 +
            forecast.FoodDeficit * 12 -
            NonFoodSpecialistPenalty(villager),
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

    private static int NonFoodSpecialistPenalty(VillagerState villager) =>
        PlayerInventory.BestAxe(villager.Inventory) is not null ||
        PlayerInventory.BestKnife(villager.Inventory) is not null ||
        PlayerInventory.BestHammer(villager.Inventory) is not null ||
        PlayerInventory.BestPickaxe(villager.Inventory) is not null
            ? 60
            : 0;
}
