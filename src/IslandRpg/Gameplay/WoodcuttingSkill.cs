namespace IslandRpg.Gameplay;

internal readonly record struct WoodcuttingStrike(
    bool Hit, int Damage, int Level, int Experience);

internal static class WoodcuttingSkill
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

    public static float HitChance(int level) =>
        Math.Clamp(.48f + (Math.Clamp(level, 1, MaximumLevel) - 1) * .026f,
            .48f, .974f);

    public static WoodcuttingStrike Roll(
        int experience, float hitRoll, float damageRoll,
        int axePower = 1)
    {
        var level = LevelForExperience(experience);
        if (hitRoll >= HitChance(level))
            return new(false, 0, level, experience);
        var toolBonus = Math.Max(0, axePower - 1) * 2;
        var minimum = 3 + level + toolBonus;
        var maximum = 7 + level * 2 + toolBonus;
        var damage = minimum + (int)MathF.Floor(
            Math.Clamp(damageRoll, 0, .999999f) * (maximum - minimum + 1));
        return new(true, damage, level, experience);
    }
}
