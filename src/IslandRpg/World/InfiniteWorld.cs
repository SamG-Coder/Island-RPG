using System.Text.Json;

namespace IslandRpg.World;

internal readonly record struct ChunkCoordinate(int X, int Y)
{
    public override string ToString() => $"{X},{Y}";
}

internal sealed class WorldChunk
{
    public const int Size = 32;
    public const int WeightSamplesPerTile = 4;
    public const int WeightHaloTiles = 8;
    public const int WeightTextureSize = (Size + WeightHaloTiles * 2) * WeightSamplesPerTile;
    public required ChunkCoordinate Coordinate { get; init; }
    public required IslandTile[] Tiles { get; init; }
    public required IslandTree[] Trees { get; init; }
    public required byte[] BiomeWeightsA { get; init; }
    public required byte[] BiomeWeightsB { get; init; }
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
            var moisture = FractalNoise(seed ^ 0x5deece66dL, worldX / 70f, worldY / 70f, 3);
            var biome = average switch
            {
                < .35f => Biome.DeepWater,
                < .85f => Biome.ShallowWater,
                < 1.45f => Biome.Beach,
                > 6.5f => Biome.Rock,
                > 4.6f => Biome.Highland,
                _ when moisture > -.08f => Biome.Forest,
                _ => Biome.Grassland
            };
            tiles[y * WorldChunk.Size + x] = new(
                worldX, worldY, biome,
                Surface(heights[x, y]), Surface(heights[x + 1, y]),
                Surface(heights[x + 1, y + 1]), Surface(heights[x, y + 1]));

            var chance = biome == Biome.Forest ? .18f :
                biome == Biome.Highland ? .055f : biome == Biome.Beach ? .012f : 0;
            if (UnitHash(seed, worldX, worldY, 91) >= chance) continue;
            var variant = (int)(UnitHash(seed, worldX, worldY, 137) * 12) % 12;
            var graphic = biome switch
            {
                Biome.Beach => "FPAL_NN",
                Biome.Highland => "FPIN_NN",
                _ => $"TREE{(char)('A' + variant)}_NN"
            };
            trees.Add(new(worldX, worldY, graphic));
        }

        var weights = BuildBiomeWeights(seed, coordinate);
        return new()
        {
            Coordinate = coordinate,
            Tiles = tiles,
            Trees = trees.ToArray(),
            BiomeWeightsA = weights.A,
            BiomeWeightsB = weights.B
        };
    }

    private static (byte[] A, byte[] B) BuildBiomeWeights(long seed, ChunkCoordinate coordinate)
    {
        const int radius = 10;
        var size = WorldChunk.WeightTextureSize;
        var channels = Enum.GetValues<Biome>().Length;
        var weights = new float[size * size * channels];
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
                weights[pixel * channels + (int)biome] = 1;
                water[pixel] = biome is Biome.DeepWater or Biome.ShallowWater;
            }
        }

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
                if (channel < 4) a[pixel * 4 + channel] = value;
                else b[pixel * 4 + channel - 4] = value;
            }
        }

        var distanceToWater = DistanceTo(targetWater: true);
        var distanceToLand = DistanceTo(targetWater: false);
        const float encodedRangeTiles = 8;
        for (var pixel = 0; pixel < size * size; pixel++)
        {
            var signedSamples = water[pixel] ? distanceToLand[pixel] : -distanceToWater[pixel];
            var signedTiles = signedSamples / WorldChunk.WeightSamplesPerTile;
            b[pixel * 4 + 3] = (byte)Math.Clamp(
                MathF.Round((signedTiles / encodedRangeTiles * .5f + .5f) * 255), 0, 255);
        }
        return (a, b);

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

    private static Biome BiomeAt(long seed, int x, int y)
    {
        var average = (HeightAt(seed, x, y) + HeightAt(seed, x + 1, y) +
                       HeightAt(seed, x + 1, y + 1) + HeightAt(seed, x, y + 1)) / 4f;
        var moisture = FractalNoise(seed ^ 0x5deece66dL, x / 70f, y / 70f, 3);
        return average switch
        {
            < .35f => Biome.DeepWater,
            < .85f => Biome.ShallowWater,
            < 1.45f => Biome.Beach,
            > 6.5f => Biome.Rock,
            > 4.6f => Biome.Highland,
            _ when moisture > -.08f => Biome.Forest,
            _ => Biome.Grassland
        };
    }

    private static byte HeightAt(long seed, int x, int y)
    {
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

        var detail = FractalNoise(seed ^ 0x13198a2e03707344L, x / 19f, y / 19f, 3) * .55f;
        return (byte)Math.Clamp((int)MathF.Floor((island - .03f) * 8.5f + detail), 0, 9);
    }

    private static byte Surface(byte height) => height <= 2 ? (byte)0 : height;

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

    private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;
}

internal sealed class WorldChunkStore
{
    private const int FormatVersion = 2;
    private const int Magic = 0x49524348; // IRCH
    private readonly string _chunkDirectory;

    public string WorldDirectory { get; }
    public long Seed { get; }

    public WorldChunkStore(long seed, string? root = null)
    {
        Seed = seed;
        root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IslandRpg", "Worlds");
        WorldDirectory = Path.Combine(root, seed.ToString());
        _chunkDirectory = Path.Combine(WorldDirectory, "chunks");
        Directory.CreateDirectory(_chunkDirectory);
        var metadataPath = Path.Combine(WorldDirectory, "world.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            formatVersion = FormatVersion,
            seed,
            updatedUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public WorldChunk LoadOrGenerate(ChunkCoordinate coordinate)
    {
        var path = ChunkPath(coordinate);
        if (!File.Exists(path)) return InfiniteWorldGenerator.Generate(Seed, coordinate);
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var magic = reader.ReadInt32();
            var version = reader.ReadInt32();
            if (magic != Magic || version != FormatVersion)
                return InfiniteWorldGenerator.Generate(Seed, coordinate);
            if (reader.ReadInt64() != Seed || reader.ReadInt32() != coordinate.X ||
                reader.ReadInt32() != coordinate.Y)
                throw new InvalidDataException($"Chunk header is invalid: {path}");
            var tileCount = reader.ReadInt32();
            var tiles = new IslandTile[tileCount];
            for (var i = 0; i < tileCount; i++)
                tiles[i] = new(reader.ReadInt32(), reader.ReadInt32(), (Biome)reader.ReadByte(),
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            var treeCount = reader.ReadInt32();
            var trees = new IslandTree[treeCount];
            for (var i = 0; i < treeCount; i++)
                trees[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadString());
            var weightsA = reader.ReadBytes(reader.ReadInt32());
            var weightsB = reader.ReadBytes(reader.ReadInt32());
            var expectedBytes = WorldChunk.WeightTextureSize * WorldChunk.WeightTextureSize * 4;
            if (weightsA.Length != expectedBytes || weightsB.Length != expectedBytes)
                throw new InvalidDataException($"Chunk biome weights are invalid: {path}");
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weightsA, BiomeWeightsB = weightsB
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Chunk file is truncated: {path}", ex);
        }
    }

    public void Save(WorldChunk chunk)
    {
        var path = ChunkPath(chunk.Coordinate);
        var temporary = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Seed);
            writer.Write(chunk.Coordinate.X);
            writer.Write(chunk.Coordinate.Y);
            writer.Write(chunk.Tiles.Length);
            foreach (var tile in chunk.Tiles)
            {
                writer.Write(tile.X); writer.Write(tile.Y); writer.Write((byte)tile.Biome);
                writer.Write(tile.North); writer.Write(tile.East);
                writer.Write(tile.South); writer.Write(tile.West);
            }
            writer.Write(chunk.Trees.Length);
            foreach (var tree in chunk.Trees)
            {
                writer.Write(tree.X); writer.Write(tree.Y); writer.Write(tree.GraphicName);
            }
            writer.Write(chunk.BiomeWeightsA.Length);
            writer.Write(chunk.BiomeWeightsA);
            writer.Write(chunk.BiomeWeightsB.Length);
            writer.Write(chunk.BiomeWeightsB);
        }
        File.Move(temporary, path, true);
    }

    private string ChunkPath(ChunkCoordinate coordinate) =>
        Path.Combine(_chunkDirectory, $"c.{coordinate.X}.{coordinate.Y}.bin");
}
