using IslandRpg.Gameplay;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;
using System.Text.Json;

namespace IslandRpg.NetworkingChecks;

internal static class ServerCheckpointChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("server checkpoint round-trips one atomic world state", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var source = CreateCheckpoint(revision: 1) with
            {
                Boats = [BoatCheckpoint(.1375)]
            };

            store.Save(source);
            var loaded = store.Load(source.WorldId);

            CheckAssert.True(loaded is not null, "saved checkpoint must load");
            CheckAssert.False(loaded!.RecoveredFromBackup,
                "healthy primary checkpoint must not use backup");
            CheckAssert.Equal(source.Revision, loaded.Checkpoint.Revision,
                "checkpoint revision must round-trip");
            CheckAssert.Equal(source.Actors[0].Inventory[0],
                loaded.Checkpoint.Actors[0].Inventory[0],
                "inventory must be part of the same durable state");
            CheckAssert.Equal(source.WorldObjects[0].Container[0],
                loaded.Checkpoint.WorldObjects[0].Container[0],
                "container contents must be part of the same durable state");
            CheckAssert.Equal(source.Actors[0].DiggingExperience,
                loaded.Checkpoint.Actors[0].DiggingExperience,
                "digging experience must be part of the same durable state");
            CheckAssert.Equal(.1375,
                loaded.Checkpoint.Boats![0].PlanningCooldownSeconds,
                "boat planning cooldown must survive exact server JSON storage");
        });

        checks.Add("server checkpoint JSON restores canonical quest counters", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var source = CreateCheckpoint(revision: 1);
            var progress = QuestService.Apply(
                QuestService.Normalize(null),
                0,
                new QuestEvent(
                    QuestEventType.GatherItem,
                    ItemIds.LargeRock),
                completionTick: source.Tick).Progress;
            source = source with
            {
                Actors = [source.Actors[0] with
                {
                    Quests = progress
                }]
            };

            store.Save(source);
            var loaded = store.Load(source.WorldId)!.Checkpoint;
            var normalized = QuestService.Normalize(
                loaded.Actors[0].Quests);

            QuestService.Validate(normalized);
            CheckAssert.Equal(1,
                normalized[0].ObjectiveCounts!
                    .GetValueOrDefault("large-rocks"),
                "JSON dictionary counters must normalize back to compact authority state");
        });

        checks.Add("server checkpoint migrates legacy world deadlines exactly", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var source = CreateCheckpoint(revision: 1);
            const double elapsedRealSeconds = 1;
            var campfire = new ServerWorldObjectCheckpoint(
                Guid.Parse("51000000-0000-0000-0000-000000000001"),
                ItemIds.Campfire,
                1,
                0,
                0,
                0,
                0,
                1,
                1,
                0,
                0,
                0,
                null,
                null,
                false,
                ItemIds.Logs,
                elapsedRealSeconds + 300,
                1,
                WorldGateAccessState.None,
                false,
                []);
            var resourceChunk = new ServerResourceChunkCheckpoint(
                0,
                0,
                0,
                1,
                [new ServerResourceNodeCheckpoint(
                    Guid.Parse("51000000-0000-0000-0000-000000000002"),
                    IslandRpg.Resources.ResourceNodeKind.FibreShrub,
                    1,
                    0,
                    0,
                    elapsedRealSeconds + 300,
                    true)]);
            source = source with
            {
                SchemaVersion =
                    ServerCheckpoint.LegacyElapsedDeadlineSchemaVersion,
                Tick = SimulationTiming.TicksPerSecond,
                Actors = [source.Actors[0] with
                {
                    AdventureExperience = 75,
                    MaximumHealth = AdventureService.BaseMaximumHealth,
                    Health = 90
                }],
                WorldObjects = [.. source.WorldObjects, campfire],
                Resources = new([resourceChunk],
                [new ServerResourceCadenceCheckpoint(
                    source.Actors[0].ActorId,
                    IslandRpg.Resources.ResourceActionKind.GatherFibre,
                    12.5,
                    1)])
            };
            var options = new IslandRpg.Server.ServerOptions(
                System.Net.IPAddress.Loopback,
                38_740,
                source.WorldId,
                source.WorldSeed,
                source.BuildVersion,
                source.ContentVersion,
                8);

            var checkpointPath = store.CheckpointPath(source.WorldId);
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
            File.WriteAllText(checkpointPath, JsonSerializer.Serialize(source));
            var loaded = store.Load(source.WorldId)!.Checkpoint;
            var simulation = ServerCheckpointMapper.ToSimulation(
                loaded, options);
            CheckAssert.Equal(102,
                simulation.Actors[0].Gameplay.MaximumHealth,
                "legacy Adventure XP must migrate to canonical maximum health");
            CheckAssert.Equal(92, simulation.Actors[0].Gameplay.Health,
                "legacy migration must preserve the actor's missing-health delta");
            var expectedWorldDeadline =
                AuthoritativeWorldTime.FromElapsedRealSeconds(
                    elapsedRealSeconds) + 300;
            CheckAssert.Equal(expectedWorldDeadline,
                simulation.World.Objects.Single(value =>
                    value.Object.ObjectId == campfire.ObjectId)
                    .Object.LitUntilGameSeconds,
                "legacy campfire remainder must move into world-game time");
            CheckAssert.Equal(expectedWorldDeadline,
                simulation.Resources!.Chunks.Single().Nodes.Single()
                    .ReadyAtGameSeconds,
                "legacy renewable remainder must move into world-game time");
            CheckAssert.Equal(12.5,
                simulation.Resources.ActorCadences.Single()
                    .ReadyAtGameSeconds,
                "resource action cadence must remain elapsed-real time");

            var upgraded = ServerCheckpointMapper.ToDurable(
                simulation, options, 2);
            CheckAssert.Equal(ServerCheckpoint.CurrentSchemaVersion,
                upgraded.SchemaVersion,
                "the next save must atomically upgrade the schema");
            CheckAssert.Equal(expectedWorldDeadline,
                upgraded.WorldObjects.Single(value =>
                    value.ObjectId == campfire.ObjectId)
                    .LitUntilGameSeconds,
                "upgraded campfire deadline must not migrate twice");
            CheckAssert.Equal(expectedWorldDeadline,
                upgraded.Resources!.Chunks.Single().Nodes.Single()
                    .ReadyAtGameSeconds,
                "upgraded resource deadline must not migrate twice");
        });

        checks.Add("server checkpoint rejects stale asynchronous writes", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var source = CreateCheckpoint(revision: 2);
            store.Save(source);

            CheckAssert.Throws<InvalidOperationException>(
                () => store.Save(source with { Revision = 1 }),
                "an older completion must not overwrite newer durable state");
            CheckAssert.Equal(2L, store.Load(source.WorldId)!.Checkpoint.Revision,
                "stale save rejection must preserve durable state");
        });

        checks.Add("server checkpoint recovers the last known good revision", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var first = CreateCheckpoint(revision: 1);
            store.Save(first);
            store.Save(first with { Revision = 2, Tick = 120 });

            File.WriteAllText(store.CheckpointPath(first.WorldId), "{broken");
            var recovered = store.Load(first.WorldId);

            CheckAssert.True(recovered is not null && recovered.RecoveredFromBackup,
                "corrupt primary must fall back to the previous durable checkpoint");
            CheckAssert.Equal(1L, recovered!.Checkpoint.Revision,
                "backup must be the last fully replaced revision");

            store.Save(first with { Revision = 3, Tick = 180 });
            var resumed = store.Load(first.WorldId);
            CheckAssert.True(resumed is not null && !resumed.RecoveredFromBackup,
                "a recovered server must be able to write a new primary checkpoint");
            CheckAssert.Equal(3L, resumed!.Checkpoint.Revision,
                "the resumed checkpoint must advance beyond the recovered revision");
        });

        checks.Add("server checkpoint validates bounded actor inventory", () =>
        {
            var source = CreateCheckpoint(revision: 1);
            var invalidActor = source.Actors[0] with
            {
                Inventory = source.Actors[0].Inventory.Take(27).ToArray()
            };

            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [invalidActor] }, source.WorldId),
                "partial player inventories must never become durable");

            var terminalRevision = source.Actors[0] with
            {
                ActorRevision = uint.MaxValue
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [terminalRevision] },
                    source.WorldId),
                "terminal actor revisions must never become durable");
        });

        checks.Add("server checkpoint enforces current adventure health invariant", () =>
        {
            var source = CreateCheckpoint(revision: 1);
            var inconsistent = source.Actors[0] with
            {
                AdventureExperience = 75,
                MaximumHealth = AdventureService.BaseMaximumHealth,
                Health = 90
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [inconsistent] }, source.WorldId),
                "the current schema must reject a noncanonical Adventure-derived maximum health");

            var excessiveExperience = checked(
                AdventureService.ExperienceForLevel(
                    AdventureService.MaximumLevel) + 1);
            var uncapped = source.Actors[0] with
            {
                AdventureExperience = excessiveExperience,
                MaximumHealth = AdventureService.MaximumHealth(
                    excessiveExperience)
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [uncapped] }, source.WorldId),
                "Adventure XP beyond the canonical cap must not become durable");
        });

        checks.Add("server checkpoint rejects inconsistent combat life state", () =>
        {
            var source = CreateCheckpoint(revision: 1);
            var invalidDeadActor = source.Actors[0] with
            {
                Health = 1,
                LifeState = ActorLifeState.Dead,
                RespawnAvailableTick = source.Tick + 60
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [invalidDeadActor] },
                    source.WorldId),
                "a durable dead actor cannot retain positive health");

            var invalidAliveActor = source.Actors[0] with
            {
                Health = 0,
                LifeState = ActorLifeState.Alive,
                RespawnAvailableTick = 0
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    source with { Actors = [invalidAliveActor] },
                    source.WorldId),
                "a durable living actor must retain positive health");
        });

        checks.Add("server checkpoint never replaces an unreadable world", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var worldId = CreateCheckpoint(1).WorldId;
            var path = store.CheckpointPath(worldId);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{broken");

            CheckAssert.Throws<InvalidDataException>(
                () => store.Load(worldId),
                "a corrupt sole checkpoint must stop startup instead of creating a fresh world");
            CheckAssert.Throws<InvalidDataException>(
                () => store.Save(CreateCheckpoint(2)),
                "a corrupt checkpoint must not be silently overwritten");
        });

        checks.Add("server checkpoint holds one exclusive world host lease", () =>
        {
            using var folder = TemporaryFolder.Create();
            var firstStore = new ServerCheckpointStore(folder.Path);
            var secondStore = new ServerCheckpointStore(folder.Path);
            var worldId = CreateCheckpoint(1).WorldId;

            using var lease = firstStore.AcquireWorldLease(worldId);
            CheckAssert.Throws<InvalidOperationException>(
                () => secondStore.AcquireWorldLease(worldId),
                "a second process must not host the same durable world");
        });

        checks.Add("server checkpoint writer coalesces without blocking authority",
            async cancellationToken =>
            {
                using var folder = TemporaryFolder.Create();
                var store = new ServerCheckpointStore(folder.Path);
                await using var writer = new ServerCheckpointWriter(store);
                var source = CreateCheckpoint(revision: 1);

                CheckAssert.True(writer.TryQueue(source),
                    "the first immutable checkpoint must enter the save queue");
                for (var revision = 2; revision <= 40; revision++)
                    CheckAssert.True(writer.TryQueue(source with
                    {
                        Revision = revision,
                        Tick = revision * 60
                    }), "newer immutable checkpoints must coalesce successfully");
                CheckAssert.False(writer.TryQueue(source with { Revision = 39 }),
                    "an old asynchronous producer must be rejected");

                await writer.FlushAsync(cancellationToken);
                CheckAssert.Equal(40L, writer.DurableRevision,
                    "flush must wait through the newest accepted revision");
                CheckAssert.Equal(40L, store.Load(source.WorldId)!.Checkpoint.Revision,
                    "the durable checkpoint must be the newest coalesced state");
            });

        checks.Add("server checkpoint writer preserves concurrent revision order",
            async cancellationToken =>
            {
                using var folder = TemporaryFolder.Create();
                var store = new ServerCheckpointStore(folder.Path);
                await using var writer = new ServerCheckpointWriter(store);
                var source = CreateCheckpoint(1);
                using var start = new ManualResetEventSlim(false);

                var producers = Enumerable.Range(1, 8).Select(worker => Task.Run(() =>
                {
                    start.Wait(cancellationToken);
                    for (var index = worker; index <= 400; index += 8)
                        writer.TryQueue(source with
                        {
                            Revision = index,
                            Tick = index * 60L
                        });
                }, cancellationToken)).ToArray();

                start.Set();
                await Task.WhenAll(producers);
                // Ensure a known final revision wins regardless of producer
                // scheduling, then prove Flush cannot wait on an evicted item.
                writer.TryQueue(source with { Revision = 401, Tick = 24_060 });
                await writer.FlushAsync(cancellationToken);

                CheckAssert.Equal(401L, writer.DurableRevision,
                    "the newest concurrently accepted revision must become durable");
                CheckAssert.Equal(401L, store.Load(source.WorldId)!.Checkpoint.Revision,
                    "concurrent producers must never evict a newer checkpoint");
            });

        checks.Add("server checkpoint maps exact simulation authority state", () =>
        {
            var options = new IslandRpg.Server.ServerOptions(
                System.Net.IPAddress.Loopback,
                38_740,
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                55_902,
                "0.4.0",
                "base",
                8);
            var original = CreateCheckpoint(revision: 7) with
            {
                Boats = [BoatCheckpoint(.0875)]
            };
            var cookingCommandId = Guid.Parse(
                "70000000-0000-0000-0000-000000000001");
            var cookingActorId = original.Actors[0].ActorId;
            var outcome = CookingSkill.Roll(
                ItemIds.RawMinnows,
                CookingSkill.LevelForExperience(
                    original.Actors[0].CookingExperience),
                AuthoritativeWorldSession.DeterministicCookingRoll(
                    original.SessionId,
                    cookingActorId,
                    cookingCommandId));
            var campfire = new ServerWorldObjectCheckpoint(
                Guid.Parse("52000000-0000-0000-0000-000000000001"),
                ItemIds.Campfire,
                6,
                9,
                0,
                0,
                0,
                1,
                1,
                0,
                0,
                0,
                null,
                null,
                false,
                ItemIds.Logs,
                300,
                1,
                WorldGateAccessState.None,
                false,
                []);
            var source = original with
            {
                WorldObjects = [.. original.WorldObjects, campfire],
                CookingJobs = [new ServerCookingJobCheckpoint(
                    cookingCommandId,
                    cookingActorId,
                    campfire.ObjectId,
                    campfire.ChunkX,
                    campfire.ChunkY,
                    campfire.WorldLevel,
                    campfire.X,
                    campfire.Y,
                    0,
                    ItemIds.RawMinnows,
                    outcome.ItemId,
                    outcome.Experience,
                    outcome.Burnt,
                    Guid.Parse("71000000-0000-0000-0000-000000000001"),
                    261)]
            };

            var simulation = ServerCheckpointMapper.ToSimulation(source, options);
            var roundTrip = ServerCheckpointMapper.ToDurable(
                simulation,
                options,
                8);

            CheckAssert.Equal(8L, roundTrip.Revision,
                "mapping must use the caller's monotonic disk revision");
            CheckAssert.Equal(source.Tick, roundTrip.Tick,
                "mapping must preserve the exact authority tick");
            CheckAssert.Equal(source.Actors[0].Inventory[0],
                roundTrip.Actors[0].Inventory[0],
                "mapping must preserve the exact player inventory");
            CheckAssert.Equal(source.WorldObjects[0].Container[0],
                roundTrip.WorldObjects[0].Container[0],
                "mapping must preserve private container contents");
            CheckAssert.Equal(source.ChunkRevisions[0],
                roundTrip.ChunkRevisions[0],
                "mapping must preserve exact optimistic-lock revisions");
            CheckAssert.Equal(source.Actors[0].CommandReceipts[0],
                roundTrip.Actors[0].CommandReceipts[0],
                "mapping must preserve durable gameplay command receipts");
            CheckAssert.Equal(source.CookingJobs![0], roundTrip.CookingJobs![0],
                "mapping must preserve a deterministic active cooking job");
            CheckAssert.Equal(source.Actors[0].DiggingExperience,
                roundTrip.Actors[0].DiggingExperience,
                "mapping must preserve authoritative digging experience");
            CheckAssert.Equal(source.Boats![0].PlanningCooldownSeconds,
                roundTrip.Boats![0].PlanningCooldownSeconds,
                "mapping must preserve exact boat planning cooldown");
        });

        checks.Add("server checkpoint preserves linked caves and dig cadence", () =>
        {
            var source = WithCaveState(CreateCheckpoint(3));
            ServerCheckpointStore.Validate(source, source.WorldId);
            var options = new IslandRpg.Server.ServerOptions(
                System.Net.IPAddress.Loopback,
                38_740,
                source.WorldId,
                source.WorldSeed,
                source.BuildVersion,
                source.ContentVersion,
                8);
            var simulation = ServerCheckpointMapper.ToSimulation(source, options);
            var roundTrip = ServerCheckpointMapper.ToDurable(
                simulation, options, 4);

            var actualObjects = roundTrip.WorldObjects.ToDictionary(
                static value => value.ObjectId);
            foreach (var expected in source.WorldObjects)
            {
                CheckAssert.True(actualObjects.TryGetValue(
                        expected.ObjectId, out var actual),
                    "every persisted cave endpoint must survive restart");
                CheckAssert.Equal(
                    expected with { Container = actual!.Container }, actual,
                    "linked cave endpoint metadata must round trip exactly");
                CheckAssert.SequenceEqual(expected.Container, actual.Container,
                    "linked cave endpoint containers must round trip exactly");
            }
            CheckAssert.SequenceEqual(source.ExcavationCadences!,
                roundTrip.ExcavationCadences!,
                "active excavation cadence must survive restart exactly");

            var broken = source with
            {
                WorldObjects = source.WorldObjects.Select((value, index) =>
                    index == 1
                        ? value with { LinkedObjectId = Guid.NewGuid() }
                        : value).ToArray()
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(broken, source.WorldId),
                "durable state must reject a non-reciprocal cave portal");
            var mismatchedDefinition = source with
            {
                WorldObjects = source.WorldObjects.Select((value, index) =>
                    index == 1
                        ? value with { DefinitionId = "cave_entrance" }
                        : value).ToArray()
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    mismatchedDefinition, source.WorldId),
                "durable state must reject portal definitions that disagree");
            var arbitraryLink = source with
            {
                WorldObjects = source.WorldObjects.Select(value =>
                    value.LinkedObjectId is null
                        ? value
                        : value with { DefinitionId = "wooden_chest" })
                    .ToArray()
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    arbitraryLink, source.WorldId),
                "durable state must reject links between arbitrary objects");
            var invalidCadence = source with
            {
                ExcavationCadences =
                [new(source.Actors[0].ActorId, Guid.NewGuid(), 10)]
            };
            CheckAssert.Throws<InvalidDataException>(
                () => ServerCheckpointStore.Validate(
                    invalidCadence, source.WorldId),
                "durable state must reject cadence for a missing excavation");
        });
    }

    private static ServerCheckpoint WithCaveState(ServerCheckpoint source)
    {
        var surfaceId = Guid.Parse(
            "53000000-0000-0000-0000-000000000001");
        var undergroundId = Guid.Parse(
            "53000000-0000-0000-0000-000000000002");
        ServerWorldObjectCheckpoint Portal(
            Guid id, Guid link, int worldLevel) => new(
            id,
            "cave_hole",
            .5f,
            .5f,
            0,
            0,
            worldLevel,
            4,
            1,
            0,
            0,
            50,
            null,
            null,
            false,
            null,
            0,
            1,
            WorldGateAccessState.None,
            false,
            [],
            link);
        return source with
        {
            Actors =
            [source.Actors[0] with { DiggingExperience = 135 }],
            WorldObjects =
            [
                .. source.WorldObjects,
                Portal(surfaceId, undergroundId, 0),
                Portal(undergroundId, surfaceId, -1),
            ],
            ChunkRevisions =
            [
                .. source.ChunkRevisions,
                new ServerChunkRevisionCheckpoint(0, 0, -1, 2),
            ],
            ExcavationCadences =
            [new(source.Actors[0].ActorId, surfaceId, 123.75)],
        };
    }

    private static ServerCheckpoint CreateCheckpoint(long revision)
    {
        var worldId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var inventory = Enumerable.Range(0, 28)
            .Select(slot => slot == 0
                ? new ServerInventorySlotCheckpoint(slot, "stick", 2)
                : new ServerInventorySlotCheckpoint(slot, null, 0))
            .ToArray();
        return new ServerCheckpoint(
            ServerCheckpoint.CurrentSchemaVersion,
            revision,
            worldId,
            55_902,
            "0.4.0",
            "base",
            worldId,
            60,
            20,
            [new ServerActorCheckpoint(
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                "Elara",
                4,
                8,
                0,
                7,
                null,
                3,
                91,
                74,
                2,
                10,
                4,
                5,
                inventory,
                Enumerable.Repeat((byte)0xA5, 32).ToArray(),
                [new ServerCommandReceiptCheckpoint(
                    Guid.Parse("60000000-0000-0000-0000-000000000001"),
                    new string('a', GameplayIntentFingerprint.HexLength),
                    IntentStatus.Accepted,
                    null)],
                DiggingExperience: 77)],
            [new ServerWorldObjectCheckpoint(
                Guid.Parse("50000000-0000-0000-0000-000000000001"),
                "wooden_chest",
                5,
                9,
                0,
                0,
                0,
                4,
                3,
                0,
                100,
                100,
                null,
                null,
                true,
                null,
                0,
                1,
                IslandRpg.Simulation.WorldGateAccessState.None,
                true,
                [new ServerContainerSlotCheckpoint(0, "stick", 1, null)])],
            [new ServerChunkRevisionCheckpoint(0, 0, 0, 6)]);
    }

    private static ServerBoatCheckpoint BoatCheckpoint(
        double planningCooldownSeconds) => new(
        Guid.Parse("72000000-0000-0000-0000-000000000001"),
        Guid.Parse("30000000-0000-0000-0000-000000000001"),
        null,
        null,
        null,
        .5f,
        .5f,
        0,
        1,
        0,
        1,
        [],
        planningCooldownSeconds);

    private sealed class TemporaryFolder : IDisposable
    {
        private TemporaryFolder(string path) => Path = path;

        public string Path { get; }

        public static TemporaryFolder Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "IslandRpg-NetworkingChecks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryFolder(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
