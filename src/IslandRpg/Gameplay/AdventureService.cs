namespace IslandRpg.Gameplay;

public static class AdventureService
{
    public const int MaximumLevel = 100;
    public const int BaseMaximumHealth = 100;
    public const int HealthPerLevel = 2;

    public static int LevelForExperience(int experience)
    {
        experience = Math.Max(0, experience);
        for (var level = MaximumLevel; level > 1; level--)
            if (experience >= ExperienceForLevel(level))
                return level;
        return 1;
    }

    public static int ExperienceForLevel(int level)
    {
        level = Math.Clamp(level, 1, MaximumLevel);
        var rank = level - 1;
        return 50 * rank * rank + 25 * rank;
    }

    public static int MaximumHealth(int experience) =>
        BaseMaximumHealth +
        (LevelForExperience(experience) - 1) * HealthPerLevel;

    internal static SkillExperienceChange AwardFromAction(
        int currentExperience, int actionExperience)
    {
        var previousLevel = LevelForExperience(currentExperience);
        var requested = actionExperience <= 0
            ? 0
            : Math.Max(1, (int)MathF.Ceiling(actionExperience * .25f));
        var experience = Math.Min(
            ExperienceForLevel(MaximumLevel),
            Math.Max(0, currentExperience) + requested);
        return new(
            experience,
            experience - Math.Max(0, currentExperience),
            previousLevel,
            LevelForExperience(experience));
    }
}
