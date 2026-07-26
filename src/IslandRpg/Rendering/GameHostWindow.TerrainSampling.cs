using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool TrySampleLoadedTerrain(
        float x,
        float y,
        out float renderedHeight,
        out Biome biome)
    {
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var coordinate = new ChunkCoordinate(
            FloorDiv(tileX, WorldChunk.Size),
            FloorDiv(tileY, WorldChunk.Size));
        if (!_worldChunks.TryGetValue(coordinate, out var gpu))
        {
            renderedHeight = 0;
            biome = default;
            return false;
        }

        var localX = PositiveMod(tileX, WorldChunk.Size);
        var localY = PositiveMod(tileY, WorldChunk.Size);
        var stride = WorldChunk.Size + 1;
        var fractionX = x - tileX;
        var fractionY = y - tileY;
        renderedHeight = LoadedTerrainSampler.Interpolate(
            gpu.RenderedHeights,
            stride,
            localX,
            localY,
            fractionX,
            fractionY);
        biome = gpu.Chunk.Tiles[
            localY * WorldChunk.Size + localX].Biome;
        return true;
    }

    private (float Height, Biome Biome) SamplePlayerTerrain(
        float x, float y)
    {
        if (TrySampleLoadedTerrain(
                x, y, out var height, out var biome))
            return (height, biome);
        return (
            InfiniteWorldGenerator.SampleRenderedHeight(
                _worldSeed, x, y),
            InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(x),
                (int)MathF.Floor(y)));
    }
}

internal static class LoadedTerrainSampler
{
    public static float Interpolate(
        IReadOnlyList<float> heights,
        int stride,
        int localX,
        int localY,
        float fractionX,
        float fractionY)
    {
        var northWest = heights[localY * stride + localX];
        var northEast = heights[localY * stride + localX + 1];
        var southWest =
            heights[(localY + 1) * stride + localX];
        var southEast =
            heights[(localY + 1) * stride + localX + 1];
        var north =
            MathHelper.Lerp(northWest, northEast, fractionX);
        var south =
            MathHelper.Lerp(southWest, southEast, fractionX);
        return MathHelper.Lerp(north, south, fractionY);
    }
}
