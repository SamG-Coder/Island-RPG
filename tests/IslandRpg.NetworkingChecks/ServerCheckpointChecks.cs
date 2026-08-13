using IslandRpg.Gameplay;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ServerCheckpointChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("server checkpoint round-trips one atomic world state", () =>
        {
            using var folder = TemporaryFolder.Create();
            var store = new ServerCheckpointStore(folder.Path);
            var source = CreateCheckpoint(revision: 1);

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
                "0.3.0",
                "base",
                8);
            var original = CreateCheckpoint(revision: 7);
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
        });
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
            "0.3.0",
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
                    null)])],
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
