namespace IslandRpg.World;

internal sealed record WorldAtlasSnapshot(
    int CenterTileX,
    int CenterTileY,
    int SpanTiles,
    int Width,
    int Height,
    byte[] Rgba);

internal static class WorldAtlasGenerator
{
    public const int ChunksAcross = 32;
    public const int PixelsPerChunk = 16;
    public const int PixelSize = ChunksAcross * PixelsPerChunk;
    public const int SpanTiles = ChunksAcross * WorldChunk.Size;

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
                    var color = ColorFor(tile.Region);
                    var elevation = (tile.North + tile.East + tile.South + tile.West) / 36f;
                    var shade = tile.Region switch
                    {
                        WorldBiome.Ocean => .88f + elevation * .08f,
                        WorldBiome.River => 1f,
                        _ => .82f + elevation * .28f
                    };
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
