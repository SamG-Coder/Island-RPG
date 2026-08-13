using IslandRpg.World;
using OpenTK.Mathematics;
using CoreNavigation = IslandRpg.Navigation;
using NumericsVector2 = System.Numerics.Vector2;

namespace IslandRpg.Gameplay;

/// <summary>
/// OpenTK compatibility value used by existing client call sites. Core
/// navigation owns the actual geometry and path search.
/// </summary>
internal readonly record struct NavigationObstacle(
    Vector2 Center,
    float Width,
    float Depth,
    float RotationRadians = 0)
{
    public bool Contains(Vector2 point, float clearance = .18f) =>
        ToCore().Contains(ToNumerics(point), clearance);

    public Vector2 AxisAlignedHalfExtents(float clearance = 0)
    {
        var result = ToCore().AxisAlignedHalfExtents(clearance);
        return new Vector2(result.X, result.Y);
    }

    internal CoreNavigation.NavigationObstacle ToCore() => new(
        ToNumerics(Center),
        Width,
        Depth,
        RotationRadians);

    private static NumericsVector2 ToNumerics(Vector2 value) =>
        new(value.X, value.Y);
}

internal static class ActionPathSearchPolicy
{
    public const int MaximumVisited =
        CoreNavigation.ActionPathSearchPolicy.MaximumVisited;
    public const float AlternativeApproachDistance =
        CoreNavigation.ActionPathSearchPolicy.AlternativeApproachDistance;

    public static bool ShouldTryAlternativeApproach(
        in Vector2 start,
        in Vector2 target) =>
        CoreNavigation.ActionPathSearchPolicy.ShouldTryAlternativeApproach(
            new NumericsVector2(start.X, start.Y),
            new NumericsVector2(target.X, target.Y));
}

/// <summary>
/// Preserves the established OpenTK API while delegating deterministic path
/// search to the headless Core implementation shared with the server.
/// </summary>
internal static class GridPathfinder
{
    public static IReadOnlyList<Vector2> Find(
        long seed,
        Vector2 startPosition,
        Vector2 requestedTarget,
        int maximumVisited = 65_536,
        CancellationToken cancellationToken = default,
        int worldLevel = (int)WorldLevel.Overworld,
        IReadOnlyList<NavigationObstacle>? obstacles = null)
    {
        var coreObstacles = obstacles?.Select(static obstacle =>
            obstacle.ToCore()).ToArray();
        return CoreNavigation.GridPathfinder.Find(
                new SoloWorldNavigationQuery(seed),
                ToNumerics(startPosition),
                ToNumerics(requestedTarget),
                maximumVisited,
                cancellationToken,
                worldLevel,
                coreObstacles)
            .Select(static point => new Vector2(point.X, point.Y))
            .ToArray();
    }

    public static bool CanStandAt(
        long seed,
        Vector2 point,
        int worldLevel,
        IReadOnlyList<NavigationObstacle>? obstacles = null) =>
        CoreNavigation.GridPathfinder.CanStandAt(
            new SoloWorldNavigationQuery(seed),
            ToNumerics(point),
            worldLevel,
            obstacles?.Select(static obstacle => obstacle.ToCore()).ToArray());

    private static NumericsVector2 ToNumerics(Vector2 value) =>
        new(value.X, value.Y);
}

/// <summary>
/// Client-only adapter adds caves to the surface query used by the dedicated
/// server. It has no render or loaded-chunk dependency.
/// </summary>
internal sealed class SoloWorldNavigationQuery :
    CoreNavigation.IWorldNavigationQuery
{
    [ThreadStatic]
    private static Dictionary<long, CaveHydrologyField.SamplingContext>?
        _caveContexts;
    private readonly CoreNavigation.ProceduralSurfaceNavigationQuery _surface;
    private readonly CaveHydrologyField.SamplingContext _cave;

    public SoloWorldNavigationQuery(long seed)
    {
        _surface = new CoreNavigation.ProceduralSurfaceNavigationQuery(seed);
        var contexts = _caveContexts ??= [];
        if (contexts.TryGetValue(seed, out var cave))
        {
            _cave = cave;
        }
        else
        {
            _cave = new CaveHydrologyField.SamplingContext(seed);
            if (contexts.Count >= 4) contexts.Clear();
            contexts[seed] = _cave;
        }
    }

    public bool SupportsWorldLevel(int worldLevel) =>
        worldLevel is (int)WorldLevel.Overworld or
            (int)WorldLevel.Underground;

    public bool CanStandAt(NumericsVector2 point, int worldLevel)
    {
        if (worldLevel == (int)WorldLevel.Underground)
            return _cave.Density(point.X, point.Y) >=
                   CaveHydrologyField.Boundary;
        return _surface.CanStandAt(point, worldLevel);
    }

    public float HeightAt(NumericsVector2 point, int worldLevel)
    {
        if (worldLevel == (int)WorldLevel.Underground)
            return MathF.Round(UndergroundWorldGenerator.Height(
                _cave.Density(point.X, point.Y)));
        return _surface.HeightAt(point, worldLevel);
    }

    public bool IsWading(NumericsVector2 point, int worldLevel) =>
        worldLevel == (int)WorldLevel.Overworld &&
        _surface.IsWading(point, worldLevel);
}
