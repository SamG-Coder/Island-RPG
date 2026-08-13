using IslandRpg.Caves;
using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal readonly record struct DiggingTerrain(
    int Health, string RewardItemId);

internal static class DiggingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;

    public static DiggingTerrain Terrain(Biome biome)
    {
        var terrain = CaveExcavationRules.Terrain(biome switch
        {
            Biome.Beach or Biome.DesertSand =>
                ExcavationTerrainKind.Sand,
            Biome.Mud or Biome.Grassland or Biome.DryGrass =>
                ExcavationTerrainKind.Soil,
            Biome.Rock or Biome.Highland =>
                ExcavationTerrainKind.Rock,
            _ => ExcavationTerrainKind.Other
        });
        return new(terrain.MaximumHealth, terrain.RewardItemId);
    }

    public static int Damage(int experience, int shovelPower = 1) =>
        CaveExcavationRules.DiggingDamage(experience, shovelPower);

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
