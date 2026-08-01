namespace IslandRpg.Gameplay;

internal readonly record struct WoodcuttingStrike(
    bool Hit, int Damage, int Level, int Experience);

internal static class WoodcuttingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, int amount) =>
        SkillService.AwardExperience(currentExperience, amount);

    public static float HitChance(int level) =>
        Math.Clamp(.48f + (Math.Clamp(level, 1, MaximumLevel) - 1) * .026f,
            .48f, .974f);

    public static float SwingLogChance(int level)
    {
        var progress = (Math.Clamp(level, 1, MaximumLevel) - 1f) /
                       (MaximumLevel - 1f);
        return .05f + progress * .20f;
    }

    public static bool GrantsSwingLog(int level, float roll) =>
        Math.Clamp(roll, 0, .999999f) < SwingLogChance(level);

    public static int MinimumDamage(int level, int axePower = 1) =>
        3 + Math.Clamp(level, 1, MaximumLevel) +
        Math.Max(0, axePower - 1) * 2;

    public static int MaximumDamage(int level, int axePower = 1) =>
        7 + Math.Clamp(level, 1, MaximumLevel) * 2 +
        Math.Max(0, axePower - 1) * 2;

    public static WoodcuttingStrike Roll(
        int experience, float hitRoll, float damageRoll,
        int axePower = 1)
    {
        var level = LevelForExperience(experience);
        if (hitRoll >= HitChance(level))
            return new(false, 0, level, experience);
        var minimum = MinimumDamage(level, axePower);
        var maximum = MaximumDamage(level, axePower);
        var damage = minimum + (int)MathF.Floor(
            Math.Clamp(damageRoll, 0, .999999f) * (maximum - minimum + 1));
        return new(true, damage, level, experience);
    }
}
