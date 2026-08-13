using System.Net;
using System.Net.Sockets;
using IslandRpg.Client;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ResourceProtocolClientChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void Register(CheckRunner checks)
    {
        checks.Add("resource protocol messages round trip", MessagesRoundTrip);
        checks.Add("resource protocol rejects malformed state", RejectsMalformedState);
        checks.Add("network client atomically merges resource revisions", MergesRevisionsAsync);
    }

    private static void MessagesRoundTrip()
    {
        var chunk = new WorldChunkKey(-7, 11, 0);
        var id = new ResourceNodeId(Guid.Parse(
            "41414141-4141-4141-4141-414141414141"));
        var reference = new ResourceNodeReference(id, chunk, 4, 8);
        var action = new ActionCommandMessage(
            1, 2, Guid.Parse("51515151-5151-5151-5151-515151515151"),
            3, 5, new ResourceActionPayload(
                ResourceActionKind.CutTree, reference, 7));
        var decodedAction = (ActionCommandMessage)ReliableProtocolCodec.Decode(
            ReliableProtocolCodec.Encode(action));
        CheckAssert.Equal(action, decodedAction,
            "a typed tree strike must round trip with exact revisions and tool slot");

        var state = Node(id, chunk, 5, health: 37, remaining: 2);
        var baseline = new ResourceChunkBaselineMessage(
            2, 3, chunk, 9, [state],
            [new(new ResourceNodeId(Guid.Parse(
                "61616161-6161-6161-6161-616161616161")), 3)]);
        var decodedBaseline = (ResourceChunkBaselineMessage)
            ReliableProtocolCodec.Decode(ReliableProtocolCodec.Encode(baseline));
        CheckAssert.Equal(
            baseline with
            {
                Nodes = decodedBaseline.Nodes,
                Tombstones = decodedBaseline.Tombstones,
            },
            decodedBaseline,
            "resource baseline metadata must round trip exactly");
        CheckAssert.SequenceEqual(baseline.Nodes, decodedBaseline.Nodes,
            "resource baseline sparse nodes must round trip exactly");
        CheckAssert.SequenceEqual(baseline.Tombstones, decodedBaseline.Tombstones,
            "resource baseline tombstones must round trip exactly");

        var delta = new ResourceNodeDeltaBatchMessage(
            3, 4,
            [new(
                ResourceNodeDeltaKind.Upsert,
                reference,
                5,
                9,
                state)]);
        var decodedDelta = (ResourceNodeDeltaBatchMessage)
            ReliableProtocolCodec.Decode(ReliableProtocolCodec.Encode(delta));
        CheckAssert.SequenceEqual(delta.Deltas, decodedDelta.Deltas,
            "resource deltas must round trip exact node and chunk transitions");

        var result = new ResourceActionResultMessage(
            4,
            5,
            action.CommandId,
            true,
            CommandRejectionCode.None,
            string.Empty,
            4,
            6,
            ResourceActionKind.CutTree,
            reference,
            [new ResourceItemRewardState("palm_logs", 2)],
            true,
            7,
            true);
        var decodedResult = (ResourceActionResultMessage)
            ReliableProtocolCodec.Decode(ReliableProtocolCodec.Encode(result));
        CheckAssert.Equal(
            result with { Rewards = decodedResult.Rewards },
            decodedResult,
            "private resource outcomes must preserve damage, wear and rewards");
        CheckAssert.SequenceEqual(result.Rewards, decodedResult.Rewards,
            "resource outcome rewards must round trip exactly");
    }

    private static void RejectsMalformedState()
    {
        var chunk = new WorldChunkKey(2, 3, 0);
        var id = new ResourceNodeId(Guid.NewGuid());
        var reference = new ResourceNodeReference(id, chunk, 0, 0);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 0, 0,
                new ResourceActionPayload(
                    ResourceActionKind.GatherTreeStick,
                    reference,
                    0))),
            "a loose-stick action must reject a forged tool slot");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 0, 0,
                new ResourceActionPayload(
                    ResourceActionKind.CutTree,
                    reference with { Id = ResourceNodeId.Empty },
                    0))),
            "resource actions must reject empty node identities");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ResourceChunkBaselineMessage(
                1, 1, chunk, 1,
                [Node(id, chunk, 1), Node(id, chunk, 1)], [])),
            "resource baselines must reject duplicate node identities");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ResourceNodeDeltaBatchMessage(
                1, 1,
                [new(
                    ResourceNodeDeltaKind.Upsert,
                    reference,
                    0,
                    1,
                    Node(id, chunk, 1))])),
            "resource deltas must advance their node revision");
        var secondId = new ResourceNodeId(Guid.NewGuid());
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ResourceNodeDeltaBatchMessage(
                1, 1,
                [
                    new(
                        ResourceNodeDeltaKind.Upsert,
                        reference,
                        1,
                        1,
                        Node(id, chunk, 1)),
                    new(
                        ResourceNodeDeltaKind.Upsert,
                        new ResourceNodeReference(secondId, chunk, 0, 0),
                        1,
                        2,
                        Node(secondId, chunk, 1)),
                ])),
            "one resource chunk cannot carry conflicting atomic revisions");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ResourceActionResultMessage(
                1, 1, Guid.NewGuid(), true, CommandRejectionCode.None,
                string.Empty, 1, 1, ResourceActionKind.CutTree,
                reference, [], false, 1, false)),
            "resource outcomes cannot report damage for a miss");

        var tooMany = Enumerable.Range(
                0, ProtocolLimits.MaxResourceNodesPerBatch + 1)
            .Select(index => Node(
                new ResourceNodeId(GuidFrom(index + 1)),
                chunk,
                1))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ResourceChunkBaselineMessage(
                1, 1, chunk, 1, tooMany, [])),
            "resource baselines must enforce their hard count limit");
    }

    private static async ValueTask MergesRevisionsAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ResourcePeer.ConnectAsync(
            client, cancellationToken);
        var chunk = new WorldChunkKey(8, -5, 0);
        var id = new ResourceNodeId(Guid.NewGuid());
        var secondId = new ResourceNodeId(Guid.NewGuid());
        var original = Node(id, chunk, 2, health: 50, remaining: 3);
        var second = Node(secondId, chunk, 1, health: 45, remaining: 2);
        var events = 0;
        client.ResourcesChanged += (_, _) => Interlocked.Increment(ref events);

        await peer.SendAsync(new ResourceChunkBaselineMessage(
            2, 100, chunk, 3, [original, second], []), cancellationToken);
        await EventuallyAsync(
            () => client.State.ResourceChunks.TryGetValue(chunk, out var state) &&
                state.ResourceChunkRevision == 3,
            "the client did not publish its resource baseline",
            cancellationToken);
        CheckAssert.Equal(
            new ResourceNodeReference(id, chunk, 2, 3),
            client.GetResourceReference(chunk, id),
            "the client must expose the exact authoritative resource reference");

        var changed = original with
        {
            NodeRevision = 3,
            Health = 30,
            ReadyAtGameSeconds = 125,
        };
        await peer.SendAsync(new ResourceNodeDeltaBatchMessage(
            3, 101,
            [new(
                ResourceNodeDeltaKind.Upsert,
                new ResourceNodeReference(id, chunk, 2, 3),
                3,
                4,
                changed)]), cancellationToken);
        await EventuallyAsync(
            () => client.State.ResourceChunks[chunk].ResourceChunkRevision == 4,
            "the client did not atomically publish the resource delta",
            cancellationToken);
        CheckAssert.Equal(30,
            client.State.ResourceChunks[chunk].Nodes[id].Health,
            "the accepted node state must replace its prior state");

        await peer.SendAsync(new ResourceNodeDeltaBatchMessage(
            4, 102,
            [new(
                ResourceNodeDeltaKind.Remove,
                new ResourceNodeReference(id, chunk, 3, 4),
                4,
                5,
                null)]), cancellationToken);
        await EventuallyAsync(
            () => !client.State.ResourceChunks[chunk].Nodes.ContainsKey(id) &&
                client.State.ResourceChunks[chunk]
                    .NodeRevisionHighWater[id] == 4,
            "the client did not retain the resource tombstone high-water",
            cancellationToken);
        CheckAssert.True(client.TryGetResourceReference(id, out var tombstone) &&
            tombstone == new ResourceNodeReference(id, chunk, 4, 5),
            "exact lookup must resolve tombstoned nodes without resurrection");

        var before = client.State.ResourceChunks[chunk];
        await peer.SendAsync(new ResourceNodeDeltaBatchMessage(
            5, 103,
            [
                new(
                    ResourceNodeDeltaKind.Upsert,
                    new ResourceNodeReference(secondId, chunk, 1, 5),
                    2,
                    6,
                    second with { NodeRevision = 2, Health = 20 }),
                new(
                    ResourceNodeDeltaKind.Upsert,
                    new ResourceNodeReference(id, chunk, 3, 5),
                    5,
                    6,
                    original with { NodeRevision = 5 }),
            ]), cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "a mismatched resource chain did not fault the client",
            cancellationToken);
        CheckAssert.True(ReferenceEquals(before,
                client.State.ResourceChunks[chunk]),
            "a rejected resource batch must preserve the prior immutable chunk");
        CheckAssert.Equal(3, Volatile.Read(ref events),
            "only the accepted baseline, upsert and removal may raise events");
    }

    private static ResourceNodeSparseState Node(
        ResourceNodeId id,
        WorldChunkKey chunk,
        uint revision,
        int health = 0,
        int remaining = 0) =>
        new(id, ResourceNodeKind.Tree, chunk, revision, health, remaining,
            0, remaining == 0 && health == 0);

    private static Guid GuidFrom(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
    }

    private static async ValueTask EventuallyAsync(
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
        throw new InvalidOperationException(failure);
    }

    private sealed class ResourcePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        private ResourcePeer(
            TcpListener listener,
            TcpClient client,
            NetworkStream stream)
        {
            _listener = listener;
            _client = client;
            _stream = stream;
        }

        public static async Task<ResourcePeer> ConnectAsync(
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
                        "resource-checks", "resource-content", Guid.NewGuid(),
                        "Resource Client", worldId,
                        Capabilities: ClientCapabilities.None),
                    cancellationToken);
                peer = await listener.AcceptTcpClientAsync(cancellationToken);
                var stream = peer.GetStream();
                var request = await TcpFrameCodec.ReadAsync(
                    stream, cancellationToken);
                if (request is not HandshakeRequestMessage handshake)
                    throw new InvalidOperationException("resource peer expected a handshake");
                await TcpFrameCodec.WriteAsync(
                    stream,
                    new HandshakeAcceptedMessage(
                        1, 90, ProtocolConstants.CurrentVersion,
                        "resource-checks", "resource-content",
                        Guid.NewGuid(), Guid.NewGuid(), 1, worldId, 77,
                        0, 0, 0, 42, handshake.ClientNonce, 1,
                        "resource-token", 0, 20, ServerCapabilities.None),
                    cancellationToken);
                await connect.WaitAsync(Timeout, cancellationToken);
                return new ResourcePeer(listener, peer, stream);
            }
            catch
            {
                peer?.Dispose();
                listener.Stop();
                throw;
            }
        }

        public ValueTask SendAsync(
            IProtocolMessage message,
            CancellationToken cancellationToken) =>
            TcpFrameCodec.WriteAsync(_stream, message, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _client.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
