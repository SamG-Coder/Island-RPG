namespace IslandRpg.Gameplay;

internal static class VillagerSkillService
{
    public static int Experience(VillagerState villager, SkillType skill) =>
        skill switch
        {
            SkillType.Attack => villager.AttackExperience,
            SkillType.Strength => villager.StrengthExperience,
            SkillType.Defence => villager.DefenceExperience,
            SkillType.Woodcutting => villager.WoodcuttingExperience,
            SkillType.Farming => villager.FarmingExperience,
            SkillType.Fishing => villager.FishingExperience,
            SkillType.Cooking => villager.CookingExperience,
            SkillType.Firemaking => villager.FiremakingExperience,
            SkillType.Crafting => villager.CraftingExperience,
            SkillType.Digging => villager.DiggingExperience,
            SkillType.Mining => villager.MiningExperience,
            _ => 0
        };

    public static int Level(VillagerState villager, SkillType skill) =>
        SkillService.LevelForExperience(Experience(villager, skill));
}
