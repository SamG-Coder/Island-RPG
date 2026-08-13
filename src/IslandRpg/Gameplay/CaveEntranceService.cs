using IslandRpg.Caves;
using IslandRpg.World;
using NumericsVector2 = System.Numerics.Vector2;

namespace IslandRpg.Gameplay;

internal static class CaveEntranceService
{
    public static bool IsHole(WorldGroundObject value) =>
        Kind(value) == ExcavationKind.OpenShaft;

    public static bool IsDigSite(WorldGroundObject value) =>
        Kind(value) == ExcavationKind.DigSite;

    public static bool IsShallowHole(WorldGroundObject value) =>
        Kind(value) == ExcavationKind.ShallowHole;

    public static bool IsEntrance(WorldGroundObject value) =>
        Kind(value) == ExcavationKind.RopedEntrance;

    public static bool IsCaveShaft(WorldGroundObject value) =>
        IsHole(value) || IsEntrance(value);

    public static bool IsExcavation(WorldGroundObject value) =>
        IsDigSite(value) ||
        IsShallowHole(value) ||
        IsHole(value) ||
        IsEntrance(value);

    public static bool CanFill(WorldGroundObject value) =>
        CaveExcavationRules.CanFill(State(value));

    public static float Opacity(WorldGroundObject value)
    {
        return CaveExcavationRules.Opacity(State(value));
    }

    public static bool CaveBelow(long seed, float x, float y) =>
        new ProceduralCaveExcavationEnvironment(seed).IsCaveBelow(
            new(x, y));

    /// <summary>
    /// Reads the local cave field after soil has been exposed. This is kept
    /// separate from world discovery so prospecting reveals only a bearing,
    /// never a hidden map or a persistent cave marker.
    /// </summary>
    public static bool TryProspect(
        long seed, float x, float y, out CaveProspect prospect)
    {
        var environment = new ProceduralCaveExcavationEnvironment(seed);
        if (!environment.TryProspect(
                new NumericsVector2(x, y), out var found))
        {
            prospect = default;
            return false;
        }
        prospect = new(
            found.Position.X,
            found.Position.Y,
            found.Distance,
            CaveExcavationRules.DirectionName(found.Direction));
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
        WorldGroundObject hole)
    {
        if (!CaveExcavationRules.TryInstallRope(
                State(hole), out var entrance))
            throw new InvalidOperationException(
                "Only a discovered cave hole can accept a rope.");
        return hole with
        {
            ItemId = CaveExcavationRules.ItemIdForKind(entrance.Kind)
        };
    }

    private static ExcavationKind Kind(WorldGroundObject value) =>
        CaveExcavationRules.KindForItemId(value.ItemId);

    private static CaveExcavationState State(WorldGroundObject value) =>
        new(
            value.Id,
            Kind(value),
            new NumericsVector2(value.X, value.Y),
            value.Health,
            Math.Max(1, value.MaxHealth));
}

internal readonly record struct CaveProspect(
    float X,
    float Y,
    float Distance,
    string Direction);
