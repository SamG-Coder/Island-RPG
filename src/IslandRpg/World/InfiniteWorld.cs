using System.Text.Json;
using System.IO.Compression;

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

        var weights = GenerateBiomeWeights(seed, coordinate);
        return new()
        {
            Coordinate = coordinate,
            Tiles = tiles,
            Trees = trees.ToArray(),
            BiomeWeightsA = weights.A,
            BiomeWeightsB = weights.B
        };
    }

    internal static (byte[] A, byte[] B) GenerateBiomeWeights(long seed, ChunkCoordinate coordinate)
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
    internal const int RegionSize = 8;
    private const int WorldFormatVersion = 3;
    private const int RegionFormatVersion = 1;
    private const int ChunkPayloadVersion = 1;
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
            }
            writer.Write(chunk.Trees.Length);
            foreach (var tree in chunk.Trees)
            {
                writer.Write((byte)PositiveMod(tree.X, WorldChunk.Size));
                writer.Write((byte)PositiveMod(tree.Y, WorldChunk.Size));
                writer.Write(tree.GraphicName);
            }
        }
        return stream.ToArray();
    }

    private WorldChunk DeserializeChunk(byte[] payload, ChunkCoordinate coordinate)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload));
            if (reader.ReadInt32() != ChunkPayloadVersion ||
                reader.ReadInt32() != coordinate.X || reader.ReadInt32() != coordinate.Y)
                throw new InvalidDataException($"Chunk payload does not match {coordinate}.");
            var tileCount = reader.ReadInt32();
            if (tileCount != WorldChunk.Size * WorldChunk.Size)
                throw new InvalidDataException($"Chunk tile count is invalid: {tileCount}");
            var tiles = new IslandTile[tileCount];
            for (var i = 0; i < tileCount; i++)
            {
                var localX = i % WorldChunk.Size;
                var localY = i / WorldChunk.Size;
                tiles[i] = new(
                    coordinate.X * WorldChunk.Size + localX,
                    coordinate.Y * WorldChunk.Size + localY,
                    (Biome)reader.ReadByte(),
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            }
            var treeCount = reader.ReadInt32();
            if (treeCount < 0 || treeCount > tileCount)
                throw new InvalidDataException($"Chunk tree count is invalid: {treeCount}");
            var trees = new IslandTree[treeCount];
            for (var i = 0; i < treeCount; i++)
                trees[i] = new(
                    coordinate.X * WorldChunk.Size + reader.ReadByte(),
                    coordinate.Y * WorldChunk.Size + reader.ReadByte(),
                    reader.ReadString());
            var weights = InfiniteWorldGenerator.GenerateBiomeWeights(Seed, coordinate);
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weights.A, BiomeWeightsB = weights.B
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Chunk payload is truncated: {coordinate}", ex);
        }
    }

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
                tiles[i] = new(reader.ReadInt32(), reader.ReadInt32(), (Biome)reader.ReadByte(),
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            var treeCount = reader.ReadInt32();
            var trees = new IslandTree[treeCount];
            for (var i = 0; i < treeCount; i++)
                trees[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadString());
            var weightsALength = reader.ReadInt32();
            _ = reader.ReadBytes(weightsALength);
            var weightsBLength = reader.ReadInt32();
            _ = reader.ReadBytes(weightsBLength);
            var weights = InfiniteWorldGenerator.GenerateBiomeWeights(Seed, coordinate);
            return new()
            {
                Coordinate = coordinate, Tiles = tiles, Trees = trees,
                BiomeWeightsA = weights.A, BiomeWeightsB = weights.B
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
