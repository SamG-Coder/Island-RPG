using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal readonly record struct DiggingTerrain(
    int Health, string RewardItemId);

internal static class DiggingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;

    public static DiggingTerrain Terrain(Biome biome) => biome switch
    {
        Biome.Beach or Biome.DesertSand =>
            new(30, ItemIds.Sand),
        Biome.Mud or Biome.Grassland or Biome.DryGrass =>
            new(50, ItemIds.Dirt),
        Biome.Rock or Biome.Highland =>
            new(100, ItemIds.Dirt),
        _ => new(70, ItemIds.Dirt)
    };

    public static int Damage(int experience, int shovelPower = 1) =>
        8 + LevelForExperience(experience) / 4 +
        (Math.Max(1, shovelPower) - 1) * 4;

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
