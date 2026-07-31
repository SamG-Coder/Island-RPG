using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal sealed record FishingCatchProfile(
    WorldFishSpecies Species,
    string ItemId,
    int RequiredLevel,
    int RequiredNetPower,
    int Experience,
    int SchoolSize);

internal static class FishingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const float AnimationSpeedMultiplier = 1.18f;

    private static readonly IReadOnlyDictionary<WorldFishSpecies, FishingCatchProfile>
        Profiles = new Dictionary<WorldFishSpecies, FishingCatchProfile>
        {
            [WorldFishSpecies.ShoreMinnows] = new(
                WorldFishSpecies.ShoreMinnows, ItemIds.RawMinnows, 1, 1, 8, 5),
            [WorldFishSpecies.RiverPerch] = new(
                WorldFishSpecies.RiverPerch, ItemIds.RawRiverPerch, 1, 1, 10, 4),
            [WorldFishSpecies.SilverHerring] = new(
                WorldFishSpecies.SilverHerring, ItemIds.RawSilverHerring, 5, 2, 18, 4),
            [WorldFishSpecies.RedSnapper] = new(
                WorldFishSpecies.RedSnapper, ItemIds.RawRedSnapper, 9, 2, 30, 3),
            [WorldFishSpecies.OceanMackerel] = new(
                WorldFishSpecies.OceanMackerel, ItemIds.RawOceanMackerel, 13, 3, 48, 3),
            [WorldFishSpecies.BluefinTuna] = new(
                WorldFishSpecies.BluefinTuna, ItemIds.RawBluefinTuna, 17, 3, 75, 2)
        };

    public static IReadOnlyList<FishingCatchProfile> CatchProfiles =>
        Profiles.Values
            .OrderBy(profile => profile.RequiredLevel)
            .ThenBy(profile => profile.Species)
            .ToArray();

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static FishingCatchProfile Profile(WorldFishSpecies species) =>
        Profiles[species];

    public static bool CanCatch(WorldFishSpecies species, int level) =>
        level >= Profile(species).RequiredLevel;

    public static bool CanCatch(
        WorldFishSpecies species, int level, int netPower) =>
        CanCatch(species, level) &&
        netPower >= Profile(species).RequiredNetPower;

    public static float AnimationFrameSeconds(float authoredFrameSeconds) =>
        authoredFrameSeconds / AnimationSpeedMultiplier;

    public static float CycleSeconds(float baseSeconds, int netPower) =>
        baseSeconds / (1f + (Math.Max(1, netPower) - 1) * .18f);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, WorldFishSpecies species) =>
        SkillService.AwardExperience(
            currentExperience, Profile(species).Experience);

    public static string InventoryMessage(string itemName) =>
        $"You add {itemName} to your inventory.";

    public static string ExperienceMessage(int experience) =>
        $"+{experience} Fishing XP.";

    public static string LevelUpMessage(int level) =>
        $"Your Fishing level is now {level}.";
}
