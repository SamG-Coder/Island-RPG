using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class LoopbackChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private const string BuildVersion = "network-checks";
    private const string ContentVersion = "test-content";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "real loopback clients replicate movement and chat",
            ReplicatesTwoClientsAsync);
        checks.Add(
            "real loopback handshake rejects incompatible content and worlds",
            RejectsIncompatibleClientsAsync);
        checks.Add(
            "real loopback reconnect resumes identity and command sequence",
            ReconnectsWithoutResettingAuthorityAsync);
        checks.Add(
            "real loopback inventory crafting and eating stay authoritative",
            ReplicatesAuthoritativePlayerActionsAsync);
        checks.Add(
            "real loopback negotiates UDP snapshots with TCP recovery",
            NegotiatesUdpSnapshotsAsync);
        checks.Add(
            "real loopback world actions split public and private state",
            ReplicatesWorldActionsWithoutPrivateLeaksAsync);
        checks.Add(
            "real loopback campfire cooking completes authoritatively",
            CompletesCampfireCookingAuthoritativelyAsync);
        checks.Add(
            "late join activation closes the public bootstrap revision gap",
            LateJoinActivationClosesPublicRevisionGapAsync);
    }

    private static async ValueTask ReplicatesTwoClientsAsync(CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackFixture.StartAsync(cancellationToken);
        await using var first = new NetworkGameClient(TimeSpan.Zero);
        await using var second = new NetworkGameClient(TimeSpan.Zero);
        var firstAccepted = await fixture.ConnectAsync(first, "Elara", cancellationToken);
        var secondAccepted = await fixture.ConnectAsync(second, "Aveline", cancellationToken);

        CheckAssert.Equal(
            firstAccepted.WorldId,
            secondAccepted.WorldId,
            "both clients must enter the same authoritative world");
        CheckAssert.False(
            firstAccepted.PlayerId == secondAccepted.PlayerId,
            "players must receive distinct identities");
        CheckAssert.False(
            firstAccepted.PlayerEntityId == secondAccepted.PlayerEntityId,
            "players must receive distinct entity identities");

        await EventuallyAsync(
            () => first.State.Players.Count == 2 && second.State.Players.Count == 2,
            "both clients did not receive the complete presence set",
            cancellationToken);
        await EventuallyAsync(
            () => HasBothEntities(first, firstAccepted, secondAccepted) &&
                  HasBothEntities(second, firstAccepted, secondAccepted),
            "both clients did not receive a two-player keyframe",
            cancellationToken);

        await SendAndAwaitAcceptedAsync(
            first,
            () => first.SendWalkAsync(3, 0, 0, cancellationToken),
            cancellationToken);
        await EventuallyAsync(
            () => second.State.Entities.TryGetValue(firstAccepted.PlayerEntityId, out var actor) &&
                  actor.X > 0.25f,
            "client B did not observe client A moving",
            cancellationToken);

        var chat = new TaskCompletionSource<NetworkChatEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        second.ChatReceived += (_, args) =>
        {
            if (args.Message.SenderPlayerId == firstAccepted.PlayerId)
            {
                chat.TrySetResult(args.Message);
            }
        };
        await first.SendChatAsync("Meet by the fire.", cancellationToken: cancellationToken);
        var received = await chat.Task.WaitAsync(Timeout, cancellationToken);
        CheckAssert.Equal("Elara", received.SenderPlayerName, "chat must preserve sender name");
        CheckAssert.Equal("Meet by the fire.", received.Text, "chat text must reach the peer exactly");
    }

    private static async ValueTask RejectsIncompatibleClientsAsync(CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackFixture.StartAsync(cancellationToken);

        await using (var wrongContent = new NetworkGameClient())
        {
            var rejection = await CaptureRejectionAsync(
                () => wrongContent.ConnectAsync(
                    fixture.Endpoint.Address.ToString(),
                    fixture.Endpoint.Port,
                    fixture.Options("ContentMismatch") with { ContentVersion = "wrong" },
                    cancellationToken),
                cancellationToken);
            CheckAssert.Equal(
                HandshakeRejectionCode.ContentMismatch,
                rejection.Code,
                "incompatible content must be rejected before joining");
        }

        await using (var wrongWorld = new NetworkGameClient())
        {
            var rejection = await CaptureRejectionAsync(
                () => wrongWorld.ConnectAsync(
                    fixture.Endpoint.Address.ToString(),
                    fixture.Endpoint.Port,
                    fixture.Options("WorldMismatch") with { RequestedWorldId = Guid.NewGuid() },
                    cancellationToken),
                cancellationToken);
            CheckAssert.Equal(
                HandshakeRejectionCode.ContentMismatch,
                rejection.Code,
                "requesting another world must be rejected before joining");
        }
    }

    private static async ValueTask ReconnectsWithoutResettingAuthorityAsync(CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackFixture.StartAsync(cancellationToken);
        var clientId = Guid.NewGuid();
        Guid playerId;
        ulong entityId;
        string reconnectToken;
        ulong previousSequence;

        await using (var original = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await fixture.ConnectAsync(
                original,
                "Serena",
                cancellationToken,
                clientId);
            playerId = accepted.PlayerId;
            entityId = accepted.PlayerEntityId;
            reconnectToken = accepted.ReconnectToken;
            previousSequence = await SendAndAwaitAcceptedAsync(
                original,
                () => original.SendWalkAsync(1, 0, 0, cancellationToken),
                cancellationToken);
            await original.DisconnectAsync(cancellationToken);
        }

        // The transport close and authoritative disconnect cross different
        // threads; retrying only the handshake keeps the test deterministic.
        HandshakeAcceptedMessage reconnected = null!;
        await using var resumed = new NetworkGameClient(TimeSpan.Zero);
        await EventuallyAsync(async () =>
            {
                try
                {
                    reconnected = await resumed.ConnectAsync(
                        fixture.Endpoint.Address.ToString(),
                        fixture.Endpoint.Port,
                        fixture.Options("Serena", clientId) with
                        {
                            ReconnectPlayerId = playerId,
                            ReconnectToken = reconnectToken,
                        },
                        cancellationToken);
                    return true;
                }
                catch (HandshakeRejectedException)
                {
                    return false;
                }
            },
            "the player could not reconnect after its connection closed",
            cancellationToken);

        CheckAssert.Equal(playerId, reconnected.PlayerId, "reconnect must preserve player identity");
        CheckAssert.Equal(entityId, reconnected.PlayerEntityId, "reconnect must preserve entity identity");
        CheckAssert.True(
            reconnected.NextCommandSequence > previousSequence,
            "reconnect must resume after the last accepted command sequence");
        var nextSequence = await SendAndAwaitAcceptedAsync(
            resumed,
            () => resumed.SendStopAsync(cancellationToken),
            cancellationToken);
        CheckAssert.Equal(
            reconnected.NextCommandSequence,
            nextSequence,
            "the first resumed command must use the server-issued next sequence");
    }

    private static async ValueTask ReplicatesAuthoritativePlayerActionsAsync(
        CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(client, "Galen", cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay is not null,
            "the server did not send the private player baseline",
            cancellationToken);

        var baseline = client.State.Gameplay!;
        CheckAssert.Equal(3, CountItem(baseline, "plant_fibres"),
            "only the server may grant the starter crafting materials");
        CheckAssert.Equal(1, CountItem(baseline, "wild_berries"),
            "only the server may grant the starter food");

        var crafted = await SendAndAwaitActionAsync(
            client,
            new CraftRecipeAction("rope"),
            cancellationToken);
        CheckAssert.True(crafted.Accepted,
            $"the canonical rope recipe was rejected: {crafted.Detail}");
        await EventuallyAsync(
            () => client.State.Gameplay?.InventoryRevision ==
                  crafted.InventoryRevision,
            "the crafted inventory baseline did not reach the client",
            cancellationToken);
        var afterCraft = client.State.Gameplay!;
        CheckAssert.Equal(1, CountItem(afterCraft, "rope"),
            "crafting must add exactly one authoritative recipe output");
        CheckAssert.Equal(0, CountItem(afterCraft, "plant_fibres"),
            "crafting must atomically consume the canonical ingredients");

        var berriesSlot = afterCraft.InventorySlots.Single(
            slot => slot.ItemId == "wild_berries").Slot;
        var eaten = await SendAndAwaitActionAsync(
            client,
            new ConsumeItemAction(berriesSlot),
            cancellationToken);
        CheckAssert.True(eaten.Accepted,
            $"the canonical food action was rejected: {eaten.Detail}");
        await EventuallyAsync(
            () => client.State.Gameplay?.InventoryRevision ==
                  eaten.InventoryRevision,
            "the consumed-food baseline did not reach the client",
            cancellationToken);
        CheckAssert.Equal(0, CountItem(client.State.Gameplay!, "wild_berries"),
            "eating must remove exactly one server-owned food item");
        CheckAssert.Equal(20f, client.State.Gameplay!.WellFedSeconds,
            "the client must receive the canonical survival effect");
    }

    private static async ValueTask NegotiatesUdpSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackFixture.StartAsync(cancellationToken);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        var udpSnapshots = 0;
        var tcpSnapshots = 0;
        client.SnapshotReceived += (_, args) =>
        {
            if (args.Snapshot.Sequence == 0) Interlocked.Increment(ref udpSnapshots);
            else Interlocked.Increment(ref tcpSnapshots);
        };

        var accepted = await fixture.ConnectAsync(client, "Udp Client", cancellationToken);
        CheckAssert.True(
            accepted.Capabilities.HasFlag(ServerCapabilities.UdpSnapshots),
            "the server should negotiate UDP when the client offers it");
        CheckAssert.True(accepted.DatagramToken != 0,
            "the handshake must issue a non-zero session datagram token");
        CheckAssert.True(accepted.ServerSnapshotPort != 0,
            "the handshake must advertise the bound UDP port");

        await EventuallyAsync(
            () => Volatile.Read(ref udpSnapshots) >= 3,
            "the client did not receive the 20 Hz UDP snapshot stream",
            cancellationToken);
        await EventuallyAsync(
            () => Volatile.Read(ref tcpSnapshots) >= 1,
            "the client did not retain its reliable recovery keyframe",
            cancellationToken);
        CheckAssert.Equal(
            NetworkGameClientStatus.Connected,
            client.State.Status,
            "UDP snapshot receipt must not disturb the reliable session");
    }

    private static async ValueTask ReplicatesWorldActionsWithoutPrivateLeaksAsync(
        CancellationToken cancellationToken)
    {
        var pickupId = Guid.Parse(
            "81000000-0000-0000-0000-000000000001");
        var chestId = Guid.Parse(
            "81000000-0000-0000-0000-000000000002");
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            startingWorldObjects:
            [
                new WorldObjectSeed(pickupId, "large_rock", new(.5f, 0)),
                new WorldObjectSeed(
                    chestId,
                    "storage_chest",
                    new(1, 0),
                    ContainerItems:
                    [("slime_gel", 3, "private-owner")]),
            ]);
        await using var requester = new NetworkGameClient(TimeSpan.Zero);
        await using var observer = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(
            requester, "Requester", cancellationToken);
        await fixture.ConnectAsync(observer, "Observer", cancellationToken);
        await EventuallyAsync(
            () => requester.State.Gameplay is not null &&
                  observer.State.Gameplay is not null,
            "both clients did not receive private gameplay baselines",
            cancellationToken);

        var removalObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        observer.WorldObjectsChanged += (_, args) =>
        {
            if (args.Changes.Any(value =>
                    value.ObjectId == pickupId &&
                    value.Kind == WorldObjectDeltaKind.Remove))
                removalObserved.TrySetResult(true);
        };
        var pickup = await SendAndAwaitActionAsync(
            requester,
            new PickUpWorldObjectAction(
                new WorldObjectReference(pickupId, 0, 0, 0, 1, 2)),
            cancellationToken);
        CheckAssert.True(pickup.Accepted,
            $"the authoritative pickup was rejected: {pickup.Detail}");
        await removalObserved.Task.WaitAsync(Timeout, cancellationToken);
        await EventuallyAsync(
            () => requester.State.Gameplay is { } gameplay &&
                  CountItem(gameplay, "large_rock") == 1,
            "the requester did not receive its private pickup inventory",
            cancellationToken);
        CheckAssert.Equal(0,
            CountItem(observer.State.Gameplay!, "large_rock"),
            "an observer must not receive another player's private inventory");

        var privateContainerEvents = 0;
        observer.ContainerStateChanged += (_, _) =>
            Interlocked.Increment(ref privateContainerEvents);
        var opened = await SendAndAwaitActionAsync(
            requester,
            new OpenContainerAction(
                new WorldObjectReference(chestId, 0, 0, 0, 1, 3)),
            cancellationToken);
        CheckAssert.True(opened.Accepted,
            $"the authoritative container open was rejected: {opened.Detail}");
        await EventuallyAsync(
            () => requester.State.Containers.TryGetValue(chestId, out var value) &&
                  value.Slots.Any(slot =>
                      slot.ItemId == "slime_gel" && slot.Quantity == 3),
            "the requester did not receive the private container baseline",
            cancellationToken);
        await Task.Delay(100, cancellationToken);
        CheckAssert.Equal(0, Volatile.Read(ref privateContainerEvents),
            "the observer received requester-only container contents");
        CheckAssert.False(observer.State.Containers.ContainsKey(chestId),
            "the observer retained requester-only container contents");
    }

    private static int CountItem(
        NetworkPlayerGameplayState state,
        string itemId) => state.InventorySlots.Sum(slot =>
            string.Equals(slot.ItemId, itemId,
                StringComparison.OrdinalIgnoreCase)
                ? slot.Quantity
                : 0);

    private static async ValueTask CompletesCampfireCookingAuthoritativelyAsync(
        CancellationToken cancellationToken)
    {
        var campfireId = Guid.Parse(
            "81000000-0000-0000-0000-000000000009");
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            [new WorldObjectSeed(
                campfireId,
                "campfire",
                new(.5f, 0),
                FuelItemId: "logs",
                LitUntilGameSeconds: 300)],
            [new("raw_minnows")]);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(client, "Cook", cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay is not null &&
                  client.State.WorldObjects.ContainsKey(campfireId),
            "cooking bootstrap state did not arrive",
            cancellationToken);
        var slot = client.State.Gameplay!.InventorySlots.Single(
            value => value.ItemId == "raw_minnows").Slot;
        var fire = client.State.WorldObjects[campfireId];
        var completion = new TaskCompletionSource<CookingResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.CookingCompleted += (_, args) =>
            completion.TrySetResult(args.Result);
        var accepted = await SendAndAwaitActionAsync(
            client,
            new CookOnCampfireAction(
                new WorldObjectReference(
                    fire.ObjectId,
                    fire.ChunkX,
                    fire.ChunkY,
                    fire.WorldLevel,
                    fire.ObjectRevision,
                    fire.ChunkRevision),
                slot),
            cancellationToken);
        CheckAssert.True(accepted.Accepted,
            $"authoritative cooking start failed: {accepted.Detail}");
        var result = await completion.Task.WaitAsync(Timeout, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.InventoryRevision ==
                  result.InventoryRevision,
            "the authoritative cooked inventory did not arrive",
            cancellationToken);
        CheckAssert.Equal(0,
            CountItem(client.State.Gameplay!, "raw_minnows"),
            "the reserved raw item must not be duplicated");
        CheckAssert.Equal(1,
            CountItem(client.State.Gameplay!, "cooked_minnows") +
            CountItem(client.State.Gameplay!, "burnt_minnows"),
            "the server must produce exactly one cooking output");
    }

    private static async ValueTask LateJoinActivationClosesPublicRevisionGapAsync(
        CancellationToken cancellationToken)
    {
        var pickupId = Guid.Parse(
            "82000000-0000-0000-0000-000000000001");
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            [new WorldObjectSeed(pickupId, "large_rock", Vector2.Zero)]);
        await using var actor = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(actor, "Actor", cancellationToken);
        await EventuallyAsync(
            () => actor.State.Gameplay is not null &&
                  actor.State.WorldObjects.ContainsKey(pickupId),
            "the actor did not receive the seeded public object",
            cancellationToken);
        var seeded = actor.State.WorldObjects[pickupId];
        var expectedPostPickupChunkRevision = checked(seeded.ChunkRevision + 1);

        await using var late = new NetworkGameClient(TimeSpan.Zero);
        var enteredWindow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cacheUpdated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new ManualResetEventSlim();
        var pauseNextActivation = 0;
        fixture.Server.DuringBootstrapActivation = _ =>
        {
            if (Interlocked.Exchange(ref pauseNextActivation, 0) == 0) return;
            enteredWindow.TrySetResult();
            if (!releaseActivation.Wait(Timeout, cancellationToken))
                throw new TimeoutException(
                    "the deterministic bootstrap activation was not released");
        };
        fixture.Server.AfterWorldBootstrapUpdatedForTest = () =>
            cacheUpdated.TrySetResult();

        Volatile.Write(ref pauseNextActivation, 1);
        var lateJoin = fixture.ConnectAsync(late, "Late", cancellationToken);
        Task<ActionResultMessage>? pickupTask = null;
        try
        {
            await enteredWindow.Task.WaitAsync(Timeout, cancellationToken);
            pickupTask = SendAndAwaitActionAsync(
                actor,
                new PickUpWorldObjectAction(
                    new WorldObjectReference(
                        pickupId,
                        seeded.ChunkX,
                        seeded.ChunkY,
                        seeded.WorldLevel,
                        seeded.ObjectRevision,
                        seeded.ChunkRevision)),
                cancellationToken);
            await cacheUpdated.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            // Never leave the server connection thread parked inside the test
            // seam, even when the mutation itself rejects or times out.
            releaseActivation.Set();
        }

        var pickup = await pickupTask!.WaitAsync(Timeout, cancellationToken);
        CheckAssert.True(pickup.Accepted,
            $"the forced bootstrap-window mutation failed: {pickup.Detail}");
        await lateJoin.WaitAsync(Timeout, cancellationToken);
        await EventuallyAsync(
            () => !actor.State.WorldObjects.ContainsKey(pickupId),
            "the existing client did not observe the bootstrap-window mutation",
            cancellationToken);
        await EventuallyAsync(
            () => late.State.Gameplay is not null &&
                  late.State.WorldChunkRevisions.TryGetValue(
                      new NetworkWorldChunk(
                          seeded.ChunkX,
                          seeded.ChunkY,
                          seeded.WorldLevel), out var revision) &&
                  revision == expectedPostPickupChunkRevision,
            "the late client did not receive the post-mutation chunk revision",
            cancellationToken);
        CheckAssert.False(late.State.WorldObjects.ContainsKey(pickupId),
            "the late baseline resurrected an object removed during bootstrap");
        CheckAssert.Equal(NetworkGameClientStatus.Connected, late.State.Status,
            "the late client faulted on its revision-consistent baseline");
    }

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
            await client.SendActionAsync(
                payload, commandId, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.ActionCompleted -= Handler;
        }
    }

    private static bool HasBothEntities(
        NetworkGameClient client,
        HandshakeAcceptedMessage first,
        HandshakeAcceptedMessage second) =>
        client.State.Entities.ContainsKey(first.PlayerEntityId) &&
        client.State.Entities.ContainsKey(second.PlayerEntityId);

    private static async Task<HandshakeRejectedMessage> CaptureRejectionAsync(
        Func<Task<HandshakeAcceptedMessage>> connect,
        CancellationToken cancellationToken)
    {
        try
        {
            await connect().WaitAsync(Timeout, cancellationToken);
        }
        catch (HandshakeRejectedException exception)
        {
            return exception.Rejection;
        }

        throw new InvalidOperationException("the incompatible handshake was unexpectedly accepted");
    }

    private static async Task<ulong> SendAndAwaitAcceptedAsync(
        NetworkGameClient client,
        Func<ValueTask<ulong>> send,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CommandResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ulong sequence = 0;
        void Handler(object? _, NetworkCommandResultEventArgs args)
        {
            if (args.Result.CommandSequence == sequence)
            {
                completion.TrySetResult(args.Result);
            }
        }

        client.CommandCompleted += Handler;
        try
        {
            sequence = await send();
            var result = await completion.Task.WaitAsync(Timeout, cancellationToken);
            CheckAssert.True(result.Accepted, $"command {sequence} was rejected: {result.Detail}");
            return sequence;
        }
        finally
        {
            client.CommandCompleted -= Handler;
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

    private static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        string failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition()) return;
            await Task.Delay(30, cancellationToken);
        }

        throw new TimeoutException(failure);
    }

    private sealed class LoopbackFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private LoopbackFixture(
            DedicatedServer server,
            IPEndPoint endpoint,
            Guid worldId,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            Server = server;
            Endpoint = endpoint;
            WorldId = worldId;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public DedicatedServer Server { get; }
        public IPEndPoint Endpoint { get; }
        public Guid WorldId { get; }

        public static async Task<LoopbackFixture> StartAsync(
            CancellationToken cancellationToken,
            IReadOnlyList<WorldObjectSeed>? startingWorldObjects = null,
            IReadOnlyList<InitialInventoryItem>? extraInventory = null)
        {
            var worldId = Guid.NewGuid();
            var server = new DedicatedServer(new ServerOptions(
                IPAddress.Loopback,
                0,
                worldId,
                424242,
                BuildVersion,
                ContentVersion,
                8)
            {
                StartingInventory =
                    new InitialInventoryItem[]
                    {
                        new("plant_fibres", 3),
                        new("wild_berries", 1)
                    }.Concat(extraInventory ?? []).ToArray(),
                StartingHunger = 80f,
                StartingPosition = Vector2.Zero,
                StartingWorldObjects = startingWorldObjects ??
                    Array.Empty<WorldObjectSeed>()
            });
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            try
            {
                var endpoint = await server.Started.WaitAsync(Timeout, cancellationToken);
                return new LoopbackFixture(server, endpoint, worldId, shutdown, serverTask);
            }
            catch
            {
                shutdown.Cancel();
                await server.DisposeAsync();
                throw;
            }
        }

        public ClientHandshakeOptions Options(string name, Guid? clientId = null) => new(
            BuildVersion,
            ContentVersion,
            clientId ?? Guid.NewGuid(),
            name,
            WorldId,
            Capabilities:
                ClientCapabilities.UdpSnapshots |
                ClientCapabilities.DeltaSnapshots);

        public Task<HandshakeAcceptedMessage> ConnectAsync(
            NetworkGameClient client,
            string name,
            CancellationToken cancellationToken,
            Guid? clientId = null) =>
            client.ConnectAsync(
                Endpoint.Address.ToString(),
                Endpoint.Port,
                Options(name, clientId),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _serverTask.WaitAsync(Timeout);
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
}
