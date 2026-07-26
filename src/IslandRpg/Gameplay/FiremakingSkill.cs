using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class FiremakingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const int ExperiencePerFire = 60;
    public const int FlameTierCount = 4;
    public const float BaseLightRadiusPixels = 142;

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static SkillExperienceChange AwardExperience(
        int currentExperience) =>
        SkillService.AwardExperience(
            currentExperience, ExperiencePerFire);

    public static double DurationGameSeconds(int level)
    {
        var progress = LevelProgress(level);
        return WorldTime.GameSecondsPerDay * (1 + progress);
    }

    public static float LightRadiusPixels(int level)
    {
        var progress = LevelProgress(level);
        return BaseLightRadiusPixels * (1 + progress * .45f);
    }

    public static float LightIntensity(int level)
    {
        var progress = LevelProgress(level);
        return 1 + progress * .18f;
    }

    public static int FlameTier(int level) =>
        Math.Clamp((Math.Clamp(level, 1, MaximumLevel) - 1) / 5,
            0, FlameTierCount - 1);

    public static float FlameScaleForTier(int tier) =>
        1 + Math.Clamp(tier, 0, FlameTierCount - 1) * .10f;

    public static string ExperienceMessage(int experience) =>
        $"+{experience} Firemaking XP.";

    public static string LevelUpMessage(int level) =>
        $"Your Firemaking level is now {level}.";

    private static float LevelProgress(int level) =>
        (Math.Clamp(level, 1, MaximumLevel) - 1f) /
        (MaximumLevel - 1);
}
