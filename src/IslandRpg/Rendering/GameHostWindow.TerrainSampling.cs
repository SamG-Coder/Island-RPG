using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private CaveHydrologyField.SamplingContext?
        _fallbackCaveSampling;
    private long _fallbackCaveSamplingSeed;
    private (float Height, Biome Biome)? _lastLoadedTerrainSample;

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
            FloorDiv(tileY, WorldChunk.Size),
            _activeWorldLevel);
        if (!_worldChunks.TryGetValue(coordinate, out var gpu))
        {
            renderedHeight = 0;
            biome = default;
            return false;
        }

        var localX = PositiveMod(tileX, WorldChunk.Size);
        var localY = PositiveMod(tileY, WorldChunk.Size);
        var fractionX = x - tileX;
        var fractionY = y - tileY;
        if (!gpu.Chunk.IsRenderable(localX, localY))
        {
            renderedHeight = 0;
            biome = default;
            return false;
        }
        if (_activeWorldLevel == (int)WorldLevel.Underground &&
            gpu.Chunk.SampleUndergroundDensity(localX + fractionX, localY + fractionY) <
            CaveHydrologyField.Boundary)
        {
            renderedHeight = 0;
            biome = default;
            return false;
        }
        var stride = WorldChunk.Size + 1;
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
        return SampleLevelTerrain(x, y);
    }

    private (float Height, Biome Biome) SampleLevelTerrain(
        float x,
        float y,
        CaveHydrologyField.SamplingContext? caveContext = null)
    {
        if (TrySampleLoadedTerrain(
                x, y, out var height, out var biome))
        {
            _lastLoadedTerrainSample = (height, biome);
            return (height, biome);
        }
        // After a network join the GPU chunks are retired. Falling back to
        // SampleRenderedHeight for the player, every slime, and every remote
        // actor is the hitch that feeds the next hitch.
        if (IsNetworkWorld)
            return _lastLoadedTerrainSample ?? (0f, default);
        return SampleProceduralLevelTerrain(x, y, caveContext);
    }

    private (float Height, Biome Biome) SampleProceduralLevelTerrain(
        float x,
        float y,
        CaveHydrologyField.SamplingContext? caveContext = null)
    {
        if (_activeWorldLevel == (int)WorldLevel.Underground)
        {
            caveContext ??=
                FallbackCaveSampling();
            var density = caveContext.Density(x, y);
            return (
                UndergroundWorldGenerator.Height(density),
                UndergroundWorldGenerator.MaterialAt(
                    _worldSeed,
                    (int)MathF.Floor(x),
                    (int)MathF.Floor(y)));
        }

        return (
            InfiniteWorldGenerator.SampleRenderedHeight(_worldSeed, x, y),
            InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(x),
                (int)MathF.Floor(y)));
    }

    private CaveHydrologyField.SamplingContext FallbackCaveSampling()
    {
        if (_fallbackCaveSampling is not null &&
            _fallbackCaveSamplingSeed == _worldSeed)
            return _fallbackCaveSampling;
        _fallbackCaveSamplingSeed = _worldSeed;
        return _fallbackCaveSampling =
            new CaveHydrologyField.SamplingContext(_worldSeed);
    }

    private void ClearFallbackCaveSampling() =>
        _fallbackCaveSampling = null;
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
