using System.Numerics;
using IslandRpg.Resources;

namespace IslandRpg.Navigation;

/// <summary>
/// Seed-backed navigation shared by the client and dedicated server for every
/// procedural world level. It samples terrain directly and never loads chunks
/// or renderer state, so route validation remains deterministic and headless.
/// </summary>
public sealed class ProceduralWorldNavigationQuery : IWorldNavigationQuery
{
    private readonly ProceduralSurfaceNavigationQuery _surface;
    private readonly ProceduralUndergroundTerrain.SamplingContext _underground;

    public ProceduralWorldNavigationQuery(long seed)
    {
        Seed = seed;
        _surface = new ProceduralSurfaceNavigationQuery(seed);
        _underground = new ProceduralUndergroundTerrain.SamplingContext(seed);
    }

    public long Seed { get; }

    public bool SupportsWorldLevel(int worldLevel) => worldLevel is
        (int)NavigationWorldLevel.Overworld or
        (int)NavigationWorldLevel.Underground;

    public bool CanStandAt(Vector2 point, int worldLevel)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return false;
        return worldLevel switch
        {
            (int)NavigationWorldLevel.Overworld =>
                _surface.CanStandAt(point, worldLevel),
            (int)NavigationWorldLevel.Underground =>
                UndergroundDensity(point) >=
                ProceduralUndergroundTerrain.Boundary,
            _ => false
        };
    }

    public float HeightAt(Vector2 point, int worldLevel) => worldLevel switch
    {
        (int)NavigationWorldLevel.Overworld =>
            _surface.HeightAt(point, worldLevel),
        (int)NavigationWorldLevel.Underground =>
            MathF.Round(UndergroundHeight(UndergroundDensity(point))),
        _ => 0
    };

    public bool IsWading(Vector2 point, int worldLevel) =>
        worldLevel == (int)NavigationWorldLevel.Overworld &&
        _surface.IsWading(point, worldLevel);

    private float UndergroundDensity(Vector2 point) =>
        _underground.Density(point.X, point.Y);

    /// <summary>
    /// Matches the canonical cave renderer's carved-floor height. Keeping the
    /// curve here in Core prevents the server from importing mesh generation.
    /// </summary>
    private static float UndergroundHeight(float density)
    {
        var normalized = Math.Clamp((density + .8f) / 3f, 0f, 1f);
        var smooth = normalized * normalized * (3f - 2f * normalized);
        return Math.Clamp(4f - smooth * 4f, 0f, 4f);
    }
}
