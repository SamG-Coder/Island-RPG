namespace IslandRpg.Gameplay;

internal enum SkillType
{
    Woodcutting,
    Farming,
    Crafting
}

internal static class SkillService
{
    public const int MaximumLevel = 20;

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

    public static int ExperienceToNextLevel(int experience)
    {
        var level = LevelForExperience(experience);
        return level >= MaximumLevel
            ? 0
            : ExperienceForLevel(level + 1) - Math.Max(0, experience);
    }
}
