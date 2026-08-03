using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum CampfireState
{
    Empty,
    Fueled,
    Lit
}

internal enum CampfireLightFailure
{
    None,
    NotFueled,
    SmallRocksMissing,
    KnifeMissing
}

internal static class CampfireService
{
    public const int AnimationFrameCount = 16;
    public const double AnimationFramesPerSecond = 10;

    public static bool IsCampfire(WorldGroundObject value) =>
        string.Equals(
            value.ItemId, ItemIds.Campfire,
            StringComparison.OrdinalIgnoreCase);

    public static CampfireState State(
        WorldGroundObject value, double gameSeconds)
    {
        if (!IsCampfire(value) ||
            string.IsNullOrWhiteSpace(value.FuelItemId))
            return CampfireState.Empty;
        return value.LitUntilGameSeconds > gameSeconds
            ? CampfireState.Lit
            : CampfireState.Fueled;
    }

    public static bool CanAddFuel(
        WorldGroundObject value, string itemId, double gameSeconds) =>
        IsCampfire(value) &&
        ItemCatalog.Get(itemId).HasTag(ItemTag.Log) &&
        State(value, gameSeconds) == CampfireState.Empty;

    public static WorldGroundObject AddFuel(
        WorldGroundObject value, string itemId, double gameSeconds)
    {
        if (!CanAddFuel(value, itemId, gameSeconds))
            throw new InvalidOperationException(
                "The campfire cannot accept that fuel.");
        return value with
        {
            FuelItemId = itemId,
            LitUntilGameSeconds = 0,
            FiremakingLevel = 1
        };
    }

    public static bool CanLight(
        WorldGroundObject value,
        IEnumerable<string?> inventory,
        double gameSeconds) =>
        LightFailure(value, inventory, gameSeconds) ==
        CampfireLightFailure.None;

    public static CampfireLightFailure LightFailure(
        WorldGroundObject value,
        IEnumerable<string?> inventory,
        double gameSeconds)
    {
        if (State(value, gameSeconds) != CampfireState.Fueled)
            return CampfireLightFailure.NotFueled;
        var hasSmallRocks = false;
        var hasKnife = false;
        foreach (var itemId in inventory)
        {
            if (itemId == ItemIds.SmallRocks) hasSmallRocks = true;
            if (itemId is not null &&
                ItemCatalog.Get(itemId).HasTag(ItemTag.Knife))
                hasKnife = true;
        }
        if (!hasSmallRocks)
            return CampfireLightFailure.SmallRocksMissing;
        return hasKnife
            ? CampfireLightFailure.None
            : CampfireLightFailure.KnifeMissing;
    }

    public static string LightFailureCode(CampfireLightFailure failure) =>
        failure switch
        {
            CampfireLightFailure.NotFueled => "campfire_not_fueled",
            CampfireLightFailure.SmallRocksMissing =>
                "campfire_small_rocks_missing",
            CampfireLightFailure.KnifeMissing => "campfire_knife_missing",
            _ => "campfire_ready"
        };

    public static string LightFailureMessage(CampfireLightFailure failure) =>
        failure switch
        {
            CampfireLightFailure.NotFueled =>
                "Add a log to the campfire before lighting it.",
            CampfireLightFailure.SmallRocksMissing =>
                "You need small rocks to strike a spark.",
            CampfireLightFailure.KnifeMissing =>
                "You need a knife to strike against the small rocks.",
            _ => "The campfire is ready to light."
        };

    public static bool CanRemoveFuel(
        WorldGroundObject value, double gameSeconds) =>
        State(value, gameSeconds) == CampfireState.Fueled;

    public static WorldGroundObject RemoveFuel(
        WorldGroundObject value, double gameSeconds)
    {
        if (!CanRemoveFuel(value, gameSeconds))
            throw new InvalidOperationException(
                "The campfire has no removable fuel.");
        return value with
        {
            FuelItemId = null,
            LitUntilGameSeconds = 0,
            FiremakingLevel = 1
        };
    }

    public static WorldGroundObject Light(
        WorldGroundObject value,
        double gameSeconds,
        int firemakingLevel = 1)
    {
        if (State(value, gameSeconds) != CampfireState.Fueled)
            throw new InvalidOperationException(
                "The campfire must contain an unburnt log.");
        return value with
        {
            LitUntilGameSeconds =
                gameSeconds +
                FiremakingSkill.DurationGameSeconds(firemakingLevel),
            FiremakingLevel = Math.Clamp(
                firemakingLevel, 1, FiremakingSkill.MaximumLevel)
        };
    }

    public static WorldGroundObject Expire(
        WorldGroundObject value, double gameSeconds) =>
        State(value, gameSeconds) == CampfireState.Fueled &&
        value.LitUntilGameSeconds > 0
            ? value with
            {
                FuelItemId = null,
                LitUntilGameSeconds = 0,
                FiremakingLevel = 1
            }
            : value;

    public static int AnimationFrame(double realSeconds) =>
        (int)Math.Floor(
            Math.Max(0, realSeconds) * AnimationFramesPerSecond) %
        AnimationFrameCount;
}
