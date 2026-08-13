using System.Collections.Immutable;
using System.Net;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ServerResourceIntegrationChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string BuildVersion = "resource-server-checks";
    private const string ContentVersion = "resource-v1";

    public static void Register(CheckRunner checks)
    {
        checks.Add("server resource checkpoint maps exact sparse state",
            CheckpointRoundTrip);
        checks.Add("real server replicates resource authority and restart",
            ReplicatesAndRestoresAsync);
    }

    private static void CheckpointRoundTrip()
    {
        var worldId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
        var actorId = Guid.Parse("a1000000-0000-0000-0000-000000000002");
        var nodeId = Guid.Parse("a1000000-0000-0000-0000-000000000003");
        var chunk = new WorldChunkKey(2, -3, 0);
        var inventory = Enumerable.Range(0, 28)
            .Select(static slot => new InventorySlotSnapshot(slot, null, 0))
            .ToImmutableArray();
        var simulation = new AuthoritativeSessionCheckpoint(
            new SessionId(worldId),
            800,
            90,
            [new AuthoritativeActorCheckpoint(
                new PlayerIdentity(
                    new PlayerId(Guid.Parse(
                        "a1000000-0000-0000-0000-000000000004")),
                    new ActorId(actorId)),
                "Elara", default, 0, 4, 800,
                new PlayerGameplaySnapshot(
                    7, 100, 80, 0, 2, 3,
                    new PlayerInventorySnapshot(5, inventory), 47),
                Enumerable.Repeat((byte)1, 32).ToImmutableArray(),
                [])],
            new AuthoritativeWorldTransactionsCheckpoint([], []),
            [],
            new AuthoritativeResourceTransactionsCheckpoint(
                [new ResourceChunkSparseState(
                    chunk,
                    6,
                    [new ResourceNodeSparseState(
                        new ResourceNodeId(nodeId),
                        ResourceNodeKind.Tree,
                        chunk,
                        4,
                        31,
                        1,
                        0,
                        false)])],
                [new ResourceActorCadenceCheckpoint(
                    new ActorId(actorId),
                    ResourceActionKind.CutTree,
                    14.5,
                    9)]));
        var options = Options(null, worldId);
        var durable = ServerCheckpointMapper.ToDurable(simulation, options, 1);
        ServerCheckpointStore.Validate(durable, worldId);
        var restored = ServerCheckpointMapper.ToSimulation(durable, options);

        CheckAssert.Equal(47,
            restored.Actors[0].Gameplay.WoodcuttingExperience,
            "woodcutting XP must round trip through durable state");
        CheckAssert.Equal(simulation.Resources!.Chunks[0].Chunk,
            restored.Resources!.Chunks[0].Chunk,
            "resource chunk identity must round trip exactly");
        CheckAssert.Equal(
            simulation.Resources.Chunks[0].ResourceChunkRevision,
            restored.Resources.Chunks[0].ResourceChunkRevision,
            "resource chunk revision must round trip exactly");
        CheckAssert.SequenceEqual(simulation.Resources.Chunks[0].Nodes,
            restored.Resources.Chunks[0].Nodes,
            "sparse resource nodes must round trip exactly");
        CheckAssert.Equal(simulation.Resources.ActorCadences[0],
            restored.Resources.ActorCadences[0],
            "resource cadence ordinal and ready time must round trip exactly");

        var malformed = durable with
        {
            Resources = durable.Resources! with
            {
                Chunks = [durable.Resources!.Chunks[0] with { Revision = 0 }]
            }
        };
        CheckAssert.Throws<InvalidDataException>(
            () => ServerCheckpointStore.Validate(malformed, worldId),
            "durable validation must reject zero resource chunk revisions");
    }

    private static async ValueTask ReplicatesAndRestoresAsync(
        CancellationToken cancellationToken)
    {
        using var save = TemporarySaveRoot.Create();
        var worldId = Guid.Parse("a2000000-0000-0000-0000-000000000001");
        var (worldSeed, tree) = FindNearbyTree();
        var options = Options(save.Path, worldId) with
        {
            WorldSeed = worldSeed,
            StartingInventory = [new InitialInventoryItem("stone_axe")]
        };

        Guid playerId;
        string reconnectToken;
        ResourceNodeSparseState mutated;
        await using (var host = await RunningServer.StartAsync(
                         options, cancellationToken))
        await using (var actor = new NetworkGameClient(TimeSpan.Zero))
        await using (var observer = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await ConnectAsync(
                actor, host, worldId, "Elara", Guid.NewGuid(),
                cancellationToken);
            playerId = accepted.PlayerId;
            reconnectToken = accepted.ReconnectToken;
            await ConnectAsync(
                observer, host, worldId, "Aveline", Guid.NewGuid(),
                cancellationToken);
            await EventuallyAsync(
                () => actor.State.Gameplay is not null &&
                      observer.State.Gameplay is not null,
                "resource test clients did not receive gameplay baselines",
                cancellationToken);

            ResourceActionResultMessage? result = null;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(1_100),
                        cancellationToken);
                try
                {
                    result = await SendResourceAsync(
                        actor,
                        new ResourceActionPayload(
                            ResourceActionKind.CutTree,
                            actor.GetResourceReference(tree.Chunk, tree.Id),
                            0),
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"resource command failed; actor={actor.State.Status}:" +
                        $"{actor.State.LastError}; observer={observer.State.Status}:" +
                        $"{observer.State.LastError}", exception);
                }
                CheckAssert.True(result.Accepted,
                    $"the authoritative tree strike was rejected: {result.Detail}");
                if (result.Hit) break;
            }
            CheckAssert.True(result is { Hit: true },
                "the bounded deterministic strike sequence never hit the tree");
            CheckAssert.Equal(ResourceActionKind.CutTree, result!.Action,
                "the private result must identify the committed action");
            await EventuallyAsync(
                () => observer.State.ResourceChunks.TryGetValue(
                          tree.Chunk, out var state) &&
                      state.Nodes.TryGetValue(tree.Id, out var node) &&
                      node.NodeRevision > 0,
                "the observer did not receive the public resource delta",
                cancellationToken);
            mutated = observer.State.ResourceChunks[tree.Chunk].Nodes[tree.Id];
            CheckAssert.True(actor.State.Gameplay!.WoodcuttingExperience >= 0,
                "the requester must receive authoritative woodcutting XP state");
        }

        await using var restarted = await RunningServer.StartAsync(
            options, cancellationToken);
        await using var resumed = new NetworkGameClient(TimeSpan.Zero);
        await ConnectAsync(
            resumed, restarted, worldId, "Elara", Guid.NewGuid(),
            cancellationToken, playerId, reconnectToken);
        await EventuallyAsync(
            () => resumed.State.ResourceChunks.TryGetValue(
                      tree.Chunk, out var chunk) &&
                  chunk.Nodes.TryGetValue(tree.Id, out var node) &&
                  node == mutated,
            "restart reconnect did not receive exact sparse resource state",
            cancellationToken);

        await using var lateJoin = new NetworkGameClient(TimeSpan.Zero);
        await ConnectAsync(
            lateJoin, restarted, worldId, "Mira", Guid.NewGuid(),
            cancellationToken);
        await EventuallyAsync(
            () => lateJoin.State.ResourceChunks.TryGetValue(
                      tree.Chunk, out var chunk) &&
                  chunk.Nodes.TryGetValue(tree.Id, out var node) &&
                  node == mutated,
            "late join did not receive the sparse resource baseline",
            cancellationToken);
    }

    private static (long Seed, ResourceNodeDescriptor Tree) FindNearbyTree()
    {
        var catalog = new ProceduralResourceCatalog(
            new SurfaceTreeResourceDescriptorSource());
        for (var seed = 1L; seed <= 2_048; seed++)
        {
            foreach (var chunk in new[]
                     {
                         new WorldChunkKey(-1, -1, 0),
                         new WorldChunkKey(0, -1, 0),
                         new WorldChunkKey(-1, 0, 0),
                         new WorldChunkKey(0, 0, 0)
                     })
            foreach (var node in catalog.DescribeChunk(seed, chunk))
                if (node.Kind == ResourceNodeKind.Tree &&
                    Vector2.DistanceSquared(node.Position, Vector2.Zero) <= 9)
                    return (seed, node);
        }
        throw new InvalidOperationException(
            "The deterministic test seed has no tree in interaction range.");
    }

    private static ServerOptions Options(string? saveRoot, Guid worldId) =>
        new(IPAddress.Loopback, 0, worldId, 424_242,
            BuildVersion, ContentVersion, 8)
        {
            SaveRoot = saveRoot,
            AutosaveInterval = TimeSpan.FromHours(1)
        };

    private static Task<HandshakeAcceptedMessage> ConnectAsync(
        NetworkGameClient client,
        RunningServer host,
        Guid worldId,
        string name,
        Guid clientId,
        CancellationToken cancellationToken,
        Guid reconnectPlayerId = default,
        string reconnectToken = "") => client.ConnectAsync(
        host.Endpoint.Address.ToString(), host.Endpoint.Port,
        new ClientHandshakeOptions(
            BuildVersion, ContentVersion, clientId, name, worldId,
            reconnectPlayerId, reconnectToken,
            Capabilities: ClientCapabilities.None),
        cancellationToken);

    private static async Task<ResourceActionResultMessage> SendResourceAsync(
        NetworkGameClient client,
        ResourceActionPayload action,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<ResourceActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkResourceActionResultEventArgs args)
        {
            if (args.Result.CommandId == id)
                completion.TrySetResult(args.Result);
        }
        client.ResourceActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(action, id, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.ResourceActionCompleted -= Handler;
        }
    }

    private static async Task EventuallyAsync(
        Func<bool> condition,
        string failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await Task.Delay(15, cancellationToken);
        }
        throw new TimeoutException(failure);
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _run;

        private RunningServer(DedicatedServer server, IPEndPoint endpoint,
            CancellationTokenSource shutdown, Task run)
        {
            Server = server;
            Endpoint = endpoint;
            _shutdown = shutdown;
            _run = run;
        }

        public DedicatedServer Server { get; }
        public IPEndPoint Endpoint { get; }

        public static async Task<RunningServer> StartAsync(
            ServerOptions options, CancellationToken cancellationToken)
        {
            var server = new DedicatedServer(options);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var run = server.RunAsync(shutdown.Token);
            var endpoint = await server.Started.WaitAsync(
                Timeout, cancellationToken);
            return new RunningServer(server, endpoint, shutdown, run);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try { await _run.WaitAsync(Timeout, CancellationToken.None); }
            catch (OperationCanceledException) { }
            await Server.DisposeAsync();
            _shutdown.Dispose();
        }
    }

    private sealed class TemporarySaveRoot : IDisposable
    {
        private TemporarySaveRoot(string path) => Path = path;
        public string Path { get; }
        public static TemporarySaveRoot Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "IslandRpg-ResourceChecks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporarySaveRoot(path);
        }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
