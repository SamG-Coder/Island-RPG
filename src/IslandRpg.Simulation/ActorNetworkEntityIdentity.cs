using System.Buffers.Binary;

namespace IslandRpg.Simulation;

/// <summary>
/// Canonical transport identity derivation for authoritative actors.
/// Actor entity IDs occupy the <c>00</c> high-bit namespace; enemies use
/// <c>01</c> and boats use <c>10</c>.
/// </summary>
public static class ActorNetworkEntityIdentity
{
    private const ulong PayloadMask = 0x3fff_ffff_ffff_ffffUL;

    public static ulong Derive(ActorId actorId)
    {
        if (actorId.Value == Guid.Empty)
            throw new ArgumentException("An actor identity is required.",
                nameof(actorId));

        Span<byte> bytes = stackalloc byte[16];
        actorId.Value.TryWriteBytes(bytes);
        var result = (BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
                      BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])) &
                     PayloadMask;
        return result == 0 ? 1 : result;
    }
}
