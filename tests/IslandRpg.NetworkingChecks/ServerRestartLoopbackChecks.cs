using System.Net;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Caves;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ServerRestartLoopbackChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string BuildVersion = "network-checks";
    private const string ContentVersion = "test-content";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "real server restart preserves actor and object-free chunk authority",
            PreservesWorldAndActorStateAcrossRestartAsync);
        checks.Add(
            "real server replicates linked cave traversal and restart",
            ReplicatesLinkedCaveAcrossRestartAsync);
    }

    private static async ValueTask ReplicatesLinkedCaveAcrossRestartAsync(
        CancellationToken cancellationToken)
    {
        using var save = TemporarySaveRoot.Create();
        var worldId = Guid.Parse("93000000-0000-0000-0000-000000000001");
        var clientId = Guid.Parse("93000000-0000-0000-0000-000000000002");
        var (seed, position) = FindNearbyCaveSite();
        var options = new ServerOptions(
            IPAddress.Loopback, 0, worldId, seed,
            BuildVersion, ContentVersion, 8)
        {
            SaveRoot = save.Path,
            AutosaveInterval = TimeSpan.FromHours(1),
            StartingInventory =
            [
                new InitialInventoryItem("stone_shovel"),
                new InitialInventoryItem(CaveExcavationRules.RopeItemId),
            ],
        };

        Guid playerId;
        string reconnectToken;
        Guid surfaceId;
        Guid undergroundId;
        int diggingExperience;
        await using (var host = await RunningServer.StartAsync(
                         options, cancellationToken))
        await using (var actor = new NetworkGameClient(TimeSpan.Zero))
        await using (var observer = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await actor.ConnectAsync(
                host.Endpoint.Address.ToString(), host.Endpoint.Port,
                CreateHandshake(worldId, clientId, "Elara") with
                {
                    Capabilities = ClientCapabilities.None,
                },
                cancellationToken);
            playerId = accepted.PlayerId;
            reconnectToken = accepted.ReconnectToken;
            await observer.ConnectAsync(
                host.Endpoint.Address.ToString(), host.Endpoint.Port,
                CreateHandshake(
                    worldId,
                    Guid.Parse("93000000-0000-0000-0000-000000000003"),
                    "Aveline") with
                {
                    Capabilities = ClientCapabilities.None,
                },
                cancellationToken);
            await EventuallyAsync(
                () => actor.State.Gameplay is not null &&
                      observer.State.Gameplay is not null,
                "cave clients did not receive player baselines",
                cancellationToken);

            var start = await SendAndAwaitCaveAsync(
                actor,
                new StartExcavationAction(
                    position.X, position.Y, 0, 0,
                    ChunkRevision(actor, position, 0)),
                cancellationToken);
            CheckAssert.True(start.Accepted,
                $"the real cave start was rejected: {start.Detail}");
            await EventuallyAsync(
                () => FindObject(observer, CaveExcavationRules.DigSiteItemId)
                    is not null,
                "observer did not receive the dig-site delta",
                cancellationToken);

            CaveActionResultMessage work = null!;
            for (var strike = 0; strike < 20; strike++)
            {
                if (strike > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(950),
                        cancellationToken);
                var site = FindObject(actor,
                    CaveExcavationRules.DigSiteItemId) ??
                    throw new InvalidOperationException(
                        "the requester lost its authoritative dig site");
                work = await SendAndAwaitCaveAsync(
                    actor,
                    new WorkExcavationAction(Reference(site), 0),
                    cancellationToken);
                CheckAssert.True(work.Accepted,
                    $"the real cave strike was rejected: {work.Detail}");
                CheckAssert.True(work.Damage > 0,
                    "accepted work must carry exact positive damage");
                if (work.Completed) break;
            }
            CheckAssert.True(work.Completed,
                "the bounded real strike sequence did not discover a cave");
            await EventuallyAsync(
                () => LinkedPair(observer) is not null,
                "observer did not receive both reciprocal cave endpoints",
                cancellationToken);
            var pair = LinkedPair(actor)!.Value;
            surfaceId = pair.Surface.ObjectId;
            undergroundId = pair.Underground.ObjectId;
            CheckAssert.False(surfaceId == undergroundId,
                "surface and underground endpoints require distinct IDs");
            CheckAssert.Equal(undergroundId, pair.Surface.LinkedObjectId,
                "surface endpoint must link to underground");
            CheckAssert.Equal(surfaceId, pair.Underground.LinkedObjectId,
                "underground endpoint must link to surface");

            var install = await SendAndAwaitCaveAsync(
                actor,
                new InstallCaveRopeAction(Reference(pair.Surface), 1),
                cancellationToken);
            CheckAssert.True(install.Accepted,
                $"rope installation was rejected: {install.Detail}");
            await EventuallyAsync(
                () => actor.State.WorldObjects.TryGetValue(
                          surfaceId, out var value) &&
                      value.DefinitionId ==
                          CaveExcavationRules.RopedEntranceItemId,
                "the requester did not receive the roped portal state",
                cancellationToken);
            var entrance = actor.State.WorldObjects[surfaceId];
            var traverse = await SendAndAwaitCaveAsync(
                actor,
                new TraverseCaveAction(Reference(entrance)),
                cancellationToken);
            CheckAssert.True(traverse.Accepted && traverse.Transitioned,
                $"authoritative traversal was rejected: {traverse.Detail}");
            CheckAssert.Equal(-1, (int)traverse.WorldLevel,
                "surface traversal must author the underground destination");
            diggingExperience = actor.State.Gameplay!.DiggingExperience;
            CheckAssert.True(diggingExperience > 0,
                "real excavation must publish authoritative digging XP");

            await actor.DisconnectAsync(cancellationToken);
        }

        await using var restarted = await RunningServer.StartAsync(
            options, cancellationToken);
        await using var resumed = new NetworkGameClient(TimeSpan.Zero);
        var resumedHandshake = await resumed.ConnectAsync(
            restarted.Endpoint.Address.ToString(), restarted.Endpoint.Port,
            CreateHandshake(worldId, clientId, "Elara") with
            {
                ReconnectPlayerId = playerId,
                ReconnectToken = reconnectToken,
                Capabilities = ClientCapabilities.None,
            },
            cancellationToken);
        CheckAssert.Equal(-1, resumedHandshake.SpawnWorldLevel,
            "restart handshake must report the persisted cave world level");
        CheckAssert.Equal(position.X, resumedHandshake.SpawnX,
            "restart handshake must report the persisted traversal X");
        CheckAssert.Equal(position.Y, resumedHandshake.SpawnY,
            "restart handshake must report the persisted traversal Y");
        await EventuallyAsync(
            () => resumed.State.Gameplay?.DiggingExperience ==
                      diggingExperience && LinkedPair(resumed) is not null,
            "restart reconnect lost digging XP or linked cave state",
            cancellationToken);

        await using var lateJoin = new NetworkGameClient(TimeSpan.Zero);
        await lateJoin.ConnectAsync(
            restarted.Endpoint.Address.ToString(), restarted.Endpoint.Port,
            CreateHandshake(
                worldId,
                Guid.Parse("93000000-0000-0000-0000-000000000004"),
                "Mira") with
            {
                Capabilities = ClientCapabilities.None,
            },
            cancellationToken);
        await EventuallyAsync(
            () => LinkedPair(lateJoin) is not null,
            "late join did not receive reciprocal cave baselines",
            cancellationToken);
        var latePair = LinkedPair(lateJoin)!.Value;
        CheckAssert.Equal(surfaceId, latePair.Surface.ObjectId,
            "late join must retain the stable surface identity");
        CheckAssert.Equal(undergroundId, latePair.Underground.ObjectId,
            "late join must retain the stable underground identity");
    }

    private static (long Seed, Vector2 Position) FindNearbyCaveSite()
    {
        var resources = new ProceduralResourceCatalog(
            new SurfaceTreeResourceDescriptorSource());
        for (var seed = 1L; seed <= 8_192; seed++)
        {
            var environment = new ProceduralCaveExcavationEnvironment(seed);
            for (var y = -2; y <= 2; y++)
            for (var x = -2; x <= 2; x++)
            {
                var position = new Vector2(x + .5f, y + .5f);
                if (Vector2.DistanceSquared(position, Vector2.Zero) > 9 ||
                    !environment.IsSurfaceDiggable(position) ||
                    !environment.IsCaveBelow(position))
                    continue;
                var chunk = WorldChunkKey.At(position, 0);
                if (resources.DescribeChunk(seed, chunk).Any(value =>
                        value.Kind == ResourceNodeKind.Tree &&
                        value.Position == position))
                    continue;
                return (seed, position);
            }
        }
        throw new InvalidOperationException(
            "No deterministic nearby cave-bearing site was found.");
    }

    private static NetworkWorldObjectState? FindObject(
        NetworkGameClient client, string definitionId) =>
        client.State.WorldObjects.Values.FirstOrDefault(value =>
            string.Equals(value.DefinitionId, definitionId,
                StringComparison.Ordinal));

    private static (NetworkWorldObjectState Surface,
        NetworkWorldObjectState Underground)? LinkedPair(
        NetworkGameClient client)
    {
        var objects = client.State.WorldObjects;
        foreach (var surface in objects.Values)
        {
            if (surface.WorldLevel != 0 ||
                surface.LinkedObjectId == Guid.Empty ||
                !objects.TryGetValue(
                    surface.LinkedObjectId, out var underground) ||
                underground.WorldLevel != -1 ||
                underground.LinkedObjectId != surface.ObjectId)
                continue;
            return (surface, underground);
        }
        return null;
    }

    private static WorldObjectReference Reference(
        NetworkWorldObjectState value) => new(
        value.ObjectId,
        value.ChunkX,
        value.ChunkY,
        value.WorldLevel,
        value.ObjectRevision,
        value.ChunkRevision);

    private static uint ChunkRevision(
        NetworkGameClient client, Vector2 position, short worldLevel)
    {
        var chunk = WorldChunkKey.At(position, worldLevel);
        return client.State.WorldChunkRevisions.TryGetValue(
            new NetworkWorldChunk(chunk.X, chunk.Y, worldLevel),
            out var revision) ? revision : 0;
    }

    private static async Task<CaveActionResultMessage> SendAndAwaitCaveAsync(
        NetworkGameClient client,
        CaveActionPayload payload,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var completion = new TaskCompletionSource<CaveActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkCaveActionResultEventArgs args)
        {
            if (args.Result.CommandId == commandId)
                completion.TrySetResult(args.Result);
        }
        client.CaveActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(
                payload, commandId, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.CaveActionCompleted -= Handler;
        }
    }

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
