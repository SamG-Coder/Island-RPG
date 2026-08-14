using System.Numerics;
using System.Collections.Immutable;
using IslandRpg.Boats;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class CombatAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "combat target stance death and respawn are authoritative",
            TargetStanceDeathAndRespawnAreAuthoritative);
        checks.Add(
            "slime split loot and combat checkpoint are durable",
            SlimeSplitLootAndCheckpointAreDurable);
        checks.Add(
            "combat loot stacks into a full inventory across reconnect",
            CombatLootStacksAcrossReconnect);
        checks.Add(
            "combat chase and special slime reactions are fixed-step durable",
            ChaseAndSpecialReactionsAreFixedStepDurable);
        checks.Add(
            "combat status movement and expiry are fixed-step authoritative",
            StatusMovementAndExpiryAreFixedStepAuthoritative);
        checks.Add(
            "combat status poison expiry converges across restart",
            PoisonExpiryConvergesAcrossRestart);
        checks.Add(
            "disconnected and restored actors cannot advance combat",
            DisconnectedAndRestoredActorsCannotAdvanceCombat);
        checks.Add(
            "disconnected combat targets clear after corpse retirement",
            DisconnectedCombatTargetClearsAfterCorpseRetirement);
        checks.Add(
            "boat occupancy cancels and suppresses combat chase",
            BoatOccupancyCancelsAndSuppressesCombatChase);
        checks.Add(
            "dead actors cannot consume inventory items",
            DeadActorsCannotConsumeInventoryItems);
        checks.Add(
            "walk and stop atomically cancel autonomous combat",
            WalkAndStopAtomicallyCancelAutonomousCombat);
        checks.Add(
            "combat death vacates boats and respawn cannot snap back",
            DeathVacatesBoatAndRespawnCannotSnapBack);
    }

    private static void TargetStanceDeathAndRespawnAreAuthoritative()
    {
        var combat = new AuthoritativeCombatTransactions(
            8123,
            options: new AuthoritativeCombatOptions
            {
                EnemyAttackIntervalTicks = 1,
                PlayerAttackIntervalTicks = 1,
                RespawnDelayTicks = 2,
                RespawnPosition = new(9, 7)
            });
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            combatTransactions: combat);
        var connection = ClientConnectionId.New();
        var join = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Combat tester", Vector2.Zero));
        session.Drain();
        var player = join.GetAwaiter().GetResult();
        var enemy = session.SeedEnemy(new(
            Enemy(1), EnemyKind.CaveSlime, new(.1f, .1f),
            PowerLevel: 50));

        var target = Send(session, connection, player.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.NewGuid(), player.Gameplay.Inventory.Revision,
                player.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(target.Accepted,
            "an exact live enemy reference must become the player target");
        CheckAssert.Equal(enemy.EnemyId,
            target.Gameplay.CombatTargetEnemyId!.Value,
            "target selection must live in the existing player gameplay state");
        CheckAssert.True(session.CaptureSnapshot().Actors[0].Destination is null,
            "selecting a combat target must clear client-authored movement");

        var cancel = Send(session, connection, player.Identity.PlayerId, 2,
            new CancelCombatIntent(
                Guid.NewGuid(), target.Gameplay.Inventory.Revision,
                target.Gameplay.ActorRevision));
        CheckAssert.True(cancel.Accepted,
            "the owner must be able to cancel combat");
        CheckAssert.True(cancel.Gameplay.CombatTargetEnemyId is null,
            "cancelling must clear the durable target");
        CheckAssert.Equal(0L, cancel.Gameplay.NextCombatAttackTick,
            "cancelling must clear the attack cadence");

        var stance = Send(session, connection, player.Identity.PlayerId, 3,
            new SetCombatStanceIntent(
                Guid.NewGuid(), cancel.Gameplay.Inventory.Revision,
                cancel.Gameplay.ActorRevision,
                MeleeCombatStance.Defensive));
        CheckAssert.True(stance.Accepted,
            "a valid stance change must be accepted");
        CheckAssert.Equal(MeleeCombatStance.Defensive,
            stance.Gameplay.CombatStance,
            "the chosen stance must enter authoritative player state");

        var guard = 0;
        while (session.CaptureSnapshot().Actors[0].Gameplay.LifeState !=
               ActorLifeState.Dead && guard++ < 1_000)
            session.Tick();
        var dead = session.CaptureSnapshot().Actors[0];
        CheckAssert.Equal(ActorLifeState.Dead, dead.Gameplay.LifeState,
            "authoritative enemy cadence must eventually defeat the player");
        CheckAssert.Equal(0, dead.Gameplay.Health,
            "death must clamp health to zero");

        var deadWalk = session.EnqueueIntentAsync(new(
            connection, player.Identity.PlayerId, 4,
            new WalkIntent(new(20, 20))));
        session.Drain();
        CheckAssert.Equal(IntentStatus.DeadActor,
            deadWalk.GetAwaiter().GetResult().Status,
            "dead actors must not accept movement routes");

        var locked = Send(session, connection, player.Identity.PlayerId, 5,
            new RespawnIntent(
                Guid.NewGuid(), dead.Gameplay.Inventory.Revision,
                dead.Gameplay.ActorRevision));
        CheckAssert.Equal(IntentStatus.RespawnLocked, locked.Status,
            "respawning before the server tick deadline must be rejected");

        session.Tick();
        var ready = session.CaptureSnapshot().Actors[0].Gameplay;
        var respawn = Send(session, connection, player.Identity.PlayerId, 6,
            new RespawnIntent(
                Guid.NewGuid(), ready.Inventory.Revision,
                ready.ActorRevision));
        CheckAssert.True(respawn.Accepted,
            "respawning at the authoritative deadline must succeed");
        var recovered = session.CaptureSnapshot().Actors[0];
        CheckAssert.Equal(ActorLifeState.Alive, recovered.Gameplay.LifeState,
            "respawn must restore the alive state");
        CheckAssert.Equal(new Vector2(9, 7), recovered.Position,
            "respawn must reset the actor to the server-owned spawn position");
        CheckAssert.True(recovered.Destination is null,
            "respawn must clear stale movement routes");
    }

    private static void SlimeSplitLootAndCheckpointAreDurable()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000001"));
        var combat = new AuthoritativeCombatTransactions(
            9191,
            options: new AuthoritativeCombatOptions
            {
                PlayerAttackIntervalTicks = 1,
                EnemyAttackIntervalTicks = 10_000
            });
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: combat);
        var connection = ClientConnectionId.New();
        var join = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Slime hunter", Vector2.Zero));
        session.Drain();
        var player = join.GetAwaiter().GetResult();
        var enemy = session.SeedEnemy(new(
            Enemy(2), EnemyKind.GrassSlime, new(.1f, .1f),
            PowerLevel: 3, Health: 1, MaximumHealth: 24));
        var target = Send(session, connection, player.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.NewGuid(), player.Gameplay.Inventory.Revision,
                player.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(target.Accepted,
            "the hunter must acquire the seeded large slime");

        var guard = 0;
        while (session.CaptureEnemies().Count(value => value.Alive) != 2 &&
               guard++ < 100)
            session.Tick();
        var enemies = session.CaptureEnemies();
        CheckAssert.Equal(3, enemies.Length,
            "a defeated large slime must remain as a dead parent plus two children");
        CheckAssert.Equal(2, enemies.Count(value => value.Alive),
            "both deterministic split children must be alive");
        CheckAssert.True(enemies.Where(value => value.Alive).All(value =>
                value.ParentEnemyId == enemy.EnemyId),
            "split children must retain their stable parent identity");

        var checkpoint = session.CaptureCheckpoint();
        var loot = checkpoint.World.Objects.Single(value =>
            value.Object.DefinitionId == ItemIds.LootBag);
        CheckAssert.True(loot.Container is not null,
            "combat loot must be a real persisted world container");
        CheckAssert.False(loot.Container!.AllowsDeposit,
            "authoritative loot bags must be withdraw-only");
        CheckAssert.True(loot.Container.Slots.Any(value =>
                value.ItemId is not null && value.Quantity > 0),
            "the deterministic loot roll must be materialized into bag slots");
        CheckAssert.True(checkpoint.Combat is not null &&
                         checkpoint.Combat.NextEventOrdinal > 1,
            "combat event ordinals must be durable");

        var restored = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(9191));
        restored.RestoreCheckpoint(checkpoint);
        CheckAssert.SequenceEqual(
            enemies.Select(value => value.EnemyId),
            restored.CaptureEnemies().Select(value => value.EnemyId),
            "restart must preserve every stable enemy identity");
        var restoredCheckpoint = restored.CaptureCheckpoint();
        CheckAssert.Equal(checkpoint.Combat!.NextEventOrdinal,
            restoredCheckpoint.Combat!.NextEventOrdinal,
            "restart must preserve the next combat event ordinal");
        CheckAssert.True(restoredCheckpoint.World.Objects.Any(value =>
                value.Object.ObjectId == loot.Object.ObjectId &&
                value.Container is not null),
            "restart must preserve the authoritative loot bag and contents");
    }

    private static void CombatLootStacksAcrossReconnect()
    {
        const long worldSeed = 9_292;
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000092"));
        var combatOptions = new AuthoritativeCombatOptions
        {
            PlayerAttackIntervalTicks = 1,
            EnemyAttackIntervalTicks = 10_000
        };
        var source = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                worldSeed, options: combatOptions));
        var sourceConnection = ClientConnectionId.New();
        var join = source.EnqueueJoinAsync(new JoinRequest(
            sourceConnection,
            "Full-bag hunter",
            Vector2.Zero,
            [
                new InitialInventoryItem(ItemIds.SlimeGel, 4),
                new InitialInventoryItem(
                    ItemIds.StoneHammer,
                    PlayerInventory.Capacity - 1)
            ]));
        source.Drain();
        var player = join.GetAwaiter().GetResult();
        CheckAssert.True(player.Accepted,
            "the full-inventory combat fixture must join");
        CheckAssert.Equal(PlayerInventory.Capacity,
            player.Gameplay.Inventory.Slots.Count(value =>
                value.ItemId is not null),
            "the combat fixture must have no empty inventory slot");

        var enemyId = Enumerable.Range(90, 100)
            .Select(Enemy)
            .First(candidate => SlimeCombatRules.RollLoot(new SlimeLootSource(
                    worldSeed,
                    candidate.Value,
                    EnemyKind.WaterSlime,
                    1))
                .Any(value => value.ItemId == ItemIds.SlimeGel &&
                              value.Quantity == 2));
        var enemy = source.SeedEnemy(new(
            enemyId,
            EnemyKind.WaterSlime,
            new(.1f, .1f),
            PowerLevel: 1,
            Health: 1,
            MaximumHealth: 1));
        var target = Send(source, sourceConnection, player.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca100000-0000-0000-0000-000000000092"),
                player.Gameplay.Inventory.Revision,
                player.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(target.Accepted,
            "the full-inventory hunter must acquire its loot source");

        var guard = 0;
        while (!source.CaptureCheckpoint().World.Objects.Any(value =>
                   value.Object.DefinitionId == ItemIds.LootBag) &&
               guard++ < 100)
            source.Tick();
        var droppedCheckpoint = source.CaptureCheckpoint();
        var droppedBag = droppedCheckpoint.World.Objects.Single(value =>
            value.Object.DefinitionId == ItemIds.LootBag);
        var owner = player.Identity.ActorId.ToString();
        CheckAssert.Equal(owner, droppedBag.Object.OwnerId,
            "combat access ownership must live on the loot-bag object");
        CheckAssert.True(droppedBag.Container is not null &&
                         droppedBag.Container.Slots
                             .Where(value => value.ItemId is not null)
                             .All(value => value.OwnerId is null),
            "combat item stacks must remain ownerless and normally stackable");

        var restored = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                worldSeed, options: combatOptions));
        restored.RestoreCheckpoint(droppedCheckpoint);
        var restoredConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            restoredConnection,
            player.Identity.PlayerId,
            player.ReconnectToken));
        restored.Drain();
        var reconnect = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnect.Accepted,
            "the loot owner must reconnect after checkpoint restore");
        var persistedBag = restored.CaptureWorldObject(
            droppedBag.Object.ObjectId);
        CheckAssert.Equal(owner, persistedBag.OwnerId,
            "checkpoint restore must preserve object-level loot ownership");

        var chunkRevision = restored.CaptureWorldChunkRevision(
            persistedBag.Chunk);
        var open = Send(restored, restoredConnection,
            player.Identity.PlayerId, reconnect.NextCommandSequence,
            new OpenWorldContainerIntent(
                Guid.Parse("ca200000-0000-0000-0000-000000000092"),
                reconnect.Gameplay.Inventory.Revision,
                reconnect.Gameplay.ActorRevision,
                new WorldObjectHandle(
                    persistedBag.ObjectId,
                    persistedBag.Chunk,
                    persistedBag.ObjectRevision,
                    chunkRevision,
                    persistedBag.ContainerRevision)));
        var openedBag = open.WorldTransaction?.Container;
        CheckAssert.True(open.Accepted && openedBag is not null,
            "the reconnected object owner must be able to open its loot");
        var gelInventorySlot = reconnect.Gameplay.Inventory.Slots.Single(value =>
            value.ItemId == ItemIds.SlimeGel).Slot;
        var gelContainerSlot = openedBag!.Slots.First(value =>
            value.ItemId == ItemIds.SlimeGel).Slot;
        var withdraw = Send(restored, restoredConnection,
            player.Identity.PlayerId,
            reconnect.NextCommandSequence + 1,
            new TransferWorldContainerIntent(
                Guid.Parse("ca300000-0000-0000-0000-000000000092"),
                reconnect.Gameplay.Inventory.Revision,
                reconnect.Gameplay.ActorRevision,
                new WorldObjectHandle(
                    persistedBag.ObjectId,
                    persistedBag.Chunk,
                    persistedBag.ObjectRevision,
                    chunkRevision,
                    persistedBag.ContainerRevision),
                WorldContainerTransferDirection.Withdraw,
                gelInventorySlot,
                gelContainerSlot,
                1));
        CheckAssert.True(withdraw.Accepted,
            "ownerless combat loot must merge despite a completely full inventory");
        CheckAssert.Equal(5, withdraw.Gameplay.Inventory.Slots.Sum(value =>
                value.ItemId == ItemIds.SlimeGel ? value.Quantity : 0),
            "loot withdrawal must extend the existing ownerless stack");
        CheckAssert.Equal(PlayerInventory.Capacity,
            withdraw.Gameplay.Inventory.Slots.Count(value =>
                value.ItemId is not null),
            "stacking loot must not require or create an empty slot");

        var stackedCheckpoint = restored.CaptureCheckpoint();
        var persistedAfterStack = stackedCheckpoint.World.Objects.Single(value =>
            value.Object.ObjectId == persistedBag.ObjectId);
        CheckAssert.Equal(owner, persistedAfterStack.Object.OwnerId,
            "partial withdrawal must retain exclusive bag ownership");
        CheckAssert.True(persistedAfterStack.Container!.Slots
                .Where(value => value.ItemId is not null)
                .All(value => value.OwnerId is null),
            "partial withdrawal must not reintroduce per-stack ownership");

        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                worldSeed, options: combatOptions));
        restarted.RestoreCheckpoint(stackedCheckpoint);
        var finalConnection = ClientConnectionId.New();
        var finalReconnectPending = restarted.EnqueueReconnectAsync(new(
            finalConnection,
            player.Identity.PlayerId,
            player.ReconnectToken));
        restarted.Drain();
        var finalReconnect = finalReconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(finalReconnect.Accepted,
            "the stacked inventory must reconnect after its second restore");
        CheckAssert.Equal(5, finalReconnect.Gameplay.Inventory.Slots.Sum(value =>
                value.ItemId == ItemIds.SlimeGel ? value.Quantity : 0),
            "checkpoint restore must retain the merged loot stack");
        CheckAssert.Equal(owner,
            restarted.CaptureWorldObject(persistedBag.ObjectId).OwnerId,
            "second restore must retain object-level bag ownership");
    }

    private static void ChaseAndSpecialReactionsAreFixedStepDurable()
    {
        var actorId = new ActorId(Guid.Parse(
            "c2000000-0000-0000-0000-000000000099"));
        var actor = Actor(actorId, new Vector2(3, 0));
        var options = new AuthoritativeCombatOptions
        {
            PlayerChaseSpeed = 3,
            EnemyAttackIntervalTicks = 10_000
        };
        var grassAuthority = new AuthoritativeCombatTransactions(
            44, options: options);
        var grass = grassAuthority.Seed(new(
            Enemy(20), EnemyKind.GrassSlime, Vector2.Zero));
        CheckAssert.True((grass.StatusFlags & CombatStatusFlags.Hidden) != 0,
            "an idle grass slime must be authoritatively hidden");
        var grassIdle = grassAuthority.Advance(
            SimulationTiming.FixedDeltaSeconds, 1, [actor]);
        CheckAssert.Equal(0, grassIdle.EnemyDeltas.Length,
            "surface slimes must not auto-aggro a nearby player");
        CheckAssert.True(
            (grassAuthority.CaptureEnemy(grass.EnemyId).StatusFlags &
             CombatStatusFlags.Hidden) != 0,
            "an unprovoked grass slime must stay camouflaged beside a player");

        var sandAuthority = new AuthoritativeCombatTransactions(
            45, options: options);
        var sand = sandAuthority.Seed(new(
            Enemy(21), EnemyKind.SandSlime, Vector2.Zero));
        var unprovokedSand = sandAuthority.Advance(
            SimulationTiming.FixedDeltaSeconds, 1, [actor]);
        CheckAssert.Equal(0, unprovokedSand.EnemyDeltas.Length,
            "sand slimes must stay idle until a player strikes them");
        var provoked = actor with
        {
            Position = new(.4f, 0),
            Gameplay = actor.Gameplay with
            {
                CombatTargetEnemyId = sand.EnemyId
            }
        };
        var acquire = sandAuthority.Advance(
            SimulationTiming.FixedDeltaSeconds, 2, [provoked]);
        var buried = acquire.EnemyDeltas.Single().Current!;
        CheckAssert.True((buried.StatusFlags & CombatStatusFlags.Burrowed) != 0,
            "sand slime acquisition must publish the burrow state");
        CheckAssert.Equal(Vector2.Zero, buried.Position,
            "a burrowed slime must not move before its authored reaction delay");

        var checkpoint = sandAuthority.CaptureCheckpoint();
        var restored = new AuthoritativeCombatTransactions(45, options: options);
        restored.RestoreCheckpoint(checkpoint);
        var emergeTick = checkpoint.Enemies.Single().BurrowEmergeTick;
        AuthoritativeEnemySnapshot emerged = buried;
        for (var tick = 3L; tick <= emergeTick; tick++)
        {
            restored.Advance(SimulationTiming.FixedDeltaSeconds, tick, [actor]);
            emerged = restored.CaptureEnemy(sand.EnemyId,
                now: tick * SimulationTiming.FixedDeltaSeconds);
        }
        CheckAssert.True((emerged.StatusFlags & CombatStatusFlags.Burrowed) == 0,
            "the server must clear burrowed exactly at the persisted emerge tick");
        CheckAssert.False(emerged.Position == Vector2.Zero,
            "the deterministic emerge must relocate the slime near its target");

        var chase = new AuthoritativeCombatTransactions(
            46,
            options: new AuthoritativeCombatOptions
            {
                PlayerChaseSpeed = 3,
                AggroRange = .1f,
                LeashRange = .1f
            });
        var target = chase.Seed(new(
            Enemy(22), EnemyKind.WaterSlime, new(10, 0)));
        var chasingGameplay = actor.Gameplay with
        {
            CombatTargetEnemyId = target.EnemyId
        };
        var chasing = actor with { Gameplay = chasingGameplay };
        var step = chase.Advance(
            SimulationTiming.FixedDeltaSeconds, 1, [chasing]);
        var moved = step.ActorMutations.Single().Position!.Value.X;
        CheckAssert.True(MathF.Abs(moved - 3.05f) < .0001f,
            $"player chase distance must equal speed times one fixed step; actual {moved}");
    }

    private static void StatusMovementAndExpiryAreFixedStepAuthoritative()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000041"));
        var limits = SimulationLimits.Default with
        {
            ActorMovementSpeed = 6,
            DestinationArrivalDistance = 0
        };
        var source = new AuthoritativeWorldSession(
            limits,
            new CombatIdentitySource(),
            sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(4_141));
        var originalConnection = ClientConnectionId.New();
        var joined = source.EnqueueJoinAsync(new JoinRequest(
            originalConnection, "Status walker", Vector2.Zero));
        source.Drain();
        var identity = joined.GetAwaiter().GetResult();
        var rootDeadline = 2 * SimulationTiming.FixedDeltaSeconds;
        var slowDeadline = 4 * SimulationTiming.FixedDeltaSeconds;
        var checkpoint = source.CaptureCheckpoint();
        checkpoint = checkpoint with
        {
            Actors = checkpoint.Actors.Select(actor => actor with
            {
                Gameplay = actor.Gameplay with
                {
                    CombatStatus = new SlimeVictimStatus(
                        SlowedUntil: slowDeadline,
                        RootedUntil: rootDeadline)
                }
            }).ToImmutableArray()
        };

        var session = new AuthoritativeWorldSession(
            limits,
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(4_141));
        session.RestoreCheckpoint(checkpoint);
        var connection = ClientConnectionId.New();
        var reconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            connection, identity.Identity.PlayerId, identity.ReconnectToken));
        session.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the status fixture must reconnect after checkpoint restore");
        var walk = session.EnqueueIntentAsync(new ActorCommand(
            connection, identity.Identity.PlayerId, 1,
            new WalkIntent(new Vector2(10, 0))));
        session.Drain();
        CheckAssert.True(walk.GetAwaiter().GetResult().Accepted,
            "the rooted actor must retain an accepted server route");
        var events = new List<CombatEventSnapshot>();
        session.CombatEventCommitted += events.Add;

        session.Tick();
        session.Tick();
        var rooted = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(Vector2.Zero, rooted.Position,
            "a root must produce exactly zero normal route movement");
        CheckAssert.Equal(Vector2.Zero, rooted.Velocity,
            "a root must publish zero velocity");
        CheckAssert.True(rooted.Destination is not null,
            "a root must pause rather than destroy the accepted route");

        session.Tick();
        var slowed = session.CaptureSnapshot().Actors.Single();
        var slowedStep = 6f * .58f / SimulationTiming.TicksPerSecond;
        CheckAssert.True(MathF.Abs(slowed.Position.X - slowedStep) < .0001f,
            "the exact root-expiry step must resume at the remaining slow multiplier");
        CheckAssert.True(
            slowed.Gameplay.CombatStatus.RootedUntil == 0 &&
            slowed.Gameplay.CombatStatus.SlowedUntil == slowDeadline,
            "root expiry must canonicalize only the elapsed deadline");

        session.Tick();
        var beforeFullSpeed = session.CaptureSnapshot().Actors.Single().Position.X;
        session.Tick();
        var resumed = session.CaptureSnapshot().Actors.Single();
        var fullStep = 6f / SimulationTiming.TicksPerSecond;
        CheckAssert.True(
            MathF.Abs((resumed.Position.X - beforeFullSpeed) - fullStep) < .0001f,
            "the exact slow-expiry step must resume full authoritative speed");
        CheckAssert.Equal(default, resumed.Gameplay.CombatStatus,
            "elapsed movement statuses must converge to canonical zero state");
        CheckAssert.Equal(2, events.Count(value =>
                value.Kind == CombatEventKind.StatusExpired),
            "root and slow must each publish one reliable expiry event");
        CheckAssert.True(events.Any(value =>
                value.Kind == CombatEventKind.StatusExpired &&
                value.Status == SlimeStatusKind.Root) &&
            events.Any(value =>
                value.Kind == CombatEventKind.StatusExpired &&
                value.Status == SlimeStatusKind.Slow),
            "expiry events must identify the effect clients should clear");
    }

    private static void PoisonExpiryConvergesAcrossRestart()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000042"));
        var source = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(4_242));
        var originalConnection = ClientConnectionId.New();
        var joined = source.EnqueueJoinAsync(new JoinRequest(
            originalConnection, "Poison walker", Vector2.Zero));
        source.Drain();
        var identity = joined.GetAwaiter().GetResult();
        var checkpoint = source.CaptureCheckpoint();
        checkpoint = checkpoint with
        {
            Tick = 300,
            SnapshotSequence = 100,
            Actors = checkpoint.Actors.Select(actor => actor with
            {
                Gameplay = actor.Gameplay with
                {
                    CombatStatus = new SlimeVictimStatus(
                        PoisonedUntil: 5,
                        NextPoisonTickAt: 4,
                        PoisonDamage: 2)
                }
            }).ToImmutableArray()
        };

        var session = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(4_242));
        session.RestoreCheckpoint(checkpoint);
        var connection = ClientConnectionId.New();
        var reconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            connection, identity.Identity.PlayerId, identity.ReconnectToken));
        session.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the poison fixture must reconnect at its expiry checkpoint");
        var events = new List<CombatEventSnapshot>();
        session.CombatEventCommitted += events.Add;

        session.Tick();
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(98, actor.Gameplay.Health,
            "restart at the deadline must retain the last poison tick strictly before expiry");
        CheckAssert.Equal(default, actor.Gameplay.CombatStatus,
            "poison expiry must clear its deadline, next tick, and damage metadata");
        CheckAssert.Equal(2u, actor.Gameplay.ActorRevision,
            "canonical status expiry must advance the private actor revision");
        CheckAssert.Equal(CombatStatusFlags.None,
            actor.Gameplay.StatusFlags(session.Clock.Current.ElapsedSeconds),
            "the authoritative private status flags must converge to none");
        CheckAssert.Equal(1, events.Count(value =>
                value.Kind == CombatEventKind.StatusExpired &&
                value.Status == SlimeStatusKind.Poison),
            "poison must publish exactly one typed expiry event");
        CheckAssert.Equal(2, events.Single(value =>
                value.Kind == CombatEventKind.EnemyAttacked).Damage,
            "the expiry step must publish the deterministic catch-up damage");

        var canonicalCheckpoint = session.CaptureCheckpoint();
        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(4_242));
        restarted.RestoreCheckpoint(canonicalCheckpoint);
        CheckAssert.Equal(default,
            restarted.CaptureSnapshot().Actors.Single().Gameplay.CombatStatus,
            "a checkpoint after expiry must not resurrect poison metadata");
    }

    private static void DisconnectedAndRestoredActorsCannotAdvanceCombat()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000051"));
        var options = new AuthoritativeCombatOptions
        {
            PlayerAttackIntervalTicks = 1,
            EnemyAttackIntervalTicks = 10_000,
            DeathRetentionTicks = 10_000
        };
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                5_151, options: options));
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Offline hunter", Vector2.Zero));
        session.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        var enemy = session.SeedEnemy(new(
            Enemy(51), EnemyKind.CaveSlime, new(.1f, 0),
            PowerLevel: 1, Health: 1, MaximumHealth: 20));
        var target = Send(session, connection, joined.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca510000-0000-0000-0000-000000000001"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(target.Accepted,
            "the disconnect fixture must begin with a durable combat target");
        var pendingDisconnect = session.EnqueueDisconnectAsync(new(
            connection, joined.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(pendingDisconnect.GetAwaiter().GetResult().Accepted,
            "the targeted actor must disconnect cleanly");

        var paused = session.CaptureCheckpoint();
        var pausedActor = paused.Actors.Single();
        var pausedEnemy = paused.Combat!.Enemies.Single();
        var events = new List<CombatEventSnapshot>();
        session.CombatEventCommitted += events.Add;
        for (var tick = 0; tick < 240; tick++) session.Tick();

        AssertOfflineCombatUnchanged(
            pausedActor,
            pausedEnemy,
            paused.Combat.NextEventOrdinal,
            session.CaptureCheckpoint(),
            "a disconnected live actor");
        CheckAssert.Equal(0, events.Count,
            "an offline actor must not publish attacks, rewards, or status events");

        // Restored actors always begin disconnected. Include already-due
        // poison metadata so this also proves the status loop cannot mutate
        // health or revisions before reconnect authentication succeeds.
        var poison = new SlimeVictimStatus(
            PoisonedUntil: 1,
            NextPoisonTickAt: .1,
            PoisonDamage: 2);
        var restoreCheckpoint = paused with
        {
            Actors = paused.Actors.Select(actor => actor with
            {
                Gameplay = actor.Gameplay with { CombatStatus = poison }
            }).ToImmutableArray()
        };
        var restored = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                5_151, options: options));
        restored.RestoreCheckpoint(restoreCheckpoint);
        var restoredBefore = restored.CaptureCheckpoint();
        var restoredEvents = new List<CombatEventSnapshot>();
        restored.CombatEventCommitted += restoredEvents.Add;
        for (var tick = 0; tick < 240; tick++) restored.Tick();

        AssertOfflineCombatUnchanged(
            restoredBefore.Actors.Single(),
            restoredBefore.Combat!.Enemies.Single(),
            restoredBefore.Combat.NextEventOrdinal,
            restored.CaptureCheckpoint(),
            "a checkpoint-restored actor");
        CheckAssert.Equal(poison,
            restored.CaptureSnapshot().Actors.Single().Gameplay.CombatStatus,
            "offline absolute status deadlines must wait for reconnect");
        CheckAssert.Equal(0, restoredEvents.Count,
            "a restored offline actor must not emit autonomous combat events");
    }

    private static void DisconnectedCombatTargetClearsAfterCorpseRetirement()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000061"));
        var options = new AuthoritativeCombatOptions
        {
            PlayerAttackIntervalTicks = 1,
            EnemyAttackIntervalTicks = 10_000,
            DeathRetentionTicks = 1
        };
        var session = new AuthoritativeWorldSession(
            identitySource: new SequencedCombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                6_161, options: options));
        var offlineConnection = ClientConnectionId.New();
        var killerConnection = ClientConnectionId.New();
        var offlineJoin = session.EnqueueJoinAsync(new JoinRequest(
            offlineConnection, "Paused hunter", Vector2.Zero));
        var killerJoin = session.EnqueueJoinAsync(new JoinRequest(
            killerConnection, "Cleanup hunter", Vector2.Zero));
        session.Drain();
        var offline = offlineJoin.GetAwaiter().GetResult();
        var killer = killerJoin.GetAwaiter().GetResult();
        var enemy = session.SeedEnemy(new(
            Enemy(61), EnemyKind.WaterSlime, new(.1f, 0),
            PowerLevel: 1, Health: 100, MaximumHealth: 100));

        var offlineTarget = Send(session, offlineConnection,
            offline.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca610000-0000-0000-0000-000000000001"),
                offline.Gameplay.Inventory.Revision,
                offline.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(offlineTarget.Accepted,
            "the offline cleanup fixture must acquire its target");
        session.Tick();
        var armedOffline = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == offline.Identity.PlayerId).Gameplay;
        CheckAssert.True(armedOffline.NextCombatAttackTick > 0,
            "the offline cleanup fixture must retain a live attack cadence");

        var disconnect = session.EnqueueDisconnectAsync(new(
            offlineConnection, offline.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(disconnect.GetAwaiter().GetResult().Accepted,
            "the targeted actor must disconnect before the corpse retires");
        var paused = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == offline.Identity.PlayerId).Gameplay;
        var currentEnemy = session.CaptureEnemies().Single(value =>
            value.EnemyId == enemy.EnemyId);
        var currentKiller = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == killer.Identity.PlayerId).Gameplay;
        var killerTarget = Send(session, killerConnection,
            killer.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca610000-0000-0000-0000-000000000002"),
                currentKiller.Inventory.Revision,
                currentKiller.ActorRevision,
                new(currentEnemy.EnemyId, currentEnemy.Revision)));
        CheckAssert.True(killerTarget.Accepted,
            "a connected actor must be able to retire the offline target");

        var guard = 0;
        while (session.CaptureEnemies().Any(value =>
                   value.EnemyId == enemy.EnemyId) && guard++ < 5_000)
            session.Tick();
        CheckAssert.False(session.CaptureEnemies().Any(value =>
                value.EnemyId == enemy.EnemyId),
            "the target corpse must retire from the combat aggregate");

        var repaired = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == offline.Identity.PlayerId).Gameplay;
        CheckAssert.True(repaired.CombatTargetEnemyId is null,
            "corpse retirement must clear a disconnected actor's target");
        CheckAssert.Equal(0L, repaired.NextCombatAttackTick,
            "corpse retirement must clear the disconnected attack cadence");
        CheckAssert.Equal(paused.ActorRevision + 1, repaired.ActorRevision,
            "offline target cleanup must commit exactly one actor revision");
        CheckAssert.Equal(paused.Health, repaired.Health,
            "offline target cleanup must not advance health or statuses");
        CheckAssert.Equal(paused.CombatStatus, repaired.CombatStatus,
            "offline target cleanup must not advance status deadlines");
        CheckAssert.Equal(paused.CombatAttackSequence,
            repaired.CombatAttackSequence,
            "offline target cleanup must not consume an attack roll");
        CheckAssert.Equal(paused.AttackExperience, repaired.AttackExperience,
            "offline target cleanup must not award attack experience");

        var checkpoint = session.CaptureCheckpoint();
        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                6_161, options: options));
        restarted.RestoreCheckpoint(checkpoint);
        var restored = restarted.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == offline.Identity.PlayerId).Gameplay;
        CheckAssert.True(restored.CombatTargetEnemyId is null &&
                         restored.NextCombatAttackTick == 0,
            "the repaired offline actor must remain restorable");
    }

    private static void BoatOccupancyCancelsAndSuppressesCombatChase()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000062"));
        var combatOptions = new AuthoritativeCombatOptions
        {
            PlayerChaseSpeed = 12,
            PlayerAttackRange = .1f,
            EnemyAttackIntervalTicks = 10_000,
            AggroRange = .1f,
            LeashRange = .1f
        };
        var navigation = new CombatBoatNavigation();
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(navigation),
            combatTransactions: new AuthoritativeCombatTransactions(
                6_262, options: combatOptions));
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Peaceful sailor", new(.5f, 1.5f)));
        session.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        var boat = session.SeedBoat(new(
            new BoatId(Guid.Parse(
                "ca620000-0000-0000-0000-000000000001")),
            joined.Identity.PlayerId,
            new(.5f, .5f)));
        var enemy = session.SeedEnemy(new(
            Enemy(62), EnemyKind.GrassSlime, new(5.5f, .5f),
            Health: 1_000, MaximumHealth: 1_000));
        var targeted = Send(session, connection, joined.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca620000-0000-0000-0000-000000000002"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.True(targeted.Accepted,
            "the sailor must acquire a target before boarding");
        var board = Send(session, connection, joined.Identity.PlayerId, 2,
            new BoardBoatIntent(
                Guid.Parse("ca620000-0000-0000-0000-000000000003"),
                targeted.Gameplay.Inventory.Revision,
                targeted.Gameplay.ActorRevision,
                new(boat.BoatId, boat.Revision)));
        CheckAssert.True(board.Accepted,
            "boarding must accept an exact nearby boat reference");
        CheckAssert.True(board.Gameplay.CombatTargetEnemyId is null,
            "boarding must cancel the active combat target atomically");
        CheckAssert.Equal(0L, board.Gameplay.NextCombatAttackTick,
            "boarding must cancel combat cadence atomically");
        CheckAssert.Equal(targeted.Gameplay.ActorRevision + 1,
            board.Gameplay.ActorRevision,
            "boarding and combat cancellation must share one actor revision");

        var aboardTarget = Send(session, connection,
            joined.Identity.PlayerId, 3,
            new SetCombatTargetIntent(
                Guid.Parse("ca620000-0000-0000-0000-000000000004"),
                board.Gameplay.Inventory.Revision,
                board.Gameplay.ActorRevision,
                new(enemy.EnemyId, enemy.Revision)));
        CheckAssert.Equal(IntentStatus.AlreadyAboard, aboardTarget.Status,
            "an occupied actor must not begin on-foot combat");
        CheckAssert.True(aboardTarget.Gameplay.CombatTargetEnemyId is null,
            "a rejected aboard target must leave combat cancelled");

        // Model a legacy save that predates atomic boarding cancellation. The
        // first fixed step must repair it before chase movement is evaluated.
        var checkpoint = session.CaptureCheckpoint();
        var actorCheckpoint = checkpoint.Actors.Single();
        var legacyCheckpoint = checkpoint with
        {
            Actors =
            [
                actorCheckpoint with
                {
                    Gameplay = actorCheckpoint.Gameplay with
                    {
                        CombatTargetEnemyId = enemy.EnemyId,
                        NextCombatAttackTick = 500
                    }
                }
            ]
        };
        var restored = new AuthoritativeWorldSession(
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(navigation),
            combatTransactions: new AuthoritativeCombatTransactions(
                6_262, options: combatOptions));
        restored.RestoreCheckpoint(legacyCheckpoint);
        var reconnectConnection = ClientConnectionId.New();
        var reconnect = restored.EnqueueReconnectAsync(new(
            reconnectConnection, joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the legacy boat occupant must reconnect");
        var before = restored.CaptureSnapshot().Actors.Single();
        var restoredBoat = restored.CaptureBoats().Single();
        restored.Tick();
        var after = restored.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(restoredBoat.Position, before.Position,
            "the restored occupant must begin synchronized to its boat");
        CheckAssert.Equal(restoredBoat.Position, after.Position,
            "combat must not move a restored occupant off its boat");
        CheckAssert.True(after.Gameplay.CombatTargetEnemyId is null &&
                         after.Gameplay.NextCombatAttackTick == 0,
            "the fixed step must canonicalize legacy aboard combat state");
        CheckAssert.Equal(before.Gameplay.ActorRevision + 1,
            after.Gameplay.ActorRevision,
            "legacy aboard combat cleanup must commit one revision");

        var repairedCheckpoint = restored.CaptureCheckpoint();
        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(navigation),
            combatTransactions: new AuthoritativeCombatTransactions(
                6_262, options: combatOptions));
        restarted.RestoreCheckpoint(repairedCheckpoint);
        CheckAssert.Equal(
            restarted.CaptureBoats().Single().Position,
            restarted.CaptureSnapshot().Actors.Single().Position,
            "the repaired occupant checkpoint must restore without divergence");
    }

    private static void DeadActorsCannotConsumeInventoryItems()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000063"));
        var options = new AuthoritativeCombatOptions
        {
            EnemyAttackIntervalTicks = 1,
            RespawnDelayTicks = 60
        };
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                6_363, options: options));
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            "Defeated diner",
            Vector2.Zero,
            [new InitialInventoryItem(ItemIds.WildBerries, 2)],
            InitialHunger: 50));
        session.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        session.SeedEnemy(new(
            Enemy(63), EnemyKind.CaveSlime, Vector2.Zero,
            PowerLevel: 100));
        var guard = 0;
        ActorSnapshot dead;
        do
        {
            session.Tick();
            dead = session.CaptureSnapshot().Actors.Single();
        } while (dead.Gameplay.LifeState != ActorLifeState.Dead &&
                 guard++ < 1_000);
        CheckAssert.Equal(ActorLifeState.Dead, dead.Gameplay.LifeState,
            "the consume fixture must first kill the actor");
        var beforeStack = dead.Gameplay.Inventory.Slots.Single(value =>
            value.Slot == 0);

        var consume = Send(session, connection, joined.Identity.PlayerId, 1,
            new ConsumeFoodIntent(
                Guid.Parse("ca630000-0000-0000-0000-000000000001"),
                dead.Gameplay.Inventory.Revision,
                dead.Gameplay.ActorRevision,
                0));
        CheckAssert.Equal(IntentStatus.DeadActor, consume.Status,
            "dead actors must reject otherwise-valid food consumption");
        CheckAssert.Equal(ActorLifeState.Dead, consume.Gameplay.LifeState,
            "rejected consumption must preserve the dead life state");
        CheckAssert.Equal(0, consume.Gameplay.Health,
            "rejected consumption must not heal a dead actor");
        CheckAssert.Equal(dead.Gameplay.ActorRevision,
            consume.Gameplay.ActorRevision,
            "rejected consumption must not advance the actor revision");
        CheckAssert.Equal(dead.Gameplay.Inventory.Revision,
            consume.Gameplay.Inventory.Revision,
            "rejected consumption must not advance inventory revision");
        CheckAssert.Equal(beforeStack,
            consume.Gameplay.Inventory.Slots.Single(value => value.Slot == 0),
            "rejected consumption must preserve the food stack");

        var checkpoint = session.CaptureCheckpoint();
        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            combatTransactions: new AuthoritativeCombatTransactions(
                6_363, options: options));
        restarted.RestoreCheckpoint(checkpoint);
        var restored = restarted.CaptureSnapshot().Actors.Single().Gameplay;
        CheckAssert.True(restored.LifeState == ActorLifeState.Dead &&
                         restored.Health == 0,
            "dead consume rejection must leave a restorable life state");
    }

    private static void WalkAndStopAtomicallyCancelAutonomousCombat()
    {
        var limits = SimulationLimits.Default with
        {
            ActorMovementSpeed = 6,
            DestinationArrivalDistance = 0
        };
        var combat = new AuthoritativeCombatTransactions(
            5_252,
            options: new AuthoritativeCombatOptions
            {
                PlayerChaseSpeed = 6,
                PlayerAttackIntervalTicks = 60,
                EnemyAttackIntervalTicks = 10_000,
                AggroRange = .1f,
                LeashRange = .1f
            });
        var session = new AuthoritativeWorldSession(
            limits,
            new CombatIdentitySource(),
            combatTransactions: combat);
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Decisive walker", Vector2.Zero));
        session.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        var distant = session.SeedEnemy(new(
            Enemy(52), EnemyKind.WaterSlime, new(0, 10),
            Health: 1_000, MaximumHealth: 1_000));
        var targeted = Send(session, connection, joined.Identity.PlayerId, 1,
            new SetCombatTargetIntent(
                Guid.Parse("ca520000-0000-0000-0000-000000000001"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                new(distant.EnemyId, distant.Revision)));
        CheckAssert.True(targeted.Accepted,
            "the movement fixture must begin with an active combat target");

        var invalidWalk = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, 2,
            new WalkIntent(new(float.NaN, 0))));
        session.Drain();
        var rejected = invalidWalk.GetAwaiter().GetResult();
        CheckAssert.Equal(IntentStatus.InvalidDestination, rejected.Status,
            "an invalid walk must reject before changing combat state");
        CheckAssert.Equal(distant.EnemyId,
            rejected.Gameplay.CombatTargetEnemyId!.Value,
            "a rejected route must preserve the existing combat target");
        CheckAssert.Equal(targeted.Gameplay.ActorRevision,
            rejected.Gameplay.ActorRevision,
            "a rejected route must preserve the actor revision");

        var walk = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, 3,
            new WalkIntent(new(20, 0))));
        session.Drain();
        var acceptedWalk = walk.GetAwaiter().GetResult();
        CheckAssert.True(acceptedWalk.Accepted,
            "a valid ordinary walk must replace combat with a server route");
        CheckAssert.True(acceptedWalk.Gameplay.CombatTargetEnemyId is null,
            "walk must clear the durable combat target atomically");
        CheckAssert.Equal(0L, acceptedWalk.Gameplay.NextCombatAttackTick,
            "walk must clear the durable attack cadence atomically");
        CheckAssert.Equal(targeted.Gameplay.ActorRevision + 1,
            acceptedWalk.Gameplay.ActorRevision,
            "leaving combat through walk must commit one actor revision");

        session.Tick();
        var walked = session.CaptureSnapshot().Actors.Single();
        var ordinaryStep = 6f / SimulationTiming.TicksPerSecond;
        CheckAssert.True(
            MathF.Abs(walked.Position.X - ordinaryStep) < .0001f &&
            MathF.Abs(walked.Position.Y) < .0001f,
            $"walk and combat chase must not both move the actor: {walked.Position}");
        CheckAssert.Equal(0UL, walked.Gameplay.CombatAttackSequence,
            "the walk step must not also execute an autonomous attack");

        var close = session.SeedEnemy(new(
            Enemy(53), EnemyKind.CaveSlime,
            walked.Position + new Vector2(.75f, 0),
            Health: 1_000, MaximumHealth: 1_000));
        var closeTarget = Send(session, connection,
            joined.Identity.PlayerId, 4,
            new SetCombatTargetIntent(
                Guid.Parse("ca520000-0000-0000-0000-000000000002"),
                walked.Gameplay.Inventory.Revision,
                walked.Gameplay.ActorRevision,
                new(close.EnemyId, close.Revision)));
        CheckAssert.True(closeTarget.Accepted,
            "the stop fixture must acquire its nearby enemy");
        session.Tick();
        var armed = session.CaptureSnapshot().Actors.Single();
        CheckAssert.True(armed.Gameplay.NextCombatAttackTick >
                         session.Clock.Tick,
            "one autonomous attack must establish a live cadence before stop");
        var playerAttacks = new List<CombatEventSnapshot>();
        session.CombatEventCommitted += value =>
        {
            if (value.Kind == CombatEventKind.PlayerAttacked)
                playerAttacks.Add(value);
        };

        var stop = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, 5,
            StopIntent.Instance));
        session.Drain();
        var acceptedStop = stop.GetAwaiter().GetResult();
        CheckAssert.True(acceptedStop.Accepted,
            "ordinary stop must be accepted during combat cadence");
        CheckAssert.True(acceptedStop.Gameplay.CombatTargetEnemyId is null,
            "stop must clear the durable combat target atomically");
        CheckAssert.Equal(0L, acceptedStop.Gameplay.NextCombatAttackTick,
            "stop must clear the pending combat cadence atomically");
        CheckAssert.Equal(armed.Gameplay.ActorRevision + 1,
            acceptedStop.Gameplay.ActorRevision,
            "leaving combat through stop must commit one actor revision");
        var stoppedPosition = session.CaptureSnapshot().Actors.Single().Position;
        var stoppedSequence = acceptedStop.Gameplay.CombatAttackSequence;
        session.Tick();
        var stopped = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(stoppedPosition, stopped.Position,
            "stop must prevent combat chase from moving the actor next step");
        CheckAssert.Equal(stoppedSequence,
            stopped.Gameplay.CombatAttackSequence,
            "stop must prevent the pending cadence from attacking");
        CheckAssert.Equal(0, playerAttacks.Count,
            "no player attack event may be emitted after ordinary stop");
    }

    private static void AssertOfflineCombatUnchanged(
        AuthoritativeActorCheckpoint expectedActor,
        AuthoritativeEnemyCheckpoint expectedEnemy,
        ulong expectedEventOrdinal,
        AuthoritativeSessionCheckpoint actual,
        string subject)
    {
        var actor = actual.Actors.Single();
        var enemy = actual.Combat!.Enemies.Single();
        CheckAssert.Equal(expectedActor.Position, actor.Position,
            $"{subject} must not chase a target");
        CheckAssert.Equal(expectedActor.Gameplay.ActorRevision,
            actor.Gameplay.ActorRevision,
            $"{subject} must not gain an autonomous gameplay revision");
        CheckAssert.Equal(expectedActor.Gameplay.Health, actor.Gameplay.Health,
            $"{subject} must not take autonomous combat damage");
        CheckAssert.Equal(expectedActor.Gameplay.AttackExperience,
            actor.Gameplay.AttackExperience,
            $"{subject} must not gain attack experience");
        CheckAssert.Equal(expectedActor.Gameplay.StrengthExperience,
            actor.Gameplay.StrengthExperience,
            $"{subject} must not gain strength experience");
        CheckAssert.Equal(expectedActor.Gameplay.DefenceExperience,
            actor.Gameplay.DefenceExperience,
            $"{subject} must not gain defence experience");
        CheckAssert.Equal(expectedActor.Gameplay.CombatAttackSequence,
            actor.Gameplay.CombatAttackSequence,
            $"{subject} must not advance attack rolls");
        CheckAssert.Equal(expectedActor.Gameplay.CombatTargetEnemyId,
            actor.Gameplay.CombatTargetEnemyId,
            $"{subject} must retain its paused durable target");
        CheckAssert.Equal(expectedEnemy.Health, enemy.Health,
            $"{subject} must not damage its target");
        CheckAssert.Equal(expectedEnemy.Revision, enemy.Revision,
            $"{subject} must not advance the target revision");
        CheckAssert.Equal(expectedEventOrdinal,
            actual.Combat.NextEventOrdinal,
            $"{subject} must not allocate combat events");
        CheckAssert.False(actual.World.Objects.Any(value =>
                value.Object.DefinitionId == ItemIds.LootBag),
            $"{subject} must not materialize combat loot");
    }

    private static void DeathVacatesBoatAndRespawnCannotSnapBack()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca000000-0000-0000-0000-000000000031"));
        var respawnPosition = new Vector2(9, 7);
        var combatOptions = new AuthoritativeCombatOptions
        {
            EnemyAttackIntervalTicks = 1,
            RespawnDelayTicks = 2,
            RespawnPosition = respawnPosition
        };
        var boatOptions = new AuthoritativeBoatTransactionOptions
        {
            MovementSpeed = .25f,
            MaximumPathSearchVisited = 2_048,
            MaximumRouteWaypoints = 128
        };
        var navigation = new CombatBoatNavigation();
        var session = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(
                navigation, boatOptions),
            combatTransactions: new AuthoritativeCombatTransactions(
                3131, options: combatOptions));
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Combat sailor", new(.5f, 1.5f)));
        session.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        var boat = session.SeedBoat(new(
            new BoatId(Guid.Parse(
                "ca100000-0000-0000-0000-000000000031")),
            joined.Identity.PlayerId,
            new(.5f, .5f)));
        var board = Send(session, connection, joined.Identity.PlayerId, 1,
            new BoardBoatIntent(
                Guid.Parse("ca200000-0000-0000-0000-000000000031"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                new(boat.BoatId, boat.Revision)));
        CheckAssert.True(board.Accepted,
            "the combat sailor must board before the death transition");
        boat = session.CaptureBoats().Single();
        var move = Send(session, connection, joined.Identity.PlayerId, 2,
            new MoveBoatIntent(
                Guid.Parse("ca300000-0000-0000-0000-000000000031"),
                board.Gameplay.Inventory.Revision,
                board.Gameplay.ActorRevision,
                new(boat.BoatId, boat.Revision),
                new(8.5f, .5f)));
        CheckAssert.True(move.Accepted,
            "the boarded actor must begin a live boat route");
        boat = session.CaptureBoats().Single();
        var movingRevision = boat.Revision;
        CheckAssert.True(boat.Destination is not null,
            "the death fixture requires an in-flight authoritative route");

        var committed = new List<BoatStateDelta>();
        var autonomous = new List<BoatStateDelta>();
        session.BoatStateCommitted += committed.Add;
        session.BoatAutonomousStateCommitted += autonomous.Add;
        var aboard = session.CaptureSnapshot().Actors.Single();
        session.SeedEnemy(new(
            Enemy(31), EnemyKind.CaveSlime, aboard.Position,
            PowerLevel: 100));
        var guard = 0;
        ActorSnapshot dead;
        do
        {
            session.Tick();
            dead = session.CaptureSnapshot().Actors.Single();
        } while (dead.Gameplay.LifeState != ActorLifeState.Dead &&
                 guard++ < 200);

        CheckAssert.Equal(ActorLifeState.Dead, dead.Gameplay.LifeState,
            "authoritative combat must kill the actor aboard the moving boat");
        var detached = session.CaptureBoats().Single();
        CheckAssert.True(detached.OccupantActorId is null &&
                         detached.OccupantPlayerId is null,
            "death must atomically clear both boat occupant identities");
        CheckAssert.True(detached.Destination is null &&
                         detached.Velocity == Vector2.Zero,
            "death must stop the boat and discard its authority route");
        CheckAssert.Equal(movingRevision + 1, detached.Revision,
            "death must publish exactly one fresh semantic boat revision");
        CheckAssert.Equal(1, committed.Count,
            "death must commit exactly one boat state delta");
        CheckAssert.Equal(1, autonomous.Count,
            "death must expose exactly one autonomous public boat delta");
        CheckAssert.Equal(detached, committed[0].Current!,
            "the committed death delta must contain the final vacant boat");
        CheckAssert.Equal(detached, autonomous[0].Current!,
            "the autonomous death delta must contain the same revision");
        CheckAssert.True(session.CaptureSnapshot().Actors.Single()
                .BoardedBoatId is null,
            "the actor snapshot must stop projecting a boat attachment at death");

        while (session.Clock.Tick < dead.Gameplay.RespawnAvailableTick)
            session.Tick();
        var readyCheckpoint = session.CaptureCheckpoint();
        var persistedActor = readyCheckpoint.Actors.Single();
        var persistedBoat = readyCheckpoint.Boats!.Boats.Single();
        // Model a legacy checkpoint that retained a dead rider and route. The
        // respawn seam must repair it before changing the actor transform.
        var legacyBoat = persistedBoat with
        {
            OccupantActorId = persistedActor.Identity.ActorId,
            OccupantPlayerId = persistedActor.Identity.PlayerId,
            Position = persistedActor.Position,
            RemainingRoute = ImmutableArray.Create(
                persistedActor.Position + Vector2.UnitX),
            PlanningCooldownSeconds = 0
        };
        var legacyCheckpoint = readyCheckpoint with
        {
            Boats = new AuthoritativeBoatTransactionsCheckpoint([legacyBoat])
        };
        var restored = new AuthoritativeWorldSession(
            identitySource: new CombatIdentitySource(),
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(
                navigation, boatOptions),
            combatTransactions: new AuthoritativeCombatTransactions(
                3131, options: combatOptions));
        restored.RestoreCheckpoint(legacyCheckpoint);
        var reconnectConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnectPending.GetAwaiter().GetResult().Accepted,
            "the dead boat occupant must reconnect after checkpoint restore");
        var restoredActor = restored.CaptureSnapshot().Actors.Single();
        var respawn = Send(restored, reconnectConnection,
            joined.Identity.PlayerId, 3,
            new RespawnIntent(
                Guid.Parse("ca400000-0000-0000-0000-000000000031"),
                restoredActor.Gameplay.Inventory.Revision,
                restoredActor.Gameplay.ActorRevision));
        CheckAssert.True(respawn.Accepted && respawn.BoatDelta is not null,
            "respawn must defensively publish its repaired boat occupancy");
        var respawnedBoat = restored.CaptureBoats().Single();
        CheckAssert.True(respawnedBoat.OccupantActorId is null &&
                         respawnedBoat.Destination is null,
            "respawn must detach and stop a retained legacy boat route");
        var respawnedActor = restored.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(respawnPosition, respawnedActor.Position,
            "respawn must teleport to the configured authority position");
        restored.Tick();
        CheckAssert.Equal(respawnPosition,
            restored.CaptureSnapshot().Actors.Single().Position,
            "the next boat synchronization tick must not snap the actor back");

        var postRespawnCheckpoint = restored.CaptureCheckpoint();
        var restarted = new AuthoritativeWorldSession(
            sessionId: sessionId,
            boatTransactions: new AuthoritativeBoatTransactions(
                navigation, boatOptions),
            combatTransactions: new AuthoritativeCombatTransactions(
                3131, options: combatOptions));
        restarted.RestoreCheckpoint(postRespawnCheckpoint);
        CheckAssert.Equal(respawnPosition,
            restarted.CaptureSnapshot().Actors.Single().Position,
            "checkpoint restore must retain the post-respawn position");
        CheckAssert.True(restarted.CaptureBoats().Single()
                .OccupantActorId is null,
            "checkpoint restore must retain the repaired vacant boat");
    }

    private static IntentResult Send(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        PlayerId player,
        long sequence,
        GameplayIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new(
            connection, player, sequence, intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static EnemyId Enemy(int value) => new(Guid.Parse(
        $"ce000000-0000-0000-0000-{value:000000000000}"));

    private static CombatActorInput Actor(ActorId actorId, Vector2 position)
    {
        var slots = Enumerable.Range(0, PlayerInventory.Capacity)
            .Select(static index => new InventorySlotSnapshot(index, null, 0))
            .ToImmutableArray();
        return new(actorId, 123, position, 0, true,
            new PlayerGameplaySnapshot(
                1, 100, 100, 0, 0, 0,
                new PlayerInventorySnapshot(1, slots)));
    }

    private sealed class CombatIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "c1000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse(
                "c2000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() => new(
            Convert.ToBase64String(new byte[32]));
    }

    private sealed class SequencedCombatIdentitySource : ISessionIdentitySource
    {
        private int _next;

        public PlayerIdentity CreatePlayerIdentity()
        {
            var ordinal = checked(++_next);
            return new(
                new PlayerId(Guid.Parse(
                    $"c1000000-0000-0000-0000-{ordinal:000000000000}")),
                new ActorId(Guid.Parse(
                    $"c2000000-0000-0000-0000-{ordinal:000000000000}")));
        }

        public ReconnectToken CreateReconnectToken()
        {
            var bytes = new byte[32];
            bytes[0] = checked((byte)_next);
            return new(Convert.ToBase64String(bytes));
        }
    }

    private sealed class CombatBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is >= 0 and < 10 && point.Y is >= 0 and < 1;

        public bool IsLanding(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is >= 0 and < 10 && point.Y is >= 1 and < 3;

        public bool IsInitialMooring(Vector2 point) => IsNavigable(point);
    }
}
