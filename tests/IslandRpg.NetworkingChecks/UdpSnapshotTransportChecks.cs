using IslandRpg.Client;
using IslandRpg.Protocol;

namespace IslandRpg.NetworkingChecks;

internal static class UdpSnapshotTransportChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "UDP snapshots remain within the safe datagram bound",
            DatagramBoundsAreEnforced);
        checks.Add(
            "UDP receiver rejects invalid tokens and reordered sequences",
            ReceiverAuthenticatesAndRejectsReordering);
        checks.Add(
            "UDP snapshot sequence comparison handles wraparound",
            SequenceWraparoundIsMonotonic);
        checks.Add(
            "UDP delta snapshots merge without forgetting prior entities",
            DeltaSnapshotsMergeAcrossInterestWindows);
    }

    private static void DatagramBoundsAreEnforced()
    {
        var entities = Enumerable.Range(0, UdpSnapshotCodec.MaxEntitiesPerDatagram)
            .Select(index => Entity((ulong)index + 1, index))
            .ToArray();
        Span<byte> datagram = stackalloc byte[ProtocolConstants.MaxUdpDatagramBytes];
        var metadata = Metadata(77, 1, 60);
        CheckAssert.True(
            UdpSnapshotCodec.TryEncode(metadata, entities, datagram, out var length),
            "the maximum safe entity payload should encode");
        CheckAssert.True(
            length <= ProtocolConstants.MaxUdpDatagramBytes,
            "encoded snapshots must never exceed 1200 bytes");
        CheckAssert.False(
            UdpSnapshotCodec.TryEncode(
                metadata,
                entities.Append(Entity(999, 0)).ToArray(),
                datagram,
                out _),
            "one entity beyond the safe payload must be rejected before sending");
    }

    private static void ReceiverAuthenticatesAndRejectsReordering()
    {
        const ulong token = 0x1122334455667788;
        var receiver = new UdpSnapshotReceiver(token);

        var first = UdpSnapshotCodec.Encode(
            Metadata(token, 41, 120),
            [Entity(7, 1)]);
        CheckAssert.True(
            receiver.TryDecode(first, out var accepted),
            "a token-bound first snapshot should be accepted");
        CheckAssert.Equal(
            (ulong)120,
            accepted!.Metadata.ServerTick,
            "the accepted snapshot must preserve its authoritative tick");

        var wrongToken = UdpSnapshotCodec.Encode(
            Metadata(token + 1, 42, 123),
            [Entity(7, 2)]);
        CheckAssert.False(
            receiver.TryDecode(wrongToken, out _),
            "another session token must never update client state");

        var newer = UdpSnapshotCodec.Encode(
            Metadata(token, 43, 126),
            [Entity(7, 3)]);
        CheckAssert.True(
            receiver.TryDecode(newer, out _),
            "a newer authenticated snapshot should be accepted");
        CheckAssert.False(
            receiver.TryDecode(first, out _),
            "a reordered older snapshot must be rejected");
        CheckAssert.False(
            receiver.TryDecode(newer, out _),
            "a duplicate snapshot must be rejected");
    }

    private static void SequenceWraparoundIsMonotonic()
    {
        const ulong token = 99;
        var receiver = new UdpSnapshotReceiver(token);
        CheckAssert.True(
            receiver.TryDecode(UdpSnapshotCodec.Encode(
                Metadata(token, ushort.MaxValue, 1),
                [Entity(1, 0)]), out _),
            "the pre-wrap snapshot should be accepted");
        CheckAssert.True(
            receiver.TryDecode(UdpSnapshotCodec.Encode(
                Metadata(token, 0, 2),
                [Entity(1, 1)]), out _),
            "zero must be newer immediately after sequence wraparound");
    }

    private static void DeltaSnapshotsMergeAcrossInterestWindows()
    {
        // The transport receiver preserves delta flags for the client state
        // merger. Codec coverage here protects the >32-entity rotation path
        // from accidentally becoming a destructive partial keyframe.
        const ulong token = 101;
        var receiver = new UdpSnapshotReceiver(token);
        var delta = UdpSnapshotCodec.Encode(
            Metadata(token, 1, 3) with { Flags = SnapshotFlags.Delta },
            [Entity(33, 5)]);
        CheckAssert.True(receiver.TryDecode(delta, out var decoded),
            "a bounded authenticated delta should decode");
        CheckAssert.True(
            decoded!.Metadata.Flags.HasFlag(SnapshotFlags.Delta),
            "interest-window snapshots must retain delta semantics");
    }

    private static SnapshotMetadata Metadata(
        ulong token,
        ushort sequence,
        ulong tick) => new(
            token,
            sequence,
            0,
            0,
            tick,
            0,
            SnapshotFlags.Keyframe);

    private static EntitySnapshot Entity(ulong id, float x) => new(
        id,
        NetworkEntityKind.Player,
        0,
        0,
        x,
        0,
        0,
        0,
        NetworkEntityState.None,
        1);
}
