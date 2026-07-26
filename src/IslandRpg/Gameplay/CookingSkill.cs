namespace IslandRpg.Gameplay;

internal sealed record CookingProfile(
    string RawItemId,
    string CookedItemId,
    string BurntItemId,
    int RequiredLevel,
    int Experience,
    float BaseBurnChance);

internal readonly record struct CookingResult(
    string ItemId,
    bool Burnt,
    int Experience);

internal static class CookingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const float PlacementAnimationSeconds = .75f;
    public const double CookingSeconds = 2.6;

    private static readonly IReadOnlyDictionary<string, CookingProfile>
        Profiles = new Dictionary<string, CookingProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.RawMinnows] = new(
                ItemIds.RawMinnows,
                ItemIds.CookedMinnows,
                ItemIds.BurntMinnows,
                1, 10, .22f),
            [ItemIds.RawRiverPerch] = new(
                ItemIds.RawRiverPerch,
                ItemIds.CookedRiverPerch,
                ItemIds.BurntRiverPerch,
                1, 14, .30f),
            [ItemIds.RawSilverHerring] = new(
                ItemIds.RawSilverHerring,
                ItemIds.CookedSilverHerring,
                ItemIds.BurntSilverHerring,
                5, 24, .34f),
            [ItemIds.RawRedSnapper] = new(
                ItemIds.RawRedSnapper,
                ItemIds.CookedRedSnapper,
                ItemIds.BurntRedSnapper,
                9, 38, .36f),
            [ItemIds.RawOceanMackerel] = new(
                ItemIds.RawOceanMackerel,
                ItemIds.CookedOceanMackerel,
                ItemIds.BurntOceanMackerel,
                13, 58, .38f),
            [ItemIds.RawBluefinTuna] = new(
                ItemIds.RawBluefinTuna,
                ItemIds.CookedBluefinTuna,
                ItemIds.BurntBluefinTuna,
                17, 90, .40f)
        };

    public static IReadOnlyList<CookingProfile> CookProfiles =>
        Profiles.Values
            .OrderBy(profile => profile.RequiredLevel)
            .ThenBy(profile => profile.RawItemId)
            .ToArray();

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static bool TryProfile(
        string itemId, out CookingProfile profile) =>
        Profiles.TryGetValue(itemId, out profile!);

    public static bool CanCook(string itemId, int level) =>
        TryProfile(itemId, out var profile) &&
        level >= profile.RequiredLevel;

    public static float BurnChance(string itemId, int level)
    {
        if (!TryProfile(itemId, out var profile)) return 1;
        var levelsAbove = Math.Max(0, level - profile.RequiredLevel);
        return Math.Clamp(
            profile.BaseBurnChance - levelsAbove * .028f,
            .02f,
            profile.BaseBurnChance);
    }

    public static CookingResult Roll(
        string itemId, int level, float roll)
    {
        var profile = Profiles[itemId];
        var burnt = roll < BurnChance(itemId, level);
        return new(
            burnt ? profile.BurntItemId : profile.CookedItemId,
            burnt,
            burnt ? 0 : profile.Experience);
    }

    public static SkillExperienceChange AwardExperience(
        int currentExperience, int amount) =>
        SkillService.AwardExperience(currentExperience, amount);

    public static string ExperienceMessage(int experience) =>
        $"+{experience} Cooking XP.";

    public static string LevelUpMessage(int level) =>
        $"Your Cooking level is now {level}.";
}
