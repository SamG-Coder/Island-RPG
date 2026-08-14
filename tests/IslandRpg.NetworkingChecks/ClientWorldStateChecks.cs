using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IslandRpg.Client;
using IslandRpg.Protocol;

namespace IslandRpg.NetworkingChecks;

internal static class ClientWorldStateChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string BuildVersion = "client-world-checks";
    private const string ContentVersion = "client-world-content";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "network client applies world baselines upserts and removals",
            AppliesWorldObjectChangesAsync);
        checks.Add(
            "network client rejects stale world state without resurrection",
            RejectsStaleWorldStateAsync);
        checks.Add(
            "network client atomically merges private container deltas",
            MergesPrivateContainerStateAsync);
        checks.Add(
            "network client signals identical container open baselines",
            SignalsIdenticalContainerOpenBaselinesAsync);
        checks.Add(
            "network client preserves container state on revision mismatch",
            PreservesContainerOnMismatchAsync);
        checks.Add(
            "network client applies every object in one chunk transaction",
            AppliesMultiObjectChunkTransactionAsync);
        checks.Add(
            "network client receives empty chunk revision baselines",
            ReceivesEmptyChunkRevisionBaselineAsync);
        checks.Add(
            "network client rejects malformed chunk revisions atomically",
            RejectsMalformedChunkRevisionBaselineAsync);
    }

    private static async ValueTask AppliesWorldObjectChangesAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var id = Guid.NewGuid();
        var linkedId = Guid.NewGuid();
        var baseline = World(id, 2, 2, 70, linkedId);
        var observed = 0;
        WorldObjectDeltaKind lastKind = default;
        client.WorldObjectsChanged += (_, args) =>
        {
            lastKind = args.Changes[^1].Kind;
            Interlocked.Add(ref observed, args.Changes.Count);
        };

        await peer.SendAsync(
            new WorldObjectStateMessage(2, 100, baseline),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.WorldObjects.TryGetValue(id, out var value) &&
                value.ObjectRevision == 2,
            "the world-object baseline was not applied",
            cancellationToken);
        CheckAssert.Equal(linkedId,
            client.State.WorldObjects[id].LinkedObjectId,
            "the public projection must retain an exact cave endpoint link");

        var upsert = World(id, 3, 3, 85);
        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                3,
                101,
                [Upsert(baseline, upsert)]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.WorldObjects[id].ObjectRevision == 3,
            "the newer world-object upsert was not applied",
            cancellationToken);
        CheckAssert.Equal(85, client.State.WorldObjects[id].Health,
            "a newer upsert must replace the public object projection");

        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                4,
                102,
                [new(
                    WorldObjectDeltaKind.Remove,
                    Reference(upsert),
                    4,
                    null)]),
            cancellationToken);
        await EventuallyAsync(
            () => !client.State.WorldObjects.ContainsKey(id) &&
                Volatile.Read(ref observed) == 3,
            "the matching world-object removal was not applied",
            cancellationToken);

        CheckAssert.Equal(3, Volatile.Read(ref observed),
            "each accepted baseline, upsert and removal must raise one change");
        CheckAssert.Equal(WorldObjectDeltaKind.Remove, lastKind,
            "the final public change must describe removal");
        CheckAssert.Equal(102ul, client.State.ServerTick,
            "world-object messages must advance the observed server tick");
    }

    private static async ValueTask RejectsStaleWorldStateAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var id = Guid.NewGuid();
        var latest = World(id, 8, 8, 90);
        var changeCount = 0;
        client.WorldObjectsChanged += (_, _) =>
            Interlocked.Increment(ref changeCount);
        await peer.SendAsync(
            new WorldObjectStateMessage(2, 200, latest),
            cancellationToken);
        await EventuallyAsync(
            () => Volatile.Read(ref changeCount) == 1,
            "the stale-state check did not receive its baseline",
            cancellationToken);

        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                3,
                201,
                [new(
                    WorldObjectDeltaKind.Remove,
                    Reference(latest),
                    9,
                    null)]),
            cancellationToken);
        await EventuallyAsync(
            () => Volatile.Read(ref changeCount) == 2,
            "the stale-state check did not receive its removal",
            cancellationToken);

        var stale = World(id, 7, 7, 10);
        await peer.SendAsync(
            new WorldObjectStateMessage(4, 202, stale),
            cancellationToken);
        await Task.Delay(50, cancellationToken);
        CheckAssert.False(client.State.WorldObjects.ContainsKey(id),
            "a stale upsert must not resurrect a removed object");
        CheckAssert.Equal(2, Volatile.Read(ref changeCount),
            "a rejected stale upsert must not raise a public change event");

        var newer = World(id, 10, 9, 55);
        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                5,
                203,
                [new(
                    WorldObjectDeltaKind.Upsert,
                    new WorldObjectReference(id, 3, -2, 0, 8, 9),
                    10,
                    newer)]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.WorldObjects.TryGetValue(id, out var value) &&
                value.ObjectRevision == 9,
            "a genuinely newer upsert did not replace the tombstone",
            cancellationToken);

        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                6,
                204,
                [new(
                    WorldObjectDeltaKind.Remove,
                    new WorldObjectReference(id, 3, -2, 0, 8, 8),
                    9,
                    null)]),
            cancellationToken);
        await Task.Delay(50, cancellationToken);
        CheckAssert.Equal(9u, client.State.WorldObjects[id].ObjectRevision,
            "an older removal must not erase a newer visible object");
    }

    private static async ValueTask MergesPrivateContainerStateAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var id = Guid.NewGuid();
        var events = 0;
        client.ContainerStateChanged += (_, _) =>
            Interlocked.Increment(ref events);
        var baselineSlots = new[]
        {
            new ContainerSlotState(0, "slime_gel", 4),
            new ContainerSlotState(1, string.Empty, 0),
            new ContainerSlotState(2, "slime_core", 1),
        };

        await peer.SendAsync(
            Container(
                2,
                300,
                id,
                0,
                4,
                true,
                baselineSlots),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.Containers.TryGetValue(id, out var value) &&
                value.ContainerRevision == 4,
            "the private container baseline was not applied",
            cancellationToken);

        await peer.SendAsync(
            Container(
                3,
                301,
                id,
                4,
                5,
                false,
                [
                    new ContainerSlotState(0, string.Empty, 0),
                    new ContainerSlotState(1, "slime_gel", 2),
                ],
                slotCount: 3),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.Containers[id].ContainerRevision == 5,
            "the private container delta was not applied",
            cancellationToken);

        var merged = client.State.Containers[id];
        CheckAssert.Equal(
            new WorldObjectReference(id, 3, -2, 0, 5, 0),
            merged.Reference,
            "a container delta must retain its exact current world reference");
        CheckAssert.SequenceEqual(
            new[]
            {
                new ContainerSlotState(0, string.Empty, 0),
                new ContainerSlotState(1, "slime_gel", 2),
                new ContainerSlotState(2, "slime_core", 1),
            },
            merged.Slots,
            "a container delta must replace only its indexed slots");
        CheckAssert.Equal(2, Volatile.Read(ref events),
            "the baseline and accepted delta must each raise an event");
        CheckAssert.False(client.State.WorldObjects.ContainsKey(id),
            "private container contents must not create a public world object");
    }

    private static async ValueTask PreservesContainerOnMismatchAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var id = Guid.NewGuid();
        await peer.SendAsync(
            Container(
                2,
                400,
                id,
                0,
                10,
                true,
                [
                    new ContainerSlotState(0, "slime_gel", 3),
                    new ContainerSlotState(1, string.Empty, 0),
                ]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.Containers.ContainsKey(id),
            "the mismatch check did not receive its baseline",
            cancellationToken);
        var accepted = client.State.Containers[id];
        var eventCount = 0;
        client.ContainerStateChanged += (_, _) =>
            Interlocked.Increment(ref eventCount);

        await peer.SendAsync(
            Container(
                3,
                401,
                id,
                9,
                11,
                false,
                [new ContainerSlotState(0, "corrupt", 1)],
                slotCount: 2),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the client did not fault on a mismatched container chain",
            cancellationToken);

        CheckAssert.True(
            client.State.LastError?.Contains(
                "current revision",
                StringComparison.OrdinalIgnoreCase) == true,
            "the container fault must identify its revision mismatch");
        CheckAssert.True(ReferenceEquals(accepted, client.State.Containers[id]),
            "a rejected container delta must preserve the prior immutable state");
        CheckAssert.SequenceEqual(
            new[]
            {
                new ContainerSlotState(0, "slime_gel", 3),
                new ContainerSlotState(1, string.Empty, 0),
            },
            client.State.Containers[id].Slots,
            "no slot from a rejected delta may be partially published");
        CheckAssert.Equal(0, Volatile.Read(ref eventCount),
            "a rejected container delta must not raise an event");
    }

    private static async ValueTask SignalsIdenticalContainerOpenBaselinesAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var id = Guid.NewGuid();
        var observed = new List<NetworkContainerState>();
        client.ContainerStateChanged += (_, args) =>
        {
            lock (observed) observed.Add(args.State);
        };
        var slots = new[]
        {
            new ContainerSlotState(0, "slime_gel", 2),
            new ContainerSlotState(1, string.Empty, 0),
        };
        var first = Container(
            2, 450, id, 0, 7, true, slots,
            objectRevision: 7, chunkRevision: 19);
        var repeated = first with { Sequence = 3, Tick = 451 };

        await peer.SendAsync(first, cancellationToken);
        await peer.SendAsync(repeated, cancellationToken);
        await EventuallyAsync(
            () =>
            {
                lock (observed) return observed.Count == 2;
            },
            "an identical baseline did not signal the second open response",
            cancellationToken);

        NetworkContainerState latest;
        lock (observed) latest = observed[^1];
        CheckAssert.Equal(first.Container, latest.Reference,
            "container projections must retain exact object and chunk revisions");
        CheckAssert.Equal(7u, latest.ContainerRevision,
            "an identical open response must retain the container revision");
        CheckAssert.SequenceEqual(slots, latest.Slots,
            "an identical open response must retain the complete slot baseline");
    }

    private static async ValueTask AppliesMultiObjectChunkTransactionAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var first = World(Guid.NewGuid(), 1, 1, 100);
        var second = first with { ObjectId = Guid.NewGuid(), X = first.X + .25f };
        var changes = 0;
        client.WorldObjectsChanged += (_, args) =>
            Interlocked.Add(ref changes, args.Changes.Count);

        await peer.SendAsync(
            new WorldObjectDeltaBatchMessage(
                2,
                500,
                [
                    new(
                        WorldObjectDeltaKind.Upsert,
                        new(first.ObjectId, first.ChunkX, first.ChunkY,
                            first.WorldLevel, 0, 0),
                        1,
                        first),
                    new(
                        WorldObjectDeltaKind.Upsert,
                        new(second.ObjectId, second.ChunkX, second.ChunkY,
                            second.WorldLevel, 0, 0),
                        1,
                        second),
                ]),
            cancellationToken);

        await EventuallyAsync(
            () => client.State.WorldObjects.Count == 2,
            "one chunk transaction did not apply every object delta",
            cancellationToken);
        CheckAssert.Equal(2, Volatile.Read(ref changes),
            "both objects in the transaction must publish together");
        CheckAssert.Equal(1u, client.State.WorldChunkRevisions[
                new NetworkWorldChunk(3, -2, 0)],
            "one chunk transaction must advance the chunk revision once");
    }

    private static async ValueTask ReceivesEmptyChunkRevisionBaselineAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var emptyChunk = new NetworkWorldChunk(-21, 34, -1);

        await peer.SendAsync(
            new WorldChunkRevisionBatchMessage(
                2,
                600,
                [new(emptyChunk.ChunkX, emptyChunk.ChunkY,
                    emptyChunk.WorldLevel, 17)]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.WorldChunkRevisions.TryGetValue(
                    emptyChunk,
                    out var revision) &&
                revision == 17,
            "the empty chunk revision baseline was not published",
            cancellationToken);

        CheckAssert.Equal(0, client.State.WorldObjects.Count,
            "a chunk baseline must not invent a public world object");
        CheckAssert.Equal(17u, client.State.WorldChunkRevisions[emptyChunk],
            "callers must be able to resolve an empty chunk revision from State");

        await peer.SendAsync(
            new WorldChunkRevisionBatchMessage(
                3,
                601,
                [new(emptyChunk.ChunkX, emptyChunk.ChunkY,
                    emptyChunk.WorldLevel, 12)]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.ServerTick >= 601,
            "the stale chunk baseline was not consumed",
            cancellationToken);
        CheckAssert.Equal(17u, client.State.WorldChunkRevisions[emptyChunk],
            "chunk revision baselines must apply monotonically");
    }

    private static async ValueTask RejectsMalformedChunkRevisionBaselineAsync(
        CancellationToken cancellationToken)
    {
        await AssertMalformedChunkBatchAsync(
            frame =>
            {
                // Copy the first entry's complete chunk key over the second
                // while retaining its different revision.
                frame.AsSpan(
                        ProtocolConstants.ReliableHeaderSize + 2,
                        sizeof(int) * 2 + sizeof(short))
                    .CopyTo(frame.AsSpan(
                        ProtocolConstants.ReliableHeaderSize + 16));
            },
            "conflicting duplicate",
            cancellationToken);
        await AssertMalformedChunkBatchAsync(
            frame => BinaryPrimitives.WriteUInt32LittleEndian(
                frame.AsSpan(
                    ProtocolConstants.ReliableHeaderSize + 26),
                0),
            "positive",
            cancellationToken);
    }

    private static async ValueTask AssertMalformedChunkBatchAsync(
        Action<byte[]> corrupt,
        string expectedError,
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedWorldPeer.ConnectAsync(
            client,
            cancellationToken);
        var valid = ReliableProtocolCodec.Encode(
            new WorldChunkRevisionBatchMessage(
                2,
                700,
                [new(4, 8, 0, 11), new(5, 9, 0, 12)]));
        corrupt(valid);
        await peer.SendRawAsync(valid, cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the malformed chunk baseline did not fault the client",
            cancellationToken);

        CheckAssert.Equal(0, client.State.WorldChunkRevisions.Count,
            "a malformed chunk batch must not partially publish revisions");
        CheckAssert.True(
            client.State.LastError?.Contains(
                expectedError,
                StringComparison.OrdinalIgnoreCase) == true,
            "the malformed chunk fault must explain its rejected revision");
    }

    private static WorldObjectState World(
        Guid id,
        uint chunkRevision,
        uint objectRevision,
        int health,
        Guid linkedObjectId = default) =>
        new(
            id,
            3,
            -2,
            0,
            chunkRevision,
            objectRevision,
            "storage_chest",
            101.5f,
            -62.25f,
            1,
            health,
            100,
            true,
            string.Empty,
            0,
            WorldObjectGateState.None,
            linkedObjectId);

    private static WorldObjectReference Reference(WorldObjectState state) =>
        new(
            state.ObjectId,
            state.ChunkX,
            state.ChunkY,
            state.WorldLevel,
            state.ObjectRevision,
            state.ChunkRevision);

    private static WorldObjectDelta Upsert(
        WorldObjectState previous,
        WorldObjectState current) =>
        new(
            WorldObjectDeltaKind.Upsert,
            Reference(previous),
            current.ChunkRevision,
            current);

    private static ContainerStateMessage Container(
        ulong sequence,
        ulong tick,
        Guid id,
        uint baselineRevision,
        uint revision,
        bool isBaseline,
        IReadOnlyList<ContainerSlotState> slots,
        int slotCount = 0,
        uint? objectRevision = null,
        uint chunkRevision = 0) =>
        new(
            sequence,
            tick,
            new WorldObjectReference(
                id, 3, -2, 0, objectRevision ?? revision, chunkRevision),
            baselineRevision,
            revision,
            "storage_chest",
            ContainerAccessMode.DepositAndWithdraw,
            slotCount > 0 ? slotCount : slots.Count,
            isBaseline,
            slots);

    internal static async Task EventuallyAsync(
        Func<bool> condition,
        string failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(failure);
    }

    internal sealed class ScriptedWorldPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private ScriptedWorldPeer(
            TcpListener listener,
            TcpClient client,
            NetworkStream stream)
        {
            _listener = listener;
            _client = client;
            _stream = stream;
        }

        public static async Task<ScriptedWorldPeer> ConnectAsync(
            NetworkGameClient client,
            CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient? peer = null;
            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var worldId = Guid.NewGuid();
                var connect = client.ConnectAsync(
                    endpoint.Address.ToString(),
                    endpoint.Port,
                    new ClientHandshakeOptions(
                        BuildVersion,
                        ContentVersion,
                        Guid.NewGuid(),
                        "World State Client",
                        worldId,
                        Capabilities: ClientCapabilities.None),
                    cancellationToken);
                peer = await listener.AcceptTcpClientAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                peer.NoDelay = true;
                var stream = peer.GetStream();
                var request = await TcpFrameCodec.ReadAsync(
                        stream,
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                if (request is not HandshakeRequestMessage handshake)
                    throw new InvalidOperationException(
                        "the scripted world peer did not receive a handshake");
                await TcpFrameCodec.WriteAsync(
                        stream,
                        new HandshakeAcceptedMessage(
                            1,
                            90,
                            ProtocolConstants.CurrentVersion,
                            BuildVersion,
                            ContentVersion,
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            9001,
                            worldId,
                            987654,
                            1,
                            2,
                            0,
                            999,
                            handshake.ClientNonce,
                            100,
                            "world-state-token",
                            0,
                            20,
                            ServerCapabilities.None),
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                await connect.WaitAsync(Timeout, cancellationToken);
                return new ScriptedWorldPeer(listener, peer, stream);
            }
            catch
            {
                peer?.Dispose();
                listener.Stop();
                throw;
            }
        }

        public async ValueTask SendAsync(
            IProtocolMessage message,
            CancellationToken cancellationToken) =>
            await TcpFrameCodec.WriteAsync(
                    _stream,
                    message,
                    cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);

        public async ValueTask SendRawAsync(
            byte[] frame,
            CancellationToken cancellationToken)
        {
            var prefix = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(
                prefix,
                checked((uint)frame.Length));
            await _stream.WriteAsync(prefix, cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);
            await _stream.WriteAsync(frame, cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
