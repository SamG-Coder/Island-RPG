using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed record WorldVegetationRenderItem(
    Vector2 World,
    string StableKey,
    string AtlasKey,
    string? ShadowAtlasKey,
    bool CanGatherFibre);

internal static class WorldVegetationRenderCache
{
    public static WorldVegetationRenderItem[] Build(
        WorldChunk chunk,
        IReadOnlyList<float> renderedHeights)
    {
        var vegetation = chunk.Vegetation;
        var result = new WorldVegetationRenderItem[vegetation.Length];
        for (var index = 0; index < vegetation.Length; index++)
        {
            var item = vegetation[index];
            var tileX = (int)MathF.Floor(item.X);
            var tileY = (int)MathF.Floor(item.Y);
            var localX = PositiveMod(tileX, WorldChunk.Size);
            var localY = PositiveMod(tileY, WorldChunk.Size);
            var elevation = LoadedTerrainSampler.Interpolate(
                renderedHeights,
                WorldChunk.Size + 1,
                localX,
                localY,
                item.X - tileX,
                item.Y - tileY);
            var world = IsometricTerrainProjection.Project(
                item.X, item.Y, elevation);
            var shadowName = ShadowName(item.GraphicName);
            var biome = chunk.Tiles[
                localY * WorldChunk.Size + localX].Biome;
            result[index] = new(
                world,
                $"vegetation:{item.X:0.000}:{item.Y:0.000}",
                $"{item.GraphicName}#{item.FrameIndex}",
                shadowName is null
                    ? null
                    : $"{shadowName}#{item.FrameIndex}",
                item.CanBecomeInstance &&
                item.Kind == WorldVegetationKind.Shrub &&
                biome is not Biome.Snow and not Biome.Tundra);
        }
        return result;
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static string? ShadowName(string graphicName)
    {
        if (!graphicName.StartsWith(
                "BUSH", StringComparison.OrdinalIgnoreCase))
            return null;
        return graphicName.EndsWith(
                "_NN", StringComparison.OrdinalIgnoreCase)
            ? graphicName[..^2] + "N0"
            : null;
    }
}
