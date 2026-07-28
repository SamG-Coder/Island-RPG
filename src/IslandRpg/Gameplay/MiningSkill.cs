namespace IslandRpg.Gameplay;

internal readonly record struct MiningStrike(
    bool Hit, int Damage, int Level);

internal static class MiningSkill
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
        Math.Clamp(.52f + (Math.Clamp(level, 1, MaximumLevel) - 1) * .024f,
            .52f, .976f);

    public static MiningStrike Roll(
        int experience, float hitRoll, float damageRoll, int pickaxePower)
    {
        var level = LevelForExperience(experience);
        if (hitRoll >= HitChance(level))
            return new(false, 0, level);
        var power = Math.Max(1, pickaxePower);
        var minimum = 3 + level + (power - 1) * 2;
        var maximum = 7 + level * 2 + (power - 1) * 3;
        var damage = minimum + (int)MathF.Floor(
            Math.Clamp(damageRoll, 0, .999999f) *
            (maximum - minimum + 1));
        return new(true, damage, level);
    }
}
