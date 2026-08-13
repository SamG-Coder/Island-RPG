using System.Buffers.Binary;
using System.Security.Cryptography;
using IslandRpg.Simulation;

namespace IslandRpg.Resources;

/// <summary>
/// Generator-owned address of a procedural resource. Integer source
/// coordinates and ordinals avoid locale-dependent formatted float keys.
/// </summary>
public readonly record struct ProceduralResourceKey(
    ResourceNodeKind Kind,
    int SourceX,
    int SourceY,
    int Ordinal,
    int Variant)
{
    public static ProceduralResourceKey Tree(
        int tileX,
        int tileY,
        int variant = 0) =>
        new(ResourceNodeKind.Tree, tileX, tileY, 0, variant);

    public static ProceduralResourceKey Vegetation(
        ResourceNodeKind kind,
        int sourceTileX,
        int sourceTileY,
        int ordinal,
        int variant = 0)
    {
        if (kind is not ResourceNodeKind.FibreShrub and
            not ResourceNodeKind.BerryBush)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return new(kind, sourceTileX, sourceTileY, ordinal, variant);
    }

    public static ProceduralResourceKey Mining(
        int sourceTileX,
        int sourceTileY,
        int ordinal,
        int variant) =>
        new(ResourceNodeKind.MiningNode, sourceTileX, sourceTileY,
            ordinal, variant);

    public static ProceduralResourceKey Fish(
        int tileX,
        int tileY,
        int species) =>
        new(ResourceNodeKind.FishSchool, tileX, tileY, 0, species);
}

/// <summary>
/// Architecture- and culture-independent resource identity derivation. All
/// numeric fields are written in network byte order before SHA-256 hashing.
/// </summary>
public static class ProceduralResourceIdentity
{
    private const uint Schema = 1;
    public const int MaximumOrdinal = 65_535;
    public const int MaximumVariant = 65_535;
    // These are the only world layers supported by the shared terrain model.
    // Keeping identity validation aligned with navigation prevents an
    // untrusted resource claim from reaching a generator with an unknown
    // level identifier.
    public const int MinimumWorldLevel = -1;
    public const int MaximumWorldLevel = 0;
    public const int MinimumCoordinate = -1_000_000;
    // The upper bound is exclusive. This makes complete 32-unit chunks at
    // both edges symmetrical and avoids accepting a mostly out-of-world
    // positive boundary chunk.
    public const int MaximumCoordinate = 1_000_000;

    public static ResourceNodeId Derive(
        long worldSeed,
        WorldChunkKey chunk,
        ProceduralResourceKey key)
    {
        Validate(chunk, key);

        // Domain + schema + seed + chunk + source + kind/ordinal/variant.
        // A fixed-size binary payload prevents ambiguous concatenation and
        // HashData avoids allocating a hasher per generated node.
        Span<byte> payload = stackalloc byte[60];
        payload.Clear();
        "IRPG-RESOURCE"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32BigEndian(payload[16..20], Schema);
        BinaryPrimitives.WriteInt64BigEndian(payload[20..28], worldSeed);
        BinaryPrimitives.WriteInt32BigEndian(payload[28..32], chunk.X);
        BinaryPrimitives.WriteInt32BigEndian(payload[32..36], chunk.Y);
        BinaryPrimitives.WriteInt32BigEndian(payload[36..40], chunk.WorldLevel);
        BinaryPrimitives.WriteInt32BigEndian(payload[40..44], key.SourceX);
        BinaryPrimitives.WriteInt32BigEndian(payload[44..48], key.SourceY);
        payload[48] = (byte)key.Kind;
        BinaryPrimitives.WriteInt32BigEndian(payload[52..56], key.Ordinal);
        BinaryPrimitives.WriteInt32BigEndian(payload[56..60], key.Variant);

        Span<byte> digest = stackalloc byte[32];
        if (SHA256.HashData(payload, digest) != digest.Length)
        {
            throw new CryptographicException(
                "Unable to derive a procedural resource identity.");
        }

        // Mark the value as an RFC 9562 name-based custom UUID (version 8)
        // while retaining 122 bits of the digest.
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new ResourceNodeId(new Guid(digest[..16], bigEndian: true));
    }

    public static ResourceNodeId ForTree(
        long worldSeed,
        int worldLevel,
        int tileX,
        int tileY,
        int variant = 0)
    {
        var chunk = ChunkAt(tileX, tileY, worldLevel);
        return Derive(worldSeed, chunk,
            ProceduralResourceKey.Tree(tileX, tileY, variant));
    }

    public static ResourceNodeId ForVegetation(
        long worldSeed,
        WorldChunkKey chunk,
        ResourceNodeKind kind,
        int sourceTileX,
        int sourceTileY,
        int ordinal,
        int variant = 0) =>
        Derive(worldSeed, chunk, ProceduralResourceKey.Vegetation(
            kind, sourceTileX, sourceTileY, ordinal, variant));

    public static ResourceNodeId ForMining(
        long worldSeed,
        WorldChunkKey chunk,
        int sourceTileX,
        int sourceTileY,
        int ordinal,
        int variant) =>
        Derive(worldSeed, chunk, ProceduralResourceKey.Mining(
            sourceTileX, sourceTileY, ordinal, variant));

    public static ResourceNodeId ForFish(
        long worldSeed,
        int worldLevel,
        int tileX,
        int tileY,
        int species)
    {
        var chunk = ChunkAt(tileX, tileY, worldLevel);
        return Derive(worldSeed, chunk,
            ProceduralResourceKey.Fish(tileX, tileY, species));
    }

    internal static bool IsValid(
        WorldChunkKey chunk,
        ProceduralResourceKey key)
    {
        try
        {
            Validate(chunk, key);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void Validate(
        WorldChunkKey chunk,
        ProceduralResourceKey key)
    {
        if (!Enum.IsDefined(key.Kind))
            throw new ArgumentOutOfRangeException(nameof(key));
        if (chunk.WorldLevel is < MinimumWorldLevel or > MaximumWorldLevel)
            throw new ArgumentOutOfRangeException(nameof(chunk));
        if (key.SourceX is < MinimumCoordinate or >= MaximumCoordinate ||
            key.SourceY is < MinimumCoordinate or >= MaximumCoordinate)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        if (key.Ordinal is < 0 or > MaximumOrdinal)
            throw new ArgumentOutOfRangeException(nameof(key));
        if (key.Variant is < 0 or > MaximumVariant)
            throw new ArgumentOutOfRangeException(nameof(key));
        if (ChunkAt(key.SourceX, key.SourceY, chunk.WorldLevel) != chunk)
            throw new ArgumentOutOfRangeException(nameof(key),
                "The procedural source coordinate is outside the claimed chunk.");
    }

    private static WorldChunkKey ChunkAt(int x, int y, int worldLevel) =>
        new(FloorDiv(x, WorldChunkKey.Size),
            FloorDiv(y, WorldChunkKey.Size), worldLevel);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}
