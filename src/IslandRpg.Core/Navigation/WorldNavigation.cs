using System.Numerics;

namespace IslandRpg.Navigation;

/// <summary>
/// The two deterministic terrain layers understood by shared navigation.
/// Values deliberately match the persisted/client world-level identifiers.
/// </summary>
public enum NavigationWorldLevel
{
    Underground = -1,
    Overworld = 0
}

/// <summary>
/// Headless terrain contract consumed by route finding and authoritative
/// movement. Implementations may sample procedural terrain or test fixtures;
/// the pathfinder never loads chunks or touches rendering state.
/// </summary>
public interface IWorldNavigationQuery
{
    bool SupportsWorldLevel(int worldLevel);

    bool CanStandAt(Vector2 point, int worldLevel);

    float HeightAt(Vector2 point, int worldLevel);

    bool IsWading(Vector2 point, int worldLevel);
}

public interface IWorldNavigationObstacleSource
{
    IReadOnlyList<NavigationObstacle> GetObstacles(int worldLevel);
}

public sealed class EmptyWorldNavigationObstacleSource :
    IWorldNavigationObstacleSource
{
    public static EmptyWorldNavigationObstacleSource Instance { get; } = new();

    private EmptyWorldNavigationObstacleSource()
    {
    }

    public IReadOnlyList<NavigationObstacle> GetObstacles(int worldLevel) => [];
}

/// <summary>
/// Unbounded deterministic query used by isolated simulation tests and hosts
/// which have not supplied a procedural world. Production servers inject a
/// seed-backed query instead.
/// </summary>
public sealed class OpenWorldNavigationQuery : IWorldNavigationQuery
{
    public static OpenWorldNavigationQuery Instance { get; } = new();

    private OpenWorldNavigationQuery()
    {
    }

    public bool SupportsWorldLevel(int worldLevel) => true;

    public bool CanStandAt(Vector2 point, int worldLevel) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    public float HeightAt(Vector2 point, int worldLevel) => 0;

    public bool IsWading(Vector2 point, int worldLevel) => false;
}

public readonly record struct NavigationObstacle(
    Vector2 Center,
    float Width,
    float Depth,
    float RotationRadians = 0)
{
    public bool Contains(Vector2 point, float clearance = .18f)
    {
        var relative = point - Center;
        if (MathF.Abs(RotationRadians) > .0001f)
        {
            var cosine = MathF.Cos(RotationRadians);
            var sine = MathF.Sin(RotationRadians);
            relative = new Vector2(
                relative.X * cosine + relative.Y * sine,
                -relative.X * sine + relative.Y * cosine);
        }

        return MathF.Abs(relative.X) < Width * .5f + clearance &&
               MathF.Abs(relative.Y) < Depth * .5f + clearance;
    }

    public Vector2 AxisAlignedHalfExtents(float clearance = 0)
    {
        var halfWidth = Width * .5f + clearance;
        var halfDepth = Depth * .5f + clearance;
        if (MathF.Abs(RotationRadians) <= .0001f)
            return new Vector2(halfWidth, halfDepth);

        var cosine = MathF.Abs(MathF.Cos(RotationRadians));
        var sine = MathF.Abs(MathF.Sin(RotationRadians));
        return new Vector2(
            halfWidth * cosine + halfDepth * sine,
            halfWidth * sine + halfDepth * cosine);
    }
}

public static class WorldPlacementGrid
{
    public const float CellSize = .25f;
    public const int CellsPerTerrainTile = 4;

    public static int Cell(float coordinate) =>
        (int)MathF.Floor(coordinate / CellSize);

    public static float CellCenter(int cell) =>
        (cell + .5f) * CellSize;

    public static Vector2 CellCenter(int x, int y) =>
        new(CellCenter(x), CellCenter(y));

    public static float Snap(float coordinate) =>
        MathF.Round(coordinate / CellSize) * CellSize;

    public static float SnapWithFootprint(float coordinate, float footprint)
    {
        var half = footprint * .5f;
        return MathF.Round((coordinate - half) / CellSize) * CellSize + half;
    }
}

public static class ActorMovementService
{
    public const float BaseMoveSpeed = 2.8f;

    public static float TerrainSpeedMultiplier(
        bool wading,
        float currentHeight,
        float targetHeight)
    {
        var uphill = Math.Max(0, targetHeight - currentHeight);
        return (wading ? .62f : 1f) /
               (1f + uphill * .18f);
    }
}
