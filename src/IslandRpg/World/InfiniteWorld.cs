using System.Text.Json;
using System.IO.Compression;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using OpenTK.Mathematics;

namespace IslandRpg.World;

internal sealed record CliffFace(int X1, int Y1, int X2, int Y2, byte Top, byte Bottom);
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
internal sealed record WorldMiningState(
    string StableKey,
    int Health,
    int MaxHealth);

internal static class WorldMiningIdentity
{
    public static string StableKey(
        WorldVegetation value,
        int ordinal)
    {
        var variant = UndergroundMiningCatalog.TryGetVisual(
            value.GraphicName, out var visual)
                ? (int)visual.Variant
                : 0;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"mining-v1:{(int)MathF.Floor(value.X)}:" +
            $"{(int)MathF.Floor(value.Y)}:{ordinal}:{variant}");
    }

    public static IEnumerable<string> LegacyKeys(WorldVegetation value)
    {
        // Older saves used the process culture while interpolating floats.
        // Accept that spelling plus invariant/English saves during the
        // one-way upgrade to the typed key.
        yield return $"vegetation:{value.X:0.000}:{value.Y:0.000}";
        var invariant = FormattableString.Invariant(
            $"vegetation:{value.X:0.000}:{value.Y:0.000}");
        if (!invariant.Equals(
                $"vegetation:{value.X:0.000}:{value.Y:0.000}",
                StringComparison.Ordinal))
            yield return invariant;
    }

    public static void UpgradeLegacyKeys(WorldChunk chunk)
    {
        if (chunk.MiningStates.Count == 0) return;
        for (var index = 0; index < chunk.Vegetation.Length; index++)
        {
            var value = chunk.Vegetation[index];
            if (!UndergroundMiningCatalog.TryGetVisual(
                    value.GraphicName, out _))
                continue;
            var legacyKeys = LegacyKeys(value).ToHashSet(
                StringComparer.Ordinal);
            var stateIndex = chunk.MiningStates.FindIndex(state =>
                legacyKeys.Contains(state.StableKey));
            if (stateIndex < 0) continue;
            chunk.MiningStates[stateIndex] =
                chunk.MiningStates[stateIndex] with
                {
                    StableKey = StableKey(value, index)
                };
        }
    }
}

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
    public bool[] RenderableTiles { get; init; } = [];
    public float[] UndergroundDensity { get; init; } = [];
    public float[] UndergroundMeshVertices { get; set; } = [];
    public Vector4 UndergroundProjectedBounds { get; set; }
    public List<WorldTreeInstance> TreeInstances { get; init; } = [];
    public List<WorldGroundObject> GroundObjects { get; init; } = [];
    public HashSet<Guid> InitialGroundObjectIds { get; init; } = [];
    public WorldVegetation[] Vegetation { get; init; } = [];
    public WorldFish[] Fish { get; init; } = [];
    public List<WorldVegetationFibreState> VegetationFibreStates
        { get; init; } = [];
    public List<WorldMiningState> MiningStates { get; init; } = [];
    public Dictionary<string, int> FishRemaining { get; init; } =
        new(StringComparer.Ordinal);

    public bool IsRenderable(int localX, int localY) =>
        RenderableTiles.Length == 0 ||
        RenderableTiles[localY * Size + localX];

    public float SampleUndergroundDensity(float localX, float localY)
    {
        if (UndergroundDensity.Length == 0) return float.MinValue;
        var sampleX = Math.Clamp(
            localX * UndergroundWorldGenerator.SamplesPerTile,
            0, UndergroundWorldGenerator.DensityStride - 1);
        var sampleY = Math.Clamp(
            localY * UndergroundWorldGenerator.SamplesPerTile,
            0, UndergroundWorldGenerator.DensityStride - 1);
        var x0 = (int)MathF.Floor(sampleX);
        var y0 = (int)MathF.Floor(sampleY);
        var x1 = Math.Min(x0 + 1, UndergroundWorldGenerator.DensityStride - 1);
        var y1 = Math.Min(y0 + 1, UndergroundWorldGenerator.DensityStride - 1);
        var tx = sampleX - x0;
        var ty = sampleY - y0;
        var stride = UndergroundWorldGenerator.DensityStride;
        return Lerp(
            Lerp(
                UndergroundDensity[y0 * stride + x0],
                UndergroundDensity[y0 * stride + x1], tx),
            Lerp(
                UndergroundDensity[y1 * stride + x0],
                UndergroundDensity[y1 * stride + x1], tx), ty);

        static float Lerp(float first, float second, float amount) =>
            first + (second - first) * amount;
    }
}

internal static class InfiniteWorldGenerator
{
    private const int IslandCellSize = 192;

    public static WorldChunk Generate(
        long seed,
        ChunkCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        if (coordinate.Level == (int)WorldLevel.Underground)
            return UndergroundWorldGenerator.Generate(
                seed, coordinate, cancellationToken);
        if (coordinate.Level != (int)WorldLevel.Overworld)
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                $"World level {coordinate.Level} is not supported.");
        var originX = coordinate.X * WorldChunk.Size;
        var originY = coordinate.Y * WorldChunk.Size;
        var heights = new byte[WorldChunk.Size + 1, WorldChunk.Size + 1];
        for (var y = 0; y <= WorldChunk.Size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x <= WorldChunk.Size; x++)
                heights[x, y] =
                    HeightAt(seed, originX + x, originY + y);
        }

        var tiles = new IslandTile[WorldChunk.Size * WorldChunk.Size];
        var trees = new List<IslandTree>();
        for (var y = 0; y < WorldChunk.Size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < WorldChunk.Size; x++)
            {
                var worldX = originX + x;
                var worldY = originY + y;
                var average = (heights[x, y] + heights[x + 1, y] +
                               heights[x + 1, y + 1] +
                               heights[x, y + 1]) / 4f;
                var (biome, region) =
                    ClassifyAt(seed, worldX, worldY, average);
                tiles[y * WorldChunk.Size + x] = new(
                    worldX, worldY, biome,
                    Surface(heights[x, y]),
                    Surface(heights[x + 1, y]),
                    Surface(heights[x + 1, y + 1]),
                    Surface(heights[x, y + 1]),
                    region);

                if (!SurfaceTreeCatalog.TryDescribeAt(
                        seed,
                        worldX,
                        worldY,
                        (ProceduralSurfaceTerrain.Region)region,
                        (ProceduralSurfaceTerrain.Material)biome,
                        average,
                        out var treeVisual))
                    continue;
                trees.Add(new(
                    worldX,
                    worldY,
                    treeVisual.GraphicName,
                    treeVisual.FrameIndex));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var weights = GenerateBiomeWeights(seed, coordinate);
        cancellationToken.ThrowIfCancellationRequested();
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
            RenderableTiles = Enumerable.Repeat(
                true, WorldChunk.Size * WorldChunk.Size).ToArray(),
            GroundObjects = groundObjects,
            Vegetation = vegetation,
            Fish = fish
        };
    }

    internal readonly record struct GroundObjectGenerationOptions(
        bool IncludeSticks,
        bool IncludeRocks,
        bool IncludeCoastal)
    {
        public static GroundObjectGenerationOptions Overworld =>
            new(true, true, true);
        public static GroundObjectGenerationOptions Underground =>
            new(false, true, false);
    }

    internal static List<WorldGroundObject> GenerateGroundObjects(
        long seed,
        IslandTile[] tiles,
        IReadOnlyCollection<IslandTree> trees,
        GroundObjectGenerationOptions? options = null,
        IReadOnlyList<bool>? renderable = null,
        IReadOnlySet<(int X, int Y)>? reservedTiles = null)
    {
        var rules = options ?? GroundObjectGenerationOptions.Overworld;
        if (rules.IncludeSticks &&
            TryCompleteChunk(tiles, out var chunk))
        {
            var catalogObjects = ProceduralGroundLootCatalog.DescribeChunk(
                    seed, chunk)
                .Where(placement =>
                    AllowsCatalogItem(rules, placement.ItemId) &&
                    !IsExcludedTile(
                        tiles,
                        renderable,
                        reservedTiles,
                        (int)MathF.Floor(placement.X),
                        (int)MathF.Floor(placement.Y)))
                .Select(static placement => new WorldGroundObject(
                    placement.Id,
                    placement.ItemId,
                    placement.X,
                    placement.Y))
                .ToList();
            if (rules.IncludeCoastal)
            {
                catalogObjects.AddRange(
                    ProceduralCoastalLootCatalog.DescribeChunk(seed, chunk)
                        .Where(placement =>
                            !IsExcludedTile(
                                tiles,
                                renderable,
                                reservedTiles,
                                (int)MathF.Floor(placement.X),
                                (int)MathF.Floor(placement.Y)))
                        .Select(static placement => new WorldGroundObject(
                            placement.Id,
                            placement.ItemId,
                            placement.X,
                            placement.Y)));
            }
            return catalogObjects;
        }

        const int maximumPerChunk = 8;
        var occupied = trees.Select(tree => (tree.X, tree.Y)).ToHashSet();
        var candidates = new List<(float Score, WorldGroundObject Object)>();
        for (var tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
        {
            if (renderable is not null && !renderable[tileIndex]) continue;
            var tile = tiles[tileIndex];
            if (occupied.Contains((tile.X, tile.Y)) ||
                reservedTiles?.Contains((tile.X, tile.Y)) == true ||
                tile.Biome is Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows)
                continue;
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
            if (!rules.IncludeSticks) stickChance = 0;
            var rockChance = tile.Region switch
            {
                WorldBiome.Alpine => .055f,
                WorldBiome.Tundra or WorldBiome.Coast => .024f,
                WorldBiome.Desert => .018f,
                WorldBiome.TemperateGrassland => .008f,
                _ => 0
            };
            if (!rules.IncludeRocks) rockChance = 0;
            var cropSeedChance = rules.IncludeSticks
                ? tile.Region switch
                {
                    WorldBiome.TemperateGrassland => .018f,
                    WorldBiome.Savanna => .012f,
                    WorldBiome.Wetland => .009f,
                    _ => 0
                }
                : 0;
            var roll = UnitHash(seed, tile.X, tile.Y, 811);
            string? itemId = roll < stickChance
                ? ItemIds.Sticks
                : roll < stickChance + rockChance
                    ? ItemIds.LargeRock
                    : roll < stickChance + rockChance + cropSeedChance
                        ? SelectCropSeed(
                            UnitHash(seed, tile.X, tile.Y, 817))
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
        if (rules.IncludeCoastal)
            objects.AddRange(
                CoastalCollectibleSpawner.GenerateInitial(
                    seed, tiles, trees, objects));
        return objects;
    }

    private static bool TryCompleteChunk(
        IReadOnlyList<IslandTile> tiles,
        out WorldChunkKey chunk)
    {
        chunk = default;
        if (tiles.Count != WorldChunk.Size * WorldChunk.Size)
            return false;
        chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(tiles[0].X, tiles[0].Y), 0);
        var originX = chunk.X * WorldChunk.Size;
        var originY = chunk.Y * WorldChunk.Size;
        for (var index = 0; index < tiles.Count; index++)
        {
            var expectedX = originX + index % WorldChunk.Size;
            var expectedY = originY + index / WorldChunk.Size;
            if (tiles[index].X != expectedX || tiles[index].Y != expectedY)
                return false;
        }
        return true;
    }

    private static bool AllowsCatalogItem(
        GroundObjectGenerationOptions rules,
        string itemId)
    {
        if (itemId == ItemIds.Sticks) return rules.IncludeSticks;
        if (itemId == ItemIds.LargeRock) return rules.IncludeRocks;
        return rules.IncludeSticks;
    }

    private static bool IsExcludedTile(
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool>? renderable,
        IReadOnlySet<(int X, int Y)>? reservedTiles,
        int tileX,
        int tileY)
    {
        if (reservedTiles?.Contains((tileX, tileY)) == true)
            return true;
        for (var index = 0; index < tiles.Count; index++)
        {
            if (tiles[index].X != tileX || tiles[index].Y != tileY)
                continue;
            return renderable is not null && !renderable[index];
        }
        return true;
    }

    private static string SelectCropSeed(float roll) => roll switch
    {
        < 1f / 3f => ItemIds.WildGrainSeeds,
        < 2f / 3f => ItemIds.BeanSeeds,
        _ => ItemIds.RootSeeds
    };

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
        ProceduralSurfaceTerrain.SampleSurfaceHeight(seed, x, y);

    internal static float SampleRenderedHeight(long seed, float x, float y) =>
        ProceduralSurfaceTerrain.SampleRenderedHeight(seed, x, y);

    internal static (byte[] A, byte[] B, byte[] C, byte[] D, byte[] Shore) GenerateBiomeWeights(
        long seed, ChunkCoordinate coordinate)
    {
        var size = WorldChunk.WeightTextureSize;
        var labels = new byte[size * size];
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
                water[pixel] = biome is Biome.DeepWater or Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows;
            }
        }
        var blended = BiomeWeightBlender.Build(labels, size);
        var a = blended.A;
        var b = blended.B;
        var c = blended.C;
        var d = blended.D;
        var shore = new byte[size * size];

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

    internal static Biome BiomeAt(long seed, int x, int y) =>
        (Biome)ProceduralSurfaceTerrain.ClassifyAt(seed, x, y).Material;

    internal static float BaseElevationAt(long seed, int x, int y) =>
        ProceduralSurfaceTerrain.BaseElevationAt(seed, x, y);

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

    private static byte HeightAt(long seed, int x, int y) =>
        ProceduralSurfaceTerrain.RawHeightAt(seed, x, y);

    private static (Biome Material, WorldBiome Region) ClassifyAt(
        long seed, int x, int y, float elevation)
    {
        var classification = ProceduralSurfaceTerrain.ClassifyAt(
            seed, x, y, elevation);
        return ((Biome)classification.Material, (WorldBiome)classification.Region);
    }

    internal static float RainfallAt(long seed, int x, int y) =>
        ProceduralSurfaceTerrain.RainfallAt(seed, x, y);

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
    private const int WorldFormatVersion = 5;
    private const int RegionFormatVersion = 1;
    private const int ChunkPayloadVersion = 27;
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

    public WorldChunk LoadOrGenerate(
        ChunkCoordinate coordinate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var payload = ReadRegionPayload(coordinate);
            if (payload is not null)
            {
                var loaded = DeserializeChunk(payload, coordinate);
                cancellationToken.ThrowIfCancellationRequested();
                return loaded;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (coordinate.Level != (int)WorldLevel.Overworld)
            return InfiniteWorldGenerator.Generate(
                Seed, coordinate, cancellationToken);
        var legacyPath = LegacyChunkPath(coordinate);
        var hadLegacyChunk = File.Exists(legacyPath);
        var migrated = LoadLegacyChunk(coordinate);
        if (migrated is not null)
        {
            Save(migrated);
            DeleteLegacyChunk(legacyPath);
            return migrated;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var generated = InfiniteWorldGenerator.Generate(
            Seed, coordinate, cancellationToken);
        if (hadLegacyChunk)
        {
            Save(generated);
            DeleteLegacyChunk(legacyPath);
        }
        return generated;
    }

    public void Save(WorldChunk chunk)
    {
        // Deterministic underground ground objects need no payload until the
        // player removes one or introduces another mutable object.
        if (!NeedsPersistence(chunk))
            return;
        var uncompressed = SerializeChunk(chunk);
        var compressed = Compress(uncompressed);
        lock (_gate)
        {
            var region = RegionFor(chunk.Coordinate);
            var path = RegionPath(
                region.X, region.Y, chunk.Coordinate.Level);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    internal static bool NeedsPersistence(WorldChunk chunk) =>
        chunk.Coordinate.Level == (int)WorldLevel.Overworld ||
        chunk.GroundObjects.Count != chunk.InitialGroundObjectIds.Count ||
        chunk.GroundObjects.Any(value =>
            !chunk.InitialGroundObjectIds.Contains(value.Id)) ||
        chunk.MiningStates.Count != 0;

    internal string RegionPathFor(ChunkCoordinate coordinate)
    {
        var region = RegionFor(coordinate);
        return RegionPath(region.X, region.Y, coordinate.Level);
    }

    private byte[]? ReadRegionPayload(ChunkCoordinate coordinate)
    {
        var region = RegionFor(coordinate);
        var path = RegionPath(
            region.X, region.Y, coordinate.Level);
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
            writer.Write(chunk.Coordinate.Level);
            writer.Write(chunk.Tiles.Length);
            foreach (var tile in chunk.Tiles)
            {
                writer.Write((byte)tile.Biome);
                writer.Write(tile.North); writer.Write(tile.East);
                writer.Write(tile.South); writer.Write(tile.West);
                writer.Write((byte)tile.Region);
            }
            writer.Write(chunk.RenderableTiles.Length);
            foreach (var renderable in chunk.RenderableTiles)
                writer.Write(renderable);
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
                writer.Write(groundObject.FiremakingLevel);
                writer.Write(groundObject.Health);
                writer.Write(groundObject.MaxHealth);
                var container = groundObject.Container;
                if (container is not null &&
                    (container.Items.Length !=
                         container.Quantities.Length ||
                     container.Items.Length > 256))
                    throw new InvalidDataException(
                        "Ground-object container state is invalid.");
                writer.Write(container?.Items.Length ?? 0);
                if (container is not null)
                    for (var slot = 0;
                         slot < container.Items.Length;
                         slot++)
                    {
                        writer.Write(container.Items[slot] ?? "");
                        writer.Write(
                            slot < container.Quantities.Length
                                ? container.Quantities[slot]
                                : 0);
                        writer.Write(
                            container.OwnerIds is { } owners &&
                            slot < owners.Length
                                ? owners[slot] ?? ""
                                : "");
                    }
                writer.Write(groundObject.OwnerId ?? "");
                writer.Write(groundObject.GroupOwnerId ?? "");
                writer.Write(groundObject.VisualFrame);
                writer.Write((byte)groundObject.GateState);
                var residents = groundObject.ResidentIds ?? [];
                if (residents.Length > BuildingOwnershipService.MaximumResidents)
                    throw new InvalidDataException(
                        "A building has too many residents.");
                writer.Write(residents.Length);
                foreach (var residentId in residents)
                    writer.Write(residentId);
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
            writer.Write(chunk.MiningStates.Count);
            foreach (var state in chunk.MiningStates)
            {
                writer.Write(state.StableKey);
                writer.Write(state.Health);
                writer.Write(state.MaxHealth);
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
            var storedLevel = payloadVersion >= 19
                ? reader.ReadInt32()
                : (int)WorldLevel.Overworld;
            if (payloadVersion < 1 || payloadVersion > ChunkPayloadVersion ||
                storedX != coordinate.X || storedY != coordinate.Y ||
                storedLevel != coordinate.Level)
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
            bool[] renderableTiles;
            if (payloadVersion >= 19)
            {
                var renderableCount = reader.ReadInt32();
                if (renderableCount != 0 &&
                    renderableCount != tileCount)
                    throw new InvalidDataException(
                        "Chunk renderable-tile count is invalid.");
                renderableTiles = new bool[renderableCount];
                for (var index = 0; index < renderableCount; index++)
                    renderableTiles[index] = reader.ReadBoolean();
            }
            else
                renderableTiles = [];
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
                            : 0,
                        payloadVersion >= 23
                            ? reader.ReadInt32()
                            : 1,
                        payloadVersion >= 20
                            ? reader.ReadInt32()
                            : 0,
                        payloadVersion >= 20
                            ? reader.ReadInt32()
                            : 0);
                    if (payloadVersion >= 24)
                    {
                        var containerSlots = reader.ReadInt32();
                        if (containerSlots is < 0 or > 256)
                            throw new InvalidDataException(
                                "Chunk container capacity is invalid.");
                        if (containerSlots > 0)
                        {
                            var items = new string?[containerSlots];
                            var quantities = new int[containerSlots];
                            var ownerIds = new string?[containerSlots];
                            for (var slot = 0;
                                 slot < containerSlots;
                                 slot++)
                            {
                                items[slot] =
                                    NullIfEmpty(reader.ReadString());
                                quantities[slot] = reader.ReadInt32();
                                if (payloadVersion >= 26)
                                    ownerIds[slot] =
                                        NullIfEmpty(reader.ReadString());
                                if (items[slot]?.Length > 64 ||
                                    ownerIds[slot]?.Length > 64 ||
                                    quantities[slot] < 0 ||
                                    (items[slot] is null) !=
                                    (quantities[slot] == 0))
                                    throw new InvalidDataException(
                                        "Chunk container slot is invalid.");
                            }
                            groundObject = groundObject with
                            {
                                Container = new(items, quantities, ownerIds)
                            };
                        }
                    }
                    if (payloadVersion >= 25)
                        groundObject = groundObject with
                        {
                            OwnerId = NullIfEmpty(reader.ReadString())
                        };
                    if (payloadVersion >= 27)
                    {
                        var groupOwnerId = NullIfEmpty(reader.ReadString());
                        var visualFrame = reader.ReadInt32();
                        var gateState = (GateAccessState)reader.ReadByte();
                        var residentCount = reader.ReadInt32();
                        if (!Enum.IsDefined(gateState) ||
                            residentCount is < 0 or >
                                BuildingOwnershipService.MaximumResidents)
                            throw new InvalidDataException(
                                "Chunk building state is invalid.");
                        var residents = new string[residentCount];
                        for (var resident = 0;
                             resident < residentCount;
                             resident++)
                            residents[resident] = reader.ReadString();
                        groundObject = groundObject with
                        {
                            GroupOwnerId = groupOwnerId,
                            VisualFrame = visualFrame,
                            GateState = gateState,
                            ResidentIds = residents
                        };
                    }
                    if (string.IsNullOrWhiteSpace(groundObject.ItemId) ||
                        groundObject.ItemId.Length > 64 ||
                        groundObject.OwnerId?.Length > 64 ||
                        groundObject.GroupOwnerId?.Length > 64 ||
                        groundObject.ResidentIds?.Any(id =>
                            string.IsNullOrWhiteSpace(id) || id.Length > 64) == true ||
                        groundObject.FuelItemId?.Length > 64 ||
                        !double.IsFinite(
                            groundObject.LitUntilGameSeconds) ||
                        groundObject.LitUntilGameSeconds < 0 ||
                        groundObject.FiremakingLevel is < 1 or > 100 ||
                        groundObject.Health < 0 ||
                        groundObject.MaxHealth < 0 ||
                        groundObject.Health > groundObject.MaxHealth ||
                        FloorDiv((int)MathF.Floor(groundObject.X), WorldChunk.Size) != coordinate.X ||
                        FloorDiv((int)MathF.Floor(groundObject.Y), WorldChunk.Size) != coordinate.Y)
                        throw new InvalidDataException(
                            $"Chunk ground object is invalid: {groundObject.Id}");
                    groundObjects.Add(groundObject);
                }
            }
            else
                groundObjects = InfiniteWorldGenerator.GenerateGroundObjects(
                    Seed,
                    tiles,
                    trees,
                    coordinate.Level == (int)WorldLevel.Underground
                        ? InfiniteWorldGenerator
                            .GroundObjectGenerationOptions.Underground
                        : InfiniteWorldGenerator
                            .GroundObjectGenerationOptions.Overworld,
                    renderableTiles);
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
            var miningStates = new List<WorldMiningState>();
            if (payloadVersion >= 22)
            {
                var stateCount = reader.ReadInt32();
                if (stateCount is < 0 or > 128)
                    throw new InvalidDataException(
                        "Chunk mining-state count is invalid.");
                for (var i = 0; i < stateCount; i++)
                {
                    var stableKey = reader.ReadString();
                    var health = reader.ReadInt32();
                    var maxHealth = reader.ReadInt32();
                    if (string.IsNullOrWhiteSpace(stableKey) ||
                        stableKey.Length > 96 ||
                        maxHealth <= 0 || health < 0 || health > maxHealth)
                        throw new InvalidDataException(
                            "Chunk mining state is invalid.");
                    miningStates.Add(new(stableKey, health, maxHealth));
                }
            }
            if (coordinate.Level == (int)WorldLevel.Underground)
            {
                // Derived presentation data is deterministic and deliberately
                // excluded from the payload.
                var generated = UndergroundWorldGenerator.Generate(
                    Seed, coordinate);
                if (payloadVersion >= 21)
                    generated.GroundObjects.Clear();
                generated.TreeInstances.AddRange(treeInstances);
                generated.GroundObjects.AddRange(groundObjects);
                foreach (var school in fishRemaining)
                    generated.FishRemaining[school.Key] = school.Value;
                generated.VegetationFibreStates.AddRange(fibreStates);
                generated.MiningStates.AddRange(miningStates);
                WorldMiningIdentity.UpgradeLegacyKeys(generated);
                return generated;
            }
            var weights = InfiniteWorldGenerator.GenerateBiomeWeights(
                Seed, coordinate);
            var cliffs = InfiniteWorldGenerator.GenerateCliffs(Seed, tiles);
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weights.A, BiomeWeightsB = weights.B,
                BiomeWeightsC = weights.C, BiomeWeightsD = weights.D,
                ShoreDistance = weights.Shore, Cliffs = cliffs,
                RenderableTiles = renderableTiles,
                TreeInstances = treeInstances,
                GroundObjects = groundObjects,
                Vegetation = WorldVegetationGenerator.Generate(
                    Seed, tiles, trees),
                Fish = WorldFishGenerator.Generate(Seed, tiles),
                FishRemaining = fishRemaining,
                VegetationFibreStates = fibreStates,
                MiningStates = miningStates
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

    private string RegionPath(int regionX, int regionY, int level) =>
        Path.Combine(
            level == (int)WorldLevel.Overworld
                ? _chunkDirectory
                : Path.Combine(
                    WorldDirectory, "levels",
                    level.ToString(), "chunks"),
            $"r.{regionX}.{regionY}.irrg");

    private string LegacyChunkPath(ChunkCoordinate coordinate) =>
        coordinate.Level == (int)WorldLevel.Overworld
            ? Path.Combine(
                _chunkDirectory,
                $"c.{coordinate.X}.{coordinate.Y}.bin")
            : Path.Combine(
                WorldDirectory, "levels",
                coordinate.Level.ToString(), "chunks",
                $"c.{coordinate.X}.{coordinate.Y}.bin");

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
