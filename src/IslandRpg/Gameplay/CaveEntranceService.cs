using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class CaveEntranceService
{
    public static bool IsHole(WorldGroundObject value) =>
        value.ItemId == ItemIds.CaveHole;

    public static bool IsDigSite(WorldGroundObject value) =>
        value.ItemId == ItemIds.DigSite;

    public static bool IsShallowHole(WorldGroundObject value) =>
        value.ItemId == ItemIds.ShallowHole;

    public static bool IsEntrance(WorldGroundObject value) =>
        value.ItemId == ItemIds.CaveEntrance;

    public static bool IsExcavation(WorldGroundObject value) =>
        IsDigSite(value) ||
        IsShallowHole(value) ||
        IsHole(value) ||
        IsEntrance(value);

    public static bool CanFill(WorldGroundObject value) =>
        IsShallowHole(value) || IsHole(value);

    public static float Opacity(WorldGroundObject value)
    {
        if (!IsDigSite(value) || value.MaxHealth <= 0) return 1f;
        var progress = 1f -
            Math.Clamp(value.Health / (float)value.MaxHealth, 0f, 1f);
        return .22f + progress * .78f;
    }

    public static bool CaveBelow(long seed, float x, float y) =>
        CaveHydrologyField.Density(seed, x, y) >=
        CaveHydrologyField.Boundary;

    public static WorldGroundObject InstallRope(
        WorldGroundObject hole) =>
        IsHole(hole)
            ? hole with { ItemId = ItemIds.CaveEntrance }
            : throw new InvalidOperationException(
                "Only a discovered cave hole can accept a rope.");
}
