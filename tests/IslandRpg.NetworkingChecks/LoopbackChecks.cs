using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;
using IslandRpg.Protocol;
using IslandRpg.Resources;
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
            "real loopback removes drained loot bags for observers and late join",
            RemovesDrainedLootBagAsync);
        checks.Add(
            "real loopback campfire cooking completes authoritatively",
            CompletesCampfireCookingAuthoritativelyAsync);
        checks.Add(
            "real loopback furniture placement enables nearby station crafting",
            PlacesFurnitureAndValidatesNearbyStationCraftingAsync);
        checks.Add(
            "late join activation closes the public bootstrap revision gap",
            LateJoinActivationClosesPublicRevisionGapAsync);
        checks.Add(
            "large blocked late-join bootstrap does not stall healthy clients",
            LargeBlockedLateJoinDoesNotStallHealthyClientsAsync);
        checks.Add(
            "stalled bootstrap writer times out and releases client capacity",
            StalledBootstrapWriterReleasesClientCapacityAsync);
        checks.Add(
            "progressing bootstrap renews its publication deadline",
            ProgressingBootstrapRenewsPublicationDeadlineAsync);
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

    private static async ValueTask RemovesDrainedLootBagAsync(
        CancellationToken cancellationToken)
    {
        var bagId = Guid.Parse("84000000-0000-0000-0000-000000000001");
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            [new WorldObjectSeed(
                bagId,
                "loot_bag",
                new(.5f, 0),
                ContainerItems: [("slime_gel", 1, null)])]);
        await using var requester = new NetworkGameClient(TimeSpan.Zero);
        await using var observer = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(requester, "Looter", cancellationToken);
        await fixture.ConnectAsync(observer, "Observer", cancellationToken);
        await EventuallyAsync(
            () => requester.State.WorldObjects.ContainsKey(bagId) &&
                  observer.State.WorldObjects.ContainsKey(bagId),
            "the loot bag baseline did not reach both clients",
            cancellationToken);

        var bag = requester.State.WorldObjects[bagId];
        var opened = await SendAndAwaitActionAsync(
            requester,
            new OpenContainerAction(new WorldObjectReference(
                bagId,
                bag.ChunkX,
                bag.ChunkY,
                bag.WorldLevel,
                bag.ObjectRevision,
                bag.ChunkRevision)),
            cancellationToken);
        CheckAssert.True(opened.Accepted,
            $"the loot bag open was rejected: {opened.Detail}");
        await EventuallyAsync(
            () => requester.State.Containers.TryGetValue(bagId, out var state) &&
                  state.Slots.Any(slot =>
                      slot.ItemId == "slime_gel" && slot.Quantity == 1),
            "the requester did not receive the private loot contents",
            cancellationToken);
        var container = requester.State.Containers[bagId];
        var gel = container.Slots.Single(slot => slot.ItemId == "slime_gel");
        var withdrawn = await SendAndAwaitActionAsync(
            requester,
            new ContainerTransferAction(
                container.Reference,
                container.ContainerRevision,
                ContainerTransferDirection.Withdraw,
                requester.State.Gameplay!.InventorySlots.First(slot =>
                    string.IsNullOrEmpty(slot.ItemId)).Slot,
                gel.Slot,
                1),
            cancellationToken);
        CheckAssert.True(withdrawn.Accepted,
            $"the final loot withdrawal was rejected: {withdrawn.Detail}");
        await EventuallyAsync(
            () => !observer.State.WorldObjects.ContainsKey(bagId),
            "the observer did not receive the drained loot-bag removal",
            cancellationToken);
        await EventuallyAsync(
            () => !requester.State.WorldObjects.ContainsKey(bagId),
            "the requester did not receive the drained loot-bag removal",
            cancellationToken);
        await EventuallyAsync(
            () => !requester.State.Containers.ContainsKey(bagId),
            "the requester retained the removed loot bag's private container",
            cancellationToken);

        await using var late = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(late, "Late", cancellationToken);
        await EventuallyAsync(
            () => late.State.Gameplay is not null,
            "the late client did not finish bootstrap",
            cancellationToken);
        CheckAssert.False(late.State.WorldObjects.ContainsKey(bagId),
            "late join resurrected a drained loot bag");
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
                LitUntilGameSeconds: AuthoritativeWorldTime
                    .FromElapsedRealSeconds(300))],
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

    private static async ValueTask
        PlacesFurnitureAndValidatesNearbyStationCraftingAsync(
            CancellationToken cancellationToken)
    {
        var stationPosition = FindClearFurniturePosition(
            424242, ItemIds.Workbench);
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            extraInventory:
            [
                new("workbench"),
                new("stone_hammer"),
                new("logs", 6),
                new("plank", 6),
                new("sticks", 2),
                new("rope")
            ],
            startingPosition: stationPosition);
        await using var builder = new NetworkGameClient(TimeSpan.Zero);
        await using var observer = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(builder, "Builder", cancellationToken);
        await fixture.ConnectAsync(observer, "Observer", cancellationToken);
        await EventuallyAsync(
            () => builder.State.Gameplay is not null &&
                  observer.State.Gameplay is not null,
            "the furniture clients did not receive their private baselines",
            cancellationToken);

        // Six legitimate level-one wall crafts cross the level-four threshold
        // without a privileged test seam. The remaining materials are the
        // exact canonical storage-chest recipe used to exercise the station.
        for (var count = 0; count < 6; count++)
        {
            var wall = await SendAndAwaitActionAsync(
                builder,
                new CraftRecipeAction("wooden-wall"),
                cancellationToken);
            CheckAssert.True(wall.Accepted,
                $"crafting progression wall {count + 1} failed: {wall.Detail}");
            await EventuallyAsync(
                () => builder.State.Gameplay?.InventoryRevision ==
                      wall.InventoryRevision,
                "a progression craft did not publish its private inventory",
                cancellationToken);
        }
        CheckAssert.True(
            builder.State.Gameplay!.CraftingExperience >= 525,
            "the wire-driven builder did not reach crafting level four");

        var missing = await SendAndAwaitActionAsync(
            builder,
            new CraftRecipeAction("storage-chest"),
            cancellationToken);
        CheckAssert.False(missing.Accepted,
            "station crafting succeeded before a station existed");
        CheckAssert.True(
            missing.Detail.Contains(
                "crafting station", StringComparison.OrdinalIgnoreCase),
            $"the missing-station rejection was not explicit: {missing.Detail}");

        var workbenchSlot = builder.State.Gameplay.InventorySlots.Single(
            value => value.ItemId == "workbench").Slot;
        var placed = await SendAndAwaitActionAsync(
            builder,
            new PlaceInventoryWorldObjectAction(
                "workbench",
                workbenchSlot,
                stationPosition.X,
                stationPosition.Y,
                0,
                0,
                0),
            cancellationToken);
        CheckAssert.True(placed.Accepted,
            $"the clear authoritative workbench site rejected: {placed.Detail}");
        await EventuallyAsync(
            () => builder.State.Gameplay?.InventoryRevision ==
                  placed.InventoryRevision,
            "furniture placement did not publish its private inventory",
            cancellationToken);
        await EventuallyAsync(
            () => HasWorkbench(builder, stationPosition) &&
                  HasWorkbench(observer, stationPosition),
            "the placed workbench was not replicated to both clients",
            cancellationToken);
        CheckAssert.Equal(0,
            CountItem(builder.State.Gameplay!, "workbench"),
            "placing furniture must consume only the requester's workbench");

        var crafted = await SendAndAwaitActionAsync(
            builder,
            new CraftRecipeAction("storage-chest"),
            cancellationToken);
        CheckAssert.True(crafted.Accepted,
            $"the nearby authoritative workbench was ignored: {crafted.Detail}");
        await EventuallyAsync(
            () => builder.State.Gameplay?.InventoryRevision ==
                  crafted.InventoryRevision,
            "nearby station crafting did not publish its private inventory",
            cancellationToken);
        CheckAssert.Equal(1,
            CountItem(builder.State.Gameplay!, "storage_chest"),
            "nearby station crafting must commit exactly one canonical output");

        await using var late = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(late, "Late Builder", cancellationToken);
        await EventuallyAsync(
            () => late.State.Gameplay is not null &&
                  HasWorkbench(late, stationPosition),
            "the late joiner did not receive the placed workbench",
            cancellationToken);
        CheckAssert.Equal(NetworkGameClientStatus.Connected, late.State.Status,
            "the furniture baseline faulted the late-joining client");
    }

    private static Vector2 FindClearFurniturePosition(
        long worldSeed,
        string itemId)
    {
        CheckAssert.True(
            PlaceableWorldObjectRules.TryGet(itemId, out var definition),
            "the loopback fixture requires a canonical furniture definition");
        var navigation = new ProceduralWorldNavigationQuery(worldSeed);
        var catalog = new ProceduralResourceCatalog(
            new CompositeResourceDescriptorSource(
                new SurfaceTreeResourceDescriptorSource(),
                new SurfaceVegetationResourceDescriptorSource()));
        var resources = new AuthoritativeResourceTransactions(
            worldSeed, catalog);
        const int maximumRadius = 160;
        for (var radius = 0; radius <= maximumRadius; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius) continue;
            var candidate = new Vector2(x, y + .5f);
            if (!PlaceableWorldObjectRules.IsSnapped(itemId, candidate) ||
                !PlaceableWorldObjectRules.IsSupportedTerrain(
                    definition, candidate, 0, 0, navigation) ||
                resources.HasBlockingResourceInFootprint(
                    candidate, 0, definition.Footprint(0)))
                continue;
            return candidate;
        }

        throw new InvalidOperationException(
            $"No clear {itemId} fixture was found within {maximumRadius} tiles.");
    }

    private static bool HasWorkbench(
        NetworkGameClient client,
        Vector2 position) =>
        client.State.WorldObjects.Values.Any(value =>
            value.DefinitionId.Equals(
                "workbench", StringComparison.OrdinalIgnoreCase) &&
            MathF.Abs(value.X - position.X) < .001f &&
            MathF.Abs(value.Y - position.Y) < .001f);

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

    private static async ValueTask
        LargeBlockedLateJoinDoesNotStallHealthyClientsAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 160;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse(
                    $"83000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2(
                    (index % 8) * 0.1f,
                    (index / 8) * 0.1f)))
            .ToArray();
        var pickupId = seeds[0].ObjectId;
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            seeds);
        await using var healthy = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(healthy, "Healthy", cancellationToken);
        await EventuallyAsync(
            () => healthy.State.Gameplay is not null &&
                  seeds.All(seed =>
                      healthy.State.WorldObjects.ContainsKey(seed.ObjectId)),
            "the healthy client did not receive the large world bootstrap",
            cancellationToken);

        var writeBlocked = new TaskCompletionSource<ClientConnectionId>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outboundKinds = new ConcurrentDictionary<Guid,
            ConcurrentQueue<ProtocolMessageKind>>();
        var blockFirstWorldObject = 1;
        fixture.Server.BeforeOutboundWriteForTest = async (
            connection, message, writerCancellation) =>
        {
            outboundKinds.GetOrAdd(
                    connection.Id.Value,
                    static _ => new ConcurrentQueue<ProtocolMessageKind>())
                .Enqueue(message.Kind);
            if (message is not WorldObjectStateMessage ||
                Interlocked.Exchange(ref blockFirstWorldObject, 0) == 0)
                return;
            writeBlocked.TrySetResult(connection.Id);
            await releaseWrite.Task.WaitAsync(writerCancellation)
                .ConfigureAwait(false);
        };

        await using var late = new NetworkGameClient(TimeSpan.Zero);
        var lateJoin = fixture.ConnectAsync(late, "Blocked Late", cancellationToken);
        try
        {
            var lateConnection = await writeBlocked.Task.WaitAsync(
                Timeout, cancellationToken);
            var target = healthy.State.WorldObjects[pickupId];
            var pickup = await SendAndAwaitActionAsync(
                healthy,
                new PickUpWorldObjectAction(new WorldObjectReference(
                    pickupId,
                    target.ChunkX,
                    target.ChunkY,
                    target.WorldLevel,
                    target.ObjectRevision,
                    target.ChunkRevision)),
                cancellationToken).WaitAsync(Timeout, cancellationToken);
            CheckAssert.True(pickup.Accepted,
                $"the healthy-client mutation was rejected: {pickup.Detail}");
            await EventuallyAsync(
                () => !healthy.State.WorldObjects.ContainsKey(pickupId),
                "the blocked join stalled public replication to the healthy client",
                cancellationToken);

            // The joining writer is still parked on its first object frame.
            // Releasing it must drain the complete >128-message bootstrap and
            // then the queued post-high-water removal without a sequence gap.
            releaseWrite.TrySetResult();
            await lateJoin.WaitAsync(Timeout, cancellationToken);
            await EventuallyAsync(
                () => late.State.Gameplay is not null &&
                      seeds.Skip(1).All(seed =>
                          late.State.WorldObjects.ContainsKey(seed.ObjectId)),
                "the late client did not drain the complete large bootstrap",
                cancellationToken);
            CheckAssert.False(late.State.WorldObjects.ContainsKey(pickupId),
                "the late client missed the mutation queued behind its bootstrap");
            CheckAssert.Equal(NetworkGameClientStatus.Connected,
                late.State.Status,
                "the blocked late client disconnected while draining bootstrap");

            var kinds = outboundKinds[lateConnection.Value].ToArray();
            var handshakeIndex = Array.IndexOf(
                kinds, ProtocolMessageKind.HandshakeAccepted);
            var privateIndex = Array.IndexOf(
                kinds, ProtocolMessageKind.PlayerState);
            var publicIndex = Array.FindIndex(kinds, static kind =>
                kind is ProtocolMessageKind.WorldChunkRevisionBatch or
                    ProtocolMessageKind.WorldObjectState);
            CheckAssert.True(
                handshakeIndex >= 0 &&
                privateIndex > handshakeIndex &&
                publicIndex > privateIndex,
                "join publication must retain handshake/private/public ordering");
        }
        finally
        {
            releaseWrite.TrySetResult();
            fixture.Server.BeforeOutboundWriteForTest = null;
        }
    }

    private static async ValueTask
        StalledBootstrapWriterReleasesClientCapacityAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 160;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse(
                    $"84000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2(
                    (index % 8) * 0.1f,
                    (index / 8) * 0.1f)))
            .ToArray();
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            seeds,
            maximumClients: 1);
        fixture.Server.OutboundPublicationWriteTimeout =
            TimeSpan.FromMilliseconds(350);

        var writeBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worldObjectWrites = 0;
        fixture.Server.BeforeOutboundWriteForTest = async (
            _, message, writerCancellation) =>
        {
            if (message is not WorldObjectStateMessage)
                return;
            Interlocked.Increment(ref worldObjectWrites);
            writeBlocked.TrySetResult();
            try
            {
                await Task.Delay(
                        System.Threading.Timeout.InfiniteTimeSpan,
                        writerCancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                writerCancellation.IsCancellationRequested)
            {
                deadlineObserved.TrySetResult();
                throw;
            }
        };

        await using var stalled = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(stalled, "Stalled", cancellationToken)
            .WaitAsync(Timeout, cancellationToken);
        try
        {
            await writeBlocked.Task.WaitAsync(Timeout, cancellationToken);
            await deadlineObserved.Task.WaitAsync(Timeout, cancellationToken);
            await EventuallyAsync(
                () => stalled.State.Status == NetworkGameClientStatus.Faulted,
                "the publication deadline did not disconnect the stalled client",
                cancellationToken);
            CheckAssert.Equal(1, Volatile.Read(ref worldObjectWrites),
                "a timed-out bootstrap must stop expanding its captured generation");
        }
        finally
        {
            fixture.Server.BeforeOutboundWriteForTest = null;
        }

        // The timed-out connection must release both the authoritative join
        // and the server's one-client transport slot. A healthy replacement
        // must therefore be able to consume the same large baseline.
        fixture.Server.OutboundPublicationWriteTimeout = Timeout;
        await using var replacement = new NetworkGameClient(TimeSpan.Zero);
        HandshakeAcceptedMessage? accepted = null;
        await EventuallyAsync(async () =>
            {
                try
                {
                    accepted = await fixture.ConnectAsync(
                        replacement, "Replacement", cancellationToken);
                    return true;
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or
                        ProtocolException or HandshakeRejectedException)
                {
                    return false;
                }
            },
            "the stalled client did not release the one-client server capacity",
            cancellationToken);
        CheckAssert.True(accepted is not null,
            "the replacement handshake did not complete");
        await EventuallyAsync(
            () => replacement.State.Gameplay is not null &&
                  seeds.All(seed =>
                      replacement.State.WorldObjects.ContainsKey(seed.ObjectId)),
            "the replacement did not drain the complete large bootstrap",
            cancellationToken);
        CheckAssert.Equal(NetworkGameClientStatus.Connected,
            replacement.State.Status,
            "the healthy replacement disconnected while draining bootstrap");
    }

    private static async ValueTask
        ProgressingBootstrapRenewsPublicationDeadlineAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 8;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse(
                    $"85000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2(index * 0.1f, 0f)))
            .ToArray();
        await using var fixture = await LoopbackFixture.StartAsync(
            cancellationToken,
            seeds);
        var inactivityTimeout = TimeSpan.FromMilliseconds(240);
        var perFrameDelay = TimeSpan.FromMilliseconds(80);
        fixture.Server.OutboundPublicationWriteTimeout = inactivityTimeout;

        var worldObjectWrites = 0;
        fixture.Server.BeforeOutboundWriteForTest = async (
            _, message, writerCancellation) =>
        {
            if (message is not WorldObjectStateMessage)
                return;
            Interlocked.Increment(ref worldObjectWrites);
            await Task.Delay(perFrameDelay, writerCancellation)
                .ConfigureAwait(false);
        };

        await using var client = new NetworkGameClient(TimeSpan.Zero);
        var elapsed = Stopwatch.StartNew();
        try
        {
            await fixture.ConnectAsync(client, "Progressing", cancellationToken)
                .WaitAsync(Timeout, cancellationToken);
            await EventuallyAsync(
                () => client.State.Gameplay is not null &&
                      seeds.All(seed =>
                          client.State.WorldObjects.ContainsKey(seed.ObjectId)),
                "the progressing client did not drain its complete bootstrap",
                cancellationToken);
        }
        finally
        {
            fixture.Server.BeforeOutboundWriteForTest = null;
        }

        CheckAssert.True(elapsed.Elapsed > inactivityTimeout,
            "the regression bootstrap must outlive one inactivity interval");
        CheckAssert.Equal(objectCount, Volatile.Read(ref worldObjectWrites),
            "the progressing bootstrap did not write every object frame");
        CheckAssert.Equal(NetworkGameClientStatus.Connected,
            client.State.Status,
            "frame-by-frame progress must keep the connection alive");
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
            try
            {
                await client.SendActionAsync(
                    payload, commandId, cancellationToken);
            }
            catch (InvalidOperationException exception)
                when (client.State.Status == NetworkGameClientStatus.Faulted &&
                      !string.IsNullOrWhiteSpace(client.State.LastError))
            {
                throw new InvalidOperationException(
                    $"{exception.Message} Client fault: {client.State.LastError}",
                    exception);
            }
            try
            {
                return await completion.Task.WaitAsync(Timeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Action result {commandId:N} timed out while client was " +
                    $"{client.State.Status}: {client.State.LastError}",
                    exception);
            }
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
            IReadOnlyList<InitialInventoryItem>? extraInventory = null,
            Vector2? startingPosition = null,
            int maximumClients = 8)
        {
            var worldId = Guid.NewGuid();
            var server = new DedicatedServer(new ServerOptions(
                IPAddress.Loopback,
                0,
                worldId,
                424242,
                BuildVersion,
                ContentVersion,
                maximumClients)
            {
                StartingInventory =
                    new InitialInventoryItem[]
                    {
                        new("plant_fibres", 3),
                        new("wild_berries", 1)
                    }.Concat(extraInventory ?? []).ToArray(),
                StartingHunger = 80f,
                StartingPosition = startingPosition ?? Vector2.Zero,
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
