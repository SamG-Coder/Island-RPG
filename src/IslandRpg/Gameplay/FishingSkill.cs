using IslandRpg.Fishing;
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
        Profiles = FishingRules.CatchProfiles.ToDictionary(
            profile => (WorldFishSpecies)profile.Species,
            profile => new FishingCatchProfile(
                (WorldFishSpecies)profile.Species,
                profile.ItemId,
                profile.RequiredLevel,
                profile.RequiredNetPower,
                profile.Experience,
                profile.SchoolSize));

    public static IReadOnlyList<FishingCatchProfile> CatchProfiles =>
        Profiles.Values
            .OrderBy(profile => profile.RequiredLevel)
            .ThenBy(profile => profile.Species)
            .ToArray();

    public static int LevelForExperience(int experience) =>
        FishingRules.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        FishingRules.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        FishingRules.ExperienceToNextLevel(experience);

    public static FishingCatchProfile Profile(WorldFishSpecies species) =>
        Profiles[species];

    public static bool CanCatch(WorldFishSpecies species, int level) =>
        level >= Profile(species).RequiredLevel;

    public static bool CanCatch(
        WorldFishSpecies species, int level, int netPower) =>
        FishingRules.CanCatch((FishSpecies)species, level, netPower);

    public static float AnimationFrameSeconds(float authoredFrameSeconds) =>
        FishingRules.AnimationFrameSeconds(authoredFrameSeconds);

    public static float CycleSeconds(float baseSeconds, int netPower) =>
        FishingRules.CycleSeconds(baseSeconds, netPower);

    public static float CatchChance(
        WorldFishSpecies species, int level, int netPower)
        => FishingRules.CatchChance((FishSpecies)species, level, netPower);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, WorldFishSpecies species)
    {
        var award = FishingRules.AwardExperience(
            currentExperience, (FishSpecies)species);
        return new(
            award.Experience,
            award.Gained,
            award.PreviousLevel,
            award.Level);
    }

    public static string InventoryMessage(string itemName) =>
        $"You add {itemName} to your inventory.";

    public static string ExperienceMessage(int experience) =>
        $"+{experience} Fishing XP.";

    public static string LevelUpMessage(int level) =>
        $"Your Fishing level is now {level}.";
}
