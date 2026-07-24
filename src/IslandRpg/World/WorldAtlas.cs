namespace IslandRpg.World;

internal sealed record WorldAtlasSnapshot(
    int CenterTileX,
    int CenterTileY,
    int SpanTiles,
    int Width,
    int Height,
    byte[] Rgba);

internal readonly record struct WorldAtlasTileKey(int X, int Y, int ChunksAcross)
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
        long seed, WorldAtlasTileKey key)
    {
        var size = TilePixelSize;
        var span = key.SpanTiles;
        var rgba = new byte[size * size * 4];
        var samples = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), IslandTile>();
        Parallel.For(0, size, imageY =>
        {
            for (var imageX = 0; imageX < size; imageX++)
            {
                var apparentIsoX = (key.X + (imageX + .5f) / size) * span;
                var apparentIsoY = (key.Y + (imageY + .5f) / size) * span;
                var terrainIsoY = apparentIsoY;
                IslandTile tile = null!;
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    var worldX = (int)MathF.Floor(apparentIsoX + terrainIsoY);
                    var worldY = (int)MathF.Floor(terrainIsoY - apparentIsoX);
                    tile = samples.GetOrAdd((worldX, worldY),
                        coordinate => InfiniteWorldGenerator.SampleTile(
                            seed, coordinate.X, coordinate.Y));
                    var elevation = (tile.North + tile.East + tile.South + tile.West) / 4f;
                    // Height is exaggerated in the atlas so mountain structure
                    // remains readable at regional zoom levels.
                    terrainIsoY = apparentIsoY + elevation * 1.35f;
                }

                var color = BaseColor(tile);
                var slopeX = (tile.East + tile.South - tile.North - tile.West) * .5f;
                var slopeY = (tile.West + tile.South - tile.North - tile.East) * .5f;
                var relief = Math.Clamp((-slopeX + slopeY) * .065f, -.24f, .22f);
                var elevationShade =
                    (tile.North + tile.East + tile.South + tile.West) / 88f;
                var shade = tile.Region == WorldBiome.Ocean
                    ? .94f
                    : .88f + elevationShade * .15f + relief;
                var index = (imageY * size + imageX) * 4;
                rgba[index] = (byte)Math.Clamp(color.R * shade, 0, 255);
                rgba[index + 1] = (byte)Math.Clamp(color.G * shade, 0, 255);
                rgba[index + 2] = (byte)Math.Clamp(color.B * shade, 0, 255);
                rgba[index + 3] = 255;
            }
        });
        return new(key, size, size, rgba);
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
