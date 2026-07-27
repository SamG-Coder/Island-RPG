namespace IslandRpg.World;

internal sealed record WorldAtlasSnapshot(
    int CenterTileX,
    int CenterTileY,
    int SpanTiles,
    int Width,
    int Height,
    byte[] Rgba);

internal enum WorldAtlasLayer
{
    Terrain,
    TreeDensity
}

internal readonly record struct WorldAtlasTileKey(
    int X,
    int Y,
    int ChunksAcross,
    WorldAtlasLayer Layer = WorldAtlasLayer.Terrain,
    int Level = (int)WorldLevel.Overworld)
{
    public int SpanTiles => ChunksAcross * WorldChunk.Size;
}

internal sealed record WorldAtlasTileSnapshot(
    WorldAtlasTileKey Key,
    int Width,
    int Height,
    byte[] Rgba);

internal static class WorldAtlasGenerator
{
    public const int ChunksAcross = 32;
    public const int PixelsPerChunk = 16;
    public const int PixelSize = ChunksAcross * PixelsPerChunk;
    public const int SpanTiles = ChunksAcross * WorldChunk.Size;
    public const int TilePixelSize = 256;

    public static WorldAtlasTileSnapshot GenerateIsometricTile(
        long seed,
        WorldAtlasTileKey key,
        CancellationToken cancellationToken = default)
    {
        if (key.Level == (int)WorldLevel.Underground)
            return GenerateUndergroundTile(seed, key, cancellationToken);
        var size = TilePixelSize;
        var span = key.SpanTiles;
        var rgba = new byte[size * size * 4];
        var river = new bool[size * size];
        var bridgeable = new bool[size * size];
        var samples = new System.Collections.Concurrent.ConcurrentDictionary<
            (int X, int Y), IslandTile>();
        Parallel.For(
            0,
            size,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2
            },
            imageY =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var imageX = 0; imageX < size; imageX++)
            {
                if ((imageX & 31) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var apparentIsoX = (key.X + (imageX + .5f) / size) * span;
                var apparentIsoY = (key.Y + (imageY + .5f) / size) * span;
                var terrainIsoY = apparentIsoY;
                IslandTile tile = null!;
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    var worldX = (int)MathF.Floor(apparentIsoX + terrainIsoY);
                    var worldY = (int)MathF.Floor(terrainIsoY - apparentIsoX);
                    var sampled = samples.GetOrAdd(
                        (worldX, worldY),
                        coordinate => InfiniteWorldGenerator.SampleTile(
                            seed, coordinate.X, coordinate.Y));
                    tile = sampled;
                    var elevation = (tile.North + tile.East + tile.South + tile.West) / 4f;
                    // Height is exaggerated in the atlas so mountain structure
                    // remains readable at regional zoom levels.
                    terrainIsoY = apparentIsoY + elevation * 1.35f;
                }

                var color = key.Layer == WorldAtlasLayer.TreeDensity
                    ? TreeDensityColor(tile)
                    : BaseColor(tile);
                var slopeX = (tile.East + tile.South - tile.North - tile.West) * .5f;
                var slopeY = (tile.West + tile.South - tile.North - tile.East) * .5f;
                var relief = Math.Clamp((-slopeX + slopeY) * .065f, -.24f, .22f);
                var elevationShade =
                    (tile.North + tile.East + tile.South + tile.West) / 88f;
                var shade = key.Layer == WorldAtlasLayer.TreeDensity
                    ? 1f
                    : tile.Region == WorldBiome.Ocean
                    ? .94f
                    : .88f + elevationShade * .15f + relief;
                var index = (imageY * size + imageX) * 4;
                var pixel = imageY * size + imageX;
                river[pixel] =
                    tile.Biome == Biome.RiverWater ||
                    tile.Region == WorldBiome.River;
                bridgeable[pixel] =
                    tile.Region != WorldBiome.Ocean &&
                    tile.Biome is not
                        (Biome.DeepWater or Biome.ShallowWater);
                rgba[index] = (byte)Math.Clamp(color.R * shade, 0, 255);
                rgba[index + 1] = (byte)Math.Clamp(color.G * shade, 0, 255);
                rgba[index + 2] = (byte)Math.Clamp(color.B * shade, 0, 255);
                rgba[index + 3] = 255;
            }
        });
        if (key.Layer == WorldAtlasLayer.Terrain)
            SmoothRiverContinuity(rgba, river, bridgeable, size);
        return new(key, size, size, rgba);
    }

    private static WorldAtlasTileSnapshot GenerateUndergroundTile(
        long seed,
        WorldAtlasTileKey key,
        CancellationToken cancellationToken)
    {
        var size = TilePixelSize;
        var span = key.SpanTiles;
        var rgba = new byte[size * size * 4];
        Parallel.For(
            0,
            size,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 2
            },
            () => new CaveHydrologyField.SamplingContext(seed),
            (imageY, _, context) =>
            {
            for (var imageX = 0; imageX < size; imageX++)
            {
                if ((imageX & 31) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var isoX =
                    (key.X + (imageX + .5f) / size) * span;
                var isoY =
                    (key.Y + (imageY + .5f) / size) * span;
                var worldX = isoX + isoY;
                var worldY = isoY - isoX;
                var color =
                    WorldLevelMapPresentation.UndergroundColor(
                        seed, context, worldX, worldY);
                var index = (imageY * size + imageX) * 4;
                rgba[index] = color.Red;
                rgba[index + 1] = color.Green;
                rgba[index + 2] = color.Blue;
                rgba[index + 3] = 255;
            }
                return context;
            },
            _ => { });
        return new(key, size, size, rgba);
    }

    private static (byte R, byte G, byte B) TreeDensityColor(
        IslandTile tile)
    {
        var elevation =
            (tile.North + tile.East + tile.South + tile.West) / 4f;
        var density = Math.Clamp(
            WorldTreeCatalog.SpawnChance(tile.Region, elevation) / .31f,
            0,
            1);
        if (density <= 0)
            return tile.Region == WorldBiome.Ocean
                ? ((byte)10, (byte)18, (byte)25)
                : ((byte)24, (byte)25, (byte)21);
        if (density < .5f)
        {
            var amount = density * 2;
            return (
                (byte)MathF.Round(24 + (43 - 24) * amount),
                (byte)MathF.Round(25 + (142 - 25) * amount),
                (byte)MathF.Round(21 + (65 - 21) * amount));
        }
        else
        {
            var amount = (density - .5f) * 2;
            return (
                (byte)MathF.Round(43 + (238 - 43) * amount),
                (byte)MathF.Round(142 + (205 - 142) * amount),
                (byte)MathF.Round(65 + (70 - 65) * amount));
        }
    }

    internal static void SmoothRiverContinuity(
        byte[] rgba, bool[] river, bool[] bridgeable, int size)
    {
        if (rgba.Length != size * size * 4 ||
            river.Length != size * size ||
            bridgeable.Length != size * size)
            throw new ArgumentException("Atlas river buffers have invalid dimensions.");

        var additions = new bool[river.Length];
        ReadOnlySpan<(int X, int Y)> directions =
        [
            (1, 0), (0, 1), (1, 1), (1, -1)
        ];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (!river[y * size + x]) continue;
            foreach (var direction in directions)
            for (var distance = 2; distance <= 3; distance++)
            {
                var endX = x + direction.X * distance;
                var endY = y + direction.Y * distance;
                if ((uint)endX >= (uint)size ||
                    (uint)endY >= (uint)size ||
                    !river[endY * size + endX])
                    continue;

                var canBridge = true;
                for (var step = 1; step < distance; step++)
                {
                    var bridgeIndex =
                        (y + direction.Y * step) * size +
                        x + direction.X * step;
                    canBridge &= bridgeable[bridgeIndex];
                }
                if (!canBridge) break;
                for (var step = 1; step < distance; step++)
                    additions[
                        (y + direction.Y * step) * size +
                        x + direction.X * step] = true;
                break;
            }
        }

        for (var pixel = 0; pixel < additions.Length; pixel++)
        {
            if (!additions[pixel]) continue;
            river[pixel] = true;
            var index = pixel * 4;
            // Match the atlas river palette while retaining a small amount of
            // underlying terrain shading at the newly bridged pixel.
            rgba[index] = (byte)((rgba[index] + 45 * 3) / 4);
            rgba[index + 1] = (byte)((rgba[index + 1] + 125 * 3) / 4);
            rgba[index + 2] = (byte)((rgba[index + 2] + 171 * 3) / 4);
            rgba[index + 3] = 255;
        }
    }

    public static WorldAtlasSnapshot Generate(
        long seed,
        int centerTileX,
        int centerTileY,
        Action<int, int>? progress = null,
        int chunksAcross = ChunksAcross,
        int pixelsPerChunk = PixelsPerChunk)
    {
        if (chunksAcross <= 0 || pixelsPerChunk <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunksAcross));
        var spanTiles = chunksAcross * WorldChunk.Size;
        var pixelSize = chunksAcross * pixelsPerChunk;
        var firstTileX = centerTileX - spanTiles / 2;
        var firstTileY = centerTileY - spanTiles / 2;
        var rgba = new byte[pixelSize * pixelSize * 4];
        var total = chunksAcross * chunksAcross;
        var done = 0;
        Parallel.For(0, chunksAcross, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        }, chunkY =>
        {
            for (var chunkX = 0; chunkX < chunksAcross; chunkX++)
            {
                for (var pixelY = 0; pixelY < pixelsPerChunk; pixelY++)
                for (var pixelX = 0; pixelX < pixelsPerChunk; pixelX++)
                {
                    var imageX = chunkX * pixelsPerChunk + pixelX;
                    var imageY = chunkY * pixelsPerChunk + pixelY;
                    var tileX = firstTileX +
                                (int)((imageX + .5f) * spanTiles / pixelSize);
                    var tileY = firstTileY +
                                (int)((imageY + .5f) * spanTiles / pixelSize);
                    var tile = InfiniteWorldGenerator.SampleTile(seed, tileX, tileY);
                    var color = BaseColor(tile);
                    var elevation = (tile.North + tile.East + tile.South + tile.West) / 36f;
                    var shade = tile.Region switch
                    {
                        WorldBiome.Ocean => .88f + elevation * .08f,
                        WorldBiome.River => 1f,
                        _ => .82f + elevation * .28f
                    };
                    // A north-west light makes ridges, foothills and river valleys
                    // legible without changing the deterministic biome colours.
                    var slopeX = (tile.East + tile.South - tile.North - tile.West) * .5f;
                    var slopeY = (tile.West + tile.South - tile.North - tile.East) * .5f;
                    shade += Math.Clamp((-slopeX - slopeY) * .055f, -.25f, .25f);
                    var index = (imageY * pixelSize + imageX) * 4;
                    rgba[index] = (byte)Math.Clamp(color.R * shade, 0, 255);
                    rgba[index + 1] = (byte)Math.Clamp(color.G * shade, 0, 255);
                    rgba[index + 2] = (byte)Math.Clamp(color.B * shade, 0, 255);
                    rgba[index + 3] = 255;
                }
                var completed = Interlocked.Increment(ref done);
                progress?.Invoke(completed, total);
            }
        });
        return new(centerTileX, centerTileY, spanTiles, pixelSize, pixelSize, rgba);
    }

    private static (byte R, byte G, byte B) BaseColor(IslandTile tile) =>
        tile.Biome switch
        {
            Biome.Snow => (224, 232, 235),
            Biome.DeepWater => (24, 72, 116),
            Biome.ShallowWater when tile.Region == WorldBiome.Ocean => (43, 112, 151),
            Biome.RiverWater => (45, 125, 171),
            Biome.MangroveShallows => (62, 119, 113),
            _ => ColorFor(tile.Region)
        };

    private static (byte R, byte G, byte B) ColorFor(WorldBiome biome) => biome switch
    {
        WorldBiome.Ocean => (34, 92, 138),
        WorldBiome.Coast => (218, 197, 137),
        WorldBiome.River => (46, 126, 174),
        WorldBiome.Wetland => (66, 112, 83),
        WorldBiome.TemperateGrassland => (111, 151, 77),
        WorldBiome.TemperateForest => (53, 109, 61),
        WorldBiome.Rainforest => (31, 91, 53),
        WorldBiome.Savanna => (166, 156, 76),
        WorldBiome.Desert => (205, 178, 104),
        WorldBiome.Taiga => (66, 104, 83),
        WorldBiome.Tundra => (145, 153, 139),
        WorldBiome.Alpine => (112, 108, 104),
        _ => (120, 120, 120)
    };
}
