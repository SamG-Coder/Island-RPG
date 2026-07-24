using IslandRpg.World;

const long seed = 8675309;
var origin = InfiniteWorldGenerator.Generate(seed, new(0, 0));
var repeated = InfiniteWorldGenerator.Generate(seed, new(0, 0));
Require(origin.Tiles.SequenceEqual(repeated.Tiles), "same seed and coordinate must reproduce tiles");
Require(origin.Trees.SequenceEqual(repeated.Trees), "same seed and coordinate must reproduce trees");
Require(origin.Cliffs.SequenceEqual(repeated.Cliffs),
    "same seed and coordinate must reproduce cliff faces");
Require(origin.BiomeWeightsA.SequenceEqual(repeated.BiomeWeightsA),
    "same seed and coordinate must reproduce primary biome weights");
Require(origin.BiomeWeightsB.SequenceEqual(repeated.BiomeWeightsB),
    "same seed and coordinate must reproduce secondary biome and coastline weights");
Require(origin.ShoreDistance.SequenceEqual(repeated.ShoreDistance),
    "same seed and coordinate must reproduce shoreline distance");

var east = InfiniteWorldGenerator.Generate(seed, new(1, 0));
for (var y = 0; y < WorldChunk.Size; y++)
{
    var westEdge = origin.Tiles[y * WorldChunk.Size + WorldChunk.Size - 1];
    var eastEdge = east.Tiles[y * WorldChunk.Size];
    Require(westEdge.East == eastEdge.North,
        $"east height seam differs on row {y}: {westEdge.East} != {eastEdge.North}");
    Require(westEdge.South == eastEdge.West,
        $"south-east height seam differs on row {y}: {westEdge.South} != {eastEdge.West}");
}

var macroBiomes = new Dictionary<WorldBiome, int>();
var snowSamples = 0;
var hillSamples = 0;
var mountainSamples = 0;
var maximumElevation = 0f;
for (var sampleY = -1000; sampleY <= 1000; sampleY += 40)
for (var sampleX = -1000; sampleX <= 1000; sampleX += 40)
{
    var tile = InfiniteWorldGenerator.SampleTile(seed, sampleX, sampleY);
    macroBiomes[tile.Region] = macroBiomes.GetValueOrDefault(tile.Region) + 1;
    if (tile.Biome == Biome.Snow) snowSamples++;
    var elevation = (tile.North + tile.East + tile.South + tile.West) / 4f;
    maximumElevation = Math.Max(maximumElevation, elevation);
    if (elevation is >= 2 and < 5) hillSamples++;
    if (elevation >= 5) mountainSamples++;
}
Require(macroBiomes.ContainsKey(WorldBiome.Ocean), "macro world must contain oceans");
Require(macroBiomes.ContainsKey(WorldBiome.River), "macro world must contain river corridors");
Require(macroBiomes.ContainsKey(WorldBiome.Alpine), "macro world must contain mountain ranges");
Require(macroBiomes.ContainsKey(WorldBiome.TemperateForest) ||
        macroBiomes.ContainsKey(WorldBiome.Rainforest) ||
        macroBiomes.ContainsKey(WorldBiome.Taiga),
    "macro world must contain regional forests");
Require(macroBiomes.Keys.Count >= 7,
    $"macro climate should produce at least seven biome types; found {macroBiomes.Keys.Count}");
Require(snowSamples > 0, "cold tundra or alpine terrain must produce visible snow");
Require(hillSamples > 0, "continental terrain must produce rolling hills and foothills");
Require(mountainSamples > 0, "continental terrain must produce mountain elevations");
Require(maximumElevation >= 10,
    $"continental ranges must include impactful high peaks; highest was {maximumElevation}");

var atlasProgress = new System.Collections.Concurrent.ConcurrentBag<(int Done, int Total)>();
Require(WorldAtlasGenerator.PixelSize == 512,
    "default atlas output must use the high-resolution 512x512 texture");
var atlas = WorldAtlasGenerator.Generate(
    seed, 128, -96,
    (done, total) => atlasProgress.Add((done, total)),
    chunksAcross: 2,
    pixelsPerChunk: 3);
var repeatedAtlas = WorldAtlasGenerator.Generate(
    seed, 128, -96, chunksAcross: 2, pixelsPerChunk: 3);
Require(atlas.Rgba.SequenceEqual(repeatedAtlas.Rgba),
    "atlas generation must be deterministic");
Require(atlasProgress.Count == 4 && atlasProgress.Max(value => value.Done) == 4 &&
        atlasProgress.All(value => value.Total == 4),
    "atlas progress must report every generated chunk");
Require(atlas.Width == 6 && atlas.Height == 6 && atlas.SpanTiles == 64,
    "atlas dimensions must follow its chunk and pixel resolution");

var textureSize = WorldChunk.WeightTextureSize;
var halo = WorldChunk.WeightHaloTiles * WorldChunk.WeightSamplesPerTile;
var originEdgeX = halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile;
var eastEdgeX = halo;
for (var y = halo; y <= halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile; y++)
for (var channel = 0; channel < 4; channel++)
{
    Require(origin.BiomeWeightsA[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsA[(y * textureSize + eastEdgeX) * 4 + channel],
        $"primary biome blend seam differs at sample {y}, channel {channel}");
    Require(origin.BiomeWeightsB[(y * textureSize + originEdgeX) * 4 + channel] ==
            east.BiomeWeightsB[(y * textureSize + eastEdgeX) * 4 + channel],
        $"secondary biome/coast blend seam differs at sample {y}, channel {channel}");
}
for (var y = halo; y <= halo + WorldChunk.Size * WorldChunk.WeightSamplesPerTile; y++)
    Require(origin.ShoreDistance[y * textureSize + originEdgeX] ==
            east.ShoreDistance[y * textureSize + eastEdgeX],
        $"shoreline distance seam differs at sample {y}");

var root = Path.Combine(Path.GetTempPath(), $"IslandRpg.WorldChecks.{Guid.NewGuid():N}");
long regionBytes = 0;
try
{
    var store = new WorldChunkStore(seed, root);
    for (var regionY = 0; regionY < WorldChunkStore.RegionSize; regionY++)
    for (var regionX = 0; regionX < WorldChunkStore.RegionSize; regionX++)
        store.Save(CloneAt(origin, new(regionX, regionY)));
    var negative = CloneAt(origin, new(-1, -1));
    store.Save(negative);

    var loaded = store.LoadOrGenerate(origin.Coordinate);
    Require(origin.Tiles.SequenceEqual(loaded.Tiles), "saved tiles must round-trip");
    Require(origin.Trees.SequenceEqual(loaded.Trees), "saved trees must round-trip");
    Require(origin.Cliffs.SequenceEqual(loaded.Cliffs), "derived cliff faces must round-trip");
    Require(origin.BiomeWeightsA.SequenceEqual(loaded.BiomeWeightsA),
        "primary biome weights must round-trip");
    Require(origin.BiomeWeightsB.SequenceEqual(loaded.BiomeWeightsB),
        "secondary biome and coastline weights must round-trip");
    Require(origin.ShoreDistance.SequenceEqual(loaded.ShoreDistance),
        "shoreline distance must round-trip");
    Require(File.Exists(Path.Combine(store.WorldDirectory, "world.json")), "world metadata must be saved");
    var positiveRegion = store.RegionPathFor(new(7, 7));
    Require(File.Exists(positiveRegion), "positive region file must exist");
    Require(store.RegionPathFor(new(0, 0)) == positiveRegion,
        "all 64 chunks in an 8x8 range must share one region file");
    Require(store.RegionPathFor(new(-1, -1)) != positiveRegion,
        "negative chunk coordinates must map to the neighboring region");
    Require(Directory.GetFiles(Path.GetDirectoryName(positiveRegion)!, "*.irrg").Length == 2,
        "65 chunks spanning two regions must use exactly two region files");
    regionBytes = new FileInfo(positiveRegion).Length;
    store.Save(origin);
    Require(new FileInfo(positiveRegion).Length == regionBytes,
        "saving an unchanged chunk must not append duplicate region data");
    Require(new FileInfo(positiveRegion).Length <
            (long)WorldChunkStore.RegionSize * WorldChunkStore.RegionSize *
            (origin.BiomeWeightsA.Length + origin.BiomeWeightsB.Length),
        "region storage must be smaller than persisting deterministic render textures");
    var farLoaded = store.LoadOrGenerate(new(7, 7));
    Require(farLoaded.Coordinate == new ChunkCoordinate(7, 7),
        "direct region lookup must load the requested slot");
    var negativeLoaded = store.LoadOrGenerate(new(-1, -1));
    Require(negativeLoaded.Coordinate == new ChunkCoordinate(-1, -1),
        "negative region coordinates must round-trip");
}
finally
{
    var resolvedRoot = Path.GetFullPath(root);
    var resolvedTemp = Path.GetFullPath(Path.GetTempPath());
    if (!resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to remove a test directory outside the temp folder.");
    if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, recursive: true);
}

Console.WriteLine(
    $"World checks passed: {macroBiomes.Count} macro biomes, deterministic generation, seams, " +
    $"persistence, and 64-slot region storage ({regionBytes:N0} bytes for the test region).");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static WorldChunk CloneAt(WorldChunk source, ChunkCoordinate coordinate) => new()
{
    Coordinate = coordinate,
    Tiles = source.Tiles,
    Trees = source.Trees,
    BiomeWeightsA = source.BiomeWeightsA,
    BiomeWeightsB = source.BiomeWeightsB,
    ShoreDistance = source.ShoreDistance,
    Cliffs = source.Cliffs
};
