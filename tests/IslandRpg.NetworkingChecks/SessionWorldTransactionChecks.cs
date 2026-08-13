using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class SessionWorldTransactionChecks
{
    private const string Log = "logs";

    public static void Register(CheckRunner checks)
    {
        checks.Add("session world pickup commits once and replays its receipt",
            PickupCommitsOnceAndReplays);
        checks.Add(
            "aggregate replay after session receipt eviction is tombstoned",
            AggregateReplayAfterSessionReceiptEvictionIsTombstoned);
        checks.Add("session world actions reject foreign stale and distant actors",
            ForeignStaleAndRangeAreRejected);
        checks.Add("session container open is private and read only",
            OpenContainerIsPrivateAndReadOnly);
        checks.Add("session drop and placement require current chunk revisions",
            DropAndPlacementRequireChunkRevisions);
        checks.Add("session construction rejects impassable terrain",
            ConstructionRejectsImpassableTerrain);
        checks.Add(
            "session construction rejects unsafe footprint boundaries",
            ConstructionRejectsUnsafeFootprintBoundaries);
        checks.Add("session furniture rejects water and steep terrain",
            FurnitureRejectsWaterAndSteepTerrain);
        checks.Add("session furniture enables only nearby station crafting",
            FurnitureEnablesNearbyStationCrafting);
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

    private static void AggregateReplayAfterSessionReceiptEvictionIsTombstoned()
    {
        var session = NewSession();
        var material = session.SeedWorldObject(new(
            Guid.Parse("81100000-0000-0000-0000-000000000001"),
            ItemIds.LargeRock,
            new Vector2(1, 0)));
        var chest = session.SeedWorldObject(new(
            Guid.Parse("81100000-0000-0000-0000-000000000002"),
            ItemIds.StorageChest,
            new Vector2(1, 0)));
        var connection = ClientConnectionId.New();
        var joined = Join(
            session,
            connection,
            "Archivist",
            Vector2.Zero,
            [
                new InitialInventoryItem(ItemIds.LargeRock, 4),
                new InitialInventoryItem(ItemIds.Sticks, 2),
                new InitialInventoryItem(ItemIds.PlantFibres, 2),
                new InitialInventoryItem(ItemIds.WildGrainSeeds)
            ]);
        var committedTransactions = 0;
        session.WorldTransactionCommitted += _ => committedTransactions++;
        var original = new PickUpWorldObjectIntent(
            Guid.Parse("82100000-0000-0000-0000-000000000001"),
            joined.Gameplay.Inventory.Revision,
            joined.Gameplay.ActorRevision,
            Handle(
                material,
                session.CaptureWorldChunkRevision(material.Chunk)));
        var first = Send(session, connection, joined, 1, original);
        CheckAssert.True(first.Accepted,
            "the fixture pickup must commit");
        CheckAssert.Equal(50, first.Gameplay.AdventureExperience,
            "held starter materials plus the pickup must complete quest one");

        var crafted = Send(
            session,
            connection,
            joined,
            2,
            new CraftRecipeIntent(
                Guid.Parse("82100000-0000-0000-0000-000000000002"),
                first.InventoryRevision,
                first.ActorRevision,
                "medium-rock"));
        CheckAssert.True(crafted.Accepted,
            "the fixture craft must advance state beyond the cached pickup");
        CheckAssert.Equal(2,
            crafted.Gameplay.Quests[1].ObjectiveCounts!
                .GetValueOrDefault("medium-rocks"),
            "the post-pickup state must carry newer quest progress");

        var cropPosition = new Vector2(.5f, 1.5f);
        var seedSlot = crafted.Gameplay.Inventory.Slots.Single(value =>
            value.ItemId == ItemIds.WildGrainSeeds).Slot;
        var planted = Send(
            session,
            connection,
            joined,
            3,
            new PlantCropIntent(
                Guid.Parse("82100000-0000-0000-0000-000000000003"),
                crafted.InventoryRevision,
                crafted.ActorRevision,
                seedSlot,
                cropPosition,
                0,
                session.CaptureWorldChunkRevision(
                    WorldChunkKey.At(cropPosition, 0))));
        CheckAssert.True(planted.Accepted,
            "the fixture crop must advance aggregate action XP");
        CheckAssert.True(
            planted.Gameplay.AdventureExperience >
            first.Gameplay.AdventureExperience,
            "the post-pickup state must carry newer Adventure XP");

        var chestHandle = Handle(
            chest,
            session.CaptureWorldChunkRevision(chest.Chunk));
        for (var index = 0; index < 254; index++)
        {
            var opened = Send(
                session,
                connection,
                joined,
                index + 4L,
                new OpenWorldContainerIntent(
                    Guid.Parse($"83100000-0000-0000-0000-{index + 1:D12}"),
                    planted.InventoryRevision,
                    planted.ActorRevision,
                    chestHandle));
            CheckAssert.True(opened.Accepted,
                "the read-only command must fill the bounded receipt history");
        }

        var beforeReplay = Actor(session, joined.Identity.PlayerId).Gameplay;
        var chunkBeforeReplay =
            session.CaptureWorldChunkRevision(material.Chunk);
        var checkpoint = session.CaptureCheckpoint();
        CheckAssert.Equal(256,
            checkpoint.Actors.Single().CommandReceipts.Length,
            "the durable session receipt history must remain bounded");
        CheckAssert.False(
            checkpoint.Actors.Single().CommandReceipts.Any(value =>
                value.CommandId == original.CommandId),
            "the original pickup must be evicted from the session receipt history");

        var conflict = Send(
            session,
            connection,
            joined,
            258,
            original with
            {
                Object = original.Object with
                {
                    ExpectedObjectRevision =
                        original.Object.ExpectedObjectRevision + 1
                }
            });
        CheckAssert.Equal(IntentStatus.CommandIdConflict, conflict.Status,
            "the longer-lived aggregate receipt must reject a changed payload");

        var replay = Send(session, connection, joined, 259, original);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "an exact aggregate-level replay must become a session duplicate");
        CheckAssert.True(replay.WorldTransaction is null,
            "an evicted aggregate receipt must replay as a stale-effect tombstone");
        CheckAssert.Equal(beforeReplay.ActorRevision, replay.ActorRevision,
            "aggregate replay must report the current actor revision");
        CheckAssert.Equal(beforeReplay.Inventory.Revision,
            replay.InventoryRevision,
            "aggregate replay must report the current inventory revision");
        CheckAssert.Equal(beforeReplay.AdventureExperience,
            replay.Gameplay.AdventureExperience,
            "aggregate replay must not rewind Adventure XP");
        CheckAssert.Equal(beforeReplay.CraftingExperience,
            replay.Gameplay.CraftingExperience,
            "aggregate replay must not rewind skill XP");
        CheckAssert.SequenceEqual(beforeReplay.Quests, replay.Gameplay.Quests,
            "aggregate replay must not rewind or reapply quest progress");
        CheckAssert.SequenceEqual(beforeReplay.Inventory.Slots,
            replay.Gameplay.Inventory.Slots,
            "aggregate replay must not rewind inventory contents");
        CheckAssert.Equal(chunkBeforeReplay,
            session.CaptureWorldChunkRevision(material.Chunk),
            "aggregate replay must not advance or rewind world revisions");
        CheckAssert.Equal(2, committedTransactions,
            "aggregate replay must not rebroadcast its public world delta");

        var secondCraft = Send(
            session,
            connection,
            joined,
            260,
            new CraftRecipeIntent(
                Guid.Parse("82100000-0000-0000-0000-000000000004"),
                replay.InventoryRevision,
                replay.ActorRevision,
                "medium-rock"));
        CheckAssert.True(secondCraft.Accepted,
            "the fixture must advance state after caching the tombstone");
        var replayFromTombstone = Send(
            session, connection, joined, 261, original);
        CheckAssert.True(
            replayFromTombstone.Accepted && replayFromTombstone.Duplicate,
            "a reinserted aggregate tombstone must remain idempotent");
        CheckAssert.True(replayFromTombstone.WorldTransaction is null,
            "a reinserted tombstone must never regain stale world effects");
        CheckAssert.Equal(secondCraft.ActorRevision,
            replayFromTombstone.ActorRevision,
            "a cached tombstone must report the newest actor revision");
        CheckAssert.Equal(secondCraft.InventoryRevision,
            replayFromTombstone.InventoryRevision,
            "a cached tombstone must report the newest inventory revision");
        CheckAssert.Equal(secondCraft.Gameplay.AdventureExperience,
            replayFromTombstone.Gameplay.AdventureExperience,
            "a cached tombstone must report the newest Adventure XP");
        CheckAssert.SequenceEqual(secondCraft.Gameplay.Quests,
            replayFromTombstone.Gameplay.Quests,
            "a cached tombstone must report the newest quest progress");
        CheckAssert.Equal(2, committedTransactions,
            "cached tombstone replay must not rebroadcast world deltas");

        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var restoredPublications = 0;
        restored.WorldTransactionCommitted += _ => restoredPublications++;
        var reconnectConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        var reconnect = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnect.Accepted,
            "the evicted-receipt checkpoint owner must reconnect");
        var afterRestart = Send(
            restored,
            reconnectConnection,
            joined,
            reconnect.NextCommandSequence,
            original);
        CheckAssert.Equal(IntentStatus.StaleActorRevision,
            afterRestart.Status,
            "an old evicted mutation must fail safely after aggregate restart");
        CheckAssert.SequenceEqual(beforeReplay.Inventory.Slots,
            afterRestart.Gameplay.Inventory.Slots,
            "restart retry must preserve current inventory");
        CheckAssert.SequenceEqual(beforeReplay.Quests,
            afterRestart.Gameplay.Quests,
            "restart retry must preserve current quest progress");
        CheckAssert.Equal(0, restoredPublications,
            "restart retry must not publish an old world delta");
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

    private static void ConstructionRejectsUnsafeFootprintBoundaries()
    {
        const string gateId = "gate_8185";
        var diagonalPlacement = new Vector2(2, 2);
        CheckAssert.True(PlaceableWorldObjectRules.TryGetCollision(
                gateId, out var gateDefinition),
            "the gate fixture must use canonical collision geometry");
        CheckAssert.Equal(
            new Vector2(4, 1),
            PlaceableWorldObjectRules.PlacementFootprint(
                gateDefinition, 0),
            "gate rotation zero must reserve four-by-one clearance");
        CheckAssert.Equal(
            new Vector2(1, 4),
            PlaceableWorldObjectRules.PlacementFootprint(
                gateDefinition, 1),
            "gate rotation one must reserve one-by-four clearance");
        CheckAssert.Equal(
            new Vector2(4, 4),
            PlaceableWorldObjectRules.PlacementFootprint(
                gateDefinition, 2),
            "diagonal gates must reserve the client's complete four-by-four footprint");
        CheckAssert.Equal(
            new Vector2(4, 4),
            PlaceableWorldObjectRules.PlacementFootprint(
                gateDefinition, 3),
            "both diagonal gates must reserve four-by-four clearance");
        var diagonalCollisionBounds =
            PlaceableWorldObjectRules.CollisionBounds(
                gateDefinition, diagonalPlacement, 2);
        var diagonalPlacementBounds =
            PlaceableWorldObjectRules.PlacementBounds(
                gateDefinition, diagonalPlacement, 2);
        CheckAssert.True(
            diagonalCollisionBounds.Minimum.X >
            diagonalPlacementBounds.Minimum.X,
            "the regression hazard must lie outside navigation contact but inside placement clearance");

        var waterSession = new AuthoritativeWorldSession(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.Parse(
                "89100000-0000-0000-0000-000000000001")),
            navigation: new EdgeWaterNavigationQuery(maximumWaterX: 1f));
        var waterConnection = ClientConnectionId.New();
        var waterJoined = Join(
            waterSession, waterConnection, "Gate Surveyor", Vector2.Zero,
            [
                new InitialInventoryItem(Log, 4),
                new InitialInventoryItem("stone_hammer")
            ]);
        var waterResult = Send(
            waterSession,
            waterConnection,
            waterJoined,
            waterJoined.NextCommandSequence,
            new PlaceConstructionIntent(
                Guid.Parse("89200000-0000-0000-0000-000000000001"),
                waterJoined.Gameplay.Inventory.Revision,
                waterJoined.Gameplay.ActorRevision,
                gateId,
                diagonalPlacement,
                0,
                2,
                0));
        CheckAssert.Equal(IntentStatus.InvalidPlacement, waterResult.Status,
            "diagonal gate placement must reject water in its outer authored edge");
        AssertRejectedConstructionUnchanged(
            waterSession, waterResult, diagonalPlacement, 4,
            "diagonal gate water-edge rejection");

        const long resourceSeed = 551_991;
        var resourceChunk = new WorldChunkKey(0, 0, 0);
        var resourceSource = new FixedResourceSource(
            resourceChunk,
            [
                new ProceduralResourceSeed(
                    ProceduralResourceKey.Tree(0, 0),
                    new Vector2(.5f, .5f),
                    InitialHealth: 100,
                    MaximumHealth: 100,
                    InitialRemaining: 1)
            ]);
        var resourceCatalog = new ProceduralResourceCatalog(resourceSource);
        var resourceSession = new AuthoritativeWorldSession(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.Parse(
                "89100000-0000-0000-0000-000000000002")),
            resourceTransactions: new AuthoritativeResourceTransactions(
                resourceSeed, resourceCatalog));
        var resourceConnection = ClientConnectionId.New();
        var resourceJoined = Join(
            resourceSession,
            resourceConnection,
            "Gate Forester",
            Vector2.Zero,
            [
                new InitialInventoryItem(Log, 4),
                new InitialInventoryItem("stone_hammer")
            ]);
        var resourceResult = Send(
            resourceSession,
            resourceConnection,
            resourceJoined,
            resourceJoined.NextCommandSequence,
            new PlaceConstructionIntent(
                Guid.Parse("89200000-0000-0000-0000-000000000002"),
                resourceJoined.Gameplay.Inventory.Revision,
                resourceJoined.Gameplay.ActorRevision,
                gateId,
                diagonalPlacement,
                0,
                2,
                0));
        CheckAssert.Equal(IntentStatus.InvalidPlacement,
            resourceResult.Status,
            "diagonal gate placement must reject a tree in its outer authored edge");
        AssertRejectedConstructionUnchanged(
            resourceSession, resourceResult, diagonalPlacement, 4,
            "diagonal gate resource-edge rejection");

        var obstaclePlacement = new Vector2(1, 1);
        var gatePieces = PlaceableWorldObjectRules.CollisionObstacles(
            gateDefinition,
            obstaclePlacement,
            0);
        var edgePiece = gatePieces.MaxBy(value =>
            Vector2.DistanceSquared(value.Center, obstaclePlacement));
        var obstacleSession = new AuthoritativeWorldSession(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.Parse(
                "89100000-0000-0000-0000-000000000003")),
            obstacles: new FixedObstacles(
            [
                new NavigationObstacle(edgePiece.Center, .2f, .2f)
            ]));
        var obstacleConnection = ClientConnectionId.New();
        var obstacleJoined = Join(
            obstacleSession,
            obstacleConnection,
            "Gate Surveyor",
            Vector2.Zero,
            [
                new InitialInventoryItem(Log, 4),
                new InitialInventoryItem("stone_hammer")
            ]);
        var obstacleResult = Send(
            obstacleSession,
            obstacleConnection,
            obstacleJoined,
            obstacleJoined.NextCommandSequence,
            new PlaceConstructionIntent(
                Guid.Parse("89200000-0000-0000-0000-000000000003"),
                obstacleJoined.Gameplay.Inventory.Revision,
                obstacleJoined.Gameplay.ActorRevision,
                gateId,
                obstaclePlacement,
                0,
                0,
                0));
        CheckAssert.Equal(IntentStatus.InvalidPlacement,
            obstacleResult.Status,
            "a clear submitted center must not hide a static gate-edge obstacle");
        AssertRejectedConstructionUnchanged(
            obstacleSession, obstacleResult, obstaclePlacement, 4,
            "obstacle-boundary gate rejection");
    }

    private static void AssertRejectedConstructionUnchanged(
        AuthoritativeWorldSession session,
        IntentResult result,
        Vector2 placement,
        int expectedLogs,
        string message)
    {
        CheckAssert.Equal(expectedLogs, Count(result.Gameplay, Log),
            $"{message} must preserve every construction material");
        CheckAssert.Equal(0, session.CaptureCheckpoint().World.Objects.Length,
            $"{message} must not create a world object");
        CheckAssert.Equal(0U, session.CaptureWorldChunkRevision(
                WorldChunkKey.At(placement, 0)),
            $"{message} must not advance the target chunk");
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

    private static void FurnitureEnablesNearbyStationCrafting()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Builder", Vector2.Zero,
        [
            new InitialInventoryItem(ItemIds.Workbench),
            new InitialInventoryItem(ItemIds.PrimitiveFishingNet),
            new InitialInventoryItem(ItemIds.Rope, 2),
            new InitialInventoryItem(ItemIds.StoneKnife)
        ]);
        (session, connection, joined) = RestartWithCraftingLevel(
            session, joined, 6);
        var before = joined.Gameplay;
        var sequence = joined.NextCommandSequence;
        var locked = Send(session, connection, joined, sequence++,
            new CraftRecipeIntent(
                Guid.NewGuid(), before.Inventory.Revision,
                before.ActorRevision, "reinforced-fishing-net"));
        CheckAssert.Equal(IntentStatus.MissingStation, locked.Status,
            "station recipes must reject before an authoritative station exists");

        var position = new Vector2(1, 1.5f);
        var workbenchSlot = before.Inventory.Slots.Single(value =>
            value.ItemId == ItemIds.Workbench).Slot;
        var placed = Send(session, connection, joined, sequence++,
            new PlaceInventoryWorldObjectIntent(
                Guid.NewGuid(), before.Inventory.Revision,
                before.ActorRevision, ItemIds.Workbench, workbenchSlot,
                position, 0, 0,
                session.CaptureWorldChunkRevision(
                    WorldChunkKey.At(position, 0))));
        CheckAssert.True(placed.Accepted,
            "a carried workbench should place through one world transaction");
        CheckAssert.Equal(0, Count(placed.Gameplay, ItemIds.Workbench),
            "placement must consume exactly one workbench");
        CheckAssert.Equal(ItemIds.Workbench,
            placed.WorldTransaction!.ObjectDeltas.Single().Object!.DefinitionId,
            "the public world delta must establish the station definition");

        var placedObjectId = placed.WorldTransaction.ObjectDeltas.Single().ObjectId;
        var durable = session.CaptureCheckpoint();
        session = NewSession();
        session.RestoreCheckpoint(durable);
        connection = ClientConnectionId.New();
        var reconnectPending = session.EnqueueReconnectAsync(new(
            connection, joined.Identity.PlayerId, joined.ReconnectToken));
        session.Drain();
        var reconnect = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnect.Accepted,
            "the station owner must reconnect after a placement checkpoint");
        joined = joined with
        {
            Gameplay = reconnect.Gameplay,
            NextCommandSequence = reconnect.NextCommandSequence
        };
        CheckAssert.Equal(ItemIds.Workbench,
            session.CaptureWorldObject(placedObjectId).DefinitionId,
            "the authoritative station must survive checkpoint restore");

        var crafted = Send(
            session, connection, joined, joined.NextCommandSequence,
            new CraftRecipeIntent(
                Guid.NewGuid(), joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision, "reinforced-fishing-net"));
        CheckAssert.True(crafted.Accepted,
            "the server must discover and validate a nearby station itself");
        CheckAssert.Equal(1,
            Count(crafted.Gameplay, ItemIds.ReinforcedFishingNet),
            "the station recipe should commit its exact product");

        var farSession = NewSession();
        farSession.SeedWorldObject(new(
            Guid.NewGuid(), ItemIds.Workbench, new Vector2(20, 20)));
        var farConnection = ClientConnectionId.New();
        var far = Join(farSession, farConnection, "Far Crafter", Vector2.Zero,
        [
            new InitialInventoryItem(ItemIds.PrimitiveFishingNet),
            new InitialInventoryItem(ItemIds.Rope, 2),
            new InitialInventoryItem(ItemIds.StoneKnife)
        ]);
        (farSession, farConnection, far) = RestartWithCraftingLevel(
            farSession, far, 6);
        var farResult = Send(
            farSession, farConnection, far, far.NextCommandSequence,
            new CraftRecipeIntent(
                Guid.NewGuid(), far.Gameplay.Inventory.Revision,
                far.Gameplay.ActorRevision, "reinforced-fishing-net"));
        CheckAssert.Equal(IntentStatus.MissingStation, farResult.Status,
            "a replicated station outside interaction range must not authorize crafting");
    }

    private static void FurnitureRejectsWaterAndSteepTerrain()
    {
        foreach (var navigation in new IWorldNavigationQuery[]
                 {
                     new HostilePlacementNavigationQuery(wading: true),
                     new HostilePlacementNavigationQuery(wading: false)
                 })
        {
            var session = new AuthoritativeWorldSession(
                identitySource: new DeterministicIdentitySource(),
                sessionId: new SessionId(Guid.NewGuid()),
                navigation: navigation);
            var connection = ClientConnectionId.New();
            var joined = Join(
                session, connection, "Terrain Builder", Vector2.Zero,
                [new InitialInventoryItem(ItemIds.Workbench)]);
            var slot = joined.Gameplay.Inventory.Slots.Single(value =>
                value.ItemId == ItemIds.Workbench).Slot;
            var position = new Vector2(1, 1.5f);
            var result = Send(
                session, connection, joined, joined.NextCommandSequence,
                new PlaceInventoryWorldObjectIntent(
                    Guid.NewGuid(),
                    joined.Gameplay.Inventory.Revision,
                    joined.Gameplay.ActorRevision,
                    ItemIds.Workbench,
                    slot,
                    position,
                    0,
                    0,
                    session.CaptureWorldChunkRevision(
                        WorldChunkKey.At(position, 0))));
            CheckAssert.Equal(IntentStatus.InvalidPlacement, result.Status,
                "crafted wire placement must reject water and steep footprints");
            CheckAssert.Equal(1, Count(result.Gameplay, ItemIds.Workbench),
                "invalid terrain must not consume carried furniture");
        }

        var extremeSession = new AuthoritativeWorldSession(
            identitySource: new DeterministicIdentitySource(),
            sessionId: new SessionId(Guid.NewGuid()),
            navigation: new HostilePlacementNavigationQuery(wading: false));
        var extremeConnection = ClientConnectionId.New();
        var extremeJoined = Join(
            extremeSession, extremeConnection, "Bounds Builder", Vector2.Zero,
            [new InitialInventoryItem(ItemIds.Workbench)]);
        var extremeSlot = extremeJoined.Gameplay.Inventory.Slots.Single(value =>
            value.ItemId == ItemIds.Workbench).Slot;
        var extremeResult = Send(
            extremeSession,
            extremeConnection,
            extremeJoined,
            extremeJoined.NextCommandSequence,
            new PlaceInventoryWorldObjectIntent(
                Guid.NewGuid(),
                extremeJoined.Gameplay.Inventory.Revision,
                extremeJoined.Gameplay.ActorRevision,
                ItemIds.Workbench,
                extremeSlot,
                new Vector2(float.MinValue, float.MaxValue),
                0,
                0,
                0));
        CheckAssert.Equal(IntentStatus.InvalidPlacement, extremeResult.Status,
            "extreme finite coordinates must reject before terrain iteration");
        CheckAssert.Equal(1, Count(extremeResult.Gameplay, ItemIds.Workbench),
            "out-of-world placement must not consume carried furniture");
    }

    private static (
        AuthoritativeWorldSession Session,
        ClientConnectionId Connection,
        JoinResult Joined) RestartWithCraftingLevel(
        AuthoritativeWorldSession session,
        JoinResult joined,
        int level,
        Func<AuthoritativeWorldSession>? replacementFactory = null)
    {
        var checkpoint = session.CaptureCheckpoint();
        var actor = checkpoint.Actors.Single(value =>
            value.Identity.PlayerId == joined.Identity.PlayerId);
        var restored = replacementFactory?.Invoke() ?? NewSession();
        restored.RestoreCheckpoint(checkpoint with
        {
            Actors = checkpoint.Actors.Select(value =>
                value.Identity.PlayerId == joined.Identity.PlayerId
                    ? value with
                    {
                        Gameplay = value.Gameplay with
                        {
                            CraftingExperience =
                                SkillService.ExperienceForLevel(level)
                        }
                    }
                    : value).ToImmutableArray()
        });
        var restoredConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            restoredConnection,
            actor.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        var reconnect = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnect.Accepted,
            "the levelled crafting fixture must reconnect after restore");
        return (restored, restoredConnection, joined with
        {
            Gameplay = reconnect.Gameplay,
            NextCommandSequence = reconnect.NextCommandSequence
        });
    }

    private static void CampfireCookingIsTimedAtomicAndDurable()
    {
        var session = NewSession();
        var campfire = session.SeedWorldObject(new(
            Guid.Parse("8c000000-0000-0000-0000-000000000001"),
            "campfire", new Vector2(1, 0),
            FuelItemId: "logs",
            LitUntilGameSeconds: AuthoritativeWorldTime
                .FromElapsedRealSeconds(300)));
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
        var actorCheckpoint = checkpoint.Actors.Single();
        var cookingQuestProgress = QuestService.Normalize(null);
        var questAdventureExperience = 0;
        foreach (var questId in new[]
                 {
                     "washed-ashore",
                     "tools-of-survival",
                     "first-light"
                 })
        {
            var questUpdate = QuestService.Complete(
                cookingQuestProgress,
                questAdventureExperience,
                questId,
                completionTick: 0);
            cookingQuestProgress = questUpdate.Progress;
            questAdventureExperience = questUpdate.AdventureExperience;
        }
        foreach (var questEvent in new[]
                 {
                     new QuestEvent(
                         QuestEventType.CraftItem,
                         ItemIds.PrimitiveFishingNet),
                     new QuestEvent(QuestEventType.CatchFish)
                 })
        {
            var questUpdate = QuestService.Apply(
                cookingQuestProgress,
                questAdventureExperience,
                questEvent,
                completionTick: 0);
            cookingQuestProgress = questUpdate.Progress;
            questAdventureExperience = questUpdate.AdventureExperience;
        }
        checkpoint = checkpoint with
        {
            Actors = [actorCheckpoint with
            {
                Gameplay = actorCheckpoint.Gameplay with
                {
                    AdventureExperience = 74,
                    MaximumHealth = 100,
                    Health = 100,
                    Quests = cookingQuestProgress
                }
            }]
        };
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
        var completionCount = 0;
        restored.CookingCompleted += _ => completionCount++;
        for (var tick = checkpoint.Tick;
             tick < checkpoint.CookingJobs[0].CompletesAtTick;
             tick++)
            restored.Tick();
        var completed = Actor(restored, joined.Identity.PlayerId).Gameplay;
        CheckAssert.Equal(1, Count(completed, ItemIds.CookedMinnows),
            "the deterministic fixture must complete one successful cooked output");
        CheckAssert.Equal(0, Count(completed, ItemIds.BurntMinnows),
            "the threshold regression must not silently take the burnt branch");
        CheckAssert.Equal(0, restored.CaptureCheckpoint().CookingJobs.Length,
            "a completed job must leave durable active state");
        CheckAssert.Equal(1, completionCount,
            "a durable cooking job must publish one atomic completion");
        CheckAssert.Equal(10, completed.CookingExperience,
            "successful minnows must award their authoritative Cooking XP");
        CheckAssert.Equal(327, completed.AdventureExperience,
            "cooking action XP and the completed cooking quest must both commit");
        CheckAssert.Equal(
            AdventureService.MaximumHealth(completed.AdventureExperience),
            completed.MaximumHealth,
            "cooking Adventure XP must reconcile maximum health before commit");
        CheckAssert.Equal(104, completed.MaximumHealth,
            "action and quest XP must cross the two expected Adventure thresholds");
        CheckAssert.Equal(completed.MaximumHealth, completed.Health,
            "cooking level crossings must heal by the exact maximum-health gain");
        CheckAssert.Equal(
            checked(actorCheckpoint.Gameplay.ActorRevision + 2),
            completed.ActorRevision,
            "inventory, skill XP, and quest completion must not add a third quest-only revision");
        CheckAssert.Equal(
            checked(actorCheckpoint.Gameplay.Inventory.Revision + 1),
            completed.Inventory.Revision,
            "cooking completion must publish its output through one inventory revision");
        CheckAssert.Equal(
            QuestStatus.Complete,
            completed.Quests.Single(value =>
                value.QuestId == "island-provision").Status,
            "the successful output must atomically complete the cooking quest");

        restored.Tick();
        var afterExtraTick = Actor(
            restored, joined.Identity.PlayerId).Gameplay;
        AssertGameplayEqual(completed, afterExtraTick,
            "a completed durable job must not commit again on a later tick");
        CheckAssert.Equal(completed.AdventureExperience,
            afterExtraTick.AdventureExperience,
            "a completed durable job must not award Adventure XP twice");
        CheckAssert.SequenceEqual(completed.Quests, afterExtraTick.Quests,
            "a completed durable job must not apply its quest event twice");
        CheckAssert.Equal(1, completionCount,
            "a completed durable job must not publish twice");
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

    private sealed class HostilePlacementNavigationQuery(bool wading) :
        IWorldNavigationQuery
    {
        public bool SupportsWorldLevel(int worldLevel) => worldLevel == 0;

        public bool CanStandAt(Vector2 point, int worldLevel) =>
            worldLevel == 0;

        public float HeightAt(Vector2 point, int worldLevel) =>
            wading || point.X < 2 ? 0 : 4;

        public bool IsWading(Vector2 point, int worldLevel) =>
            wading && worldLevel == 0;
    }

    private sealed class EdgeWaterNavigationQuery(
        float? minimumWaterX = null,
        float? maximumWaterX = null) :
        IWorldNavigationQuery
    {
        public bool SupportsWorldLevel(int worldLevel) => worldLevel == 0;

        public bool CanStandAt(Vector2 point, int worldLevel) =>
            worldLevel == 0;

        public float HeightAt(Vector2 point, int worldLevel) => 0;

        public bool IsWading(Vector2 point, int worldLevel) =>
            worldLevel == 0 &&
            (minimumWaterX is { } minimum && point.X >= minimum ||
             maximumWaterX is { } maximum && point.X < maximum);
    }

    private sealed class FixedObstacles(
        IReadOnlyList<NavigationObstacle> values) :
        IWorldNavigationObstacleSource
    {
        public IReadOnlyList<NavigationObstacle> GetObstacles(int worldLevel) =>
            worldLevel == 0 ? values : [];
    }

    private sealed class FixedResourceSource(
        WorldChunkKey chunk,
        IReadOnlyList<ProceduralResourceSeed> values) :
        IProceduralResourceDescriptorSource
    {
        public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
            long worldSeed,
            WorldChunkKey requestedChunk) =>
            requestedChunk == chunk ? values : [];
    }
}
