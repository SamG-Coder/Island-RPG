using System.Numerics;
using IslandRpg.World;

namespace IslandRpg.Navigation;

/// <summary>
/// Player traversal over the deterministic surface. This matches the existing
/// solo A*: shallow water, rivers and mangrove shallows are wadeable; only deep
/// water is impassable.
/// </summary>
public sealed class ProceduralSurfaceNavigationQuery(long seed) :
    IWorldNavigationQuery
{
    public long Seed { get; } = seed;

    public bool SupportsWorldLevel(int worldLevel) =>
        worldLevel == (int)NavigationWorldLevel.Overworld;

    public bool CanStandAt(Vector2 point, int worldLevel)
    {
        if (!SupportsWorldLevel(worldLevel) ||
            !float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return false;
        return TerrainAt(point) !=
               ProceduralSurfaceTerrain.Material.DeepWater;
    }

    public float HeightAt(Vector2 point, int worldLevel)
    {
        if (worldLevel != (int)NavigationWorldLevel.Overworld)
            return 0;
        return ProceduralSurfaceTerrain.SampleSurfaceHeight(
            Seed,
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y));
    }

    public bool IsWading(Vector2 point, int worldLevel)
    {
        if (worldLevel != (int)NavigationWorldLevel.Overworld)
            return false;
        return TerrainAt(point) is
            ProceduralSurfaceTerrain.Material.ShallowWater or
            ProceduralSurfaceTerrain.Material.RiverWater or
            ProceduralSurfaceTerrain.Material.MangroveShallows;
    }

    private ProceduralSurfaceTerrain.Material TerrainAt(Vector2 point) =>
        ProceduralSurfaceTerrain.ClassifyAt(
            Seed,
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y)).Material;
}
