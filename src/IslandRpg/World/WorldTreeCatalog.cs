using IslandRpg.Resources;

namespace IslandRpg.World;

internal static class WorldTreeCatalog
{
    public static bool HasVariants(string graphicName) =>
        SurfaceTreeCatalog.HasVariants(graphicName);

    public static float SpawnChance(WorldBiome region, float elevation)
    {
        return SurfaceTreeCatalog.SpawnChance(
            (ProceduralSurfaceTerrain.Region)region, elevation);
    }

    public static int FrameCount(string graphicName) =>
        SurfaceTreeCatalog.FrameCount(graphicName);

    public static int SelectFrame(
        long seed, int x, int y, string graphicName)
    {
        return SurfaceTreeCatalog.SelectFrame(seed, x, y, graphicName);
    }

    public static string SelectGraphic(
        long seed, IslandTile tile)
    {
        return SurfaceTreeCatalog.SelectGraphic(
            seed,
            tile.X,
            tile.Y,
            (ProceduralSurfaceTerrain.Region)tile.Region,
            (ProceduralSurfaceTerrain.Material)tile.Biome);
    }

    public static string AtlasKey(IslandTree tree) =>
        AtlasKey(tree.GraphicName, tree.FrameIndex);

    public static string AtlasKey(string graphicName, int frameIndex) =>
        FrameCount(graphicName) > 1
            ? $"{graphicName}#{Math.Clamp(frameIndex, 0, FrameCount(graphicName) - 1)}"
            : graphicName;
}
