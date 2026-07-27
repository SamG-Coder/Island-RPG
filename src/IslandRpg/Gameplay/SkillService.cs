namespace IslandRpg.Gameplay;

internal enum SkillType
{
    Woodcutting,
    Farming,
    Crafting,
    Fishing,
    Cooking,
    Firemaking,
    Digging
}

internal static class SkillService
{
    // Extension contract for every current and future skill:
    // - Skill levels, XP thresholds, maximum level, next-level progress, XP
    //   clamping, and level transitions must come from this service.
    // - A skill may define reward amounts and gameplay unlocks, but its
    //   AwardExperience method must delegate to SkillService.AwardExperience.
    // - UI, action, and developer code must not reproduce XP arithmetic.
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

    public static SkillExperienceChange AwardExperience(
        int currentExperience, int requestedAmount)
    {
        var previousLevel = LevelForExperience(currentExperience);
        var experience = Math.Min(
            ExperienceForLevel(MaximumLevel),
            Math.Max(0, currentExperience) +
            Math.Max(0, requestedAmount));
        return new(
            experience,
            experience - Math.Max(0, currentExperience),
            previousLevel,
            LevelForExperience(experience));
    }
}

internal readonly record struct SkillExperienceChange(
    int Experience,
    int Gained,
    int PreviousLevel,
    int Level)
{
    public bool LevelledUp => Level > PreviousLevel;
}
