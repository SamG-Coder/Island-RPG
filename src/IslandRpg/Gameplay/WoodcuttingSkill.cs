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
