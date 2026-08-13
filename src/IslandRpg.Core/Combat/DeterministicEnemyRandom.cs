namespace IslandRpg.Gameplay;

/// <summary>
/// Platform-independent enemy randomness. Callers supply stable entity IDs and
/// accepted-action sequences, so replay and checkpoint recovery do not depend
/// on process-local <see cref="Random"/> or <see cref="HashCode"/> state.
/// </summary>
public static class DeterministicEnemyRandom
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private const double Unit24 = 1.0 / (1 << 24);

    public static float UnitFloat(
        long worldSeed,
        Guid entityId,
        ulong sequence,
        ulong domain = 0)
    {
        var value = Seed(worldSeed, entityId, sequence, domain);
        return (float)((Mix(value) >> 40) * Unit24);
    }

    public static Guid StableGuid(
        long worldSeed,
        Guid entityId,
        ulong sequence,
        ulong domain = 0)
    {
        var first = Mix(Seed(worldSeed, entityId, sequence, domain));
        var second = Mix(Seed(
            worldSeed, entityId, sequence,
            domain ^ 0xD1B54A32D192ED03UL));
        Span<byte> bytes = stackalloc byte[16];
        WriteUInt64(bytes, first);
        WriteUInt64(bytes[8..], second);
        // Give generated entities an RFC 4122 variant/version marker. Besides
        // making diagnostics clearer, this guarantees a non-empty identifier.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static ulong Seed(
        long worldSeed,
        Guid entityId,
        ulong sequence,
        ulong domain)
    {
        var state = AppendUInt64(OffsetBasis, unchecked((ulong)worldSeed));
        Span<byte> bytes = stackalloc byte[16];
        entityId.TryWriteBytes(bytes, bigEndian: true, out _);
        foreach (var item in bytes) state = AppendByte(state, item);
        state = AppendUInt64(state, sequence);
        return AppendUInt64(state, domain);
    }

    private static ulong AppendUInt64(ulong hash, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            hash = AppendByte(hash, (byte)value);
            value >>= 8;
        }
        return hash;
    }

    private static ulong AppendByte(ulong hash, byte value) =>
        unchecked((hash ^ value) * Prime);

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value = unchecked(value * 0xBF58476D1CE4E5B9UL);
        value ^= value >> 27;
        value = unchecked(value * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    private static void WriteUInt64(Span<byte> destination, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            destination[index] = (byte)value;
            value >>= 8;
        }
    }
}
