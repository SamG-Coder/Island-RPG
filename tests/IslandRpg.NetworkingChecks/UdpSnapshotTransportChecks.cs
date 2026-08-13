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
        checks.Add(
            "UDP rotating deltas reconstruct complete mixed entity frames",
            RotatingDeltasReconstructCompleteFrames);
        checks.Add(
            "UDP loss and reordering preserve reconstructed membership",
            LossAndReorderingPreserveMembership);
        checks.Add(
            "reliable keyframes reconcile UDP state without stale resurrection",
            ReliableKeyframesRecoverMembership);
        checks.Add(
            "network client publishes only reconstructed complete snapshots",
            ClientPublishesOnlyCompleteSnapshots);
        checks.Add(
            "UDP reconstruction remains bounded under unknown additions",
            ReconstructionRejectsOverflowAtomically);
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

    private static void RotatingDeltasReconstructCompleteFrames()
    {
        const int count = UdpSnapshotCodec.MaxEntitiesPerDatagram * 2 + 9;
        var baseline = Enumerable.Range(1, count)
            .Select(index => Entity(
                (ulong)index,
                index,
                index % 2 == 0
                    ? NetworkEntityKind.Boat
                    : NetworkEntityKind.Player))
            .ToArray();
        var reconstructor = new EntitySnapshotReconstructor();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(1, 100, SnapshotFlags.Keyframe, baseline),
                out var initial),
            "the complete reliable baseline should seed reconstruction");
        AssertComplete(initial.Snapshot, count, 100);

        var firstWindow = baseline
            .Take(UdpSnapshotCodec.MaxEntitiesPerDatagram)
            .Select(entity => entity with { X = entity.X + 1_000 })
            .ToArray();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(0, 101, SnapshotFlags.Delta, firstWindow),
                out var first),
            "the first rotating interest window should merge");
        AssertComplete(first.Snapshot, count, 100);
        CheckAssert.Equal(
            1_001f,
            first.Snapshot.Entities.Single(entity => entity.EntityId == 1).X,
            "the first interest window must update its actor");
        CheckAssert.Equal(
            (float)count,
            first.Snapshot.Entities.Single(entity =>
                entity.EntityId == (ulong)count).X,
            "entities outside the interest window must retain prior state");

        var secondWindow = baseline
            .Skip(UdpSnapshotCodec.MaxEntitiesPerDatagram)
            .Take(UdpSnapshotCodec.MaxEntitiesPerDatagram)
            .Select(entity => entity with { X = entity.X + 2_000 })
            .ToArray();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(0, 102, SnapshotFlags.Delta, secondWindow),
                out var second),
            "the second rotating interest window should merge");
        AssertComplete(second.Snapshot, count, 100);
        CheckAssert.Equal(
            2_000f + UdpSnapshotCodec.MaxEntitiesPerDatagram + 1,
            second.Snapshot.Entities.Single(entity =>
                entity.EntityId ==
                    (ulong)UdpSnapshotCodec.MaxEntitiesPerDatagram + 1).X,
            "the second window must update without dropping the first");
        CheckAssert.Equal(
            1_001f,
            second.Snapshot.Entities.Single(entity => entity.EntityId == 1).X,
            "a later disjoint window must retain the earlier actor update");
    }

    private static void LossAndReorderingPreserveMembership()
    {
        const ulong token = 0xABCDEF;
        const int count = UdpSnapshotCodec.MaxEntitiesPerDatagram + 12;
        var baseline = Enumerable.Range(1, count)
            .Select(index => Entity((ulong)index, index))
            .ToArray();
        var reconstructor = new EntitySnapshotReconstructor();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(1, 200, SnapshotFlags.Keyframe, baseline),
                out _),
            "the reliable baseline should seed reconstruction");

        var receiver = new UdpSnapshotReceiver(token);
        var lost = UdpSnapshotCodec.Encode(
            Metadata(token, 10, 201) with { Flags = SnapshotFlags.Delta },
            baseline.Take(UdpSnapshotCodec.MaxEntitiesPerDatagram)
                .Select(entity => entity with { X = entity.X + 10 })
                .ToArray());
        var newer = UdpSnapshotCodec.Encode(
            Metadata(token, 11, 202) with { Flags = SnapshotFlags.Delta },
            baseline.Skip(UdpSnapshotCodec.MaxEntitiesPerDatagram)
                .Select(entity => entity with { X = entity.X + 20 })
                .ToArray());

        // Sequence 10 is deliberately lost. Sequence 11 still creates a
        // complete frame from the retained membership baseline.
        CheckAssert.True(receiver.TryDecode(newer, out var decoded),
            "the newer interest window should be accepted after loss");
        CheckAssert.True(
            reconstructor.TryReconstruct(decoded!, out var reconstructed),
            "the newer partial datagram should reconstruct");
        AssertComplete(reconstructed.Snapshot, count, 200);
        CheckAssert.Equal(
            1f,
            reconstructed.Snapshot.Entities.Single(entity =>
                entity.EntityId == 1).X,
            "a lost update must retain the last authoritative transform");
        CheckAssert.Equal(
            (float)count + 20,
            reconstructed.Snapshot.Entities.Single(entity =>
                entity.EntityId == (ulong)count).X,
            "the received rotating window must update normally");

        CheckAssert.False(receiver.TryDecode(lost, out _),
            "a late datagram must be rejected by UDP sequence ordering");
        CheckAssert.False(
            reconstructor.TryReconstruct(
                Snapshot(0, 201, SnapshotFlags.Delta, [Entity(1, 999)]),
                out _),
            "a stale tick must not overwrite a newer reconstructed frame");
    }

    private static void ReliableKeyframesRecoverMembership()
    {
        var baseline = Enumerable.Range(1, 70)
            .Select(index => Entity((ulong)index, index))
            .ToArray();
        var reconstructor = new EntitySnapshotReconstructor();
        var interpolation = new SnapshotInterpolationBuffer(
            TimeSpan.Zero,
            capacity: 4);
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(1, 300, SnapshotFlags.Keyframe, baseline),
                out var initial) && interpolation.Add(initial.Snapshot, 1),
            "the initial keyframe should seed render state");

        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(0, 305, SnapshotFlags.Delta,
                [Entity(1, 305), Entity(71, 305)]),
                out var udp) && interpolation.Add(udp.Snapshot, 2),
            "newer UDP state should advance the render frame");

        // This reliable keyframe was in flight while tick 305 arrived over
        // UDP. It removes entity 2, retains the post-keyframe spawn 71, and
        // cannot roll entity 1 back from its newer UDP transform.
        var delayedMembers = baseline
            .Where(entity => entity.EntityId != 2)
            .Select(entity => entity.EntityId == 1
                ? entity with { X = 303 }
                : entity)
            .ToArray();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(2, 303, SnapshotFlags.Keyframe, delayedMembers),
                out var recovered),
            "a delayed newer keyframe should reconcile membership");
        CheckAssert.True(recovered.ReplacesLatestFrame,
            "delayed membership recovery must replace the latest frame");
        CheckAssert.True(interpolation.ReplaceLatest(recovered.Snapshot),
            "the interpolation buffer should accept same-tick reconciliation");
        CheckAssert.Equal(305ul, recovered.Snapshot.Metadata.ServerTick,
            "delayed recovery must not rewind the effective render tick");
        CheckAssert.False(recovered.Snapshot.Entities.Any(entity =>
                entity.EntityId == 2),
            "the keyframe must remove an entity absent from its membership");
        CheckAssert.Equal(305f, recovered.Snapshot.Entities.Single(entity =>
                entity.EntityId == 1).X,
            "the delayed keyframe must not roll back a newer transform");
        CheckAssert.True(recovered.Snapshot.Entities.Any(entity =>
                entity.EntityId == 71),
            "an entity first observed after the delayed keyframe must survive");

        CheckAssert.True(interpolation.TrySample(out var sampled, 3),
            "the reconciled frame should remain sampleable");
        CheckAssert.False(sampled!.Entities.Any(entity =>
                entity.EntityId == 2),
            "despawned membership must be removed from the latest render frame");

        var definitiveMembers = delayedMembers
            .Where(entity => entity.EntityId != 71)
            .ToArray();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(3, 310, SnapshotFlags.Keyframe, definitiveMembers),
                out var definitive),
            "the next keyframe should become the definitive membership");
        CheckAssert.False(definitive.Snapshot.Entities.Any(entity =>
                entity.EntityId is 2 or 71),
            "later keyframes must remove stale entities without resurrection");
        CheckAssert.False(
            reconstructor.TryReconstruct(
                Snapshot(4, 309, SnapshotFlags.Delta, [Entity(71, 999)]),
                out _),
            "a reordered pre-keyframe delta must not resurrect a removed entity");
    }

    private static void ClientPublishesOnlyCompleteSnapshots()
    {
        var client = new NetworkGameClient(TimeSpan.Zero);
        try
        {
            const int count = UdpSnapshotCodec.MaxEntitiesPerDatagram + 8;
            var baseline = Enumerable.Range(1, count)
                .Select(index => Entity(
                    (ulong)index,
                    index,
                    index % 3 == 0
                        ? NetworkEntityKind.Boat
                        : NetworkEntityKind.Player))
                .ToArray();
            EntitySnapshotMessage? published = null;
            client.SnapshotReceived += (_, args) => published = args.Snapshot;

            client.ConsumeSnapshot(
                Snapshot(1, 400, SnapshotFlags.Keyframe, baseline));
            client.ConsumeSnapshot(
                Snapshot(0, 401, SnapshotFlags.Delta,
                [Entity((ulong)count, 4_001, NetworkEntityKind.Boat)]));

            CheckAssert.Equal(count, client.State.Entities.Count,
                "client state must retain entities omitted by a UDP delta");
            CheckAssert.Equal(count, published!.Entities.Count,
                "snapshot subscribers must receive complete reconstructed frames");
            CheckAssert.Equal(SnapshotFlags.Keyframe, published.Metadata.Flags,
                "snapshot subscribers must never mistake a partial frame for complete state");
            CheckAssert.Equal(
                4_001f,
                client.State.Entities[(ulong)count].X,
                "client state must apply the entity carried by the delta");
            CheckAssert.True(client.SnapshotBuffer.TrySample(out var sampled),
                "the complete reconstructed frame must reach interpolation");
            CheckAssert.Equal(count, sampled!.Entities.Count,
                "the renderer must never sample a rotating subset as a frame");

            client.ConsumeSnapshot(
                Snapshot(2, 402, SnapshotFlags.Keyframe,
                    baseline.Where(entity => entity.EntityId != 1).ToArray()));
            CheckAssert.False(client.State.Entities.ContainsKey(1),
                "a full keyframe must remove despawned membership from client state");
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void ReconstructionRejectsOverflowAtomically()
    {
        var baseline = Enumerable.Range(1, ProtocolLimits.MaxSnapshotEntities)
            .Select(index => Entity((ulong)index, index))
            .ToArray();
        var reconstructor = new EntitySnapshotReconstructor();
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(1, 500, SnapshotFlags.Keyframe, baseline),
                out _),
            "the protocol maximum should seed reconstruction");
        CheckAssert.False(
            reconstructor.TryReconstruct(
                Snapshot(0, 501, SnapshotFlags.Delta,
                    [Entity((ulong)ProtocolLimits.MaxSnapshotEntities + 1, 1)]),
                out _),
            "an unknown delta entity beyond the bound must be rejected");
        CheckAssert.True(
            reconstructor.TryReconstruct(
                Snapshot(0, 502, SnapshotFlags.Delta, [Entity(1, 502)]),
                out var after),
            "a rejected overflow must not poison later valid updates");
        CheckAssert.Equal(ProtocolLimits.MaxSnapshotEntities,
            after.Snapshot.Entities.Count,
            "overflow rejection must preserve bounded membership atomically");
        CheckAssert.False(after.Snapshot.Entities.Any(entity =>
                entity.EntityId ==
                (ulong)ProtocolLimits.MaxSnapshotEntities + 1),
            "a rejected addition must not leak into reconstructed state");
    }

    private static void AssertComplete(
        EntitySnapshotMessage snapshot,
        int expectedCount,
        ulong expectedBaselineTick)
    {
        CheckAssert.Equal(expectedCount, snapshot.Entities.Count,
            "each reconstructed render frame must contain full membership");
        CheckAssert.Equal(SnapshotFlags.Keyframe, snapshot.Metadata.Flags,
            "reconstructed frames must be explicitly complete");
        CheckAssert.Equal(expectedBaselineTick, snapshot.Metadata.BaselineTick,
            "reconstructed frames must identify their membership keyframe");
    }

    private static EntitySnapshotMessage Snapshot(
        ulong sequence,
        ulong tick,
        SnapshotFlags flags,
        IReadOnlyList<EntitySnapshot> entities) => new(
            sequence,
            tick,
            new SnapshotMetadata(101, 0, 0, 0, tick, 0, flags),
            entities);

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

    private static EntitySnapshot Entity(
        ulong id,
        float x,
        NetworkEntityKind kind = NetworkEntityKind.Player) => new(
        id,
        kind,
        0,
        0,
        x,
        0,
        0,
        0,
        NetworkEntityState.None,
        1);
}
