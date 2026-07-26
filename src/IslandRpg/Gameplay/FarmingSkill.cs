namespace IslandRpg.Gameplay;

internal static class FarmingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const int PlantingExperience = 25;

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, int amount) =>
        SkillService.AwardExperience(currentExperience, amount);
}
