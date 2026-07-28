namespace IslandRpg.Gameplay;

internal static class FarmingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const int PlantingExperience = 25;
    public const float BaseGatherSeconds = .75f;

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, int amount) =>
        SkillService.AwardExperience(currentExperience, amount);

    public static float GatherSeconds(ItemDefinition? sickle) =>
        BaseGatherSeconds /
        (1 + (sickle?.FarmingPower ?? 0) * .35f);

    public static int BonusBerryCount(
        int level, ItemDefinition? sickle, float roll)
    {
        if (sickle is null) return 0;
        var chance = .35f +
                     Math.Clamp(level - 1, 0, MaximumLevel - 1) *
                     .01f +
                     sickle.FarmingPower * .10f;
        return roll < Math.Min(.75f, chance) ? 1 : 0;
    }

    public static string ExperienceMessage(int experience) =>
        $"+{experience} Farming XP.";

    public static string LevelUpMessage(int level) =>
        $"Your Farming level is now {level}.";
}
