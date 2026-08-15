using System.Net;
using System.Net.Sockets;
using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Client;
using IslandRpg.Fishing;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Real TCP/UDP coverage for the complete authoritative boat and fishing
/// boundary. These checks deliberately use production procedural identities
/// rather than injecting protocol state.
/// </summary>
internal static class BoatFishingServerIntegrationChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string BuildVersion = "boat-fishing-server-checks";
    private const string ContentVersion = "boat-fishing-v1";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "island servers cap clients at authoritative raft capacity",
            IslandCapacityMatchesRaftAuthority);
        checks.Add(
            "real server provisions boats only for island-start worlds",
            ProvisionsOnlyForIslandWorldsAsync);
        checks.Add(
            "omitted boat removal is stale without a reliable sequence gap",
            OmittedBoatRemovalIsStaleWithoutSequenceGapAsync);
        checks.Add(
            "real server replicates boat fishing UDP privacy and restart",
            ReplicatesBoatFishingAndRestartAsync);
        checks.Add(
            "real server shore fishing with a net walks then catches",
            ShoreFishingWithNetWalksThenCatchesAsync);
    }

    private static void IslandCapacityMatchesRaftAuthority()
    {
        CheckAssert.Throws<ArgumentException>(() => ServerOptions.Parse(
            ["--island-start", "--max-clients", "257"]),
            "the CLI must reject an island population above raft capacity");

        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            Guid.NewGuid(),
            1,
            BuildVersion,
            ContentVersion,
            ServerOptions.MaximumIslandStartClients + 1)
        {
            IslandStart = true
        };
        CheckAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new DedicatedServer(options),
            "direct server construction must enforce the same raft capacity");
    }

    private static async ValueTask ProvisionsOnlyForIslandWorldsAsync(
        CancellationToken cancellationToken)
    {
        var scenario = FindScenario();
        var ordinaryWorldId = Guid.NewGuid();
        var ordinaryOptions = Options(
            ordinaryWorldId, scenario, saveRoot: null, islandStart: false);
        await using (var ordinary = await RunningServer.StartAsync(
                         ordinaryOptions, cancellationToken))
        await using (var client = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await ConnectAsync(
                client, ordinary, ordinaryWorldId, Guid.NewGuid(), "Mira",
                cancellationToken);
            CheckAssert.False(accepted.IslandStart || client.State.IslandStart,
                "ordinary worlds must project a non-island profile");
            await EventuallyAsync(
                () => client.State.Gameplay is not null,
                "ordinary client did not receive its private baseline",
                cancellationToken);
            CheckAssert.Equal(0, client.State.Boats.Count,
                "ordinary worlds must not provision a hidden raft");
        }

        var islandWorldId = Guid.NewGuid();
        var islandOptions = Options(
            islandWorldId, scenario, saveRoot: null, islandStart: true);
        await using var island = await RunningServer.StartAsync(
            islandOptions, cancellationToken);
        await using var islandClient = new NetworkGameClient(TimeSpan.Zero);
        var islandAccepted = await ConnectAsync(
            islandClient, island, islandWorldId, Guid.NewGuid(), "Elara",
            cancellationToken);
        CheckAssert.True(
            islandAccepted.IslandStart && islandClient.State.IslandStart,
            "the trusted island-start profile must cross the handshake");
        await EventuallyAsync(
            () => islandClient.State.Boats.Count == 1,
            "the fresh join baseline raced ahead of raft provisioning",
            cancellationToken);
        var boat = islandClient.State.Boats.Values.Single();
        CheckAssert.Equal(islandAccepted.PlayerId, boat.OwnerPlayerId,
            "the server-provisioned raft must belong to the joining player");
        CheckAssert.True((boat.EntityId & (1UL << 63)) != 0,
            "boat entity IDs must remain disjoint from actor IDs");
        CheckAssert.True((islandAccepted.PlayerEntityId & (1UL << 63)) == 0,
            "actor entity IDs must remain in the low-bit namespace");
    }

    private static async ValueTask
        OmittedBoatRemovalIsStaleWithoutSequenceGapAsync(
            CancellationToken cancellationToken)
    {
        var worldId = Guid.Parse("959e9fe3-82ba-5b83-bba0-25882a1fc7d8");
        await using var server = new DedicatedServer(new ServerOptions(
            IPAddress.Loopback,
            0,
            worldId,
            47,
            BuildVersion,
            ContentVersion,
            1));
        await using var connection = new ClientConnection(
            ClientConnectionId.New(),
            new TcpClient(AddressFamily.InterNetwork),
            server,
            cancellationToken);

        CheckAssert.True(connection.TryQueuePublicBootstrapAndActivate(
                Array.Empty<WorldChunkRevisionState>(),
                Array.Empty<ResourceChunkSparseState>(),
                Array.Empty<AuthoritativeBoatSnapshot>(),
                Array.Empty<AuthoritativeEnemySnapshot>(),
                1,
                sequence =>
                [
                    new BoatBaselineMessage(sequence, 1,
                        Array.Empty<BoatState>())
                ]),
            "the empty boat baseline could not initialize public high-water");

        var boatId = Guid.Parse("86041049-ae45-5d79-a867-7386110eeb45");
        ulong removalSequence = 0;
        CheckAssert.True(connection.TryQueuePublicSequenced(sequence =>
            {
                removalSequence = sequence;
                return new BoatDeltaBatchMessage(
                    sequence,
                    2,
                    [new BoatDelta(
                        BoatDeltaKind.Remove,
                        new IslandRpg.Protocol.BoatReference(boatId, 7),
                        8,
                        null)]);
            }),
            "a removal already represented by the baseline must be stale");
        CheckAssert.Equal(2UL, removalSequence,
            "the stale removal was offered the first post-baseline sequence");

        var staleUpsert = new BoatState(
            boatId,
            1UL << 63,
            8,
            Guid.Parse("453474a9-98a2-5015-bad6-2ef8272f7b1c"),
            string.Empty,
            Guid.Empty,
            0,
            1,
            2,
            1,
            0,
            0,
            false);
        CheckAssert.Throws<InvalidOperationException>(() =>
                connection.TryQueuePublicSequenced(sequence =>
                    new BoatDeltaBatchMessage(
                        sequence,
                        3,
                        [new BoatDelta(
                            BoatDeltaKind.Upsert,
                            new IslandRpg.Protocol.BoatReference(boatId, 7),
                            8,
                            staleUpsert)])),
            "an unknown upsert must still fail its retained revision chain");

        ulong addSequence = 0;
        var added = staleUpsert with { Revision = 1 };
        CheckAssert.True(connection.TryQueuePublicSequenced(sequence =>
            {
                addSequence = sequence;
                return new BoatDeltaBatchMessage(
                    sequence,
                    4,
                    [new BoatDelta(
                        BoatDeltaKind.Upsert,
                        new IslandRpg.Protocol.BoatReference(boatId, 0),
                        1,
                        added)]);
            }),
            "a legitimate first upsert must remain admissible");
        CheckAssert.Equal(removalSequence, addSequence,
            "filtering the stale removal must not consume a reliable sequence");
    }

    private static async ValueTask ReplicatesBoatFishingAndRestartAsync(
        CancellationToken cancellationToken)
    {
        using var save = TemporarySaveRoot.Create();
        var scenario = FindScenario();
        var worldId = Guid.NewGuid();
        var actorClientId = Guid.NewGuid();
        var observerClientId = Guid.NewGuid();
        var options = Options(
            worldId, scenario, save.Path, islandStart: true);

        Guid actorPlayerId;
        string reconnectToken;
        Guid actorBoatId;
        ulong actorBoatEntityId;
        uint completedBoatRevision;
        int fishingExperience;
        int caughtQuantity;
        ResourceNodeSparseState caughtSchool;

        await using (var host = await RunningServer.StartAsync(
                         options, cancellationToken))
        await using (var actor = new NetworkGameClient(TimeSpan.Zero))
        await using (var observer = new NetworkGameClient(TimeSpan.Zero))
        {
            var accepted = await ConnectAsync(
                actor, host, worldId, actorClientId, "Elara",
                cancellationToken);
            actorPlayerId = accepted.PlayerId;
            reconnectToken = accepted.ReconnectToken;
            await EventuallyAsync(
                () => actor.State.Gameplay is not null &&
                      actor.State.Boats.Count == 1,
                "the first island join did not receive its atomic raft baseline",
                cancellationToken);

            var observerBoatReceipts = 0;
            var observerFishReceipts = 0;
            observer.BoatActionCompleted += (_, _) =>
                Interlocked.Increment(ref observerBoatReceipts);
            observer.ResourceActionCompleted += (_, _) =>
                Interlocked.Increment(ref observerFishReceipts);
            await ConnectAsync(
                observer, host, worldId, observerClientId, "Aveline",
                cancellationToken);
            await EventuallyAsync(
                () => observer.State.Gameplay is not null &&
                      observer.State.Boats.Count == 2 &&
                      actor.State.Boats.Count == 2,
                "the second raft was not public to the observer and first client",
                cancellationToken);

            var ownedBoat = actor.State.Boats.Values.Single(value =>
                value.OwnerPlayerId == actorPlayerId);
            actorBoatId = ownedBoat.BoatId;
            actorBoatEntityId = ownedBoat.EntityId;
            var initialBoatPosition = new Vector2(ownedBoat.X, ownedBoat.Y);

            var boarded = await SendBoatAsync(
                actor,
                new BoardBoatAction(actor.GetBoatReference(actorBoatId)),
                cancellationToken);
            CheckAssert.True(boarded.Accepted && boarded.Transitioned,
                $"the owner could not board its raft: {boarded.Detail}");
            await EventuallyAsync(
                () => observer.State.Boats.TryGetValue(
                          actorBoatId, out var value) &&
                      value.OccupantPlayerId == actorPlayerId,
                "the observer did not receive public boat occupancy",
                cancellationToken);
            CheckAssert.Equal(0, Volatile.Read(ref observerBoatReceipts),
                "another player's private boat receipt leaked to the observer");

            var udpMovement = new TaskCompletionSource<EntitySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            observer.SnapshotReceived += (_, args) =>
            {
                if (args.Snapshot.Sequence != 0) return;
                foreach (var entity in args.Snapshot.Entities)
                {
                    if (entity.EntityId == actorBoatEntityId &&
                        entity.EntityKind == NetworkEntityKind.Boat &&
                        Vector2.DistanceSquared(
                            new(entity.X, entity.Y), initialBoatPosition) > .01f)
                    {
                        udpMovement.TrySetResult(entity);
                    }
                }
            };

            var moved = await SendBoatAsync(
                actor,
                new MoveBoatAction(
                    actor.GetBoatReference(actorBoatId),
                    scenario.Fish.Position.X,
                    scenario.Fish.Position.Y),
                cancellationToken);
            CheckAssert.True(moved.Accepted,
                $"the target-only raft route was rejected: {moved.Detail}");
            var udpBoat = await udpMovement.Task.WaitAsync(
                Timeout, cancellationToken);
            CheckAssert.True(
                udpBoat.State.HasFlag(NetworkEntityState.Moving),
                "the UDP boat transform did not carry moving state");

            await EventuallyAsync(
                () => actor.State.Boats.TryGetValue(
                          actorBoatId, out var value) &&
                      !value.Moving &&
                      Vector2.DistanceSquared(
                          new(value.X, value.Y),
                          scenario.Fish.Position) < .01f,
                "the reliable route-completion delta did not publish arrival",
                cancellationToken);
            await EventuallyAsync(
                () => observer.State.Boats.TryGetValue(
                          actorBoatId, out var value) &&
                      !value.Moving &&
                      Vector2.DistanceSquared(
                          new(value.X, value.Y),
                          scenario.Fish.Position) < .01f,
                "the observer did not receive the reliable boat arrival",
                cancellationToken);
            completedBoatRevision = actor.State.Boats[actorBoatId].Revision;

            var observerInitialFish = Quantity(
                observer.State.Gameplay!, ItemIds.RawMinnows);
            ResourceActionResultMessage caught = null!;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (attempt != 0)
                    await Task.Delay(TimeSpan.FromSeconds(2.9),
                        cancellationToken);
                caught = await SendResourceAsync(
                    actor,
                    new ResourceActionPayload(
                        ResourceActionKind.Fish,
                        actor.GetResourceReference(
                            scenario.Fish.Chunk, scenario.Fish.Id),
                        ToolInventorySlot: 0),
                    cancellationToken);
                CheckAssert.True(caught.Accepted &&
                                 caught.FishingOutcome is not null,
                    $"the authoritative fishing attempt failed: {caught.Detail}");
                CheckAssert.Equal(FishSpecies.ShoreMinnows,
                    caught.FishingOutcome!.Value.Species,
                    "the typed fishing result changed species");
                if (caught.FishingOutcome.Value.Caught) break;
            }
            CheckAssert.True(caught.FishingOutcome is { Caught: true },
                "the bounded deterministic attempts never produced a catch");
            await EventuallyAsync(
                () => actor.State.Gameplay is { FishingExperience: > 0 } &&
                      Quantity(actor.State.Gameplay, ItemIds.RawMinnows) > 0,
                "the requester did not receive private catch inventory and XP",
                cancellationToken);
            await EventuallyAsync(
                () => observer.State.ResourceChunks.TryGetValue(
                          scenario.Fish.Chunk, out var chunk) &&
                      chunk.Nodes.TryGetValue(
                          scenario.Fish.Id, out var node) &&
                      node.NodeRevision > 0,
                "the observer did not receive public depleted fish stock",
                cancellationToken);
            CheckAssert.Equal(0, Volatile.Read(ref observerFishReceipts),
                "another player's private fishing receipt leaked to the observer");
            CheckAssert.Equal(observerInitialFish,
                Quantity(observer.State.Gameplay!, ItemIds.RawMinnows),
                "another player's catch leaked into the observer inventory");

            fishingExperience = actor.State.Gameplay!.FishingExperience;
            caughtQuantity = Quantity(
                actor.State.Gameplay, ItemIds.RawMinnows);
            caughtSchool = observer.State.ResourceChunks[scenario.Fish.Chunk]
                .Nodes[scenario.Fish.Id];
            await actor.DisconnectAsync(cancellationToken);
            await observer.DisconnectAsync(cancellationToken);
        }

        await using var restarted = await RunningServer.StartAsync(
            options, cancellationToken);
        await using var resumed = new NetworkGameClient(TimeSpan.Zero);
        var resumedHandshake = await ConnectAsync(
            resumed, restarted, worldId, actorClientId, "Elara",
            cancellationToken, actorPlayerId, reconnectToken);
        CheckAssert.True(resumedHandshake.IslandStart,
            "restart reconnect lost trusted island-start metadata");
        await EventuallyAsync(
            () => resumed.State.Gameplay?.FishingExperience ==
                      fishingExperience &&
                  Quantity(resumed.State.Gameplay, ItemIds.RawMinnows) ==
                      caughtQuantity &&
                  resumed.State.Boats.Count == 2 &&
                  resumed.State.Boats.TryGetValue(
                      actorBoatId, out var boat) &&
                  boat.EntityId == actorBoatEntityId &&
                  boat.Revision == completedBoatRevision,
            "restart reconnect lost catch state or stable raft authority",
            cancellationToken);

        await using var lateJoin = new NetworkGameClient(TimeSpan.Zero);
        await ConnectAsync(
            lateJoin, restarted, worldId, Guid.NewGuid(), "Yvette",
            cancellationToken);
        await EventuallyAsync(
            () => lateJoin.State.Boats.Count == 3 &&
                  lateJoin.State.Boats.TryGetValue(
                      actorBoatId, out var boat) &&
                  boat.EntityId == actorBoatEntityId &&
                  lateJoin.State.ResourceChunks.TryGetValue(
                      scenario.Fish.Chunk, out var chunk) &&
                  chunk.Nodes.TryGetValue(
                      scenario.Fish.Id, out var node) &&
                  node == caughtSchool,
            "late join did not receive durable boats and sparse fish stock",
            cancellationToken);
    }

    private static async ValueTask ShoreFishingWithNetWalksThenCatchesAsync(
        CancellationToken cancellationToken)
    {
        var scenario = FindShoreScenario();
        var worldId = Guid.NewGuid();
        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            worldId,
            scenario.Seed,
            BuildVersion,
            ContentVersion,
            8)
        {
            AutosaveInterval = TimeSpan.FromHours(1),
            StartingPosition = scenario.Spawn,
            StartingInventory =
            [
                new InitialInventoryItem(ItemIds.PrimitiveFishingNet)
            ]
        };

        await using var host = await RunningServer.StartAsync(
            options, cancellationToken);
        await using var actor = new NetworkGameClient(TimeSpan.Zero);
        var accepted = await ConnectAsync(
            actor, host, worldId, Guid.NewGuid(), "Elara",
            cancellationToken);
        await EventuallyAsync(
            () => actor.State.Gameplay is not null &&
                  Quantity(
                      actor.State.Gameplay, ItemIds.PrimitiveFishingNet) > 0,
            "the shore fisher did not receive a fishing net",
            cancellationToken);

        var netSlot = actor.State.Gameplay!.InventorySlots
            .First(slot => string.Equals(
                slot.ItemId, ItemIds.PrimitiveFishingNet,
                StringComparison.Ordinal))
            .Slot;
        CheckAssert.True(
            GameHostWindowReach.WithinServerShoreReach(
                scenario.Stand, scenario.Fish.Position),
            "the chosen stand must be inside the 2.4 shore fishing reach");
        CheckAssert.False(
            GameHostWindowReach.WithinServerShoreReach(
                scenario.Spawn, scenario.Fish.Position),
            "the walk-to-act spawn must start outside server fishing reach");

        await actor.SendWalkAsync(
            scenario.Stand.X, scenario.Stand.Y, 0, cancellationToken);
        await EventuallyAsync(
            () => actor.State.Entities.TryGetValue(
                      accepted.PlayerEntityId, out var pose) &&
                  GameHostWindowReach.WithinServerShoreReach(
                      new(pose.X, pose.Y), scenario.Fish.Position),
            "the server pose never arrived inside fishing reach",
            cancellationToken);

        await actor.SendPresentSkillAsync(
            (byte)EntityAction.Fish, cancellationToken: cancellationToken);

        ResourceActionResultMessage caught = null!;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (attempt != 0)
                await Task.Delay(TimeSpan.FromSeconds(2.9), cancellationToken);
            caught = await SendResourceAsync(
                actor,
                new ResourceActionPayload(
                    ResourceActionKind.Fish,
                    actor.GetResourceReference(
                        scenario.Fish.Chunk, scenario.Fish.Id),
                    netSlot),
                cancellationToken);
            CheckAssert.True(
                caught.Accepted && caught.FishingOutcome is not null,
                $"shore fishing with a net was rejected: {caught.Detail} " +
                $"({caught.RejectionCode})");
            CheckAssert.Equal(
                scenario.Fish.Species, caught.FishingOutcome!.Value.Species,
                "the shore catch changed species");
            if (caught.FishingOutcome.Value.Caught) break;
        }
        CheckAssert.True(caught.FishingOutcome is { Caught: true },
            "eight shore casts with a net never produced a catch");
        await EventuallyAsync(
            () => Quantity(actor.State.Gameplay, ItemIds.RawMinnows) > 0 &&
                  actor.State.Gameplay!.FishingExperience > 0,
            "the shore catch never appeared in inventory or Fishing XP",
            cancellationToken);
    }

    private static ShoreScenario FindShoreScenario()
    {
        var fishSource = new ProceduralFishSchoolSource();
        for (var seed = 1L; seed <= 2_048; seed++)
        {
            var land = new ProceduralSurfaceNavigationQuery(seed);
            for (var chunkY = -2; chunkY <= 2; chunkY++)
            for (var chunkX = -2; chunkX <= 2; chunkX++)
            {
                var chunk = new WorldChunkKey(chunkX, chunkY, 0);
                foreach (var fish in fishSource.DescribeSchools(seed, chunk)
                             .Where(static value =>
                                 value.Species == FishSpecies.ShoreMinnows))
                {
                    if (!TryFindStand(land, fish.Position, 2.4f, out var stand))
                        continue;
                    if (!TryFindSpawnOutsideReach(
                            land, fish.Position, stand, out var spawn))
                        continue;
                    return new ShoreScenario(seed, spawn, stand, fish);
                }
            }
        }
        throw new InvalidOperationException(
            "No bounded land spawn/stand/minnow scenario was found.");
    }

    private static bool TryFindStand(
        ProceduralSurfaceNavigationQuery land,
        Vector2 fish,
        float reach,
        out Vector2 stand)
    {
        stand = default;
        var best = float.MaxValue;
        var tileX = (int)MathF.Floor(fish.X);
        var tileY = (int)MathF.Floor(fish.Y);
        for (var y = -3; y <= 3; y++)
        for (var x = -3; x <= 3; x++)
        {
            var candidate = new Vector2(tileX + x + .5f, tileY + y + .5f);
            var distance = Vector2.Distance(candidate, fish);
            if (distance > reach || distance >= best ||
                !land.CanStandAt(candidate, 0))
                continue;
            best = distance;
            stand = candidate;
        }
        return best < float.MaxValue;
    }

    private static bool TryFindSpawnOutsideReach(
        ProceduralSurfaceNavigationQuery land,
        Vector2 fish,
        Vector2 stand,
        out Vector2 spawn)
    {
        spawn = default;
        var best = float.MaxValue;
        for (var y = -10; y <= 10; y++)
        for (var x = -10; x <= 10; x++)
        {
            var candidate = new Vector2(stand.X + x, stand.Y + y);
            var toFish = Vector2.Distance(candidate, fish);
            var toStand = Vector2.Distance(candidate, stand);
            if (toFish <= 2.4f || toStand is < 3 or > 8 ||
                toStand >= best ||
                !land.CanStandAt(candidate, 0))
                continue;
            best = toStand;
            spawn = candidate;
        }
        return best < float.MaxValue;
    }

    private static class GameHostWindowReach
    {
        public static bool WithinServerShoreReach(
            Vector2 origin, Vector2 target) =>
            Vector2.DistanceSquared(origin, target) <= 2.4f * 2.4f;
    }

    private static Scenario FindScenario()
    {
        var fishSource = new ProceduralFishSchoolSource();
        for (var seed = 1L; seed <= 1_024; seed++)
        {
            var boats = new ProceduralBoatNavigationQuery(seed);
            var players = new ProceduralSurfaceNavigationQuery(seed);
            for (var radius = 0; radius <= 24; radius++)
            for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius) continue;
                var spawn = new Vector2(x + .5f, y + .5f);
                if (!players.CanStandAt(spawn, 0) ||
                    boats.IsNavigable(spawn))
                    continue;
                var boat = BoatTravelRules.FindInitialPosition(
                    boats, spawn, maximumRadius: 4);
                if (!boats.IsNavigable(boat) ||
                    Vector2.DistanceSquared(spawn, boat) > 2.4f * 2.4f)
                    continue;
                var center = WorldChunkKey.At(boat, 0);
                for (var chunkY = center.Y - 1; chunkY <= center.Y + 1;
                     chunkY++)
                for (var chunkX = center.X - 1; chunkX <= center.X + 1;
                     chunkX++)
                {
                    var chunk = new WorldChunkKey(chunkX, chunkY, 0);
                    foreach (var fish in fishSource.DescribeSchools(seed, chunk)
                                 .Where(static value =>
                                     value.Species ==
                                     FishSpecies.ShoreMinnows))
                    {
                        var distance = Vector2.Distance(boat, fish.Position);
                        if (distance < 3 || distance > 18 ||
                            !boats.IsNavigable(fish.Position))
                            continue;
                        var route = BoatRoutePlanner.Find(
                            boats, boat, fish.Position, 4_096);
                        if (route.Count == 0) continue;
                        return new Scenario(seed, spawn, boat, fish);
                    }
                }
            }
        }
        throw new InvalidOperationException(
            "No bounded deterministic shore/raft/beginner-fish scenario was found.");
    }

    private static ServerOptions Options(
        Guid worldId,
        Scenario scenario,
        string? saveRoot,
        bool islandStart) => new(
        IPAddress.Loopback,
        0,
        worldId,
        scenario.Seed,
        BuildVersion,
        ContentVersion,
        8)
    {
        SaveRoot = saveRoot,
        AutosaveInterval = TimeSpan.FromHours(1),
        StartingPosition = scenario.Spawn,
        StartingInventory =
        [
            new InitialInventoryItem(ItemIds.PrimitiveFishingNet)
        ],
        IslandStart = islandStart
    };

    private static Task<HandshakeAcceptedMessage> ConnectAsync(
        NetworkGameClient client,
        RunningServer server,
        Guid worldId,
        Guid clientId,
        string name,
        CancellationToken cancellationToken,
        Guid reconnectPlayerId = default,
        string reconnectToken = "") => client.ConnectAsync(
        server.Endpoint.Address.ToString(),
        server.Endpoint.Port,
        new ClientHandshakeOptions(
            BuildVersion,
            ContentVersion,
            clientId,
            name,
            worldId,
            reconnectPlayerId,
            reconnectToken,
            Capabilities:
                ClientCapabilities.UdpSnapshots |
                ClientCapabilities.DeltaSnapshots),
        cancellationToken);

    private static async Task<BoatActionResultMessage> SendBoatAsync(
        NetworkGameClient client,
        BoatActionPayload action,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var completion = new TaskCompletionSource<BoatActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkBoatActionResultEventArgs args)
        {
            if (args.Result.CommandId == commandId)
                completion.TrySetResult(args.Result);
        }
        client.BoatActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(
                action, commandId, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.BoatActionCompleted -= Handler;
        }
    }

    private static async Task<ResourceActionResultMessage> SendResourceAsync(
        NetworkGameClient client,
        ResourceActionPayload action,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var completion =
            new TaskCompletionSource<ResourceActionResultMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkResourceActionResultEventArgs args)
        {
            if (args.Result.CommandId == commandId)
                completion.TrySetResult(args.Result);
        }
        client.ResourceActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(
                action, commandId, cancellationToken);
            return await completion.Task.WaitAsync(Timeout, cancellationToken);
        }
        finally
        {
            client.ResourceActionCompleted -= Handler;
        }
    }

    private static int Quantity(
        NetworkPlayerGameplayState? gameplay,
        string itemId) => gameplay?.InventorySlots
        .Where(value => string.Equals(
            value.ItemId, itemId, StringComparison.Ordinal))
        .Sum(static value => value.Quantity) ?? 0;

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
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException(failure);
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _run;

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
            var endpoint = await server.Started.WaitAsync(
                Timeout, cancellationToken);
            return new RunningServer(server, endpoint, shutdown, run);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _run.WaitAsync(Timeout, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
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
                System.IO.Path.GetTempPath(),
                "IslandRpg-BoatFishingChecks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporarySaveRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }

    private sealed record Scenario(
        long Seed,
        Vector2 Spawn,
        Vector2 Boat,
        FishSchoolDescriptor Fish);

    private sealed record ShoreScenario(
        long Seed,
        Vector2 Spawn,
        Vector2 Stand,
        FishSchoolDescriptor Fish);
}
