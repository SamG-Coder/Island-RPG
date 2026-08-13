using System.Net;
using System.Net.Sockets;
using IslandRpg.Client;
using IslandRpg.Protocol;

namespace IslandRpg.NetworkingChecks;

internal static class CombatProtocolClientChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string BuildVersion = "combat-client-checks";
    private const string ContentVersion = "combat-client-content";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "combat protocol messages round trip exactly",
            MessagesRoundTrip);
        checks.Add(
            "combat protocol rejects malformed state and bounds",
            RejectsMalformedStateAndBounds);
        checks.Add(
            "network client applies combat player state and correlates actions",
            AppliesPlayerStateAndCorrelatesActionAsync);
        checks.Add(
            "network client rejects enemy batches atomically",
            RejectsEnemyBatchAtomicallyAsync);
        checks.Add(
            "network client retains enemy tombstones across baselines",
            RetainsEnemyTombstonesAsync);
        checks.Add(
            "network client rejects combat event ordinal replay",
            RejectsCombatEventReplayAsync);
    }

    private static void MessagesRoundTrip()
    {
        var enemyId = Guid.Parse(
            "c0100000-0000-0000-0000-000000000001");
        var commandId = Guid.Parse(
            "c0200000-0000-0000-0000-000000000001");
        var enemyReference = new CombatEnemyReference(enemyId, 7);
        CombatActionPayload[] payloads =
        [
            new SetCombatTargetAction(enemyReference),
            new CancelCombatAction(),
            new SetCombatStanceAction(CombatStance.Defensive),
            new RespawnAction(),
        ];

        for (var index = 0; index < payloads.Length; index++)
        {
            var expected = new ActionCommandMessage(
                (ulong)(index + 1),
                40,
                commandId,
                12,
                18,
                payloads[index]);
            var actual = (ActionCommandMessage)ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(expected));
            CheckAssert.Equal(expected, actual,
                $"{payloads[index].Action} must round trip exactly");
        }

        var first = Enemy(
            enemyId,
            0x8000_0000_0000_0101,
            7,
            CombatEnemyArchetype.GrassSlime,
            CombatEnemySize.Medium,
            CombatEnemyBehavior.Idle,
            CombatStatusFlags.Hidden,
            45,
            60);
        var second = Enemy(
            Guid.Parse("c0100000-0000-0000-0000-000000000002"),
            0x8000_0000_0000_0102,
            3,
            CombatEnemyArchetype.SandSlime,
            CombatEnemySize.Large,
            CombatEnemyBehavior.Burrowed,
            CombatStatusFlags.Burrowed,
            180,
            240,
            enemyId);
        var expectedBaseline = new EnemyBaselineMessage(
            8, 41, [first, second]);
        var actualBaseline = (EnemyBaselineMessage)
            ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(expectedBaseline));
        CheckAssert.Equal(
            expectedBaseline with { Enemies = actualBaseline.Enemies },
            actualBaseline,
            "enemy baseline metadata must round trip exactly");
        CheckAssert.SequenceEqual(expectedBaseline.Enemies, actualBaseline.Enemies,
            "enemy baseline states must round trip exactly");

        var changed = first with
        {
            Revision = 8,
            Behavior = CombatEnemyBehavior.Attacking,
            StatusFlags = CombatStatusFlags.Rooted,
            Health = 32,
            TargetEntityId = 707,
        };
        var expectedDeltas = new EnemyDeltaBatchMessage(
            9,
            42,
            [
                new EnemyDelta(
                    EnemyDeltaKind.Upsert,
                    new CombatEnemyReference(first.EnemyId, 7),
                    8,
                    changed),
                new EnemyDelta(
                    EnemyDeltaKind.Remove,
                    new CombatEnemyReference(second.EnemyId, 3),
                    4,
                    null),
            ]);
        var actualDeltas = (EnemyDeltaBatchMessage)
            ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(expectedDeltas));
        CheckAssert.Equal(
            expectedDeltas with { Deltas = actualDeltas.Deltas },
            actualDeltas,
            "enemy delta metadata must round trip exactly");
        CheckAssert.SequenceEqual(expectedDeltas.Deltas, actualDeltas.Deltas,
            "enemy upserts and removals must round trip exactly");

        CombatEvent[] events =
        [
            new(91, CombatEventKind.AttackStarted,
                first.EntityId, 707, 0, CombatStatusEffect.None,
                11.25f, -4.5f, 0, 0),
            new(92, CombatEventKind.Damage,
                first.EntityId, 707, 14, CombatStatusEffect.None,
                11.5f, -4.25f, 0, 0),
            new(93, CombatEventKind.StatusApplied,
                first.EntityId, 707, 0, CombatStatusEffect.Root,
                11.5f, -4.25f, 0, second.EntityId),
        ];
        var expectedEvents = new CombatEventBatchMessage(10, 43, events);
        var actualEvents = (CombatEventBatchMessage)
            ReliableProtocolCodec.Decode(
                ReliableProtocolCodec.Encode(expectedEvents));
        CheckAssert.Equal(
            expectedEvents with { Events = actualEvents.Events },
            actualEvents,
            "combat event metadata must round trip exactly");
        CheckAssert.SequenceEqual(expectedEvents.Events, actualEvents.Events,
            "combat events must round trip exactly");

        CombatActionResultMessage[] results =
        [
            new(11, 44, commandId, CombatActionKind.SetTarget,
                enemyReference, true, CommandRejectionCode.None, string.Empty,
                13, 18, 8),
            new(12, 45, Guid.Parse(
                    "c0200000-0000-0000-0000-000000000002"),
                CombatActionKind.Cancel, default, false,
                CommandRejectionCode.Invalid, "already_cancelled",
                13, 18, 0),
        ];
        foreach (var expectedResult in results)
        {
            var actualResult = (CombatActionResultMessage)
                ReliableProtocolCodec.Decode(
                    ReliableProtocolCodec.Encode(expectedResult));
            CheckAssert.Equal(expectedResult, actualResult,
                $"{expectedResult.Action} combat receipt must round trip exactly");
        }
    }

    private static void RejectsMalformedStateAndBounds()
    {
        var enemyId = Guid.Parse(
            "c0300000-0000-0000-0000-000000000001");
        var otherId = Guid.Parse(
            "c0300000-0000-0000-0000-000000000002");
        var state = Enemy(enemyId, 101, 3);

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 1, 1,
                new SetCombatTargetAction(new CombatEnemyReference(
                    Guid.Empty, 3)))),
            "combat targets must reject empty enemy identities");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 1, 1,
                new SetCombatTargetAction(new CombatEnemyReference(
                    enemyId, 0)))),
            "combat targets must require a nonzero expected revision");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 1, 1,
                new SetCombatStanceAction((CombatStance)byte.MaxValue))),
            "combat actions must reject unknown stances");

        var unknownAction = ReliableProtocolCodec.Encode(
            new ActionCommandMessage(
                1, 1, Guid.NewGuid(), 1, 1,
                new CancelCombatAction()));
        // Action payload: command ID (16), actor/inventory revisions (8),
        // outer action kind (1), then the combat action enum.
        unknownAction[ProtocolConstants.ReliableHeaderSize + 25] = byte.MaxValue;
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Decode(unknownAction),
            "the decoder must reject an unknown combat action kind");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with
                {
                    Archetype = (CombatEnemyArchetype)byte.MaxValue,
                }])),
            "enemy state must reject unknown archetypes");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with
                {
                    Size = (CombatEnemySize)byte.MaxValue,
                }])),
            "enemy state must reject unknown sizes");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with
                {
                    Behavior = (CombatEnemyBehavior)byte.MaxValue,
                }])),
            "enemy state must reject unknown behavior");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with
                {
                    StatusFlags = (CombatStatusFlags)(1u << 31),
                }])),
            "enemy state must reject unknown status flags");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with { X = float.NaN }])),
            "enemy state must reject non-finite positions");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with { Health = state.MaximumHealth + 1 }])),
            "enemy health must not exceed its maximum");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with
                {
                    Health = 0,
                    Behavior = CombatEnemyBehavior.Idle,
                }])),
            "zero-health enemy state must use dead behavior");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state with { ParentEnemyId = enemyId }])),
            "enemy state must reject a self-parent identity");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state, state with { EntityId = 102 }])),
            "enemy baselines must reject duplicate enemy IDs");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, [state, Enemy(otherId, state.EntityId, 1)])),
            "enemy baselines must reject duplicate entity IDs");

        var oversizedEnemies = Enumerable.Range(
                0, ProtocolLimits.MaxEnemiesPerBatch + 1)
            .Select(index => Enemy(
                GuidFrom(index + 1),
                (ulong)(1_000 + index),
                1))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyBaselineMessage(
                1, 1, oversizedEnemies)),
            "enemy baselines must enforce their hard count limit");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1, [])),
            "enemy delta batches must not be empty");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1,
                [new EnemyDelta(
                    EnemyDeltaKind.Upsert,
                    new CombatEnemyReference(enemyId, 3),
                    3,
                    state)])),
            "enemy deltas must advance retained revision high-water");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1,
                [new EnemyDelta(
                    EnemyDeltaKind.Upsert,
                    new CombatEnemyReference(enemyId, 3),
                    4,
                    state with { EnemyId = otherId, Revision = 4 })])),
            "enemy upserts must match their referenced identity");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1,
                [new EnemyDelta(
                    (EnemyDeltaKind)byte.MaxValue,
                    new CombatEnemyReference(enemyId, 3),
                    4,
                    null)])),
            "enemy deltas must reject unknown delta kinds");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1,
                [
                    new EnemyDelta(
                        EnemyDeltaKind.Remove,
                        new CombatEnemyReference(enemyId, 3),
                        4,
                        null),
                    new EnemyDelta(
                        EnemyDeltaKind.Remove,
                        new CombatEnemyReference(enemyId, 4),
                        5,
                        null),
                ])),
            "one enemy batch must reject duplicate revision chains");
        var oversizedDeltas = Enumerable.Range(
                0, ProtocolLimits.MaxEnemiesPerBatch + 1)
            .Select(index => new EnemyDelta(
                EnemyDeltaKind.Remove,
                new CombatEnemyReference(GuidFrom(index + 1), 1),
                2,
                null))
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new EnemyDeltaBatchMessage(
                1, 1, oversizedDeltas)),
            "enemy delta batches must enforce their hard count limit");

        var validEvent = new CombatEvent(
            1, CombatEventKind.Damage, 101, 707, 10,
            CombatStatusEffect.None, 1, 2, 0, 0);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1, [validEvent with
                {
                    Kind = (CombatEventKind)byte.MaxValue,
                }])),
            "combat events must reject unknown event kinds");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1, [validEvent with
                {
                    StatusEffect = (CombatStatusEffect)byte.MaxValue,
                }])),
            "combat events must reject unknown status effects");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1, [validEvent with { Y = float.PositiveInfinity }])),
            "combat events must reject non-finite positions");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1, [validEvent with { Amount = -1 }])),
            "combat events must reject negative amounts");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1,
                [validEvent with { EventOrdinal = 2 }, validEvent])),
            "combat event ordinals must increase within each batch");
        var oversizedEvents = Enumerable.Range(
                0, ProtocolLimits.MaxCombatEventsPerBatch + 1)
            .Select(index => validEvent with
            {
                EventOrdinal = (ulong)(index + 1),
            })
            .ToArray();
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatEventBatchMessage(
                1, 1, oversizedEvents)),
            "combat event batches must enforce their hard count limit");

        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(new CombatActionResultMessage(
                1, 1, Guid.NewGuid(), CombatActionKind.SetTarget,
                new CombatEnemyReference(enemyId, 5), true,
                CommandRejectionCode.None, string.Empty, 1, 1, 4)),
            "combat action results must not regress enemy revisions");

        var slots = FullInventory();
        var playerState = new PlayerStateMessage(
            1, 1, Guid.NewGuid(), 707,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            0, 0, 1, 1, 80, 50, 0, 0, 0, slots);
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(playerState with
            {
                Hunger = float.NaN,
            }),
            "combat player state must reject non-finite survival state");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(playerState with
            {
                Health = 101,
                MaximumHealth = 100,
            }),
            "combat player health must not exceed its maximum");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(playerState with
            {
                CombatStance = (CombatStance)byte.MaxValue,
            }),
            "combat player state must reject an unknown stance");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(playerState with
            {
                LifeState = (CombatLifeState)byte.MaxValue,
            }),
            "combat player state must reject an unknown life state");
        CheckAssert.Throws<ProtocolException>(
            () => ReliableProtocolCodec.Encode(playerState with
            {
                CombatStatusFlags = (CombatStatusFlags)(1u << 31),
            }),
            "combat player state must reject unknown status flags");
    }

    private static async ValueTask AppliesPlayerStateAndCorrelatesActionAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await CombatPeer.ConnectAsync(
            client, cancellationToken);
        var enemy = Enemy(
            Guid.Parse("c0400000-0000-0000-0000-000000000001"),
            0x8000_0000_0000_0401,
            4,
            CombatEnemyArchetype.CaveSlime,
            CombatEnemySize.Large,
            CombatEnemyBehavior.Chasing,
            CombatStatusFlags.None,
            215,
            240);
        await peer.SendAsync(new EnemyBaselineMessage(
            2, 500, [enemy]), cancellationToken);

        var baseline = new PlayerStateMessage(
            3,
            501,
            peer.PlayerId,
            peer.PlayerEntityId,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            0,
            0,
            6,
            9,
            142,
            88.5f,
            12.25f,
            111,
            222,
            FullInventory(),
            333,
            444,
            555,
            666,
            777,
            888,
            180,
            901,
            802,
            703,
            CombatStance.Defensive,
            CombatLifeState.Alive,
            0,
            CombatStatusFlags.Slowed | CombatStatusFlags.Poisoned,
            enemy.EnemyId);
        await peer.SendAsync(baseline, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.ActorRevision == 6 &&
                  client.State.Enemies.ContainsKey(enemy.EnemyId),
            "the client did not publish the combat baselines",
            cancellationToken);

        var gameplay = client.State.Gameplay!;
        CheckAssert.Equal(180, gameplay.MaximumHealth,
            "the client must apply authoritative maximum health");
        CheckAssert.Equal(901, gameplay.AttackExperience,
            "the client must apply attack experience");
        CheckAssert.Equal(802, gameplay.StrengthExperience,
            "the client must apply strength experience");
        CheckAssert.Equal(703, gameplay.DefenceExperience,
            "the client must apply defence experience");
        CheckAssert.Equal(CombatStance.Defensive, gameplay.CombatStance,
            "the client must apply combat stance");
        CheckAssert.Equal(CombatLifeState.Alive, gameplay.LifeState,
            "the client must apply combat life state");
        CheckAssert.Equal(0ul, gameplay.RespawnTick,
            "a living player must have no respawn deadline");
        CheckAssert.Equal(
            CombatStatusFlags.Slowed | CombatStatusFlags.Poisoned,
            gameplay.CombatStatusFlags,
            "the client must apply all combat status flags");
        CheckAssert.Equal<Guid?>(enemy.EnemyId,
            gameplay.CombatTargetEnemyId,
            "a reconnect baseline must establish its authoritative combat target");

        var commandId = Guid.Parse(
            "c0500000-0000-0000-0000-000000000001");
        var payload = new SetCombatTargetAction(
            client.GetEnemyReference(enemy.EnemyId));
        var sequence = await client.SendActionAsync(
            payload, commandId, cancellationToken);
        var outbound = await peer.ReceiveAsync(cancellationToken);
        CheckAssert.True(outbound is ActionCommandMessage,
            "the client must publish combat through the action command stream");
        var action = (ActionCommandMessage)outbound;
        CheckAssert.Equal(sequence, action.Sequence,
            "the returned sequence must identify the published combat action");
        CheckAssert.Equal(commandId, action.CommandId,
            "the combat command must preserve its caller correlation ID");
        CheckAssert.Equal(6u, action.ActorRevision,
            "the combat command must use current actor high-water");
        CheckAssert.Equal(9u, action.InventoryRevision,
            "the combat command must use current inventory high-water");
        CheckAssert.Equal(payload, action.Payload,
            "the combat command must carry the exact enemy reference");

        var expectedResult = new CombatActionResultMessage(
            4, 503, commandId, CombatActionKind.SetTarget,
            payload.Enemy, true, CommandRejectionCode.None, string.Empty,
            7, 9, enemy.Revision);
        var completion = new TaskCompletionSource<CombatActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.CombatActionCompleted += (_, args) =>
            completion.TrySetResult(args.Result);
        await peer.SendAsync(expectedResult, cancellationToken);
        var actualResult = await completion.Task.WaitAsync(
            Timeout, cancellationToken);
        CheckAssert.Equal(expectedResult, actualResult,
            "the typed combat receipt must retain command correlation exactly");

        var dead = baseline with
        {
            Sequence = 5,
            Tick = 510,
            Flags = PlayerStateFlags.Actor,
            BaselineActorRevision = 6,
            BaselineInventoryRevision = 9,
            ActorRevision = 7,
            InventoryRevision = 9,
            Health = 0,
            Hunger = 75,
            WellFedSeconds = 0,
            InventorySlots = [],
            MaximumHealth = 185,
            AttackExperience = 902,
            StrengthExperience = 803,
            DefenceExperience = 704,
            CombatStance = CombatStance.Aggressive,
            LifeState = CombatLifeState.Dead,
            RespawnTick = 650,
            CombatStatusFlags = CombatStatusFlags.Rooted,
            CombatTargetEnemyId = Guid.Empty,
        };
        await peer.SendAsync(dead, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.LifeState == CombatLifeState.Dead,
            "the client did not apply the combat death delta",
            cancellationToken);
        gameplay = client.State.Gameplay!;
        CheckAssert.Equal(185, gameplay.MaximumHealth,
            "an actor delta must replace maximum health");
        CheckAssert.Equal(902, gameplay.AttackExperience,
            "an actor delta must replace attack experience");
        CheckAssert.Equal(803, gameplay.StrengthExperience,
            "an actor delta must replace strength experience");
        CheckAssert.Equal(704, gameplay.DefenceExperience,
            "an actor delta must replace defence experience");
        CheckAssert.Equal(CombatStance.Aggressive, gameplay.CombatStance,
            "an actor delta must replace combat stance");
        CheckAssert.Equal(650ul, gameplay.RespawnTick,
            "a death delta must expose its authoritative respawn deadline");
        CheckAssert.Equal(CombatStatusFlags.Rooted,
            gameplay.CombatStatusFlags,
            "an actor delta must replace combat status flags");
        CheckAssert.True(gameplay.CombatTargetEnemyId is null,
            "an actor death delta must clear the authoritative combat target");
    }

    private static async ValueTask RejectsEnemyBatchAtomicallyAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await CombatPeer.ConnectAsync(
            client, cancellationToken);
        var first = Enemy(
            Guid.Parse("c0600000-0000-0000-0000-000000000001"),
            601,
            1);
        var second = Enemy(
            Guid.Parse("c0600000-0000-0000-0000-000000000002"),
            602,
            1);
        var enemyEvents = 0;
        client.EnemiesChanged += (_, _) =>
            Interlocked.Increment(ref enemyEvents);
        await peer.SendAsync(new EnemyBaselineMessage(
            2, 600, [first, second]), cancellationToken);
        await EventuallyAsync(
            () => client.State.Enemies.Count == 2,
            "the atomic enemy check did not receive its baseline",
            cancellationToken);
        var accepted = client.State.Enemies;

        var poisoned = new EnemyDeltaBatchMessage(
            3,
            601,
            [
                new EnemyDelta(
                    EnemyDeltaKind.Upsert,
                    new CombatEnemyReference(first.EnemyId, 1),
                    2,
                    first with { Revision = 2, Health = 40 }),
                new EnemyDelta(
                    EnemyDeltaKind.Upsert,
                    new CombatEnemyReference(second.EnemyId, 0),
                    1,
                    second),
            ]);
        await peer.SendAsync(poisoned, cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the client did not fault on the mismatched later enemy delta",
            cancellationToken);

        CheckAssert.True(ReferenceEquals(accepted, client.State.Enemies),
            "a rejected enemy batch must preserve the prior immutable map");
        CheckAssert.Equal(first, client.State.Enemies[first.EnemyId],
            "a valid earlier delta must not partially publish before rejection");
        CheckAssert.Equal(second, client.State.Enemies[second.EnemyId],
            "a rejected batch must preserve every later enemy");
        CheckAssert.Equal(1, Volatile.Read(ref enemyEvents),
            "only the accepted enemy baseline may raise a change event");
    }

    private static async ValueTask RetainsEnemyTombstonesAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await CombatPeer.ConnectAsync(
            client, cancellationToken);
        var enemy = Enemy(
            Guid.Parse("c0700000-0000-0000-0000-000000000001"),
            701,
            1);
        await peer.SendAsync(new EnemyBaselineMessage(
            2, 700, [enemy]), cancellationToken);
        await EventuallyAsync(
            () => client.State.Enemies.ContainsKey(enemy.EnemyId),
            "the tombstone check did not receive its baseline",
            cancellationToken);
        await peer.SendAsync(new EnemyDeltaBatchMessage(
            3,
            701,
            [new EnemyDelta(
                EnemyDeltaKind.Remove,
                new CombatEnemyReference(enemy.EnemyId, 1),
                2,
                null)]), cancellationToken);
        await EventuallyAsync(
            () => !client.State.Enemies.ContainsKey(enemy.EnemyId),
            "the client did not apply the enemy removal",
            cancellationToken);
        CheckAssert.False(client.TryGetEnemyReference(enemy.EnemyId, out _),
            "removed enemies must not remain targetable");

        await peer.SendAsync(new EnemyBaselineMessage(
            4, 702, [enemy with { Revision = 2 }]), cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the client did not reject a baseline resurrection at tombstone high-water",
            cancellationToken);
        CheckAssert.False(client.State.Enemies.ContainsKey(enemy.EnemyId),
            "a rejected baseline must not resurrect a tombstoned enemy");
    }

    private static async ValueTask RejectsCombatEventReplayAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await CombatPeer.ConnectAsync(
            client, cancellationToken);
        var received = new TaskCompletionSource<IReadOnlyList<CombatEvent>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var eventNotifications = 0;
        client.CombatEventsReceived += (_, args) =>
        {
            Interlocked.Increment(ref eventNotifications);
            received.TrySetResult(args.Events);
        };
        CombatEvent[] firstBatch =
        [
            new(80, CombatEventKind.AttackStarted,
                801, 802, 0, CombatStatusEffect.None,
                1, 2, 0, 0),
            new(81, CombatEventKind.Damage,
                801, 802, 9, CombatStatusEffect.None,
                1, 2, 0, 0),
        ];
        await peer.SendAsync(new CombatEventBatchMessage(
            2, 800, firstBatch), cancellationToken);
        var actual = await received.Task.WaitAsync(Timeout, cancellationToken);
        CheckAssert.SequenceEqual(firstBatch, actual,
            "the accepted combat event order must be preserved");

        await peer.SendAsync(new CombatEventBatchMessage(
            3,
            801,
            [firstBatch[^1] with { Kind = CombatEventKind.Death }]),
            cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the client did not fault on replayed combat event ordinal",
            cancellationToken);
        CheckAssert.Equal(1, Volatile.Read(ref eventNotifications),
            "replayed combat effects must not reach presentation subscribers");
    }

    private static EnemyState Enemy(
        Guid id,
        ulong entityId,
        uint revision,
        CombatEnemyArchetype archetype = CombatEnemyArchetype.WaterSlime,
        CombatEnemySize size = CombatEnemySize.Medium,
        CombatEnemyBehavior behavior = CombatEnemyBehavior.Idle,
        CombatStatusFlags statusFlags = CombatStatusFlags.None,
        int health = 60,
        int maximumHealth = 60,
        Guid parentEnemyId = default) => new(
            id,
            entityId,
            revision,
            archetype,
            size,
            behavior,
            statusFlags,
            12.5f,
            -8.25f,
            0,
            health,
            maximumHealth,
            0,
            parentEnemyId,
            1);

    private static InventorySlotState[] FullInventory() =>
        Enumerable.Range(0, ProtocolLimits.PlayerInventorySlots)
            .Select(static slot => slot == 0
                ? new InventorySlotState(slot, "training_sword", 1)
                : new InventorySlotState(slot, string.Empty, 0))
            .ToArray();

    private static Guid GuidFrom(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
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
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(failure);
    }

    private sealed class CombatPeer : IAsyncDisposable
    {
        public const ulong FirstCommandSequence = 400;
        private readonly TcpListener _listener;
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;

        private CombatPeer(
            TcpListener listener,
            TcpClient tcpClient,
            NetworkStream stream,
            Guid playerId,
            ulong playerEntityId)
        {
            _listener = listener;
            _tcpClient = tcpClient;
            _stream = stream;
            PlayerId = playerId;
            PlayerEntityId = playerEntityId;
        }

        public Guid PlayerId { get; }
        public ulong PlayerEntityId { get; }

        public static async Task<CombatPeer> ConnectAsync(
            NetworkGameClient client,
            CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient? tcpClient = null;
            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var worldId = Guid.NewGuid();
                var connect = client.ConnectAsync(
                    endpoint.Address.ToString(),
                    endpoint.Port,
                    new ClientHandshakeOptions(
                        BuildVersion,
                        ContentVersion,
                        Guid.NewGuid(),
                        "Combat Client",
                        worldId,
                        Capabilities: ClientCapabilities.None),
                    cancellationToken);
                tcpClient = await listener.AcceptTcpClientAsync(
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                tcpClient.NoDelay = true;
                var stream = tcpClient.GetStream();
                var requestMessage = await TcpFrameCodec.ReadAsync(
                        stream,
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                if (requestMessage is not HandshakeRequestMessage request)
                    throw new InvalidOperationException(
                        "the combat peer did not receive a client handshake");

                var playerId = Guid.NewGuid();
                const ulong playerEntityId = 707;
                await TcpFrameCodec.WriteAsync(
                        stream,
                        new HandshakeAcceptedMessage(
                            1,
                            450,
                            ProtocolConstants.CurrentVersion,
                            BuildVersion,
                            ContentVersion,
                            Guid.NewGuid(),
                            playerId,
                            playerEntityId,
                            worldId,
                            123456,
                            4.5f,
                            -2.25f,
                            0,
                            9090,
                            request.ClientNonce,
                            FirstCommandSequence,
                            "combat-reconnect-token",
                            0,
                            20,
                            ServerCapabilities.None),
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                await connect.WaitAsync(Timeout, cancellationToken);
                return new CombatPeer(
                    listener,
                    tcpClient,
                    stream,
                    playerId,
                    playerEntityId);
            }
            catch
            {
                tcpClient?.Dispose();
                listener.Stop();
                throw;
            }
        }

        public async ValueTask SendAsync(
            IProtocolMessage message,
            CancellationToken cancellationToken) =>
            await TcpFrameCodec.WriteAsync(_stream, message, cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);

        public async ValueTask<IProtocolMessage> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            var message = await TcpFrameCodec.ReadAsync(
                    _stream,
                    cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);
            return message ?? throw new EndOfStreamException(
                "the client closed before sending the expected combat frame");
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _tcpClient.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
