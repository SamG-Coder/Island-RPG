using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum FishingTargetFailure : byte
{
    None,
    FishNotFound,
    FishingNetNotFound,
    FishingLevelRequired,
    FishingNetTooWeak
}

internal readonly record struct FishingTargetResult(
    WorldFish? Fish,
    FishingTargetFailure Failure,
    FishingCatchProfile? Requirement = null)
{
    public bool Success => Fish is not null;
}

/// <summary>
/// Selects only fish an actor can catch. Callers may provide an exact key;
/// otherwise the nearest eligible candidate wins.
/// </summary>
internal static class FishingTargetSelection
{
    public static FishingTargetResult Select(
        IEnumerable<WorldFish> candidates,
        string? stableKey,
        int fishingLevel,
        int? netPower)
    {
        if (netPower is null)
            return new(null, FishingTargetFailure.FishingNetNotFound);

        var available = candidates.ToArray();
        if (!string.IsNullOrWhiteSpace(stableKey))
        {
            var requested = available.FirstOrDefault(value =>
                value.StableKey.Equals(stableKey, StringComparison.Ordinal));
            return requested is null
                ? new(null, FishingTargetFailure.FishNotFound)
                : Validate(requested, fishingLevel, netPower.Value);
        }

        FishingTargetResult? firstRejected = null;
        foreach (var candidate in available)
        {
            var result = Validate(candidate, fishingLevel, netPower.Value);
            if (result.Success) return result;
            firstRejected ??= result;
        }
        return firstRejected ??
               new(null, FishingTargetFailure.FishNotFound);
    }

    public static FishingTargetResult Validate(
        WorldFish fish, int fishingLevel, int netPower)
    {
        var profile = FishingSkill.Profile(fish.Species);
        if (fishingLevel < profile.RequiredLevel)
            return new(null, FishingTargetFailure.FishingLevelRequired, profile);
        if (netPower < profile.RequiredNetPower)
            return new(null, FishingTargetFailure.FishingNetTooWeak, profile);
        return new(fish, FishingTargetFailure.None, profile);
    }
}
