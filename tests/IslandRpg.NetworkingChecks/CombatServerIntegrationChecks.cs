using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class CombatServerIntegrationChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("combat server adapter preserves revisions and event roles",
            AdapterProjection);
        checks.Add("combat server checkpoint preserves exact authority",
            CheckpointRoundTrip);
        checks.Add("fresh combat bootstrap is deterministic and bounded",
            Bootstrap);
        checks.Add("actor entity namespace agrees across combat transport",
            ActorEntityIdentityLoopbackAsync);
        checks.Add("real server replicates private combat and enemy UDP",
            CombatLoopbackAsync);
        checks.Add("two clients observe combat death and respawn entity flags",
            DeathAndRespawnEntityFlagsLoopbackAsync);
        checks.Add("dense UDP interest keeps rotating remote players",
            DenseUdpInterestKeepsRemotePlayersAsync);
    }

    private static void AdapterProjection()
    {
        var enemyId = new EnemyId(Guid.Parse(
            "30a0b340-424f-5f7e-a754-c47d527b487a"));
        var enemy = Enemy(enemyId, revision: 3);
        var delta = CombatActionProtocolAdapter.ToPublicDelta(
            9, 12, new EnemyStateDelta(EnemyChangeKind.Added, null, enemy));
        Equal(0u, delta!.Deltas[0].Reference.ExpectedRevision,
            "new enemy expected revision");
        Equal(3u, delta.Deltas[0].CurrentRevision,
            "new enemy current revision");

        var childId = new EnemyId(Guid.Parse(
            "8a103e46-6597-50a4-a494-fc0fd292327f"));
        var split = new CombatEventSnapshot(
            7, 12, IslandRpg.Simulation.CombatEventKind.EnemySplit,
            null, enemyId, SpawnedEnemyIds: [childId]);
        var projected = CombatActionProtocolAdapter.ToEvent(
            split,
            new Dictionary<EnemyId, AuthoritativeEnemySnapshot>
                { [enemyId] = enemy },
            new Dictionary<ActorId, (ulong, float, float, int)>());
        Equal(enemy.NetworkEntityId, projected!.Value.SourceEntityId,
            "split source");
        Equal(0UL, projected.Value.TargetEntityId, "split target");
        Equal(AuthoritativeCombatTransactions.DeriveNetworkEntityId(childId),
            projected.Value.RelatedEntityId, "split child");

        var actorId = new ActorId(Guid.Parse(
            "8c2c074f-8f97-526a-9aae-95171495d1dc"));
        const ulong actorEntity = 418;
        var expired = new CombatEventSnapshot(
            8, 13, IslandRpg.Simulation.CombatEventKind.StatusExpired,
            actorId, null, Status: SlimeStatusKind.Root);
        var projectedExpiry = CombatActionProtocolAdapter.ToEvent(
            expired,
            new Dictionary<EnemyId, AuthoritativeEnemySnapshot>(),
            new Dictionary<ActorId, (ulong, float, float, int)>
            {
                [actorId] = (actorEntity, 5, 6, 0)
            });
        Equal(IslandRpg.Protocol.CombatEventKind.StatusExpired,
            projectedExpiry!.Value.Kind, "status expiry kind");
        Equal(0UL, projectedExpiry.Value.SourceEntityId,
            "status expiry source");
        Equal(actorEntity, projectedExpiry.Value.TargetEntityId,
            "status expiry target");
        Equal(CombatStatusEffect.Root, projectedExpiry.Value.StatusEffect,
            "status expiry effect");
    }

    private static void CheckpointRoundTrip()
    {
        var worldId = Guid.Parse("74fbc589-2870-52bf-b6de-99237bc73695");
        const long seed = 912_117;
        var options = new ServerOptions(
            System.Net.IPAddress.Loopback, 0, worldId, seed,
            "combat-test", "base", 4);
        var actorId = new ActorId(Guid.Parse(
            "45179e9b-4f1d-5a06-99f9-f7cd41c6ff66"));
        var playerId = new PlayerId(Guid.Parse(
            "91dcf263-3a21-54da-b45c-54e6308f7e65"));
        var enemyId = new EnemyId(Guid.Parse(
            "dd1d0f5a-907e-5734-a598-705c5749476d"));
        var gameplay = new PlayerGameplaySnapshot(
            4, 47, 65, 0, 2, 3,
            new PlayerInventorySnapshot(2,
                Enumerable.Range(0, PlayerInventory.Capacity).Select(index =>
                    new InventorySlotSnapshot(index, null, 0))
                    .ToImmutableArray()),
            AdventureExperience: AdventureService.ExperienceForLevel(15),
            MaximumHealth: 128,
            AttackExperience: 17,
            StrengthExperience: 19,
            DefenceExperience: 23,
            CombatStance: MeleeCombatStance.Defensive,
            LifeState: ActorLifeState.Alive,
            RespawnAvailableTick: 0,
            CombatStatus: new SlimeVictimStatus(PoisonedUntil: 15,
                NextPoisonTickAt: 14, PoisonDamage: 2),
            CombatTargetEnemyId: enemyId,
            CombatAttackSequence: 8,
            NextCombatAttackTick: 250);
        var checkpointEnemy = new AuthoritativeEnemyCheckpoint(
            enemyId, 6, EnemyKind.CaveSlime, EnemyBehavior.Chase,
            new Vector2(4, 5), new Vector2(6, 7), new Vector2(.1f, .2f),
            -1, 5, 18, 36, 1.2f, default, actorId, null, 1, 4, 80, 0,
            0, 0, 0);
        var source = new AuthoritativeSessionCheckpoint(
            new SessionId(worldId), 700, 13,
            [new AuthoritativeActorCheckpoint(
                new PlayerIdentity(playerId, actorId), "fighter",
                Vector2.Zero, 0, 1, null, gameplay,
                Enumerable.Repeat((byte)7, 32).ToImmutableArray(), [])],
            new AuthoritativeWorldTransactionsCheckpoint([], []),
            Resources: AuthoritativeResourceTransactionsCheckpoint.Empty,
            Boats: AuthoritativeBoatTransactionsCheckpoint.Empty,
            Combat: new AuthoritativeCombatCheckpoint(seed, 9, 2,
                [checkpointEnemy]));
        var durable = ServerCheckpointMapper.ToDurable(source, options, 1);
        ServerCheckpointStore.Validate(durable, worldId);
        var restored = ServerCheckpointMapper.ToSimulation(durable, options);
        var actual = restored.Actors[0].Gameplay;
        Equal(gameplay.MaximumHealth, actual.MaximumHealth, "maximum health");
        Equal(gameplay.AttackExperience, actual.AttackExperience,
            "attack experience");
        Equal(gameplay.StrengthExperience, actual.StrengthExperience,
            "strength experience");
        Equal(gameplay.DefenceExperience, actual.DefenceExperience,
            "defence experience");
        Equal(gameplay.CombatStance, actual.CombatStance, "combat stance");
        Equal(gameplay.LifeState, actual.LifeState, "life state");
        Equal(gameplay.RespawnAvailableTick, actual.RespawnAvailableTick,
            "respawn tick");
        Equal(gameplay.CombatStatus, actual.CombatStatus, "combat status");
        Equal(gameplay.CombatTargetEnemyId, actual.CombatTargetEnemyId,
            "combat target");
        Equal(gameplay.CombatAttackSequence, actual.CombatAttackSequence,
            "attack sequence");
        Equal(gameplay.NextCombatAttackTick, actual.NextCombatAttackTick,
            "next attack tick");
        Equal(checkpointEnemy, restored.Combat!.Enemies[0], "enemy checkpoint");
        Equal(9UL, restored.Combat.NextEventOrdinal, "combat event ordinal");

        // A split child may remain after its parent's retained corpse is
        // retired; ParentEnemyId is durable provenance rather than a live FK.
        var retiredParent = Guid.Parse(
            "0e22bb85-917c-51be-bffe-f857ea777a25");
        var childOnly = durable with
        {
            Combat = durable.Combat! with
            {
                Enemies = durable.Combat.Enemies.Select(enemy => enemy with
                {
                    ParentEnemyId = retiredParent
                }).ToArray()
            }
        };
        ServerCheckpointStore.Validate(childOnly, worldId);
    }

    private static void Bootstrap()
    {
        const long seed = 71_311;
        var navigation = OpenWorldNavigationQuery.Instance;
        var first = ProceduralEnemyBootstrap.Create(
            seed, Vector2.Zero, navigation);
        var second = ProceduralEnemyBootstrap.Create(
            seed, Vector2.Zero, navigation);
        Equal(true, first.Count is > 0 and <= 16, "enemy count bound");
        Equal(string.Join('|', first.Select(Identity)),
            string.Join('|', second.Select(Identity)),
            "deterministic bootstrap");
        Equal(first.Count, first.Select(value => value.EnemyId).Distinct().Count(),
            "unique enemy identities");
        static string Identity(AuthoritativeEnemySeed value) =>
            $"{value.EnemyId.Value:N}:{value.Kind}:{value.Position.X:R}:" +
            $"{value.Position.Y:R}:{value.WorldLevel}:{value.PowerLevel}";
    }

    private static async ValueTask ActorEntityIdentityLoopbackAsync(
        CancellationToken cancellationToken)
    {
        const long seed = 58_409;
        var actorId = new ActorId(Guid.Parse(
            "00000000-0000-0000-0000-000000000040"));
        var identity = new PlayerIdentity(
            new PlayerId(Guid.Parse(
                "72537c1a-1038-52cc-baaa-6d66e433aaec")),
            actorId);
        var legacyServerId = LegacyServerActorEntityId(actorId.Value);
        CheckAssert.True((legacyServerId & (1UL << 62)) != 0,
            "the regression actor must occupy the enemy namespace under the old server mask");
        var expected = ActorNetworkEntityIdentity.Derive(actorId);
        Equal(0UL, expected >> 62, "canonical actor namespace");
        var enemyEntity = AuthoritativeCombatTransactions.DeriveNetworkEntityId(
            new EnemyId(actorId.Value));
        var boatEntity = AuthoritativeBoatTransactions.DeriveNetworkEntityId(
            new BoatId(actorId.Value));
        Equal(1UL, enemyEntity >> 62, "enemy namespace");
        Equal(2UL, boatEntity >> 62, "boat namespace");
        CheckAssert.True(expected != enemyEntity && expected != boatEntity &&
                         enemyEntity != boatEntity,
            "actor, enemy, and boat transport namespaces must be disjoint");

        var worldId = Guid.NewGuid();
        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            worldId,
            seed,
            "combat-loopback",
            "combat-v1",
            1)
        {
            StartingPosition = BoatTravelRules.FindPlayableLandSpawn(
                seed, cancellationToken),
            SnapshotPort = 0
        };
        await using var server = new RunningServer(
            options,
            cancellationToken,
            new FixedIdentitySource(identity));
        await server.Started;
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        var playerSnapshot = new TaskCompletionSource<EntitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var targetedActor = new TaskCompletionSource<CombatEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotReceived += (_, args) =>
        {
            foreach (var entity in args.Snapshot.Entities)
                if (entity.EntityKind == NetworkEntityKind.Player)
                    playerSnapshot.TrySetResult(entity);
        };
        client.CombatEventsReceived += (_, args) =>
        {
            foreach (var combatEvent in args.Events)
                if (combatEvent.TargetEntityId != 0)
                    targetedActor.TrySetResult(combatEvent);
        };

        var accepted = await Connect(
            client, server.Endpoint, worldId, "Namespace", cancellationToken);
        Equal(expected, accepted.PlayerEntityId, "handshake player entity");
        var motion = await playerSnapshot.Task.WaitAsync(
            TimeSpan.FromSeconds(8), cancellationToken);
        Equal(expected, motion.EntityId, "UDP player snapshot entity");

        await Eventually(
            () => client.State.Enemies.Values.Any(enemy =>
                enemy.WorldLevel == accepted.SpawnWorldLevel &&
                enemy.Health > 0),
            "an enemy baseline did not arrive for the identity check",
            cancellationToken);
        var target = client.State.Enemies.Values
            .Where(enemy => enemy.WorldLevel == accepted.SpawnWorldLevel &&
                            enemy.Health > 0)
            .OrderBy(enemy => Vector2.DistanceSquared(
                new Vector2(accepted.SpawnX, accepted.SpawnY),
                new Vector2(enemy.X, enemy.Y)))
            .First();
        await client.SendWalkAsync(
            target.X, target.Y, target.WorldLevel, cancellationToken);
        var attack = await targetedActor.Task.WaitAsync(
            TimeSpan.FromSeconds(12), cancellationToken);
        Equal(expected, attack.TargetEntityId,
            "combat event actor target entity");
    }

    private static ulong LegacyServerActorEntityId(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        var result = (BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
                      BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])) &
                     0x7fff_ffff_ffff_ffffUL;
        return result == 0 ? 1 : result;
    }

    private static async ValueTask CombatLoopbackAsync(
        CancellationToken cancellationToken)
    {
        var worldId = Guid.NewGuid();
        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            worldId,
            44_211,
            "combat-loopback",
            "combat-v1",
            4)
        {
            StartingPosition = Vector2.Zero,
            SnapshotPort = 0
        };
        await using var server = new RunningServer(options, cancellationToken);
        await server.Started;
        await using var actor = new NetworkGameClient(TimeSpan.Zero);
        await using var observer = new NetworkGameClient(TimeSpan.Zero);
        var enemyMotion = new TaskCompletionSource<EntitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        actor.SnapshotReceived += (_, args) =>
        {
            if (args.Snapshot.Sequence != 0) return;
            foreach (var entity in args.Snapshot.Entities)
                if (entity.EntityKind == NetworkEntityKind.Enemy)
                    enemyMotion.TrySetResult(entity);
        };
        var actorHandshake = await Connect(
            actor, server.Endpoint, worldId, "Fighter", cancellationToken);
        var observerReceipts = 0;
        observer.CombatActionCompleted += (_, _) =>
            Interlocked.Increment(ref observerReceipts);
        await Connect(observer, server.Endpoint, worldId, "Witness",
            cancellationToken);
        await Eventually(
            () => actor.State.Gameplay is not null &&
                  actor.State.Enemies.Values.Any(enemy =>
                      enemy.WorldLevel == 0) &&
                  observer.State.Enemies.Count == actor.State.Enemies.Count,
            "combat baselines did not reach both clients",
            cancellationToken);
        var udpEnemy = await enemyMotion.Task.WaitAsync(
            TimeSpan.FromSeconds(8), cancellationToken);
        CheckAssert.True(udpEnemy.EntityId != 0,
            "enemy UDP identity must be stable and nonzero");

        var stance = await SendCombat(
            actor,
            new SetCombatStanceAction(CombatStance.Defensive),
            cancellationToken);
        CheckAssert.True(stance.Accepted,
            $"the combat stance was rejected: {stance.Detail}");
        await Eventually(
            () => actor.State.Gameplay?.CombatStance == CombatStance.Defensive,
            "the authoritative private stance did not precede its receipt",
            cancellationToken);
        var target = actor.State.Enemies.Values
            .Where(enemy => enemy.WorldLevel == 0 && enemy.Health > 0)
            .OrderBy(enemy => enemy.EnemyId)
            .First();
        var targeted = await SendCombat(
            actor,
            new SetCombatTargetAction(
                actor.GetEnemyReference(target.EnemyId)),
            cancellationToken);
        CheckAssert.True(targeted.Accepted,
            $"the exact-revision target was rejected: {targeted.Detail}");
        await Eventually(
            () => actor.State.Gameplay?.CombatTargetEnemyId == target.EnemyId,
            "the private combat state did not converge to its target",
            cancellationToken);
        await Task.Delay(100, cancellationToken);
        CheckAssert.Equal(0, Volatile.Read(ref observerReceipts),
            "another player's private combat receipt leaked to an observer");

        await actor.DisconnectAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);
        await using var reconnect = new NetworkGameClient(TimeSpan.Zero);
        await reconnect.ConnectAsync(
            server.Endpoint.Address.ToString(),
            server.Endpoint.Port,
            new ClientHandshakeOptions(
                "combat-loopback",
                "combat-v1",
                Guid.NewGuid(),
                "Fighter",
                worldId,
                ReconnectPlayerId: actorHandshake.PlayerId,
                ReconnectToken: actorHandshake.ReconnectToken,
                Capabilities: ClientCapabilities.UdpSnapshots |
                              ClientCapabilities.DeltaSnapshots),
            cancellationToken);
        await Eventually(
            () => reconnect.State.Gameplay?.CombatTargetEnemyId ==
                      target.EnemyId &&
                  reconnect.State.Enemies.ContainsKey(target.EnemyId),
            "a reconnect baseline did not restore the durable combat target",
            cancellationToken);
    }

    private static async ValueTask DeathAndRespawnEntityFlagsLoopbackAsync(
        CancellationToken cancellationToken)
    {
        var worldId = Guid.NewGuid();
        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            worldId,
            44_212,
            "combat-loopback",
            "combat-v1",
            4)
        {
            StartingPosition = Vector2.Zero,
            SnapshotPort = 0,
            CombatOptions = new AuthoritativeCombatOptions
            {
                AggroRange = 4,
                LeashRange = 12,
                EnemyAttackRange = 4,
                EnemyAttackIntervalTicks = 1,
                RespawnDelayTicks = 12,
                RespawnPosition = new Vector2(30, 30)
            }
        };
        await using var server = new RunningServer(options, cancellationToken);
        await server.Started;
        await using var victim = new NetworkGameClient(TimeSpan.Zero);
        await using var observer = new NetworkGameClient(TimeSpan.Zero);
        var accepted = await Connect(
            victim, server.Endpoint, worldId, "Flag victim", cancellationToken);
        var deadSnapshot = new TaskCompletionSource<EntitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var respawnSnapshot = new TaskCompletionSource<EntitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var respawnRequested = 0;
        observer.SnapshotReceived += (_, args) =>
        {
            foreach (var entity in args.Snapshot.Entities)
            {
                if (entity.EntityId != accepted.PlayerEntityId) continue;
                if (entity.State.HasFlag(NetworkEntityState.Dead))
                    deadSnapshot.TrySetResult(entity);
                else if (Volatile.Read(ref respawnRequested) != 0)
                    respawnSnapshot.TrySetResult(entity);
            }
        };
        await Connect(
            observer, server.Endpoint, worldId, "Flag observer",
            cancellationToken);
        await Eventually(
            () => victim.State.Gameplay is not null &&
                  victim.State.Enemies.Values.Any(enemy =>
                      enemy.WorldLevel == accepted.SpawnWorldLevel &&
                      enemy.Health > 0),
            "the death-flag fixture did not receive its combat baseline",
            cancellationToken);
        var target = victim.State.Enemies.Values
            .Where(enemy => enemy.WorldLevel == accepted.SpawnWorldLevel &&
                            enemy.Health > 0)
            .OrderBy(enemy => Vector2.DistanceSquared(
                new Vector2(accepted.SpawnX, accepted.SpawnY),
                new Vector2(enemy.X, enemy.Y)))
            .First();
        await victim.SendWalkAsync(
            target.X, target.Y, target.WorldLevel, cancellationToken);
        await Eventually(
            () => victim.State.Gameplay?.LifeState == CombatLifeState.Dead,
            "the authoritative combat fixture did not defeat its victim",
            cancellationToken);
        var dead = await deadSnapshot.Task.WaitAsync(
            TimeSpan.FromSeconds(8), cancellationToken);
        CheckAssert.True(dead.State.HasFlag(NetworkEntityState.Dead),
            "a remote player's public entity must carry the dead bit");

        await Task.Delay(300, cancellationToken);
        Interlocked.Exchange(ref respawnRequested, 1);
        var respawn = await SendCombat(
            victim, new RespawnAction(), cancellationToken);
        CheckAssert.True(respawn.Accepted,
            $"the authoritative respawn was rejected: {respawn.Detail}");
        await Eventually(
            () => victim.State.Gameplay?.LifeState == CombatLifeState.Alive,
            "the respawn private state did not become alive",
            cancellationToken);
        var alive = await respawnSnapshot.Task.WaitAsync(
            TimeSpan.FromSeconds(8), cancellationToken);
        CheckAssert.False(alive.State.HasFlag(NetworkEntityState.Dead),
            "the next public entity snapshot must clear the dead bit after respawn");
    }

    private static async ValueTask DenseUdpInterestKeepsRemotePlayersAsync(
        CancellationToken cancellationToken)
    {
        const ulong ownId = 1;
        var worldId = Guid.Parse("fedb1a18-9ddb-57bd-a412-f2e2a369b945");
        var options = new ServerOptions(
            IPAddress.Loopback, 0, worldId, 73_911,
            "combat-loopback", "combat-v1", 64);
        await using var server = new DedicatedServer(options);
        await using var firstConnection = new ClientConnection(
            ClientConnectionId.New(),
            new TcpClient(AddressFamily.InterNetwork),
            server,
            cancellationToken);
        await using var replayConnection = new ClientConnection(
            ClientConnectionId.New(),
            new TcpClient(AddressFamily.InterNetwork),
            server,
            cancellationToken);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 39_211);
        firstConnection.ConfigureSnapshotTransport(
            endpoint, 1, ownId, deltaSnapshotsEnabled: true);
        replayConnection.ConfigureSnapshotTransport(
            endpoint, 2, ownId, deltaSnapshotsEnabled: true);

        var entities = new List<EntitySnapshot>
        {
            SnapshotEntity(ownId, NetworkEntityKind.Player, 0, 0, 0)
        };
        entities.AddRange(Enumerable.Range(0, 20).Select(index =>
            SnapshotEntity((ulong)(100 + index), NetworkEntityKind.Player,
                0, 40 + index, 0)));
        entities.AddRange(Enumerable.Range(0, 40).Select(index =>
            SnapshotEntity((ulong)(1_000 + index), NetworkEntityKind.Enemy,
                0, 1 + index, 0)));
        entities.AddRange(Enumerable.Range(0, 12).Select(index =>
            SnapshotEntity((ulong)(2_000 + index), NetworkEntityKind.Boat,
                0, 2 + index, 1)));
        entities.AddRange(Enumerable.Range(0, 8).Select(index =>
            SnapshotEntity((ulong)(3_000 + index), NetworkEntityKind.Enemy,
                -1, index, index)));
        var all = entities.ToArray();
        CheckAssert.True(all.Length > UdpSnapshotCodec.MaxEntitiesPerDatagram,
            "the UDP interest fixture must overflow one datagram");
        var snapshot = SessionSnapshot.Empty(new SessionId(worldId));

        var first = DedicatedServer.SelectUdpEntities(
            firstConnection, snapshot, all).ToArray();
        var replay = DedicatedServer.SelectUdpEntities(
            replayConnection, snapshot, all).ToArray();
        CheckAssert.SequenceEqual(
            first.Select(static value => value.EntityId),
            replay.Select(static value => value.EntityId),
            "identical dense populations must produce deterministic first interest slices");
        CheckAssert.Equal(UdpSnapshotCodec.MaxEntitiesPerDatagram, first.Length,
            "the dense interest slice must consume but never exceed its packet bound");
        CheckAssert.Equal(ownId, first[0].EntityId,
            "the controlled player must retain first UDP priority");
        CheckAssert.True(first.Count(value =>
                value.EntityKind == NetworkEntityKind.Player &&
                value.EntityId != ownId) >=
            Math.Max(1, UdpSnapshotCodec.MaxEntitiesPerDatagram / 8),
            "dense enemies must not starve the reserved remote-player quota");
        CheckAssert.True(first.Any(value => value.EntityId == 1_000),
            "the nearest same-level enemy must remain combat-priority");
        CheckAssert.True(first.Any(value => value.EntityId == 2_000),
            "the nearest same-level boat must retain travel priority");

        var observedRemotePlayers = new HashSet<ulong>();
        for (var publication = 0; publication < 20; publication++)
        {
            foreach (var entity in DedicatedServer.SelectUdpEntities(
                         firstConnection, snapshot, all))
                if (entity.EntityKind == NetworkEntityKind.Player &&
                    entity.EntityId != ownId)
                    observedRemotePlayers.Add(entity.EntityId);
        }
        CheckAssert.Equal(20, observedRemotePlayers.Count,
            "the reserved remote-player window must rotate fairly across publications");

        static EntitySnapshot SnapshotEntity(
            ulong id,
            NetworkEntityKind kind,
            short worldLevel,
            float x,
            float y) => new(
            id, kind, 0, worldLevel, x, y, 0, 0,
            NetworkEntityState.None, 1);
    }

    private static Task<HandshakeAcceptedMessage> Connect(
        NetworkGameClient client,
        IPEndPoint endpoint,
        Guid worldId,
        string name,
        CancellationToken cancellationToken) => client.ConnectAsync(
        endpoint.Address.ToString(),
        endpoint.Port,
        new ClientHandshakeOptions(
            "combat-loopback",
            "combat-v1",
            Guid.NewGuid(),
            name,
            worldId,
            Capabilities: ClientCapabilities.UdpSnapshots |
                          ClientCapabilities.DeltaSnapshots),
        cancellationToken);

    private static async Task<CombatActionResultMessage> SendCombat(
        NetworkGameClient client,
        CombatActionPayload payload,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        var completion = new TaskCompletionSource<CombatActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkCombatActionResultEventArgs args)
        {
            if (args.Result.CommandId == commandId)
                completion.TrySetResult(args.Result);
        }
        client.CombatActionCompleted += Handler;
        try
        {
            await client.SendActionAsync(payload, commandId, cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            client.CombatActionCompleted -= Handler;
        }
    }

    private static async Task Eventually(
        Func<bool> predicate,
        string failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate()) return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException(failure);
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly DedicatedServer _server;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _run;

        public RunningServer(
            ServerOptions options,
            CancellationToken cancellationToken,
            ISessionIdentitySource? identitySource = null)
        {
            _server = new DedicatedServer(options, identitySource);
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _run = _server.RunAsync(_shutdown.Token);
            Started = Initialize();
        }

        public Task Started { get; }

        public IPEndPoint Endpoint { get; private set; } = null!;

        private async Task Initialize() => Endpoint = await _server.Started;

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (OperationCanceledException)
            {
            }
            await _server.DisposeAsync();
            _shutdown.Dispose();
        }
    }

    private sealed class FixedIdentitySource(PlayerIdentity identity) :
        ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => identity;

        public ReconnectToken CreateReconnectToken() =>
            new("fixed-combat-identity-token");
    }

    private static AuthoritativeEnemySnapshot Enemy(
        EnemyId id,
        uint revision) => new(
        id,
        AuthoritativeCombatTransactions.DeriveNetworkEntityId(id),
        revision,
        EnemyKind.GrassSlime,
        EnemyBehavior.Idle,
        new Vector2(2, 3),
        new Vector2(2, 3),
        Vector2.Zero,
        0,
        3,
        20,
        20,
        1,
        IslandRpg.Simulation.CombatStatusFlags.None,
        null,
        0,
        null,
        1,
        0,
        0,
        0,
        0,
        0,
        0);

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{name}: expected {expected}, actual {actual}");
    }
}
