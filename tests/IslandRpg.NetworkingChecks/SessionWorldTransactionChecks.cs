using System.Numerics;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class SessionWorldTransactionChecks
{
    private const string Log = "logs";

    public static void Register(CheckRunner checks)
    {
        checks.Add("session world pickup commits once and replays its receipt",
            PickupCommitsOnceAndReplays);
        checks.Add("session world actions reject foreign stale and distant actors",
            ForeignStaleAndRangeAreRejected);
        checks.Add("session container open is private and read only",
            OpenContainerIsPrivateAndReadOnly);
        checks.Add("session drop and placement require current chunk revisions",
            DropAndPlacementRequireChunkRevisions);
        checks.Add("session construction rejects impassable terrain",
            ConstructionRejectsImpassableTerrain);
        checks.Add("session checkpoint restores exact durable authority state",
            CheckpointRestoresExactState);
        checks.Add("session checkpoint rejects invalid player survival state",
            CheckpointRejectsInvalidGameplay);
        checks.Add("session checkpoint preserves command idempotency receipts",
            CheckpointPreservesCommandReceipts);
        checks.Add("session campfire cooking is timed atomic and durable",
            CampfireCookingIsTimedAtomicAndDurable);
    }

    private static void PickupCommitsOnceAndReplays()
    {
        var session = NewSession();
        var worldObject = session.SeedWorldObject(new(
            Guid.Parse("81000000-0000-0000-0000-000000000001"),
            Log, new Vector2(1, 0)));
        var chunkRevision = session.CaptureWorldChunkRevision(worldObject.Chunk);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Alys", Vector2.Zero);
        var intent = new PickUpWorldObjectIntent(
            Guid.Parse("82000000-0000-0000-0000-000000000001"),
            joined.Gameplay.Inventory.Revision,
            joined.Gameplay.ActorRevision,
            Handle(worldObject, chunkRevision));

        var first = Send(session, connection, joined, 1, intent);
        CheckAssert.True(first.Accepted,
            "a nearby portable object should be picked up");
        CheckAssert.True(first.WorldTransaction is { Accepted: true },
            "the immutable world receipt must be exposed");
        CheckAssert.Equal(WorldObjectChangeKind.Removed,
            first.WorldTransaction!.ObjectDeltas.Single().Kind,
            "pickup must publish one public removal delta");
        CheckAssert.Equal(1, Count(first.Gameplay, Log),
            "pickup must commit the item into session state");
        CheckAssert.Equal(2U, first.InventoryRevision,
            "pickup must advance inventory once");
        CheckAssert.Equal(2U, first.ActorRevision,
            "pickup must advance actor state once");
        var committed = session.CaptureSnapshot().Actors.Single().Gameplay;

        var replay = Send(session, connection, joined, 2, intent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "an identical command must replay its session receipt");
        CheckAssert.True(ReferenceEquals(first.WorldTransaction,
                replay.WorldTransaction),
            "the original world receipt must be replayed");
        AssertGameplayEqual(committed,
            session.CaptureSnapshot().Actors.Single().Gameplay,
            "replay must not commit actor state twice");
        CheckAssert.Equal(chunkRevision + 1,
            session.CaptureWorldChunkRevision(worldObject.Chunk),
            "replay must not advance the chunk twice");

        var conflict = Send(session, connection, joined, 3, intent with
        {
            Object = intent.Object with
            {
                ExpectedObjectRevision =
                    intent.Object.ExpectedObjectRevision + 1
            }
        });
        CheckAssert.Equal(IntentStatus.CommandIdConflict, conflict.Status,
            "one command id cannot bind a different world payload");
        AssertGameplayEqual(committed,
            session.CaptureSnapshot().Actors.Single().Gameplay,
            "a payload conflict must be mutation free");
    }

    private static void ForeignStaleAndRangeAreRejected()
    {
        var session = NewSession();
        var firstConnection = ClientConnectionId.New();
        var first = Join(session, firstConnection, "Eadric", Vector2.Zero);
        var secondConnection = ClientConnectionId.New();
        Join(session, secondConnection, "Mabel", new Vector2(0, 1));
        var nearby = session.SeedWorldObject(new(
            Guid.Parse("83000000-0000-0000-0000-000000000001"),
            Log, new Vector2(1, 0), OwnerId: "another-actor"));
        var nearRevision = session.CaptureWorldChunkRevision(nearby.Chunk);
        var before = first.Gameplay;
        var command = new PickUpWorldObjectIntent(
            Guid.Parse("84000000-0000-0000-0000-000000000001"),
            before.Inventory.Revision, before.ActorRevision,
            Handle(nearby, nearRevision));

        var foreignPending = session.EnqueueIntentAsync(new ActorCommand(
            secondConnection, first.Identity.PlayerId, 1, command));
        session.Drain();
        CheckAssert.Equal(IntentStatus.InvalidConnection,
            foreignPending.GetAwaiter().GetResult().Status,
            "a foreign connection cannot mutate another actor");

        var denied = Send(session, firstConnection, first, 1, command);
        CheckAssert.Equal(IntentStatus.AccessDenied, denied.Status,
            "foreign object ownership must be enforced");
        var stale = Send(session, firstConnection, first, 2, command with
        {
            CommandId = Guid.Parse(
                "84000000-0000-0000-0000-000000000002"),
            Object = command.Object with
            {
                ExpectedObjectRevision =
                    command.Object.ExpectedObjectRevision + 1
            }
        });
        CheckAssert.Equal(IntentStatus.StaleObjectRevision, stale.Status,
            "stale object revision detail must survive session routing");
        CheckAssert.Equal(WorldTransactionStatus.StaleObjectRevision,
            stale.WorldTransaction!.Status,
            "the aggregate rejection must remain available");

        var farObject = session.SeedWorldObject(new(
            Guid.Parse("83000000-0000-0000-0000-000000000002"),
            Log, new Vector2(10, 0)));
        var distant = Send(session, firstConnection, first, 3,
            new PickUpWorldObjectIntent(
                Guid.Parse("84000000-0000-0000-0000-000000000003"),
                before.Inventory.Revision, before.ActorRevision,
                Handle(farObject,
                    session.CaptureWorldChunkRevision(farObject.Chunk))));
        CheckAssert.Equal(IntentStatus.OutOfRange, distant.Status,
            "interaction range must use authoritative actor position");
        AssertGameplayEqual(before,
            Actor(session, first.Identity.PlayerId).Gameplay,
            "all rejected requests must preserve actor state");
    }

    private static void OpenContainerIsPrivateAndReadOnly()
    {
        var session = NewSession();
        var chest = session.SeedWorldObject(new(
            Guid.Parse("85000000-0000-0000-0000-000000000001"),
            "storage_chest", new Vector2(1, 0),
            ContainerItems: [(Log, 2, null)]));
        var revision = session.CaptureWorldChunkRevision(chest.Chunk);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Joan", Vector2.Zero);
        var before = joined.Gameplay;
        var result = Send(session, connection, joined, 1,
            new OpenWorldContainerIntent(
                Guid.Parse("86000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision, before.ActorRevision,
                Handle(chest, revision)));

        CheckAssert.True(result.Accepted,
            "accessible storage should open");
        var receipt = result.WorldTransaction!;
        CheckAssert.Equal(0, receipt.ObjectDeltas.Length,
            "open must not publish an object mutation");
        CheckAssert.Equal(0, receipt.ChunkDeltas.Length,
            "open must not advance the chunk");
        CheckAssert.True(receipt.Container is not null,
            "requester-only container state must be present");
        CheckAssert.Equal(chest.Chunk, receipt.Container!.Chunk,
            "private container state must identify its chunk");
        CheckAssert.Equal(revision, receipt.Container.ChunkRevision,
            "private container state must carry current chunk revision");
        CheckAssert.Equal(2, receipt.Container.Slots.Where(slot =>
                slot.ItemId == Log).Sum(slot => slot.Quantity),
            "private state must include container contents");
        CheckAssert.Equal(before.ActorRevision, result.ActorRevision,
            "open must not advance actor revision");
        CheckAssert.Equal(before.Inventory.Revision, result.InventoryRevision,
            "open must not advance inventory revision");
        AssertGameplayEqual(before,
            session.CaptureSnapshot().Actors.Single().Gameplay,
            "open must leave actor state unchanged");
    }

    private static void DropAndPlacementRequireChunkRevisions()
    {
        var ids = new Queue<Guid>(
        [
            Guid.Parse("87000000-0000-0000-0000-000000000001"),
            Guid.Parse("87000000-0000-0000-0000-000000000002")
        ]);
        var session = NewSession(new AuthoritativeWorldTransactions(
            () => ids.Dequeue()));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Wulfric", Vector2.Zero,
            [
                new InitialInventoryItem(Log, 2),
                new InitialInventoryItem("stone_hammer", 1)
            ]);
        var position = new Vector2(1, 0);
        var before = joined.Gameplay;

        var staleDrop = Send(session, connection, joined, 1,
            new DropInventoryItemIntent(
                Guid.Parse("88000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision, before.ActorRevision,
                0, 1, position, 0, 1));
        CheckAssert.Equal(IntentStatus.StaleChunkRevision, staleDrop.Status,
            "drop must reject a stale target chunk");
        CheckAssert.Equal(2, Count(staleDrop.Gameplay, Log),
            "stale drop must not consume inventory");

        var dropped = Send(session, connection, joined, 2,
            new DropInventoryItemIntent(
                Guid.Parse("88000000-0000-0000-0000-000000000002"),
                before.Inventory.Revision, before.ActorRevision,
                0, 1, position, 0, 0));
        CheckAssert.True(dropped.Accepted,
            "drop should accept the current chunk revision");
        CheckAssert.Equal(1U,
            dropped.WorldTransaction!.ChunkDeltas.Single().CurrentRevision,
            "drop must advance its chunk once");

        var stalePlace = Send(session, connection, joined, 3,
            new PlaceConstructionIntent(
                Guid.Parse("88000000-0000-0000-0000-000000000003"),
                dropped.InventoryRevision, dropped.ActorRevision,
                "wooden_wall", position, 0, 3, 0));
        CheckAssert.Equal(IntentStatus.StaleChunkRevision, stalePlace.Status,
            "placement must reject the pre-drop chunk revision");
        CheckAssert.Equal(1, Count(stalePlace.Gameplay, Log),
            "stale placement must preserve its resource");

        var placed = Send(session, connection, joined, 4,
            new PlaceConstructionIntent(
                Guid.Parse("88000000-0000-0000-0000-000000000004"),
                dropped.InventoryRevision, dropped.ActorRevision,
                "wooden_wall", position, 0, 3, 1));
        CheckAssert.True(placed.Accepted,
            "placement should accept the current chunk revision");
        CheckAssert.Equal(2U,
            placed.WorldTransaction!.ChunkDeltas.Single().CurrentRevision,
            "placement must advance the chunk once");
        CheckAssert.Equal(0, Count(placed.Gameplay, Log),
            "placement must atomically consume one log");
    }

    private static void ConstructionRejectsImpassableTerrain()
    {
        var blocked = new BlockedPlacementNavigationQuery();
        var session = new AuthoritativeWorldSession(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.Parse(
                "89000000-0000-0000-0000-000000000001")),
            navigation: blocked);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Maud", Vector2.Zero,
            [new InitialInventoryItem(Log, 1)]);
        var before = joined.Gameplay;

        var result = Send(session, connection, joined, 1,
            new PlaceConstructionIntent(
                Guid.Parse("88000000-0000-0000-0000-000000000005"),
                before.Inventory.Revision,
                before.ActorRevision,
                "wooden_wall",
                BlockedPlacementNavigationQuery.BlockedPoint,
                0,
                0,
                0));

        CheckAssert.Equal(IntentStatus.InvalidPlacement, result.Status,
            "the server must reject construction on impassable terrain");
        CheckAssert.Equal(1, Count(result.Gameplay, Log),
            "invalid terrain must not consume construction resources");
        CheckAssert.Equal(0U, session.CaptureWorldChunkRevision(
                WorldChunkKey.At(BlockedPlacementNavigationQuery.BlockedPoint, 0)),
            "invalid terrain must not mutate its chunk");
    }

    private static void CheckpointRestoresExactState()
    {
        var session = NewSession();
        var chest = session.SeedWorldObject(new(
            Guid.Parse("8c000000-0000-0000-0000-000000000001"),
            "storage_chest", new Vector2(1, 0),
            ObjectRevision: 7, ContainerRevision: 5,
            ContainerItems: [("slime_gel", 4, "group-a")]));
        var gate = session.SeedWorldObject(new(
            Guid.Parse("8c000000-0000-0000-0000-000000000002"),
            "gate_8185", new Vector2(33, 0),
            ObjectRevision: 9, ContainerRevision: 1,
            GateState: WorldGateAccessState.Locked));
        CheckAssert.Throws<ArgumentException>(() =>
                session.SeedWorldObject(new(
                    Guid.Parse("8c000000-0000-0000-0000-000000000003"),
                    Log,
                    new Vector2(2, 0),
                    GateState: WorldGateAccessState.Locked)),
            "non-gate seeds must reject gate state rather than erase it");
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Edith", Vector2.Zero,
            [new InitialInventoryItem(Log, 1)]);
        for (var tick = 0; tick < 9; tick++) session.Tick();

        var checkpoint = session.CaptureCheckpoint();
        CheckAssert.Equal(32, checkpoint.Actors.Single()
                .ReconnectTokenHash.Length,
            "checkpoint must preserve the token hash, never the bearer token");
        CheckAssert.False(checkpoint.Actors.Single().ToString()
                .Contains("session-world-secret", StringComparison.Ordinal),
            "checkpoint actor logging must redact reconnect credentials");

        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var roundTrip = restored.CaptureCheckpoint();
        CheckAssert.Equal(checkpoint.Tick, roundTrip.Tick,
            "restore must preserve the exact fixed tick");
        CheckAssert.Equal(checkpoint.SnapshotSequence,
            roundTrip.SnapshotSequence,
            "restore must preserve the exact snapshot sequence");
        CheckAssert.SequenceEqual(
            checkpoint.Actors.Single().ReconnectTokenHash,
            roundTrip.Actors.Single().ReconnectTokenHash,
            "restore must preserve reconnect token hashes exactly");
        AssertGameplayEqual(checkpoint.Actors.Single().Gameplay,
            roundTrip.Actors.Single().Gameplay,
            "restore must preserve actor gameplay");
        CheckAssert.Equal(checkpoint.Tick,
            roundTrip.Actors.Single().DisconnectedAtTick,
            "actors connected at save time must restore with a restart disconnect tick");

        var restoredChest = restored.CaptureWorldObject(chest.ObjectId);
        CheckAssert.Equal(7U, restoredChest.ObjectRevision,
            "restore must not increment object revision");
        CheckAssert.Equal(5U, restoredChest.ContainerRevision,
            "restore must not increment container revision");
        var restoredGate = restored.CaptureWorldObject(gate.ObjectId);
        CheckAssert.Equal(9U, restoredGate.ObjectRevision,
            "gate object revision must restore exactly");
        CheckAssert.Equal(WorldGateAccessState.Locked,
            restoredGate.GateState,
            "gate access state must survive restart");
        foreach (var chunk in checkpoint.World.ChunkRevisions)
            CheckAssert.Equal(chunk.Revision,
                restored.CaptureWorldChunkRevision(chunk.Chunk),
                "chunk revisions must restore without AddObject increments");

        var reconnectConnection = ClientConnectionId.New();
        var reconnect = restored.EnqueueReconnectAsync(new ReconnectRequest(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "restored token hash must authenticate the original bearer token");
        CheckAssert.Equal(joined.Identity,
            reconnect.GetAwaiter().GetResult().Identity,
            "restart must preserve stable player and actor identities");
    }

    private static void CheckpointRejectsInvalidGameplay()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        Join(session, connection, "Rohese", Vector2.Zero);
        var checkpoint = session.CaptureCheckpoint();
        var actor = checkpoint.Actors.Single();
        var invalid = checkpoint with
        {
            Actors = [actor with
            {
                Gameplay = actor.Gameplay with { Hunger = float.NaN }
            }]
        };

        var restored = NewSession();
        CheckAssert.Throws<InvalidOperationException>(
            () => restored.RestoreCheckpoint(invalid),
            "restore must reject non-finite survival state before committing authority");
        CheckAssert.Equal(0, restored.ActorCount,
            "a rejected checkpoint must leave a pristine session unchanged");
    }

    private static void CheckpointPreservesCommandReceipts()
    {
        var session = NewSession();
        var worldObject = session.SeedWorldObject(new(
            Guid.Parse("8d000000-0000-0000-0000-000000000001"),
            Log, new Vector2(1, 0)));
        var originalChunkRevision =
            session.CaptureWorldChunkRevision(worldObject.Chunk);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Agnes", Vector2.Zero);
        var commandId =
            Guid.Parse("8e000000-0000-0000-0000-000000000001");
        var intent = new PickUpWorldObjectIntent(
            commandId,
            joined.Gameplay.Inventory.Revision,
            joined.Gameplay.ActorRevision,
            Handle(worldObject, originalChunkRevision));
        var accepted = Send(session, connection, joined, 1, intent);
        CheckAssert.True(accepted.Accepted && !accepted.Duplicate,
            "fixture pickup must commit exactly once before checkpointing");

        var checkpoint = session.CaptureCheckpoint();
        CheckAssert.Equal(1,
            checkpoint.Actors.Single().CommandReceipts.Length,
            "accepted command must be captured in bounded durable history");
        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var restoredConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(
            new ReconnectRequest(
                restoredConnection,
                joined.Identity.PlayerId,
                joined.ReconnectToken));
        restored.Drain();
        var reconnect = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnect.Accepted,
            "fixture actor must reconnect after restore");
        var beforeRetry = Actor(restored, joined.Identity.PlayerId).Gameplay;
        var chunkBeforeRetry =
            restored.CaptureWorldChunkRevision(worldObject.Chunk);

        var replayPending = restored.EnqueueIntentAsync(new ActorCommand(
            restoredConnection,
            joined.Identity.PlayerId,
            2,
            intent));
        restored.Drain();
        var replay = replayPending.GetAwaiter().GetResult();
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "a response-lost command must replay as accepted after restart");
        CheckAssert.True(replay.WorldTransaction is null,
            "a restored tombstone must not replay stale public deltas");
        AssertGameplayEqual(beforeRetry, replay.Gameplay,
            "restored duplicate must report current authoritative gameplay");
        AssertGameplayEqual(beforeRetry,
            Actor(restored, joined.Identity.PlayerId).Gameplay,
            "restored duplicate must not mutate inventory twice");
        CheckAssert.Equal(chunkBeforeRetry,
            restored.CaptureWorldChunkRevision(worldObject.Chunk),
            "restored duplicate must not mutate its chunk twice");

        var conflictPending = restored.EnqueueIntentAsync(new ActorCommand(
            restoredConnection,
            joined.Identity.PlayerId,
            3,
            intent with
            {
                Object = intent.Object with
                {
                    ExpectedObjectRevision =
                        intent.Object.ExpectedObjectRevision + 1
                }
            }));
        restored.Drain();
        var conflict = conflictPending.GetAwaiter().GetResult();
        CheckAssert.Equal(IntentStatus.CommandIdConflict, conflict.Status,
            "a restored command id must reject a different payload");
        AssertGameplayEqual(beforeRetry,
            Actor(restored, joined.Identity.PlayerId).Gameplay,
            "restored command conflict must not mutate authority");
    }

    private static AuthoritativeWorldSession NewSession(
        AuthoritativeWorldTransactions? aggregate = null) => new(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.Parse(
                "89000000-0000-0000-0000-000000000001")),
            worldTransactions: aggregate);

    private static void CampfireCookingIsTimedAtomicAndDurable()
    {
        var session = NewSession();
        var campfire = session.SeedWorldObject(new(
            Guid.Parse("8c000000-0000-0000-0000-000000000001"),
            "campfire", new Vector2(1, 0),
            FuelItemId: "logs", LitUntilGameSeconds: 300));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Cook", Vector2.Zero,
            [new InitialInventoryItem("raw_minnows")]);
        var rawSlot = joined.Gameplay.Inventory.Slots.Single(
            value => value.ItemId == "raw_minnows").Slot;
        var commandId = Guid.Parse(
            "8d000000-0000-0000-0000-000000000001");
        var intent = new CookOnCampfireIntent(
                commandId,
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                Handle(campfire,
                    session.CaptureWorldChunkRevision(campfire.Chunk)),
                rawSlot);
        var start = Send(session, connection, joined, 1, intent);
        CheckAssert.True(start.Accepted,
            "a nearby lit fire must accept a level-one raw fish");
        CheckAssert.Equal(0, Count(start.Gameplay, "raw_minnows"),
            "acceptance must atomically reserve the raw item");
        CheckAssert.Equal(1, session.CaptureCheckpoint().CookingJobs.Length,
            "the active timed job must be durable");
        var replay = Send(session, connection, joined, 2, intent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "retrying the same cook command must replay without reserving twice");
        CheckAssert.Equal(1, session.CaptureCheckpoint().CookingJobs.Length,
            "a duplicate command must not create a second cooking job");
        var conflict = Send(session, connection, joined, 3,
            intent with { InventorySlot = rawSlot + 1 });
        CheckAssert.Equal(IntentStatus.CommandIdConflict, conflict.Status,
            "reusing a cook command id for another payload must conflict");
        CheckAssert.Equal(0, Count(conflict.Gameplay, "raw_minnows"),
            "replay and conflict handling must not restore or consume another item");

        var checkpoint = session.CaptureCheckpoint();
        var invalidOutcome = NewSession();
        CheckAssert.Throws<InvalidDataException>(
            () => invalidOutcome.RestoreCheckpoint(checkpoint with
            {
                CookingJobs = [checkpoint.CookingJobs[0] with
                {
                    Experience = checkpoint.CookingJobs[0].Experience + 1
                }]
            }),
            "restore must reject cooking outcomes that do not match the deterministic roll");
        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        for (var tick = checkpoint.Tick;
             tick < checkpoint.CookingJobs[0].CompletesAtTick;
             tick++)
            restored.Tick();
        var completed = Actor(restored, joined.Identity.PlayerId).Gameplay;
        CheckAssert.Equal(1,
            Count(completed, "cooked_minnows") +
            Count(completed, "burnt_minnows"),
            "restored authority must complete exactly one deterministic output");
        CheckAssert.Equal(0, restored.CaptureCheckpoint().CookingJobs.Length,
            "a completed job must leave durable active state");
        CheckAssert.True(completed.CookingExperience >= 0,
            "only the authority may award cooking experience");
    }

    private static JoinResult Join(AuthoritativeWorldSession session,
        ClientConnectionId connection, string name, Vector2 position,
        IReadOnlyList<InitialInventoryItem>? inventory = null)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection, name, position, inventory));
        session.Drain();
        var result = pending.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "test actor must join");
        return result;
    }

    private static IntentResult Send(AuthoritativeWorldSession session,
        ClientConnectionId connection, JoinResult joined, long sequence,
        SessionIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, sequence, intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static ActorSnapshot Actor(AuthoritativeWorldSession session,
        PlayerId playerId) => session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == playerId);

    private static WorldObjectHandle Handle(
        AuthoritativeWorldObjectSnapshot value, uint chunkRevision) => new(
            value.ObjectId, value.Chunk, value.ObjectRevision, chunkRevision,
            value.ContainerRevision);

    private static int Count(PlayerGameplaySnapshot gameplay, string itemId) =>
        gameplay.Inventory.Slots.Sum(slot =>
            string.Equals(slot.ItemId, itemId,
                StringComparison.OrdinalIgnoreCase) ? slot.Quantity : 0);

    private static void AssertGameplayEqual(
        PlayerGameplaySnapshot expected,
        PlayerGameplaySnapshot actual,
        string message)
    {
        CheckAssert.Equal(expected.ActorRevision, actual.ActorRevision, message);
        CheckAssert.Equal(expected.Health, actual.Health, message);
        CheckAssert.Equal(expected.Hunger, actual.Hunger, message);
        CheckAssert.Equal(expected.WellFedSeconds, actual.WellFedSeconds, message);
        CheckAssert.Equal(expected.CraftingExperience,
            actual.CraftingExperience, message);
        CheckAssert.Equal(expected.CookingExperience,
            actual.CookingExperience, message);
        CheckAssert.Equal(expected.Inventory.Revision,
            actual.Inventory.Revision, message);
        CheckAssert.SequenceEqual(expected.Inventory.Slots,
            actual.Inventory.Slots, message);
    }

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        private int _next;

        public PlayerIdentity CreatePlayerIdentity()
        {
            var index = ++_next;
            return new PlayerIdentity(
                new PlayerId(Guid.Parse(
                    $"8a000000-0000-0000-0000-{index:D12}")),
                new ActorId(Guid.Parse(
                    $"8b000000-0000-0000-0000-{index:D12}")));
        }

        public ReconnectToken CreateReconnectToken() =>
            new($"session-world-secret-{_next}");
    }

    private sealed class BlockedPlacementNavigationQuery :
        IslandRpg.Navigation.IWorldNavigationQuery
    {
        public static readonly Vector2 BlockedPoint = new(1, 0);

        public bool SupportsWorldLevel(int worldLevel) => worldLevel == 0;

        public bool CanStandAt(Vector2 point, int worldLevel) =>
            worldLevel == 0 && point != BlockedPoint;

        public float HeightAt(Vector2 point, int worldLevel) => 0;

        public bool IsWading(Vector2 point, int worldLevel) => false;
    }
}
