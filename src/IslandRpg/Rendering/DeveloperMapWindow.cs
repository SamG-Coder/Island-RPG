using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Owns the in-game developer atlas session and converts atlas selections
/// into safe world teleport destinations.
/// </summary>
internal sealed class DeveloperMapWindow
{
    public bool IsOpen { get; private set; }
    public WorldAtlasLayer Layer { get; private set; } =
        WorldAtlasLayer.Terrain;

    public void Open()
    {
        IsOpen = true;
        Layer = WorldAtlasLayer.Terrain;
    }
    public void Close() => IsOpen = false;
    public void ToggleTreeDensity() =>
        Layer = Layer == WorldAtlasLayer.TreeDensity
            ? WorldAtlasLayer.Terrain
            : WorldAtlasLayer.TreeDensity;

    public static Vector2 ResolveDestination(
        Vector2 pointer,
        Vector2 viewportCenter,
        Vector2 atlasCenterIso,
        float pixelsPerTile,
        long seed,
        Vector2 fallback)
    {
        var apparent = atlasCenterIso +
            (pointer - viewportCenter) / pixelsPerTile;
        var terrainIsoY = apparent.Y;
        float tileX = 0;
        float tileY = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            tileX = apparent.X + terrainIsoY;
            tileY = terrainIsoY - apparent.X;
            var tile = InfiniteWorldGenerator.SampleTile(
                seed, (int)MathF.Floor(tileX), (int)MathF.Floor(tileY));
            var elevation =
                (tile.North + tile.East + tile.South + tile.West) / 4f;
            terrainIsoY = apparent.Y + elevation * 1.35f;
        }
        return NearestWalkable(seed, new(tileX, tileY), fallback);
    }

    private static Vector2 NearestWalkable(
        long seed, Vector2 destination, Vector2 fallback)
    {
        var originX = (int)MathF.Floor(destination.X);
        var originY = (int)MathF.Floor(destination.Y);
        for (var radius = 0; radius <= 24; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                continue;
            var tileX = originX + x;
            var tileY = originY + y;
            var biome = InfiniteWorldGenerator.BiomeAt(seed, tileX, tileY);
            if (biome is Biome.DeepWater or Biome.ShallowWater or
                Biome.RiverWater or Biome.MangroveShallows)
                continue;
            return new(tileX + .5f, tileY + .5f);
        }
        return fallback;
    }
}
