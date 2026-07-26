using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum CampfireState
{
    Empty,
    Fueled,
    Lit
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
            LitUntilGameSeconds = 0
        };
    }

    public static bool CanLight(
        WorldGroundObject value,
        IEnumerable<string?> inventory,
        double gameSeconds) =>
        State(value, gameSeconds) == CampfireState.Fueled &&
        inventory.Any(item => item == ItemIds.SmallRocks) &&
        inventory
            .Where(item => item is not null)
            .Select(item => ItemCatalog.Get(item!))
            .Any(item => item.HasTag(ItemTag.Knife));

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
            LitUntilGameSeconds = 0
        };
    }

    public static WorldGroundObject Light(
        WorldGroundObject value, double gameSeconds)
    {
        if (State(value, gameSeconds) != CampfireState.Fueled)
            throw new InvalidOperationException(
                "The campfire must contain an unburnt log.");
        return value with
        {
            LitUntilGameSeconds =
                gameSeconds + WorldTime.GameSecondsPerDay
        };
    }

    public static WorldGroundObject Expire(
        WorldGroundObject value, double gameSeconds) =>
        State(value, gameSeconds) == CampfireState.Fueled &&
        value.LitUntilGameSeconds > 0
            ? value with
            {
                FuelItemId = null,
                LitUntilGameSeconds = 0
            }
            : value;

    public static int AnimationFrame(double realSeconds) =>
        (int)Math.Floor(
            Math.Max(0, realSeconds) * AnimationFramesPerSecond) %
        AnimationFrameCount;
}
