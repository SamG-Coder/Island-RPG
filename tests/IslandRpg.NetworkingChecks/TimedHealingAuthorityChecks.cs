using System.Net;
using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Gameplay;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class TimedHealingAuthorityChecks
{
    private static readonly SessionId WorldId = new(Guid.Parse(
        "a8c188d7-3ad6-42b7-96ca-ecdd33e1b561"));

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "authoritative medicine starts replaces and advances timed healing",
            MedicineStartsReplacesAndAdvances);
        checks.Add(
            "authoritative timed healing clears at full health",
            HealingClearsAtFullHealth);
        checks.Add(
            "authoritative damage interrupts timed healing",
            DamageInterruptsHealing);
        checks.Add(
            "timed healing survives checkpoints and pauses offline",
            HealingSurvivesCheckpointAndPausesOffline);
        checks.Add(
            "timed healing persistence validates and round trips exact state",
            PersistenceValidatesAndRoundTrips);
        checks.Add(
            "combat-disabled starvation death detaches boats and can respawn",
            CombatDisabledStarvationDeathDetachesAndRespawns);
    }

    private static void CombatDisabledStarvationDeathDetachesAndRespawns()
    {
        var navigation = new SurvivalBoatNavigation();
        var bootstrap = NewSession(new AuthoritativeBoatTransactions(navigation));
        var connection = ClientConnectionId.New();
        var pendingJoin = bootstrap.EnqueueJoinAsync(new JoinRequest(
            connection,
            "Stranded survivor",
            new Vector2(.5f, 1.5f),
            InitialHunger: 0,
            ProvisionBoat: true));
        bootstrap.Drain();
        var joined = pendingJoin.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted && joined.Boat is not null,
            "the fallback survival fixture must provision a boat");

        var boardPending = bootstrap.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new BoardBoatIntent(
                Guid.NewGuid(),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                new BoatReference(
                    joined.Boat!.BoatId,
                    joined.Boat.Revision))));
        bootstrap.Drain();
        CheckAssert.True(boardPending.GetAwaiter().GetResult().Accepted,
            "the fallback survival actor must board before starving");

        var checkpoint = bootstrap.CaptureCheckpoint();
        checkpoint = checkpoint with
        {
            Actors = [checkpoint.Actors.Single() with
            {
                Gameplay = checkpoint.Actors.Single().Gameplay with
                {
                    Health = 1,
                    StarvationDamageRemainder = .5f
                }
            }]
        };

        var session = NewSession(new AuthoritativeBoatTransactions(navigation));
        session.RestoreCheckpoint(checkpoint);
        var reconnectConnection = ClientConnectionId.New();
        var reconnectPending = session.EnqueueReconnectAsync(new(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        session.Drain();
        var reconnected = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnected.Accepted,
            "the starving boat occupant must reconnect after restore");

        var autonomousBoatDeltas = new List<BoatStateDelta>();
        session.BoatAutonomousStateCommitted += autonomousBoatDeltas.Add;
        TickSeconds(session, 1);
        var deadActor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(ActorLifeState.Dead, deadActor.Gameplay.LifeState,
            "lethal starvation must enter the canonical dead state without combat authority");
        CheckAssert.True(deadActor.BoardedBoatId is null &&
                         session.CaptureBoats().Single().OccupantActorId is null,
            "lethal starvation must detach the dead occupant from its boat");
        CheckAssert.Equal(1, autonomousBoatDeltas.Count,
            "fallback death must publish exactly one autonomous boat detachment");

        var lockedPending = session.EnqueueIntentAsync(new ActorCommand(
            reconnectConnection,
            joined.Identity.PlayerId,
            reconnected.NextCommandSequence,
            new RespawnIntent(
                Guid.NewGuid(),
                deadActor.Gameplay.Inventory.Revision,
                deadActor.Gameplay.ActorRevision)));
        session.Drain();
        CheckAssert.Equal(IntentStatus.RespawnLocked,
            lockedPending.GetAwaiter().GetResult().Status,
            "the combat-disabled fallback must enforce the respawn delay");

        while (session.Clock.Tick < deadActor.Gameplay.RespawnAvailableTick)
            session.Tick();
        var ready = session.CaptureSnapshot().Actors.Single().Gameplay;
        var respawnPending = session.EnqueueIntentAsync(new ActorCommand(
            reconnectConnection,
            joined.Identity.PlayerId,
            reconnected.NextCommandSequence + 1,
            new RespawnIntent(
                Guid.NewGuid(),
                ready.Inventory.Revision,
                ready.ActorRevision)));
        session.Drain();
        var respawn = respawnPending.GetAwaiter().GetResult();
        CheckAssert.True(respawn.Accepted,
            "a dead actor must remain recoverable without combat authority");
        CheckAssert.Equal(ActorLifeState.Alive, respawn.Gameplay.LifeState,
            "fallback respawn must restore the canonical alive state");
        CheckAssert.Equal(respawn.Gameplay.MaximumHealth / 2,
            respawn.Gameplay.Health,
            "fallback respawn must restore the same half-health recovery as combat");
        CheckAssert.Equal(25f, respawn.Gameplay.Hunger,
            "fallback respawn must restore the same hunger as combat");
        CheckAssert.Equal(Vector2.Zero,
            session.CaptureSnapshot().Actors.Single().Position,
            "fallback respawn must use the default authoritative spawn point");
    }

    private static void MedicineStartsReplacesAndAdvances()
    {
        var prepared = PrepareDamagedSession(
            40,
            new InitialInventoryItem(ItemIds.MedicinalHerbs),
            new InitialInventoryItem(ItemIds.HerbalPoultice));
        var session = prepared.Session;

        var herb = Consume(session, prepared.Connection,
            prepared.Identity.PlayerId, 1, ItemIds.MedicinalHerbs);
        CheckAssert.True(herb.Accepted,
            "an injured actor must be able to apply medicinal herbs");
        var started = Gameplay(session);
        CheckAssert.Equal(40, started.Health,
            "medicine must not grant its timed health immediately");
        CheckAssert.Equal(8f, started.TimedHealingRemainingHealth,
            "medicinal herbs must start their canonical health budget");
        CheckAssert.Equal(8f, started.TimedHealingRemainingSeconds,
            "medicinal herbs must start their canonical duration");
        CheckAssert.Equal(0f, started.TimedHealingFractionalHealth,
            "new medicine must start without fractional progress");

        TickSeconds(session, 2);
        var advanced = Gameplay(session);
        CheckAssert.Equal(42, advanced.Health,
            "two authoritative seconds must apply two herb healing points");
        CheckAssert.Equal(6f, advanced.TimedHealingRemainingHealth,
            "authoritative advancement must consume the herb health budget");
        CheckAssert.Equal(6f, advanced.TimedHealingRemainingSeconds,
            "authoritative advancement must consume the herb duration");

        var poultice = Consume(session, prepared.Connection,
            prepared.Identity.PlayerId, 2, ItemIds.HerbalPoultice);
        CheckAssert.True(poultice.Accepted,
            "a newer medicinal treatment must replace an active one");
        var replaced = Gameplay(session);
        CheckAssert.Equal(18f, replaced.TimedHealingRemainingHealth,
            "a poultice must replace the previous residual health budget");
        CheckAssert.Equal(12f, replaced.TimedHealingRemainingSeconds,
            "a poultice must replace the previous residual duration");

        TickSeconds(session, 1);
        var poulticeAdvanced = Gameplay(session);
        CheckAssert.Equal(43, poulticeAdvanced.Health,
            "the first poultice second must apply its whole healing point");
        CheckAssert.Equal(16.5f,
            poulticeAdvanced.TimedHealingRemainingHealth,
            "poultice advancement must preserve the exact remaining budget");
        CheckAssert.Equal(11f,
            poulticeAdvanced.TimedHealingRemainingSeconds,
            "poultice advancement must preserve the exact remaining duration");
        CheckAssert.Equal(.5f,
            poulticeAdvanced.TimedHealingFractionalHealth,
            "poultice advancement must retain fractional healing deterministically");
    }

    private static void HealingClearsAtFullHealth()
    {
        var prepared = PrepareDamagedSession(
            99,
            new InitialInventoryItem(ItemIds.HerbalPoultice));
        CheckAssert.True(Consume(
                prepared.Session,
                prepared.Connection,
                prepared.Identity.PlayerId,
                1,
                ItemIds.HerbalPoultice).Accepted,
            "an injured actor must be able to start a poultice treatment");

        TickSeconds(prepared.Session, 1);
        var full = Gameplay(prepared.Session);
        CheckAssert.Equal(full.MaximumHealth, full.Health,
            "timed healing must clamp to authoritative maximum health");
        AssertNoTimedHealing(full,
            "reaching full health must clear unused medicinal progress");

        var fullPrepared = PrepareDamagedSession(
            100,
            new InitialInventoryItem(ItemIds.HerbalPoultice));
        var rejected = Consume(
            fullPrepared.Session,
            fullPrepared.Connection,
            fullPrepared.Identity.PlayerId,
            1,
            ItemIds.HerbalPoultice);
        CheckAssert.Equal(IntentStatus.AlreadyFull, rejected.Status,
            "full healthy actors must not consume another treatment");
        CheckAssert.Equal(1, ItemCount(Gameplay(fullPrepared.Session),
                ItemIds.HerbalPoultice),
            "a rejected full-health treatment must remain in inventory");
    }

    private static void DamageInterruptsHealing()
    {
        var prepared = PrepareDamagedSession(
            40,
            initialHunger: 0,
            new InitialInventoryItem(ItemIds.MedicinalHerbs));
        CheckAssert.True(Consume(
                prepared.Session,
                prepared.Connection,
                prepared.Identity.PlayerId,
                1,
                ItemIds.MedicinalHerbs).Accepted,
            "the interruption scenario must start an active treatment");

        TickSeconds(prepared.Session, 2);
        var interrupted = Gameplay(prepared.Session);
        CheckAssert.Equal(40, interrupted.Health,
            "starvation damage must not be cancelled by treatment in the same cadence");
        AssertNoTimedHealing(interrupted,
            "authoritative damage must interrupt the active medicinal treatment");
    }

    private static void HealingSurvivesCheckpointAndPausesOffline()
    {
        var prepared = PrepareDamagedSession(
            40,
            new InitialInventoryItem(ItemIds.MedicinalHerbs));
        CheckAssert.True(Consume(
                prepared.Session,
                prepared.Connection,
                prepared.Identity.PlayerId,
                1,
                ItemIds.MedicinalHerbs).Accepted,
            "the checkpoint scenario must start an active treatment");
        TickSeconds(prepared.Session, 1);
        var before = Gameplay(prepared.Session);
        var checkpoint = prepared.Session.CaptureCheckpoint();

        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var restoredBefore = Gameplay(restored);
        AssertTimedHealingEqual(before, restoredBefore,
            "the in-memory authority checkpoint must preserve exact treatment progress");

        TickSeconds(restored, 3);
        var offline = Gameplay(restored);
        CheckAssert.Equal(restoredBefore.Health, offline.Health,
            "offline actors must not receive authoritative timed healing");
        CheckAssert.Equal(restoredBefore.ActorRevision, offline.ActorRevision,
            "offline treatment must not churn private actor revisions");
        AssertTimedHealingEqual(restoredBefore, offline,
            "offline treatment duration and fractional progress must pause exactly");

        var reconnectConnection = ClientConnectionId.New();
        var reconnect = restored.EnqueueReconnectAsync(new ReconnectRequest(
            reconnectConnection,
            prepared.Identity.PlayerId,
            prepared.Token));
        restored.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the checkpoint actor must reconnect with its durable credential");
        TickSeconds(restored, 1);
        var resumed = Gameplay(restored);
        CheckAssert.Equal(offline.Health + 1, resumed.Health,
            "reconnecting must resume treatment at the next authority cadence");
        CheckAssert.Equal(offline.TimedHealingRemainingSeconds - 1,
            resumed.TimedHealingRemainingSeconds,
            "reconnecting must resume the preserved treatment duration");
    }

    private static void PersistenceValidatesAndRoundTrips()
    {
        var prepared = PrepareDamagedSession(
            40,
            new InitialInventoryItem(ItemIds.HerbalPoultice));
        CheckAssert.True(Consume(
                prepared.Session,
                prepared.Connection,
                prepared.Identity.PlayerId,
                1,
                ItemIds.HerbalPoultice).Accepted,
            "the persistence scenario must start an active treatment");
        TickSeconds(prepared.Session, 1);
        var expected = Gameplay(prepared.Session);
        var checkpoint = prepared.Session.CaptureCheckpoint();
        var options = new ServerOptions(
            IPAddress.Loopback,
            0,
            WorldId.Value,
            71_209,
            "timed-healing-test",
            "base",
            4);
        var durable = ServerCheckpointMapper.ToDurable(
            checkpoint, options, revision: 1);

        using var folder = TemporaryFolder.Create();
        var store = new ServerCheckpointStore(folder.Path);
        store.Save(durable);
        var loaded = store.Load(WorldId.Value)!.Checkpoint;
        var restored = ServerCheckpointMapper.ToSimulation(loaded, options);
        var actual = restored.Actors.Single().Gameplay;
        CheckAssert.Equal(expected.Health, actual.Health,
            "disk persistence must preserve health beside treatment progress");
        AssertTimedHealingEqual(expected, actual,
            "disk DTOs and mappers must preserve exact treatment progress");

        var invalidActor = durable.Actors[0] with
        {
            TimedHealingRemainingHealth = 5,
            TimedHealingRemainingSeconds = 0
        };
        CheckAssert.Throws<InvalidDataException>(
            () => ServerCheckpointStore.Validate(
                durable with { Actors = [invalidActor] }, WorldId.Value),
            "durable treatment state must reject a non-canonical inactive remainder");

        var activeAtFull = durable.Actors[0] with
        {
            Health = durable.Actors[0].MaximumHealth,
            TimedHealingRemainingHealth = 8,
            TimedHealingRemainingSeconds = 8,
            TimedHealingFractionalHealth = 0
        };
        CheckAssert.Throws<InvalidDataException>(
            () => ServerCheckpointStore.Validate(
                durable with { Actors = [activeAtFull] }, WorldId.Value),
            "durable treatment state must reject active healing at full health");

        var malformedSimulation = checkpoint with
        {
            Actors = [checkpoint.Actors[0] with
            {
                Gameplay = checkpoint.Actors[0].Gameplay with
                {
                    TimedHealingFractionalHealth = 1
                }
            }]
        };
        CheckAssert.Throws<InvalidOperationException>(
            () => NewSession().RestoreCheckpoint(malformedSimulation),
            "session restore must reject out-of-range fractional treatment progress");
    }

    private static PreparedSession PrepareDamagedSession(
        int health,
        params InitialInventoryItem[] inventory) =>
        PrepareDamagedSession(health, 100, inventory);

    private static PreparedSession PrepareDamagedSession(
        int health,
        float initialHunger,
        params InitialInventoryItem[] inventory)
    {
        var bootstrap = NewSession();
        var connection = ClientConnectionId.New();
        var joined = bootstrap.EnqueueJoinAsync(new JoinRequest(
            connection,
            "Mira",
            Vector2.Zero,
            inventory,
            InitialHunger: initialHunger));
        bootstrap.Drain();
        var join = joined.GetAwaiter().GetResult();
        CheckAssert.True(join.Accepted,
            "the timed-healing test actor must join the bootstrap session");
        var checkpoint = bootstrap.CaptureCheckpoint();
        checkpoint = checkpoint with
        {
            Actors = [checkpoint.Actors[0] with
            {
                Gameplay = checkpoint.Actors[0].Gameplay with
                {
                    Health = health
                }
            }]
        };

        var session = NewSession();
        session.RestoreCheckpoint(checkpoint);
        var restoredConnection = ClientConnectionId.New();
        var reconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            restoredConnection,
            join.Identity.PlayerId,
            join.ReconnectToken));
        session.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the damaged test actor must reconnect before treatment");
        return new PreparedSession(
            session,
            restoredConnection,
            join.Identity,
            join.ReconnectToken);
    }

    private static IntentResult Consume(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        PlayerId playerId,
        long sequence,
        string itemId)
    {
        var gameplay = Gameplay(session);
        var slot = gameplay.Inventory.Slots.First(value =>
            value.ItemId == itemId).Slot;
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            playerId,
            sequence,
            new ConsumeFoodIntent(
                Guid.NewGuid(),
                gameplay.Inventory.Revision,
                gameplay.ActorRevision,
                slot)));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static PlayerGameplaySnapshot Gameplay(
        AuthoritativeWorldSession session) =>
        session.CaptureSnapshot().Actors.Single().Gameplay;

    private static int ItemCount(
        PlayerGameplaySnapshot gameplay,
        string itemId) => gameplay.Inventory.Slots
        .Where(value => value.ItemId == itemId)
        .Sum(static value => value.Quantity);

    private static void TickSeconds(
        AuthoritativeWorldSession session,
        int seconds)
    {
        for (var tick = 0;
             tick < seconds * SimulationTiming.TicksPerSecond;
             tick++)
            session.Tick();
    }

    private static void AssertNoTimedHealing(
        PlayerGameplaySnapshot value,
        string message)
    {
        CheckAssert.Equal(0f, value.TimedHealingRemainingHealth, message);
        CheckAssert.Equal(0f, value.TimedHealingRemainingSeconds, message);
        CheckAssert.Equal(0f, value.TimedHealingFractionalHealth, message);
    }

    private static void AssertTimedHealingEqual(
        PlayerGameplaySnapshot expected,
        PlayerGameplaySnapshot actual,
        string message)
    {
        CheckAssert.Equal(expected.TimedHealingRemainingHealth,
            actual.TimedHealingRemainingHealth, message);
        CheckAssert.Equal(expected.TimedHealingRemainingSeconds,
            actual.TimedHealingRemainingSeconds, message);
        CheckAssert.Equal(expected.TimedHealingFractionalHealth,
            actual.TimedHealingFractionalHealth, message);
    }

    private static AuthoritativeWorldSession NewSession(
        AuthoritativeBoatTransactions? boatTransactions = null) => new(
        identitySource: new DeterministicIdentitySource(),
        sessionId: WorldId,
        boatTransactions: boatTransactions);

    private sealed class SurvivalBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is >= 0 and < 10 && point.Y is >= 0 and < 1;

        public bool IsLanding(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is >= 0 and < 10 && point.Y is >= 1 and < 3;

        public bool IsInitialMooring(Vector2 point) => IsNavigable(point);
    }

    private sealed record PreparedSession(
        AuthoritativeWorldSession Session,
        ClientConnectionId Connection,
        PlayerIdentity Identity,
        ReconnectToken Token);

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "10000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse(
                "20000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() =>
            new("timed-healing-authority-secret");
    }

    private sealed class TemporaryFolder : IDisposable
    {
        private TemporaryFolder(string path) => Path = path;

        public string Path { get; }

        public static TemporaryFolder Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "IslandRpg-TimedHealingChecks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryFolder(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
