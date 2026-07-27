using IslandRpg.Rendering;

namespace IslandRpg.World;

/// <summary>
/// Produces a coordinate-stable underground layer. Its ridged scalar field is
/// thresholded like surface hydrology: narrow connected channels become cave
/// passages and low-frequency accumulation pockets become chambers.
/// </summary>
internal static class UndergroundWorldGenerator
{
    internal const int SamplesPerTile = 4;
    internal const int DensityStride =
        WorldChunk.Size * SamplesPerTile + 1;

    public static WorldChunk Generate(
        long seed,
        ChunkCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        var originX = coordinate.X * WorldChunk.Size;
        var originY = coordinate.Y * WorldChunk.Size;
        var context = new CaveHydrologyField.SamplingContext(seed);
        var density = BuildDensity(
            context, originX, originY, cancellationToken);
        var heights = new byte[WorldChunk.Size + 1, WorldChunk.Size + 1];
        for (var y = 0; y <= WorldChunk.Size; y++)
        for (var x = 0; x <= WorldChunk.Size; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            heights[x, y] = (byte)Math.Clamp(
                MathF.Round(Height(DensityAt(density, x, y))),
                0, 4);
        }

        var tiles = new IslandTile[WorldChunk.Size * WorldChunk.Size];
        var renderable = new bool[tiles.Length];
        for (var y = 0; y < WorldChunk.Size; y++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        for (var x = 0; x < WorldChunk.Size; x++)
        {
            var worldX = originX + x;
            var worldY = originY + y;
            renderable[y * WorldChunk.Size + x] =
                TileIntersectsCave(density, x, y);
            var material = MaterialAt(seed, worldX, worldY);
            tiles[y * WorldChunk.Size + x] = new(
                worldX, worldY, material,
                heights[x, y],
                heights[x + 1, y],
                heights[x + 1, y + 1],
                heights[x, y + 1],
                WorldBiome.Alpine);
        }
        }

        var weights = BuildWeights(
            seed, coordinate, cancellationToken);
        var chunk = new WorldChunk
        {
            Coordinate = coordinate,
            Tiles = tiles,
            Trees = [],
            BiomeWeightsA = weights[0],
            BiomeWeightsB = weights[1],
            BiomeWeightsC = weights[2],
            BiomeWeightsD = weights[3],
            ShoreDistance = Enumerable.Repeat(
                (byte)128,
                WorldChunk.WeightTextureSize *
                WorldChunk.WeightTextureSize).ToArray(),
            Cliffs = [],
            RenderableTiles = renderable,
            UndergroundDensity = density,
            GroundObjects = [],
            Vegetation = [],
            Fish = []
        };
        chunk.UndergroundMeshVertices =
            UndergroundTerrainMeshBuilder.Build(
                chunk, seed, cancellationToken);
        chunk.UndergroundProjectedBounds =
            WorldChunkProjection.TerrainBounds(
                chunk.UndergroundMeshVertices,
                12,
                cancellationToken);
        return chunk;
    }

    private static float[] BuildDensity(
        CaveHydrologyField.SamplingContext context,
        int originX,
        int originY,
        CancellationToken cancellationToken)
    {
        var values = new float[DensityStride * DensityStride];
        var step = 1f / SamplesPerTile;
        for (var y = 0; y < DensityStride; y++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        for (var x = 0; x < DensityStride; x++)
            values[y * DensityStride + x] =
                context.Density(originX + x * step, originY + y * step);
        }
        return values;
    }

    private static float DensityAt(float[] density, int tileX, int tileY) =>
        density[
            tileY * SamplesPerTile * DensityStride +
            tileX * SamplesPerTile];

    internal static float CaveStrength(long seed, float x, float y)
        => CaveHydrologyField.Strength(seed, x, y);

    internal static int SampleHeight(long seed, int x, int y) =>
        (int)MathF.Round(Height(seed, x, y));

    internal static float SampleHeight(
        long seed, float x, float y) =>
        Height(seed, x, y);

    private static float Height(long seed, float x, float y)
    {
        var density = CaveHydrologyField.Density(seed, x, y);
        return Height(density);
    }

    internal static float Height(float density)
    {
        // A carved passage sits below its irregular rocky lip.
        return Math.Clamp(
            4f - SmoothStep(-.8f, 2.2f, density) * 4f,
            0, 4);
    }

    internal static Biome MaterialAt(long seed, int x, int y)
    {
        var variation = Value(seed ^ 0x6D756431, x / 11f, y / 11f);
        return variation switch
        {
            < .28f => Biome.Mud,
            > .76f => Biome.CrackedEarth,
            _ => Biome.Rock
        };
    }

    private static byte[][] BuildWeights(
        long seed,
        ChunkCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var size = WorldChunk.WeightTextureSize;
        var textures = Enumerable.Range(0, 4)
            .Select(_ => new byte[size * size * 4])
            .ToArray();
        var firstX = coordinate.X * WorldChunk.Size -
                     WorldChunk.WeightHaloTiles;
        var firstY = coordinate.Y * WorldChunk.Size -
                     WorldChunk.WeightHaloTiles;
        for (var y = 0; y < size; y++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        for (var x = 0; x < size; x++)
        {
            var worldX = firstX +
                x / (float)WorldChunk.WeightSamplesPerTile;
            var worldY = firstY +
                y / (float)WorldChunk.WeightSamplesPerTile;
            var biome = MaterialAt(
                seed, (int)MathF.Floor(worldX), (int)MathF.Floor(worldY));
            var layer = (int)biome;
            textures[layer / 4][(y * size + x) * 4 + layer % 4] = 255;
        }
        }
        return textures;
    }

    private static bool TileIntersectsCave(
        float[] density, int x, int y)
    {
        var firstX = x * SamplesPerTile;
        var firstY = y * SamplesPerTile;
        for (var sampleY = 0; sampleY <= SamplesPerTile; sampleY++)
        for (var sampleX = 0; sampleX <= SamplesPerTile; sampleX++)
            if (density[
                    (firstY + sampleY) * DensityStride +
                    firstX + sampleX] >= CaveHydrologyField.Boundary)
                return true;
        return false;
    }

    private static float Fractal(long seed, float x, float y) =>
        Value(seed, x, y) * .62f +
        Value(seed + 17, x * 2f, y * 2f) * .27f +
        Value(seed + 41, x * 4f, y * 4f) * .11f;

    private static float Value(long seed, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = Fade(x - x0);
        var ty = Fade(y - y0);
        return Lerp(
            Lerp(Hash(seed, x0, y0), Hash(seed, x0 + 1, y0), tx),
            Lerp(Hash(seed, x0, y0 + 1), Hash(seed, x0 + 1, y0 + 1), tx),
            ty);
    }

    private static float Hash(long seed, int x, int y)
    {
        unchecked
        {
            var value = seed;
            value ^= (long)x *
                     unchecked((long)0x632BE59BD9B4E019UL);
            value ^= (long)y *
                     unchecked((long)0x9E3779B185EBCA87UL);
            value ^= value >> 27;
            value *= 0x3C79AC492BA7B653L;
            value ^= value >> 33;
            return (value & 0xFFFFFF) / 16777215f;
        }
    }

    private static float Fade(float value) =>
        value * value * (3f - 2f * value);

    private static float Lerp(float a, float b, float amount) =>
        a + (b - a) * amount;

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        var normalized = Math.Clamp(
            (value - minimum) / (maximum - minimum), 0f, 1f);
        return normalized * normalized * (3f - 2f * normalized);
    }
}
