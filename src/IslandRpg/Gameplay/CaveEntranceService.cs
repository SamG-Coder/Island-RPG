using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class CaveEntranceService
{
    private const int ProspectRadius = 32;

    public static bool IsHole(WorldGroundObject value) =>
        value.ItemId == ItemIds.CaveHole;

    public static bool IsDigSite(WorldGroundObject value) =>
        value.ItemId == ItemIds.DigSite;

    public static bool IsShallowHole(WorldGroundObject value) =>
        value.ItemId == ItemIds.ShallowHole;

    public static bool IsEntrance(WorldGroundObject value) =>
        value.ItemId == ItemIds.CaveEntrance;

    public static bool IsCaveShaft(WorldGroundObject value) =>
        IsHole(value) || IsEntrance(value);

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

    /// <summary>
    /// Reads the local cave field after soil has been exposed. This is kept
    /// separate from world discovery so prospecting reveals only a bearing,
    /// never a hidden map or a persistent cave marker.
    /// </summary>
    public static bool TryProspect(
        long seed, float x, float y, out CaveProspect prospect)
    {
        var sampling = new CaveHydrologyField.SamplingContext(seed);
        var bestDistanceSquared = float.MaxValue;
        var bestX = 0f;
        var bestY = 0f;
        for (var offsetY = -ProspectRadius;
             offsetY <= ProspectRadius;
             offsetY++)
        for (var offsetX = -ProspectRadius;
             offsetX <= ProspectRadius;
             offsetX++)
        {
            if (offsetX == 0 && offsetY == 0) continue;
            var distanceSquared =
                offsetX * offsetX + offsetY * offsetY;
            if (distanceSquared > ProspectRadius * ProspectRadius ||
                distanceSquared >= bestDistanceSquared)
                continue;
            var sampleX = x + offsetX;
            var sampleY = y + offsetY;
            if (sampling.Density(sampleX, sampleY) <
                CaveHydrologyField.Boundary)
                continue;
            bestDistanceSquared = distanceSquared;
            bestX = sampleX;
            bestY = sampleY;
        }

        if (bestDistanceSquared == float.MaxValue)
        {
            prospect = default;
            return false;
        }

        prospect = new(
            bestX,
            bestY,
            MathF.Sqrt(bestDistanceSquared),
            CompassDirection(bestX - x, bestY - y));
        return true;
    }

    public static string ProspectMessage(CaveProspect prospect)
    {
        var range = prospect.Distance switch
        {
            <= 8 => "very near",
            <= 18 => "a short walk away",
            _ => "some distance away"
        };
        return $"Cool air seeps through the exposed soil. " +
               $"Hollower ground lies {range} to the {prospect.Direction}.";
    }

    public static WorldGroundObject InstallRope(
        WorldGroundObject hole) =>
        IsHole(hole)
            ? hole with { ItemId = ItemIds.CaveEntrance }
            : throw new InvalidOperationException(
                "Only a discovered cave hole can accept a rope.");

    private static string CompassDirection(float x, float y)
    {
        var horizontal = x switch
        {
            < -.5f => "west",
            > .5f => "east",
            _ => ""
        };
        var vertical = y switch
        {
            < -.5f => "north",
            > .5f => "south",
            _ => ""
        };
        return (vertical, horizontal) switch
        {
            ("", "") => "nearby",
            ("", _) => horizontal,
            (_, "") => vertical,
            _ => $"{vertical}-{horizontal}"
        };
    }
}

internal readonly record struct CaveProspect(
    float X,
    float Y,
    float Distance,
    string Direction);
