using System.Text.Json;
using System.IO.Compression;

namespace IslandRpg.World;

internal readonly record struct ChunkCoordinate(int X, int Y)
{
    public override string ToString() => $"{X},{Y}";
}

internal sealed record CliffFace(int X1, int Y1, int X2, int Y2, byte Top, byte Bottom);
internal enum TreeLifecycleState : byte { Standing, Stump }
internal sealed record WorldTreeInstance(
    Guid Id,
    int X,
    int Y,
    string TreeType,
    int Health,
    int MaxHealth,
    TreeLifecycleState State,
    int SticksRemaining = -1,
    int InitialStickCount = -1);
internal sealed record WorldGroundObject(
    Guid Id,
    string ItemId,
    float X,
    float Y,
    string? FuelItemId = null,
    double LitUntilGameSeconds = 0,
    int FiremakingLevel = 1);
internal enum WorldVegetationKind : byte
{
    Plant,
    Shrub,
    FloweringShrub,
    BerryBush
}
internal sealed record WorldVegetation(
    float X,
    float Y,
    string GraphicName,
    int FrameIndex,
    WorldVegetationKind Kind,
    bool CanBecomeInstance);
internal sealed record WorldVegetationFibreState(
    string StableKey,
    double ReadyAtGameSeconds);

internal sealed class WorldChunk
{
    public const int Size = 32;
    public const int MaximumStoredGroundObjects = 4096;
    public const int WeightSamplesPerTile = 4;
    public const int WeightHaloTiles = 8;
    public const int WeightTextureSize = (Size + WeightHaloTiles * 2) * WeightSamplesPerTile;
    public required ChunkCoordinate Coordinate { get; init; }
    public required IslandTile[] Tiles { get; init; }
    public required IslandTree[] Trees { get; set; }
    public required byte[] BiomeWeightsA { get; init; }
    public required byte[] BiomeWeightsB { get; init; }
    public required byte[] BiomeWeightsC { get; init; }
    public required byte[] BiomeWeightsD { get; init; }
    public required byte[] ShoreDistance { get; init; }
    public required CliffFace[] Cliffs { get; init; }
    public List<WorldTreeInstance> TreeInstances { get; init; } = [];
    public List<WorldGroundObject> GroundObjects { get; init; } = [];
    public WorldVegetation[] Vegetation { get; init; } = [];
    public WorldFish[] Fish { get; init; } = [];
    public List<WorldVegetationFibreState> VegetationFibreStates
        { get; init; } = [];
    public Dictionary<string, int> FishRemaining { get; init; } =
        new(StringComparer.Ordinal);
}

internal static class InfiniteWorldGenerator
{
    private const int IslandCellSize = 192;

    public static WorldChunk Generate(long seed, ChunkCoordinate coordinate)
    {
        var originX = coordinate.X * WorldChunk.Size;
        var originY = coordinate.Y * WorldChunk.Size;
        var heights = new byte[WorldChunk.Size + 1, WorldChunk.Size + 1];
        for (var y = 0; y <= WorldChunk.Size; y++)
        for (var x = 0; x <= WorldChunk.Size; x++)
            heights[x, y] = HeightAt(seed, originX + x, originY + y);

        var tiles = new IslandTile[WorldChunk.Size * WorldChunk.Size];
        var trees = new List<IslandTree>();
        for (var y = 0; y < WorldChunk.Size; y++)
        for (var x = 0; x < WorldChunk.Size; x++)
        {
            var worldX = originX + x;
            var worldY = originY + y;
            var average = (heights[x, y] + heights[x + 1, y] +
                           heights[x + 1, y + 1] + heights[x, y + 1]) / 4f;
            var (biome, region) = ClassifyAt(seed, worldX, worldY, average);
            tiles[y * WorldChunk.Size + x] = new(
                worldX, worldY, biome,
                Surface(heights[x, y]), Surface(heights[x + 1, y]),
                Surface(heights[x + 1, y + 1]), Surface(heights[x, y + 1]), region);

            var chance = WorldTreeCatalog.SpawnChance(region, average);
            if (UnitHash(seed, worldX, worldY, 91) >= chance) continue;
            var tile = tiles[y * WorldChunk.Size + x];
            var graphic = WorldTreeCatalog.SelectGraphic(seed, tile);
            var frame = WorldTreeCatalog.SelectFrame(
                seed, worldX, worldY, graphic);
            trees.Add(new(worldX, worldY, graphic, frame));
        }

        var weights = GenerateBiomeWeights(seed, coordinate);
        var cliffs = GenerateCliffs(seed, tiles);
        var groundObjects = GenerateGroundObjects(seed, tiles, trees);
        var vegetation = WorldVegetationGenerator.Generate(
            seed, tiles, trees);
        var fish = WorldFishGenerator.Generate(seed, tiles);
        return new()
        {
            Coordinate = coordinate,
            Tiles = tiles,
            Trees = trees.ToArray(),
            BiomeWeightsA = weights.A,
            BiomeWeightsB = weights.B,
            BiomeWeightsC = weights.C,
            BiomeWeightsD = weights.D,
            ShoreDistance = weights.Shore,
            Cliffs = cliffs,
            GroundObjects = groundObjects,
            Vegetation = vegetation,
            Fish = fish
        };
    }

    internal static List<WorldGroundObject> GenerateGroundObjects(
        long seed, IslandTile[] tiles, IReadOnlyCollection<IslandTree> trees)
    {
        const int maximumPerChunk = 8;
        var occupied = trees.Select(tree => (tree.X, tree.Y)).ToHashSet();
        var candidates = new List<(float Score, WorldGroundObject Object)>();
        foreach (var tile in tiles)
        {
            if (occupied.Contains((tile.X, tile.Y))) continue;
            var relief = Math.Max(
                Math.Max(tile.North, tile.East),
                Math.Max(tile.South, tile.West)) -
                Math.Min(
                    Math.Min(tile.North, tile.East),
                    Math.Min(tile.South, tile.West));
            if (relief > 2) continue;
            var stickChance = tile.Region switch
            {
                WorldBiome.TemperateForest or WorldBiome.Rainforest => .035f,
                WorldBiome.Taiga or WorldBiome.Wetland => .025f,
                WorldBiome.Savanna => .012f,
                _ => 0
            };
            var rockChance = tile.Region switch
            {
                WorldBiome.Alpine => .055f,
                WorldBiome.Tundra or WorldBiome.Coast => .024f,
                WorldBiome.Desert => .018f,
                WorldBiome.TemperateGrassland => .008f,
                _ => 0
            };
            var roll = UnitHash(seed, tile.X, tile.Y, 811);
            string? itemId = roll < stickChance
                ? "sticks"
                : roll < stickChance + rockChance
                    ? "large_rock"
                    : null;
            if (itemId is null) continue;
            var x = tile.X + .18f + UnitHash(seed, tile.X, tile.Y, 823) * .64f;
            var y = tile.Y + .18f + UnitHash(seed, tile.X, tile.Y, 827) * .64f;
            candidates.Add((
                UnitHash(seed, tile.X, tile.Y, 829),
                new(
                    StableGroundObjectId(seed, tile.X, tile.Y, itemId),
                    itemId, x, y)));
        }
        var objects = candidates
            .OrderBy(candidate => candidate.Score)
            .Take(maximumPerChunk)
            .Select(candidate => candidate.Object)
            .ToList();
        objects.AddRange(
            CoastalCollectibleSpawner.GenerateInitial(
                seed, tiles, trees, objects));
        return objects;
    }

    private static Guid StableGroundObjectId(
        long seed, int x, int y, string itemId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, seed);
        BitConverter.TryWriteBytes(bytes[8..], x);
        var discriminator = itemId.Equals(
            "sticks", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        BitConverter.TryWriteBytes(bytes[12..], y ^ (discriminator << 28));
        return new Guid(bytes);
    }

    internal static CliffFace[] GenerateCliffs(long seed, IslandTile[] tiles)
    {
        var cliffs = new List<CliffFace>();
        foreach (var tile in tiles)
        {
            var localX = PositiveMod(tile.X, WorldChunk.Size);
            var localY = PositiveMod(tile.Y, WorldChunk.Size);
            var height = AverageHeight(tile);
            var east = localX + 1 < WorldChunk.Size
                ? tiles[localY * WorldChunk.Size + localX + 1]
                : SampleTile(seed, tile.X + 1, tile.Y);
            var south = localY + 1 < WorldChunk.Size
                ? tiles[(localY + 1) * WorldChunk.Size + localX]
                : SampleTile(seed, tile.X, tile.Y + 1);
            var eastHeight = AverageHeight(east);
            var southHeight = AverageHeight(south);
            var core = IsMountainCore(seed, tile.X, tile.Y, height);
            var eastCore = IsMountainCore(seed, tile.X + 1, tile.Y, eastHeight);
            var southCore = IsMountainCore(seed, tile.X, tile.Y + 1, southHeight);
            if (core != eastCore)
                cliffs.Add(new(tile.X + 1, tile.Y, tile.X + 1, tile.Y + 1,
                    Math.Max(height, eastHeight), Math.Min(height, eastHeight)));
            if (core != southCore)
                cliffs.Add(new(tile.X, tile.Y + 1, tile.X + 1, tile.Y + 1,
                    Math.Max(height, southHeight), Math.Min(height, southHeight)));
        }
        return cliffs.ToArray();

        static byte AverageHeight(IslandTile tile) => (byte)MathF.Round(
            (tile.North + tile.East + tile.South + tile.West) / 4f);
    }

    private static bool IsMountainCore(long seed, int x, int y, byte height)
    {
        if (height < 9) return false;
        var continental = FractalNoise(seed ^ 0x6a09e667f3bcc909L, x / 720f, y / 720f, 4);
        var (_, innerRidge) = MountainProfileAt(seed, x, y);
        var uplift = Math.Clamp((continental + .15f) * 1.7f, 0, 1);
        return innerRidge * uplift > .42f;
    }

    internal static IslandTile SampleTile(long seed, int x, int y)
    {
        var north = HeightAt(seed, x, y);
        var east = HeightAt(seed, x + 1, y);
        var south = HeightAt(seed, x + 1, y + 1);
        var west = HeightAt(seed, x, y + 1);
        var average = (north + east + south + west) / 4f;
        var (material, region) = ClassifyAt(seed, x, y, average);
        return new(x, y, material, Surface(north), Surface(east),
            Surface(south), Surface(west), region);
    }

    internal static byte SampleSurfaceHeight(long seed, int x, int y) =>
        Surface(HeightAt(seed, x, y));

    internal static float SampleRenderedHeight(long seed, float x, float y)
    {
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var fractionX = x - tileX;
        var fractionY = y - tileY;
        var northWest = SmoothedVertex(tileX, tileY);
        var northEast = SmoothedVertex(tileX + 1, tileY);
        var southWest = SmoothedVertex(tileX, tileY + 1);
        var southEast = SmoothedVertex(tileX + 1, tileY + 1);
        var north = northWest + (northEast - northWest) * fractionX;
        var south = southWest + (southEast - southWest) * fractionX;
        return north + (south - north) * fractionY;

        float SmoothedVertex(int vertexX, int vertexY)
        {
            var weightedHeight = 0f;
            var totalWeight = 0f;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var weight = (offsetX == 0 ? 2 : 1) *
                             (offsetY == 0 ? 2 : 1);
                weightedHeight += SampleSurfaceHeight(
                    seed, vertexX + offsetX, vertexY + offsetY) * weight;
                totalWeight += weight;
            }
            return weightedHeight / totalWeight;
        }
    }

    internal static (byte[] A, byte[] B, byte[] C, byte[] D, byte[] Shore) GenerateBiomeWeights(
        long seed, ChunkCoordinate coordinate)
    {
        const int radius = 10;
        var size = WorldChunk.WeightTextureSize;
        var labels = new byte[size * size];
        var usedMaterials = new bool[Enum.GetValues<Biome>().Length];
        var water = new bool[size * size];
        var firstTileX = coordinate.X * WorldChunk.Size - WorldChunk.WeightHaloTiles;
        var firstTileY = coordinate.Y * WorldChunk.Size - WorldChunk.WeightHaloTiles;
        for (var tileY = 0; tileY < WorldChunk.Size + WorldChunk.WeightHaloTiles * 2; tileY++)
        for (var tileX = 0; tileX < WorldChunk.Size + WorldChunk.WeightHaloTiles * 2; tileX++)
        {
            var biome = BiomeAt(seed, firstTileX + tileX, firstTileY + tileY);
            for (var sampleY = 0; sampleY < WorldChunk.WeightSamplesPerTile; sampleY++)
            for (var sampleX = 0; sampleX < WorldChunk.WeightSamplesPerTile; sampleX++)
            {
                var x = tileX * WorldChunk.WeightSamplesPerTile + sampleX;
                var y = tileY * WorldChunk.WeightSamplesPerTile + sampleY;
                var pixel = y * size + x;
                labels[pixel] = (byte)biome;
                usedMaterials[(int)biome] = true;
                water[pixel] = biome is Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows;
            }
        }
        var activeMaterials = Enumerable.Range(0, usedMaterials.Length)
            .Where(index => usedMaterials[index]).ToArray();
        var activeLookup = new int[usedMaterials.Length];
        for (var channel = 0; channel < activeMaterials.Length; channel++)
            activeLookup[activeMaterials[channel]] = channel;
        var channels = activeMaterials.Length;
        var weights = new float[size * size * channels];
        for (var pixel = 0; pixel < labels.Length; pixel++)
            weights[pixel * channels + activeLookup[labels[pixel]]] = 1;

        var kernel = new float[radius * 2 + 1];
        var kernelTotal = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var value = MathF.Exp(-(i * i) / (2f * 4.6f * 4.6f));
            kernel[i + radius] = value;
            kernelTotal += value;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= kernelTotal;

        var scratch = new float[weights.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var channel = 0; channel < channels; channel++)
        {
            var value = 0f;
            for (var k = -radius; k <= radius; k++)
                value += weights[(y * size + Math.Clamp(x + k, 0, size - 1)) * channels + channel] *
                         kernel[k + radius];
            scratch[(y * size + x) * channels + channel] = value;
        }
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        for (var channel = 0; channel < channels; channel++)
        {
            var value = 0f;
            for (var k = -radius; k <= radius; k++)
                value += scratch[(Math.Clamp(y + k, 0, size - 1) * size + x) * channels + channel] *
                         kernel[k + radius];
            weights[(y * size + x) * channels + channel] = value;
        }

        var a = new byte[size * size * 4];
        var b = new byte[size * size * 4];
        var c = new byte[size * size * 4];
        var d = new byte[size * size * 4];
        var shore = new byte[size * size];
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var total = 0f;
            for (var channel = 0; channel < channels; channel++)
                total += weights[pixel * channels + channel];
            for (var channel = 0; channel < channels; channel++)
            {
                var value = (byte)Math.Clamp(
                    MathF.Round(weights[pixel * channels + channel] / Math.Max(total, .0001f) * 255),
                    0, 255);
                var material = activeMaterials[channel];
                if (material < 4) a[pixel * 4 + material] = value;
                else if (material < 8) b[pixel * 4 + material - 4] = value;
                else if (material < 12) c[pixel * 4 + material - 8] = value;
                else d[pixel * 4 + material - 12] = value;
            }
        }

        var distanceToWater = DistanceTo(targetWater: true);
        var distanceToLand = DistanceTo(targetWater: false);
        const float encodedRangeTiles = 8;
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var signedSamples = water[pixel] ? distanceToLand[pixel] : -distanceToWater[pixel];
            var signedTiles = signedSamples / WorldChunk.WeightSamplesPerTile;
            shore[pixel] = (byte)Math.Clamp(
                MathF.Round((signedTiles / encodedRangeTiles * .5f + .5f) * 255), 0, 255);
        }
        return (a, b, c, d, shore);

        float[] DistanceTo(bool targetWater)
        {
            const float diagonal = 1.41421356f;
            var distance = new float[size * size];
            for (var i = 0; i < distance.Length; i++)
                distance[i] = water[i] == targetWater ? 0 : 100000;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var index = y * size + x;
                if (x > 0) distance[index] = Math.Min(distance[index], distance[index - 1] + 1);
                if (y > 0) distance[index] = Math.Min(distance[index], distance[index - size] + 1);
                if (x > 0 && y > 0)
                    distance[index] = Math.Min(distance[index], distance[index - size - 1] + diagonal);
                if (x + 1 < size && y > 0)
                    distance[index] = Math.Min(distance[index], distance[index - size + 1] + diagonal);
            }
            for (var y = size - 1; y >= 0; y--)
            for (var x = size - 1; x >= 0; x--)
            {
                var index = y * size + x;
                if (x + 1 < size) distance[index] = Math.Min(distance[index], distance[index + 1] + 1);
                if (y + 1 < size) distance[index] = Math.Min(distance[index], distance[index + size] + 1);
                if (x + 1 < size && y + 1 < size)
                    distance[index] = Math.Min(distance[index], distance[index + size + 1] + diagonal);
                if (x > 0 && y + 1 < size)
                    distance[index] = Math.Min(distance[index], distance[index + size - 1] + diagonal);
            }
            return distance;
        }
    }

    internal static Biome BiomeAt(long seed, int x, int y)
    {
        var average = (HeightAt(seed, x, y) + HeightAt(seed, x + 1, y) +
                       HeightAt(seed, x + 1, y + 1) + HeightAt(seed, x, y + 1)) / 4f;
        return ClassifyAt(seed, x, y, average).Material;
    }

    internal static float BaseElevationAt(long seed, int x, int y)
    {
        var continental = FractalNoise(seed ^ 0x6a09e667f3bcc909L, x / 720f, y / 720f, 4);
        var continentalDetail = FractalNoise(
            seed ^ unchecked((long)0xbb67ae8584caa73bUL), x / 280f, y / 280f, 3);
        var continentHeight = (continental + continentalDetail * .22f + .12f) * 5.4f;

        var cellX = FloorDiv(x, IslandCellSize);
        var cellY = FloorDiv(y, IslandCellSize);
        var island = -1f;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
        for (var cx = cellX - 1; cx <= cellX + 1; cx++)
        {
            var centerX = (cx + .18f + UnitHash(seed, cx, cy, 11) * .64f) * IslandCellSize;
            var centerY = (cy + .18f + UnitHash(seed, cx, cy, 17) * .64f) * IslandCellSize;
            var radiusX = IslandCellSize * (.25f + UnitHash(seed, cx, cy, 23) * .20f);
            var radiusY = IslandCellSize * (.23f + UnitHash(seed, cx, cy, 29) * .19f);
            var dx = (x - centerX) / radiusX;
            var dy = (y - centerY) / radiusY;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var warp = FractalNoise(seed ^ 0x243f6a8885a308d3L, x / 48f, y / 48f, 3) * .28f;
            island = MathF.Max(island, 1f - distance + warp);
        }

        var islandHeight = (island - .08f) * 7.2f;
        // Oriented tectonic spines establish coherent ranges. Their wide distance
        // field becomes foothills; a narrower profile becomes the steep core.
        var (rangeRamp, mountainCore) = MountainProfileAt(seed, x, y);
        var mountainGate = Math.Clamp((continental + .15f) * 1.7f, 0, 1);
        var passNoise = FractalNoise(seed ^ 0x428a2f98d728ae22L, x / 115f, y / 115f, 2);
        var passCut = Math.Clamp((passNoise - .42f) * 2.3f, 0, .72f);
        var mountains = mountainCore * mountainGate * 12.5f * (1f - passCut);
        var foothills = rangeRamp * mountainGate * 6.0f * (1f - passCut * .55f);
        var hillNoise = MathF.Max(0,
            FractalNoise(seed ^ 0x7137449123ef65cdL, x / 92f, y / 92f, 3));
        var hills = hillNoise * hillNoise *
                    Math.Clamp((continental + .3f) * 1.25f, 0, 1) * 2.6f;
        var detail = FractalNoise(seed ^ 0x13198a2e03707344L, x / 22f, y / 22f, 3) * .8f;
        return MathF.Max(continentHeight, islandHeight) +
               mountains + foothills + hills + detail;
    }

    private static (float Ramp, float Core) MountainProfileAt(long seed, int x, int y)
    {
        const int rangeCellSize = 768;
        var warpedX = x + FractalNoise(seed ^ 0x3c6ef372fe94f82bL, x / 310f, y / 310f, 3) * 42;
        var warpedY = y + FractalNoise(seed ^ 0x428a2f98d728ae22L, x / 310f, y / 310f, 3) * 42;
        var cellX = FloorDiv(x, rangeCellSize);
        var cellY = FloorDiv(y, rangeCellSize);
        var ramp = 0f;
        var core = 0f;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
        for (var cx = cellX - 1; cx <= cellX + 1; cx++)
        {
            var centerX = (cx + .5f + (UnitHash(seed, cx, cy, 401) - .5f) * .34f) *
                          rangeCellSize;
            var centerY = (cy + .5f + (UnitHash(seed, cx, cy, 409) - .5f) * .34f) *
                          rangeCellSize;
            var angle = UnitHash(seed, cx, cy, 419) * MathF.PI;
            var halfLength = 300 + UnitHash(seed, cx, cy, 421) * 250;
            var halfWidth = 125 + UnitHash(seed, cx, cy, 431) * 105;
            var axisX = MathF.Cos(angle);
            var axisY = MathF.Sin(angle);
            var relativeX = warpedX - centerX;
            var relativeY = warpedY - centerY;
            var along = Math.Clamp(relativeX * axisX + relativeY * axisY,
                -halfLength, halfLength);
            var nearestX = centerX + axisX * along;
            var nearestY = centerY + axisY * along;
            var distance = MathF.Sqrt(
                (warpedX - nearestX) * (warpedX - nearestX) +
                (warpedY - nearestY) * (warpedY - nearestY));
            var normalized = distance / halfWidth;
            ramp = Math.Max(ramp, 1f - SmoothStep(.15f, 1f, normalized));
            core = Math.Max(core, 1f - SmoothStep(.05f, .34f, normalized));
        }
        return (ramp, core);
    }

    private static byte HeightAt(long seed, int x, int y)
    {
        var elevation = BaseElevationAt(seed, x, y);
        var drainage = MacroHydrology.At(seed, x, y);
        if (elevation > .35f)
        {
            var channelCarve = drainage.River * MathF.Min(6.5f, elevation - .25f);
            var lakeCarve = drainage.Lake * MathF.Min(3.2f, elevation - .2f);
            elevation -= Math.Max(channelCarve, lakeCarve);
        }
        return (byte)Math.Clamp((int)MathF.Floor(elevation), 0, 22);
    }

    private static (Biome Material, WorldBiome Region) ClassifyAt(
        long seed, int x, int y, float elevation)
    {
        var baseElevation = BaseElevationAt(seed, x, y);
        // Keep the bright continental shelf close to land. The renderer adds a
        // mid-water stage between this boundary and the light coastal fringe.
        if (baseElevation < -.35f) return (Biome.DeepWater, WorldBiome.Ocean);
        if (baseElevation < .9f) return (Biome.ShallowWater, WorldBiome.Ocean);

        var drainage = MacroHydrology.At(seed, x, y);
        var river = drainage.River;
        var continental = FractalNoise(seed ^ 0x6a09e667f3bcc909L, x / 720f, y / 720f, 4);
        if (drainage.Lake > .48f && elevation < 5.5f)
        {
            var warmBand = MathF.Sin((y + seed % 10000) / 1450f) > -.05f;
            var coastalMangrove = baseElevation < 1.7f && warmBand &&
                                   RainfallAt(seed, x, y) > .72f;
            return (coastalMangrove ? Biome.MangroveShallows : Biome.RiverWater,
                WorldBiome.Wetland);
        }
        if (river > .48f && continental > -.18f)
            return (Biome.RiverWater, WorldBiome.River);
        if (elevation < 1.45f) return (Biome.Beach, WorldBiome.Coast);

        var moisture = Math.Clamp(
            .5f + FractalNoise(seed ^ 0x5deece66dL, x / 430f, y / 430f, 4) * .34f +
            FractalNoise(seed ^ unchecked((long)0xa54ff53a5f1d36f1UL),
                x / 105f, y / 105f, 2) * .16f +
            river * .24f, 0, 1);
        var climateBand = MathF.Sin((y + seed % 10000) / 1450f);
        var temperature = Math.Clamp(
            .55f + climateBand * .24f +
            FractalNoise(seed ^ 0x510e527fade682d1L, x / 610f, y / 610f, 3) * .22f -
            MathF.Max(0, elevation - 3) * .032f, 0, 1);

        if (elevation > 13.0f)
            return temperature < .43f && moisture > .34f
                ? (Biome.Snow, WorldBiome.Alpine)
                : (Biome.Rock, WorldBiome.Alpine);
        if (elevation > 9.0f)
            return temperature < .30f && moisture > .42f
                ? (Biome.Snow, WorldBiome.Alpine)
                : (Biome.Rock, WorldBiome.Alpine);
        if (elevation > 6.0f)
            return temperature < .24f && moisture > .48f
                ? (Biome.Snow, WorldBiome.Alpine)
                : (Biome.Highland, WorldBiome.TemperateGrassland);
        if (temperature < .20f) return (Biome.Tundra, WorldBiome.Tundra);
        if (temperature < .36f)
            return moisture > .43f
                ? (Biome.Forest, WorldBiome.Taiga)
                : (Biome.Tundra, WorldBiome.Tundra);
        if (moisture < .18f && temperature > .58f)
            return (Biome.CrackedEarth, WorldBiome.Desert);
        if (moisture < .30f && temperature > .5f)
            return (Biome.DesertSand, WorldBiome.Desert);
        if (moisture < .43f && temperature > .55f)
            return (Biome.DryGrass, WorldBiome.Savanna);
        if (river > .24f && moisture > .62f) return (Biome.Mud, WorldBiome.Wetland);
        if (moisture > .72f && temperature > .58f)
            return (Biome.JungleFloor, WorldBiome.Rainforest);
        if (moisture > .53f) return (Biome.Forest, WorldBiome.TemperateForest);
        return (Biome.Grassland, WorldBiome.TemperateGrassland);
    }

    private static float RiverStrength(long seed, int x, int y) =>
        MacroHydrology.At(seed, x, y).River;

    internal static float RainfallAt(long seed, int x, int y)
    {
        var broad = FractalNoise(seed ^ 0x5deece66dL, x / 430f, y / 430f, 4);
        var detail = FractalNoise(seed ^ unchecked((long)0xa54ff53a5f1d36f1UL),
            x / 105f, y / 105f, 2);
        var windAngle = UnitHash(seed, 0, 0, 557) * MathF.PI * 2;
        var windX = MathF.Cos(windAngle);
        var windY = MathF.Sin(windAngle);
        var localElevation = BaseElevationAt(seed, x, y);
        var upwindNear = BaseElevationAt(
            seed, (int)(x - windX * 72), (int)(y - windY * 72));
        var upwindFar = BaseElevationAt(
            seed, (int)(x - windX * 152), (int)(y - windY * 152));
        var barrier = MathF.Max(upwindNear, upwindFar) - localElevation;
        var rainShadow = Math.Clamp(barrier * .045f, 0, .48f);
        var oceanMoisture = upwindFar < .5f ? .16f : 0;
        return Math.Clamp(.65f + broad * .28f + detail * .12f +
                          oceanMoisture - rainShadow, .10f, 1.2f);
    }

    private static byte Surface(byte height) => height <= 2 ? (byte)0 : height;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float FractalNoise(long seed, float x, float y, int octaves)
    {
        var value = 0f;
        var amplitude = 1f;
        var total = 0f;
        for (var octave = 0; octave < octaves; octave++)
        {
            value += ValueNoise(seed + octave * 1013, x, y) * amplitude;
            total += amplitude;
            amplitude *= .5f;
            x *= 2.03f;
            y *= 2.03f;
        }
        return value / total * 2f - 1f;
    }

    private static float ValueNoise(long seed, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        var a = UnitHash(seed, x0, y0, 0);
        var b = UnitHash(seed, x0 + 1, y0, 0);
        var c = UnitHash(seed, x0, y0 + 1, 0);
        var d = UnitHash(seed, x0 + 1, y0 + 1, 0);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }

    private static float UnitHash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)seed ^ ((ulong)(long)x * 0x9e3779b185ebca87UL) ^
                        ((ulong)(long)y * 0xc2b2ae3d27d4eb4fUL) ^ (uint)salt;
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (value >> 40) / 16777216f;
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;
}

internal sealed class WorldChunkStore
{
    internal const int RegionSize = 8;
    private const int WorldFormatVersion = 4;
    private const int RegionFormatVersion = 1;
    private const int ChunkPayloadVersion = 18;
    private const int RegionMagic = 0x49525247; // IRRG
    private const int LegacyChunkMagic = 0x49524348; // IRCH
    private const int LegacyChunkVersion = 2;
    private const int RegionHeaderSize = 32;
    private const int RegionEntrySize = 16;
    private const int RegionSlotCount = RegionSize * RegionSize;
    private readonly string _chunkDirectory;
    private readonly object _gate = new();

    public string WorldDirectory { get; }
    public long Seed { get; }

    public WorldChunkStore(
        long seed, string? root = null, string? worldDirectoryName = null)
    {
        Seed = seed;
        root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IslandRpg", "Worlds");
        WorldDirectory = Path.Combine(
            root, worldDirectoryName ?? seed.ToString());
        _chunkDirectory = Path.Combine(WorldDirectory, "chunks");
        Directory.CreateDirectory(_chunkDirectory);
        var metadataPath = Path.Combine(WorldDirectory, "world.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            formatVersion = WorldFormatVersion,
            regionSize = RegionSize,
            compression = "brotli",
            seed,
            updatedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public WorldChunk LoadOrGenerate(ChunkCoordinate coordinate)
    {
        lock (_gate)
        {
            var payload = ReadRegionPayload(coordinate);
            if (payload is not null) return DeserializeChunk(payload, coordinate);
        }

        var legacyPath = LegacyChunkPath(coordinate);
        var hadLegacyChunk = File.Exists(legacyPath);
        var migrated = LoadLegacyChunk(coordinate);
        if (migrated is not null)
        {
            Save(migrated);
            DeleteLegacyChunk(legacyPath);
            return migrated;
        }
        var generated = InfiniteWorldGenerator.Generate(Seed, coordinate);
        if (hadLegacyChunk)
        {
            Save(generated);
            DeleteLegacyChunk(legacyPath);
        }
        return generated;
    }

    public void Save(WorldChunk chunk)
    {
        var uncompressed = SerializeChunk(chunk);
        var compressed = Compress(uncompressed);
        lock (_gate)
        {
            var region = RegionFor(chunk.Coordinate);
            var path = RegionPath(region.X, region.Y);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            EnsureRegionHeader(stream, region.X, region.Y);
            var slot = RegionSlot(chunk.Coordinate);
            var entryPosition = RegionHeaderSize + slot * RegionEntrySize;
            stream.Position = entryPosition;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var existingOffset = reader.ReadInt64();
            var existingLength = reader.ReadInt32();
            var existingUncompressedLength = reader.ReadInt32();
            if (existingOffset > 0 && existingLength > 0)
            {
                if (existingOffset + existingLength > stream.Length)
                    throw new InvalidDataException($"Region entry is invalid: {path}");
                stream.Position = existingOffset;
                var existingCompressed = new byte[existingLength];
                stream.ReadExactly(existingCompressed);
                var existing = Decompress(existingCompressed, existingUncompressedLength);
                if (existing.AsSpan().SequenceEqual(uncompressed)) return;
            }

            stream.Position = stream.Length;
            var payloadOffset = stream.Position;
            stream.Write(compressed);
            stream.Flush(flushToDisk: true);
            stream.Position = entryPosition;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(payloadOffset);
            writer.Write(compressed.Length);
            writer.Write(uncompressed.Length);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    internal string RegionPathFor(ChunkCoordinate coordinate)
    {
        var region = RegionFor(coordinate);
        return RegionPath(region.X, region.Y);
    }

    private byte[]? ReadRegionPayload(ChunkCoordinate coordinate)
    {
        var region = RegionFor(coordinate);
        var path = RegionPath(region.X, region.Y);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ValidateRegionHeader(stream, region.X, region.Y);
        stream.Position = RegionHeaderSize + RegionSlot(coordinate) * RegionEntrySize;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var offset = reader.ReadInt64();
        var compressedLength = reader.ReadInt32();
        var uncompressedLength = reader.ReadInt32();
        if (offset == 0 || compressedLength == 0) return null;
        if (offset < RegionHeaderSize + RegionSlotCount * RegionEntrySize ||
            compressedLength < 0 || uncompressedLength < 0 ||
            offset + compressedLength > stream.Length)
            throw new InvalidDataException($"Region entry is invalid: {path}");
        stream.Position = offset;
        var compressed = new byte[compressedLength];
        stream.ReadExactly(compressed);
        return Decompress(compressed, uncompressedLength);
    }

    private void EnsureRegionHeader(FileStream stream, int regionX, int regionY)
    {
        if (stream.Length > 0)
        {
            ValidateRegionHeader(stream, regionX, regionY);
            return;
        }
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(RegionMagic);
        writer.Write(RegionFormatVersion);
        writer.Write(Seed);
        writer.Write(regionX);
        writer.Write(regionY);
        writer.Write(RegionSlotCount);
        writer.Write(0);
        writer.Write(new byte[RegionSlotCount * RegionEntrySize]);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void ValidateRegionHeader(Stream stream, int regionX, int regionY)
    {
        if (stream.Length < RegionHeaderSize + RegionSlotCount * RegionEntrySize)
            throw new InvalidDataException("Region file is truncated.");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != RegionMagic || reader.ReadInt32() != RegionFormatVersion ||
            reader.ReadInt64() != Seed || reader.ReadInt32() != regionX ||
            reader.ReadInt32() != regionY || reader.ReadInt32() != RegionSlotCount)
            throw new InvalidDataException("Region header does not match this world.");
        _ = reader.ReadInt32();
    }

    private static byte[] SerializeChunk(WorldChunk chunk)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ChunkPayloadVersion);
            writer.Write(chunk.Coordinate.X);
            writer.Write(chunk.Coordinate.Y);
            writer.Write(chunk.Tiles.Length);
            foreach (var tile in chunk.Tiles)
            {
                writer.Write((byte)tile.Biome);
                writer.Write(tile.North); writer.Write(tile.East);
                writer.Write(tile.South); writer.Write(tile.West);
                writer.Write((byte)tile.Region);
            }
            writer.Write(chunk.Trees.Length);
            foreach (var tree in chunk.Trees)
            {
                writer.Write((byte)PositiveMod(tree.X, WorldChunk.Size));
                writer.Write((byte)PositiveMod(tree.Y, WorldChunk.Size));
                writer.Write(tree.GraphicName);
                writer.Write((byte)tree.FrameIndex);
            }
            writer.Write(chunk.TreeInstances.Count);
            foreach (var tree in chunk.TreeInstances)
            {
                writer.Write(tree.Id.ToByteArray());
                writer.Write((byte)PositiveMod(tree.X, WorldChunk.Size));
                writer.Write((byte)PositiveMod(tree.Y, WorldChunk.Size));
                writer.Write(tree.TreeType);
                writer.Write(tree.Health);
                writer.Write(tree.MaxHealth);
                writer.Write((byte)tree.State);
                writer.Write((sbyte)tree.SticksRemaining);
                writer.Write((sbyte)tree.InitialStickCount);
            }
            if (chunk.GroundObjects.Count > WorldChunk.MaximumStoredGroundObjects)
                throw new InvalidDataException(
                    $"Chunk contains too many ground objects: " +
                    $"{chunk.GroundObjects.Count}");
            writer.Write(chunk.GroundObjects.Count);
            foreach (var groundObject in chunk.GroundObjects)
            {
                writer.Write(groundObject.Id.ToByteArray());
                writer.Write(groundObject.ItemId);
                writer.Write(
                    groundObject.X -
                    MathF.Floor(groundObject.X / WorldChunk.Size) *
                    WorldChunk.Size);
                writer.Write(
                    groundObject.Y -
                    MathF.Floor(groundObject.Y / WorldChunk.Size) *
                    WorldChunk.Size);
                writer.Write(groundObject.FuelItemId ?? "");
                writer.Write(groundObject.LitUntilGameSeconds);
            }
            writer.Write(chunk.FishRemaining.Count);
            foreach (var school in chunk.FishRemaining)
            {
                writer.Write(school.Key);
                writer.Write(school.Value);
            }
            writer.Write(chunk.VegetationFibreStates.Count);
            foreach (var state in chunk.VegetationFibreStates)
            {
                writer.Write(state.StableKey);
                writer.Write(state.ReadyAtGameSeconds);
            }
        }
        return stream.ToArray();
    }

    private WorldChunk DeserializeChunk(byte[] payload, ChunkCoordinate coordinate)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload));
            var payloadVersion = reader.ReadInt32();
            var storedX = reader.ReadInt32();
            var storedY = reader.ReadInt32();
            if (payloadVersion < 1 || payloadVersion > ChunkPayloadVersion ||
                storedX != coordinate.X || storedY != coordinate.Y)
                throw new InvalidDataException($"Chunk payload does not match {coordinate}.");
            if (payloadVersion < 10)
                return InfiniteWorldGenerator.Generate(Seed, coordinate);
            var tileCount = reader.ReadInt32();
            if (tileCount != WorldChunk.Size * WorldChunk.Size)
                throw new InvalidDataException($"Chunk tile count is invalid: {tileCount}");
            var tiles = new IslandTile[tileCount];
            for (var i = 0; i < tileCount; i++)
            {
                var localX = i % WorldChunk.Size;
                var localY = i / WorldChunk.Size;
                var material = (Biome)reader.ReadByte();
                var north = reader.ReadByte();
                var east = reader.ReadByte();
                var south = reader.ReadByte();
                var west = reader.ReadByte();
                var region = payloadVersion >= 2
                    ? (WorldBiome)reader.ReadByte()
                    : InferWorldBiome(material);
                tiles[i] = new(
                    coordinate.X * WorldChunk.Size + localX,
                    coordinate.Y * WorldChunk.Size + localY,
                    material, north, east, south, west, region);
            }
            var treeCount = reader.ReadInt32();
            if (treeCount < 0 || treeCount > tileCount)
                throw new InvalidDataException($"Chunk tree count is invalid: {treeCount}");
            var trees = new IslandTree[treeCount];
            for (var i = 0; i < treeCount; i++)
            {
                var treeX = coordinate.X * WorldChunk.Size + reader.ReadByte();
                var treeY = coordinate.Y * WorldChunk.Size + reader.ReadByte();
                var graphicName = reader.ReadString();
                var frameIndex = payloadVersion >= 15
                    ? reader.ReadByte()
                    : WorldTreeCatalog.SelectFrame(
                        Seed, treeX, treeY, graphicName);
                if (frameIndex < 0 ||
                    frameIndex >= WorldTreeCatalog.FrameCount(graphicName))
                    throw new InvalidDataException(
                        $"Chunk tree frame is invalid: {graphicName}#{frameIndex}");
                trees[i] = new(treeX, treeY, graphicName, frameIndex);
            }
            var instanceCount = reader.ReadInt32();
            if (instanceCount < 0 || instanceCount > treeCount)
                throw new InvalidDataException(
                    $"Chunk tree-instance count is invalid: {instanceCount}");
            var treeInstances = new List<WorldTreeInstance>(instanceCount);
            for (var i = 0; i < instanceCount; i++)
            {
                var idBytes = reader.ReadBytes(16);
                if (idBytes.Length != 16)
                    throw new EndOfStreamException();
                var instance = new WorldTreeInstance(
                    new Guid(idBytes),
                    coordinate.X * WorldChunk.Size + reader.ReadByte(),
                    coordinate.Y * WorldChunk.Size + reader.ReadByte(),
                    reader.ReadString(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    (TreeLifecycleState)reader.ReadByte(),
                    payloadVersion >= 13 ? reader.ReadSByte() : -1,
                    payloadVersion >= 14
                        ? reader.ReadSByte()
                        : -1);
                if (instance.MaxHealth <= 0 || instance.Health < 0 ||
                    instance.Health > instance.MaxHealth ||
                    instance.SticksRemaining is < -1 or > 3 ||
                    instance.InitialStickCount is < -1 or > 3 ||
                    (instance.InitialStickCount >= 0 &&
                     instance.SticksRemaining > instance.InitialStickCount) ||
                    !Enum.IsDefined(instance.State))
                    throw new InvalidDataException(
                        $"Chunk tree instance is invalid: {instance.Id}");
                treeInstances.Add(instance);
            }
            List<WorldGroundObject> groundObjects;
            if (payloadVersion >= 11)
            {
                var groundObjectCount = reader.ReadInt32();
                if (groundObjectCount is < 0 or
                    > WorldChunk.MaximumStoredGroundObjects)
                    throw new InvalidDataException(
                        $"Chunk ground-object count is invalid: {groundObjectCount}");
                groundObjects = new(groundObjectCount);
                for (var i = 0; i < groundObjectCount; i++)
                {
                    var idBytes = reader.ReadBytes(16);
                    if (idBytes.Length != 16)
                        throw new EndOfStreamException();
                    var itemId = payloadVersion >= 12
                        ? reader.ReadString()
                        : reader.ReadByte() == 0
                            ? "sticks"
                            : "large_rock";
                    var groundObject = new WorldGroundObject(
                        new Guid(idBytes),
                        itemId,
                        coordinate.X * WorldChunk.Size + reader.ReadSingle(),
                        coordinate.Y * WorldChunk.Size + reader.ReadSingle(),
                        payloadVersion >= 18
                            ? NullIfEmpty(reader.ReadString())
                            : null,
                        payloadVersion >= 18
                            ? reader.ReadDouble()
                            : 0);
                    if (string.IsNullOrWhiteSpace(groundObject.ItemId) ||
                        groundObject.ItemId.Length > 64 ||
                        groundObject.FuelItemId?.Length > 64 ||
                        !double.IsFinite(
                            groundObject.LitUntilGameSeconds) ||
                        groundObject.LitUntilGameSeconds < 0 ||
                        FloorDiv((int)MathF.Floor(groundObject.X), WorldChunk.Size) != coordinate.X ||
                        FloorDiv((int)MathF.Floor(groundObject.Y), WorldChunk.Size) != coordinate.Y)
                        throw new InvalidDataException(
                            $"Chunk ground object is invalid: {groundObject.Id}");
                    groundObjects.Add(groundObject);
                }
            }
            else
                groundObjects = InfiniteWorldGenerator.GenerateGroundObjects(
                    Seed, tiles, trees);
            var fishRemaining = new Dictionary<string, int>(
                StringComparer.Ordinal);
            if (payloadVersion >= 16)
            {
                var fishSchoolCount = reader.ReadInt32();
                if (fishSchoolCount is < 0 or > WorldFishGenerator.MaximumPerChunk)
                    throw new InvalidDataException(
                        $"Chunk fish-school count is invalid: {fishSchoolCount}");
                for (var i = 0; i < fishSchoolCount; i++)
                {
                    var stableKey = reader.ReadString();
                    var remaining = reader.ReadInt32();
                    if (string.IsNullOrWhiteSpace(stableKey) ||
                        stableKey.Length > 96 || remaining < 0)
                        throw new InvalidDataException(
                            $"Chunk fish-school state is invalid: {stableKey}");
                    fishRemaining[stableKey] = remaining;
                }
            }
            var fibreStates = new List<WorldVegetationFibreState>();
            if (payloadVersion >= 17)
            {
                var stateCount = reader.ReadInt32();
                if (stateCount is < 0 or > 96)
                    throw new InvalidDataException(
                        $"Chunk vegetation-fibre count is invalid: {stateCount}");
                for (var i = 0; i < stateCount; i++)
                {
                    var stableKey = reader.ReadString();
                    var readyAt = reader.ReadDouble();
                    if (string.IsNullOrWhiteSpace(stableKey) ||
                        stableKey.Length > 96 ||
                        !double.IsFinite(readyAt) || readyAt < 0)
                        throw new InvalidDataException(
                            "Chunk vegetation-fibre state is invalid.");
                    fibreStates.Add(new(stableKey, readyAt));
                }
            }
            var weights = InfiniteWorldGenerator.GenerateBiomeWeights(Seed, coordinate);
            var cliffs = InfiniteWorldGenerator.GenerateCliffs(Seed, tiles);
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weights.A, BiomeWeightsB = weights.B,
                BiomeWeightsC = weights.C, BiomeWeightsD = weights.D,
                ShoreDistance = weights.Shore, Cliffs = cliffs,
                TreeInstances = treeInstances,
                GroundObjects = groundObjects,
                Vegetation = WorldVegetationGenerator.Generate(
                    Seed, tiles, trees),
                Fish = WorldFishGenerator.Generate(Seed, tiles),
                FishRemaining = fishRemaining,
                VegetationFibreStates = fibreStates
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Chunk payload is truncated: {coordinate}", ex);
        }
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static WorldBiome InferWorldBiome(Biome material) => material switch
    {
        Biome.DeepWater or Biome.ShallowWater => WorldBiome.Ocean,
        Biome.RiverWater => WorldBiome.River,
        Biome.MangroveShallows or Biome.Mud => WorldBiome.Wetland,
        Biome.Beach => WorldBiome.Coast,
        Biome.Forest => WorldBiome.TemperateForest,
        Biome.JungleFloor => WorldBiome.Rainforest,
        Biome.DryGrass => WorldBiome.Savanna,
        Biome.DesertSand or Biome.CrackedEarth => WorldBiome.Desert,
        Biome.Tundra => WorldBiome.Tundra,
        Biome.Highland or Biome.Rock => WorldBiome.Alpine,
        _ => WorldBiome.TemperateGrassland
    };

    private WorldChunk? LoadLegacyChunk(ChunkCoordinate coordinate)
    {
        var path = LegacyChunkPath(coordinate);
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != LegacyChunkMagic ||
                reader.ReadInt32() != LegacyChunkVersion ||
                reader.ReadInt64() != Seed || reader.ReadInt32() != coordinate.X ||
                reader.ReadInt32() != coordinate.Y)
                return null;
            var tileCount = reader.ReadInt32();
            var tiles = new IslandTile[tileCount];
            for (var i = 0; i < tileCount; i++)
            {
                var tileX = reader.ReadInt32();
                var tileY = reader.ReadInt32();
                var material = (Biome)reader.ReadByte();
                tiles[i] = new(tileX, tileY, material,
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(),
                    InferWorldBiome(material));
            }
            var treeCount = reader.ReadInt32();
            var trees = new IslandTree[treeCount];
            for (var i = 0; i < treeCount; i++)
            {
                var treeX = reader.ReadInt32();
                var treeY = reader.ReadInt32();
                var graphicName = reader.ReadString();
                trees[i] = new(
                    treeX, treeY, graphicName,
                    WorldTreeCatalog.SelectFrame(
                        Seed, treeX, treeY, graphicName));
            }
            var weightsALength = reader.ReadInt32();
            _ = reader.ReadBytes(weightsALength);
            var weightsBLength = reader.ReadInt32();
            _ = reader.ReadBytes(weightsBLength);
            var weights = InfiniteWorldGenerator.GenerateBiomeWeights(Seed, coordinate);
            var cliffs = InfiniteWorldGenerator.GenerateCliffs(Seed, tiles);
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weights.A, BiomeWeightsB = weights.B,
                BiomeWeightsC = weights.C, BiomeWeightsD = weights.D,
                ShoreDistance = weights.Shore, Cliffs = cliffs,
                Vegetation = WorldVegetationGenerator.Generate(
                    Seed, tiles, trees),
                Fish = WorldFishGenerator.Generate(Seed, tiles)
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Legacy chunk file is truncated: {path}", ex);
        }
    }

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(payload);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] payload, int expectedLength)
    {
        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        brotli.CopyTo(output);
        if (output.Length != expectedLength)
            throw new InvalidDataException(
                $"Chunk decompressed to {output.Length} bytes; expected {expectedLength}.");
        return output.ToArray();
    }

    private static (int X, int Y) RegionFor(ChunkCoordinate coordinate) =>
        (FloorDiv(coordinate.X, RegionSize), FloorDiv(coordinate.Y, RegionSize));

    private static int RegionSlot(ChunkCoordinate coordinate) =>
        PositiveMod(coordinate.Y, RegionSize) * RegionSize +
        PositiveMod(coordinate.X, RegionSize);

    private string RegionPath(int regionX, int regionY) =>
        Path.Combine(_chunkDirectory, $"r.{regionX}.{regionY}.irrg");

    private string LegacyChunkPath(ChunkCoordinate coordinate) =>
        Path.Combine(_chunkDirectory, $"c.{coordinate.X}.{coordinate.Y}.bin");

    private void DeleteLegacyChunk(string path)
    {
        var resolvedLegacy = Path.GetFullPath(path);
        var resolvedDirectory = Path.GetFullPath(_chunkDirectory) + Path.DirectorySeparatorChar;
        if (!resolvedLegacy.StartsWith(resolvedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a legacy chunk outside the world directory.");
        File.Delete(resolvedLegacy);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
