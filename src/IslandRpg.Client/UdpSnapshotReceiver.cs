using IslandRpg.Protocol;

namespace IslandRpg.Client;

/// <summary>
/// Validates the session-bound token and monotonic UDP sequence before any
/// snapshot reaches client state. Its decode storage is fixed at the wire
/// maximum, so malformed datagrams cannot drive unbounded allocation.
/// </summary>
internal sealed class UdpSnapshotReceiver
{
    private readonly EntitySnapshot[] _decodeBuffer =
        new EntitySnapshot[UdpSnapshotCodec.MaxEntitiesPerDatagram];
    private readonly ulong _datagramToken;
    private bool _hasSequence;
    private ushort _latestSequence;

    public UdpSnapshotReceiver(ulong datagramToken)
    {
        if (datagramToken == 0)
            throw new ArgumentOutOfRangeException(nameof(datagramToken));
        _datagramToken = datagramToken;
    }

    public bool TryDecode(
        ReadOnlySpan<byte> datagram,
        out EntitySnapshotMessage? snapshot)
    {
        snapshot = null;
        if (!UdpSnapshotCodec.TryDecode(
                datagram,
                _decodeBuffer,
                out var metadata,
                out var count) ||
            metadata.DatagramToken != _datagramToken ||
            _hasSequence && !SnapshotSequence.IsNewer(
                metadata.Sequence,
                _latestSequence))
        {
            return false;
        }

        _hasSequence = true;
        _latestSequence = metadata.Sequence;
        var entities = new EntitySnapshot[count];
        _decodeBuffer.AsSpan(0, count).CopyTo(entities);
        snapshot = new EntitySnapshotMessage(
            0,
            metadata.ServerTick,
            metadata,
            Array.AsReadOnly(entities));
        return true;
    }
}
