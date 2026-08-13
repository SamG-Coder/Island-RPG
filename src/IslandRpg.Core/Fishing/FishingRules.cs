using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Fishing;

/// <summary>
/// Stable numeric species identifiers. These values are persisted as the
/// variant of a procedural fish-school resource and must not be reordered.
/// </summary>
public enum FishSpecies : byte
{
    ShoreMinnows = 0,
    RiverPerch = 1,
    SilverHerring = 2,
    BluefinTuna = 3,
    RedSnapper = 4,
    OceanMackerel = 5
}

public sealed record FishSpeciesProfile(
    FishSpecies Species,
    string ItemId,
    string DisplayName,
    string GraphicName,
    int FrameCount,
    string Rarity,
    string Habitat,
    int RequiredLevel,
    int RequiredNetPower,
    int Experience,
    int SchoolSize);

/// <summary>
/// Complete deterministic description of one fish school. The resource ID is
/// the network/persistence identity; StableKey exists only for the legacy solo
/// chunk adapter.
/// </summary>
public sealed record FishSchoolDescriptor(
    ResourceNodeId Id,
    WorldChunkKey Chunk,
    Vector2 Position,
    FishSpecies Species,
    int AnimationOffset,
    string ItemId,
    int RequiredLevel,
    int RequiredNetPower,
    int Experience,
    int SchoolSize,
    double RegrowthGameSeconds)
{
    public string StableKey => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"fish:{(int)MathF.Floor(Position.X)}:" +
        $"{(int)MathF.Floor(Position.Y)}:{(int)Species}");
}

public enum FishingTargetFailure : byte
{
    None = 0,
    FishNotFound = 1,
    FishingNetNotFound = 2,
    FishingLevelRequired = 3,
    FishingNetTooWeak = 4,
    FishDepleted = 5,
    FishNotReachable = 6
}

public readonly record struct FishingTargetCandidate(
    ResourceNodeId Id,
    Vector2 Position,
    FishSpecies Species,
    bool Depleted = false);

public readonly record struct FishingTargetSelectionResult(
    FishingTargetCandidate? Target,
    FishingTargetFailure Failure,
    FishSpeciesProfile? Requirement = null)
{
    public bool Success => Target is not null;
}

public readonly record struct FishingExperienceAward(
    int Experience,
    int Gained,
    int PreviousLevel,
    int Level)
{
    public bool LevelledUp => Level > PreviousLevel;
}

/// <summary>
/// Canonical equipment, progression and target-selection policy shared by
/// solo play and the multiplayer authority.
/// </summary>
public static class FishingRules
{
    public const int MaximumLevel = SkillService.MaximumLevel;
    public const float AnimationSpeedMultiplier = 1.18f;

    private static readonly FishSpeciesProfile[] Profiles =
    [
        new(
            FishSpecies.ShoreMinnows, ItemIds.RawMinnows,
            "shore minnows", "FISHS_NN", 34, "Common",
            "Sheltered shallows and mangrove edges",
            1, 1, 8, 5),
        new(
            FishSpecies.RiverPerch, ItemIds.RawRiverPerch,
            "river perch", "FISH1_NN", 49, "Common",
            "Freshwater rivers and wetlands",
            1, 1, 10, 4),
        new(
            FishSpecies.SilverHerring, ItemIds.RawSilverHerring,
            "silver herring", "FISH2_NN", 49, "Common",
            "Coastal sea and open ocean",
            5, 2, 18, 4),
        new(
            FishSpecies.BluefinTuna, ItemIds.RawBluefinTuna,
            "bluefin tuna", "FISH3_NN", 49, "Rare",
            "Deep open ocean",
            17, 3, 75, 2),
        new(
            FishSpecies.RedSnapper, ItemIds.RawRedSnapper,
            "red snapper", "FISH4_NN", 49, "Uncommon",
            "Warm coastal shallows and mangroves",
            9, 2, 30, 3),
        new(
            FishSpecies.OceanMackerel, ItemIds.RawOceanMackerel,
            "ocean mackerel", "FISHX_NN", 30, "Uncommon",
            "Deep open ocean",
            13, 3, 48, 3)
    ];

    public static IReadOnlyList<FishSpeciesProfile> CatchProfiles => Profiles;

    public static FishSpeciesProfile Profile(FishSpecies species)
    {
        var index = (int)species;
        if ((uint)index >= (uint)Profiles.Length ||
            Profiles[index].Species != species)
        {
            // Red snapper and tuna retain their legacy numeric order, while
            // progression order differs. Fall back to an explicit lookup.
            return Profiles.FirstOrDefault(value => value.Species == species)
                   ?? throw new ArgumentOutOfRangeException(nameof(species));
        }
        return Profiles[index];
    }

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static FishingExperienceAward AwardExperience(
        int currentExperience,
        FishSpecies species)
    {
        var award = SkillService.AwardExperience(
            currentExperience, Profile(species).Experience);
        return new(
            award.Experience,
            award.Gained,
            award.PreviousLevel,
            award.Level);
    }

    public static bool CanCatch(
        FishSpecies species,
        int level,
        int netPower) =>
        level >= Profile(species).RequiredLevel &&
        netPower >= Profile(species).RequiredNetPower;

    public static float AnimationFrameSeconds(float authoredFrameSeconds) =>
        authoredFrameSeconds / AnimationSpeedMultiplier;

    public static float CycleSeconds(float baseSeconds, int netPower) =>
        baseSeconds / (1f + (Math.Max(1, netPower) - 1) * .18f);

    public static float CatchChance(
        FishSpecies species,
        int level,
        int netPower)
    {
        var profile = Profile(species);
        var levelBonus = Math.Max(0, level - profile.RequiredLevel) * .015f;
        var netBonus = Math.Max(0, netPower - profile.RequiredNetPower) * .08f;
        return Math.Clamp(.72f + levelBonus + netBonus, .72f, .95f);
    }

    /// <summary>
    /// Selects an exact authority-derived resource ID or the nearest eligible
    /// school. Candidate enumeration order never affects the result.
    /// </summary>
    public static FishingTargetSelectionResult SelectTarget(
        IEnumerable<FishingTargetCandidate> candidates,
        ResourceNodeId? exactId,
        Vector2 actorPosition,
        int fishingLevel,
        int? netPower,
        float maximumReach = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (netPower is null)
            return new(null, FishingTargetFailure.FishingNetNotFound);
        if (!IsFinite(actorPosition) ||
            float.IsNaN(maximumReach) || maximumReach < 0)
            return new(null, FishingTargetFailure.FishNotFound);

        var values = candidates
            .Where(value => !value.Id.IsEmpty && IsFinite(value.Position))
            .ToArray();
        if (exactId is { } requested && !requested.IsEmpty)
        {
            var exact = values.FirstOrDefault(value => value.Id == requested);
            if (exact.Id.IsEmpty)
                return new(null, FishingTargetFailure.FishNotFound);
            return ValidateTarget(
                exact, actorPosition, fishingLevel, netPower.Value,
                maximumReach);
        }

        FishingTargetSelectionResult? firstRejected = null;
        foreach (var candidate in values
                     .OrderBy(value => Vector2.DistanceSquared(
                         actorPosition, value.Position))
                     .ThenBy(value => value.Id.Value))
        {
            var result = ValidateTarget(
                candidate, actorPosition, fishingLevel, netPower.Value,
                maximumReach);
            if (result.Success) return result;
            firstRejected ??= result;
        }
        return firstRejected ??
               new(null, FishingTargetFailure.FishNotFound);
    }

    public static FishingTargetSelectionResult ValidateTarget(
        FishingTargetCandidate candidate,
        Vector2 actorPosition,
        int fishingLevel,
        int netPower,
        float maximumReach = float.PositiveInfinity)
    {
        if (candidate.Id.IsEmpty || !IsFinite(candidate.Position) ||
            !IsFinite(actorPosition) || float.IsNaN(maximumReach) ||
            maximumReach < 0)
            return new(null, FishingTargetFailure.FishNotFound);

        var profile = Profile(candidate.Species);
        if (candidate.Depleted)
            return new(null, FishingTargetFailure.FishDepleted, profile);
        if (!float.IsPositiveInfinity(maximumReach) &&
            Vector2.DistanceSquared(actorPosition, candidate.Position) >
            maximumReach * maximumReach)
            return new(null, FishingTargetFailure.FishNotReachable, profile);
        if (fishingLevel < profile.RequiredLevel)
            return new(null, FishingTargetFailure.FishingLevelRequired, profile);
        if (netPower < profile.RequiredNetPower)
            return new(null, FishingTargetFailure.FishingNetTooWeak, profile);
        return new(candidate, FishingTargetFailure.None, profile);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
