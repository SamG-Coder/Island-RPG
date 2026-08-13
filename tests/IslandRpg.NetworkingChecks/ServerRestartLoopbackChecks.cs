using System.Net;
using IslandRpg.Client;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ServerRestartLoopbackChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string BuildVersion = "network-checks";
    private const string ContentVersion = "test-content";

    public static void Register(CheckRunner checks) => checks.Add(
        "real server restart preserves actor and object-free chunk authority",
        PreservesWorldAndActorStateAcrossRestartAsync);

    private static async ValueTask PreservesWorldAndActorStateAcrossRestartAsync(
        CancellationToken cancellationToken)
    {
        using var save = TemporarySaveRoot.Create();
        var worldId = Guid.Parse("92000000-0000-0000-0000-000000000001");
        var objectId = Guid.Parse("92000000-0000-0000-0000-000000000002");
        var clientId = Guid.Parse("92000000-0000-0000-0000-000000000003");
        var options = CreateOptions(save.Path, worldId, objectId);

        Guid playerId;
        ulong playerEntityId;
        string reconnectToken;
        NetworkWorldChunk removedChunk;
        uint removedChunkRevision;

        await using (var firstHost = await RunningServer.StartAsync(
                         options, cancellationToken))
        await using (var original = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await original.ConnectAsync(
                firstHost.Endpoint.Address.ToString(),
                firstHost.Endpoint.Port,
                CreateHandshake(worldId, clientId, "Elara"),
                cancellationToken);
            playerId = accepted.PlayerId;
            playerEntityId = accepted.PlayerEntityId;
            reconnectToken = accepted.ReconnectToken;

            await EventuallyAsync(
                () => original.State.Gameplay is not null &&
                      original.State.WorldObjects.ContainsKey(objectId),
                "the initial persistent-world baseline did not arrive",
                cancellationToken);

            var worldObject = original.State.WorldObjects[objectId];
            removedChunk = new NetworkWorldChunk(
                worldObject.ChunkX,
                worldObject.ChunkY,
                worldObject.WorldLevel);
            var pickup = await SendAndAwaitActionAsync(
                original,
                new PickUpWorldObjectAction(new WorldObjectReference(
                    worldObject.ObjectId,
                    worldObject.ChunkX,
                    worldObject.ChunkY,
                    worldObject.WorldLevel,
                    worldObject.ChunkRevision,
                    worldObject.ObjectRevision)),
                cancellationToken);
            CheckAssert.True(pickup.Accepted,
                $"the authoritative pickup was rejected: {pickup.Detail}");

            await EventuallyAsync(
                () => !original.State.WorldObjects.ContainsKey(objectId) &&
                      original.State.WorldChunkRevisions.TryGetValue(
                          removedChunk, out var revision) &&
                      revision > worldObject.ChunkRevision &&
                      CountItem(original.State.Gameplay, "large_rock") == 1,
                "pickup did not atomically advance inventory and empty-chunk state",
                cancellationToken);
            removedChunkRevision =
                original.State.WorldChunkRevisions[removedChunk];

            await original.DisconnectAsync(cancellationToken);
        }

        // Disposing the first host waits for the authority thread's final
        // checkpoint and releases the exclusive world lease before restart.
        await using var restartedHost = await RunningServer.StartAsync(
            options, cancellationToken);
        await using var resumed = new NetworkGameClient(TimeSpan.Zero);
        var resumedHandshake = await resumed.ConnectAsync(
            restartedHost.Endpoint.Address.ToString(),
            restartedHost.Endpoint.Port,
            CreateHandshake(worldId, clientId, "Elara") with
            {
                ReconnectPlayerId = playerId,
                ReconnectToken = reconnectToken,
            },
            cancellationToken);

        CheckAssert.Equal(playerId, resumedHandshake.PlayerId,
            "restart reconnect must preserve player identity");
        CheckAssert.Equal(playerEntityId, resumedHandshake.PlayerEntityId,
            "restart reconnect must preserve actor identity");
        await EventuallyAsync(
            () => resumed.State.Gameplay is not null &&
                  CountItem(resumed.State.Gameplay, "large_rock") == 1 &&
                  resumed.State.WorldChunkRevisions.TryGetValue(
                      removedChunk, out var revision) &&
                  revision == removedChunkRevision,
            "restart reconnect did not restore exact inventory and chunk revisions",
            cancellationToken);
        CheckAssert.False(resumed.State.WorldObjects.ContainsKey(objectId),
            "the removed object reappeared after authoritative restart");

        await using var lateJoin = new NetworkGameClient(TimeSpan.Zero);
        await lateJoin.ConnectAsync(
            restartedHost.Endpoint.Address.ToString(),
            restartedHost.Endpoint.Port,
            CreateHandshake(
                worldId,
                Guid.Parse("92000000-0000-0000-0000-000000000004"),
                "Aveline"),
            cancellationToken);
        await EventuallyAsync(
            () => lateJoin.State.Gameplay is not null &&
                  lateJoin.State.WorldChunkRevisions.TryGetValue(
                      removedChunk, out var revision) &&
                  revision == removedChunkRevision,
            "late join did not receive the advanced object-free chunk revision",
            cancellationToken);
        CheckAssert.False(lateJoin.State.WorldObjects.ContainsKey(objectId),
            "late join received an object already removed from the world");
        CheckAssert.Equal(0, CountItem(lateJoin.State.Gameplay, "large_rock"),
            "late join received another actor's private persisted inventory");
    }

    private static ServerOptions CreateOptions(
        string saveRoot,
        Guid worldId,
        Guid objectId) => new(
            IPAddress.Loopback,
            0,
            worldId,
            424_242,
            BuildVersion,
            ContentVersion,
            8)
        {
            SaveRoot = saveRoot,
            AutosaveInterval = TimeSpan.FromHours(1),
            StartingWorldObjects =
            [
                new WorldObjectSeed(
                    objectId,
                    "large_rock",
                    new(.5f, 0))
            ]
        };

    private static ClientHandshakeOptions CreateHandshake(
        Guid worldId,
        Guid clientId,
        string name) => new(
            BuildVersion,
            ContentVersion,
            clientId,
            name,
            worldId,
            Capabilities:
                ClientCapabilities.UdpSnapshots |
                ClientCapabilities.DeltaSnapshots);

    private static int CountItem(
        NetworkPlayerGameplayState? state,
        string itemId) => state?.InventorySlots.Sum(slot =>
            string.Equals(slot.ItemId, itemId,
                StringComparison.OrdinalIgnoreCase)
                ? slot.Quantity
                : 0) ?? 0;

    private static async Task<ActionResultMessage> SendAndAwaitActionAsync(
        NetworkGameClient client,
        IActionCommandPayload payload,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var completion = new TaskCompletionSource<ActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkActionResultEventArgs args)
        {
            if (args.Result.CommandId == commandId)
                completion.TrySetResult(args.Result);
        }

        client.ActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(payload, commandId, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.ActionCompleted -= Handler;
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
        private int _disposed;

        private RunningServer(
            DedicatedServer server,
            IPEndPoint endpoint,
            CancellationTokenSource shutdown,
            Task run)
        {
            Server = server;
            Endpoint = endpoint;
            _shutdown = shutdown;
            _run = run;
        }

        public DedicatedServer Server { get; }
        public IPEndPoint Endpoint { get; }

        public static async Task<RunningServer> StartAsync(
            ServerOptions options,
            CancellationToken cancellationToken)
        {
            var server = new DedicatedServer(options);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var run = server.RunAsync(shutdown.Token);
            try
            {
                var endpoint = await server.Started.WaitAsync(
                    Timeout, cancellationToken);
                return new RunningServer(server, endpoint, shutdown, run);
            }
            catch
            {
                shutdown.Cancel();
                try
                {
                    await run.WaitAsync(Timeout, CancellationToken.None);
                }
                catch
                {
                    // Preserve the startup exception after best-effort cleanup.
                }
                await server.DisposeAsync();
                shutdown.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _shutdown.Cancel();
            try
            {
                await _run.WaitAsync(Timeout, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await Server.DisposeAsync();
                _shutdown.Dispose();
            }
        }
    }

    private sealed class TemporarySaveRoot : IDisposable
    {
        private TemporarySaveRoot(string path) => Path = path;

        public string Path { get; }

        public static TemporarySaveRoot Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "IslandRpg-NetworkingChecks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporarySaveRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
