using System.Diagnostics;
using System.Net;
using System.Numerics;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class MmoAuthorityPerformanceChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "populated 60-tick snapshot publish stays real-time",
            PopulatedTickBatchStaysRealTime);
        checks.Add(
            "budgeted join apply cannot dump a join-sized baseline in one slice",
            BudgetedJoinApplyCannotDumpBaselineInOneSliceAsync);
        checks.Add(
            "world-object ingest does not restart when the generation changes mid-pass",
            WorldObjectIngestDoesNotRestartWhenGenerationChangesMidPass);
        checks.Add(
            "budgeted world-object slices keep known ids observers and container close",
            BudgetedWorldObjectSlicesDriveObserversAsync);
        checks.Add(
            "healthy client keeps commands and snapshots while a late join is blocked",
            HealthyClientAdvancesWhileLateJoinBlockedAsync);
        checks.Add(
            "dedicated server handshake accepts a host-port join",
            DedicatedServerHandshakeAcceptsHostPortJoinAsync);
    }

    private static void PopulatedTickBatchStaysRealTime()
    {
        var combat = new AuthoritativeCombatTransactions(
            77_001,
            options: new AuthoritativeCombatOptions
            {
                EnemyAttackIntervalTicks = 10_000
            });
        var world = new AuthoritativeWorldTransactions();
        for (var index = 0; index < 256; index++)
        {
            world.AddObject(new WorldObjectSeed(
                Guid.Parse($"b1000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2(40 + index * .2f, 40)));
        }
        var session = new AuthoritativeWorldSession(
            worldTransactions: world,
            combatTransactions: combat);

        for (var index = 0; index < 8; index++)
        {
            var connection = ClientConnectionId.New();
            var pending = session.EnqueueJoinAsync(new JoinRequest(
                connection,
                $"Walker{index}",
                new Vector2(index, 0)));
            session.Drain();
            var joined = pending.GetAwaiter().GetResult();
            CheckAssert.True(joined.Accepted, "populated tick fixture must join walkers");
            var walk = session.EnqueueIntentAsync(new ActorCommand(
                connection,
                joined.Identity.PlayerId,
                1,
                new WalkIntent(new Vector2(12, index))));
            session.Drain();
            CheckAssert.True(
                walk.GetAwaiter().GetResult().Accepted,
                "populated tick fixture must accept walker routes");
        }

        for (var index = 0; index < 24; index++)
        {
            session.SeedEnemy(new AuthoritativeEnemySeed(
                new EnemyId(Guid.Parse(
                    $"ae000000-0000-0000-0000-{index + 1:D12}")),
                EnemyKind.GrassSlime,
                new Vector2(3 + index * .15f, 2)));
        }

        var published = 0;
        var materialized = 0;
        var timer = Stopwatch.StartNew();
        SessionSnapshot? last = null;
        for (var step = 0; step < SimulationTiming.TicksPerSecond; step++)
        {
            var tick = session.Tick();
            if (tick.PublishedSnapshot is not { } snapshot)
                continue;
            published++;
            last = snapshot;
            var entities = DedicatedServer.MaterializeSnapshotEntities(snapshot);
            materialized += entities.Length;
            CheckAssert.True(
                entities.Length >= 8,
                "a populated snapshot must include the walking actors");
        }
        timer.Stop();

        CheckAssert.Equal(
            SimulationTiming.SnapshotsPerSecond,
            published,
            "sixty 60 Hz ticks must publish exactly twenty snapshots");
        CheckAssert.True(
            last is not null && last.Clock.Tick == SimulationTiming.TicksPerSecond,
            "the batch must end on the sixtieth authoritative tick");
        CheckAssert.True(
            last!.Actors.Count(static actor => actor.Velocity != Vector2.Zero) >= 4,
            "walking actors must still be in motion after the batch");
        CheckAssert.True(
            last.Enemies.Length >= 24,
            "seeded enemies must remain in the published snapshot");
        CheckAssert.Equal(
            last.Actors.Length + last.Enemies.Length +
            (last.Boats.IsDefault ? 0 : last.Boats.Length),
            DedicatedServer.MaterializeSnapshotEntities(last).Length,
            "snapshot publish must not grow with the world-object set");
        CheckAssert.True(
            world.CaptureCheckpoint().Objects.Length >= 256,
            "the fixture must keep a large world-object set off the snapshot");
        CheckAssert.True(
            materialized > 0,
            "snapshot publication must materialize entities on the shipped path");
        CheckAssert.True(
            timer.ElapsedMilliseconds < 250,
            $"sixty populated ticks plus snapshot publish must stay well under 1s; took {timer.ElapsedMilliseconds} ms");
        Console.WriteLine(
            $"populated-tick-batch wall={timer.Elapsed.TotalMilliseconds:0.000}ms " +
            $"snapshots={published} entities={materialized}");
    }

    private static async ValueTask
        BudgetedJoinApplyCannotDumpBaselineInOneSliceAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 160;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse($"a3000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2((index % 10) * .2f, (index / 10) * .2f)))
            .ToArray();
        await using var fixture = await LoopbackChecks.StartHostAsync(
            cancellationToken, seeds);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(client, "Ingest", cancellationToken);
        await LoopbackChecks.EventuallySharedAsync(
            () => client.State.WorldObjects.Count >= objectCount,
            "the join-sized baseline did not reach the shipped client",
            cancellationToken);

        CheckAssert.True(
            client.State.WorldObjects.Count > NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            "the fixture must be larger than one presentation slice");

        var apply = new NetworkPresentationApply();
        var timer = Stopwatch.StartNew();
        var first = apply.ApplyWorldObjects(client.State.WorldObjects);
        timer.Stop();

        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            first.Applied,
            "one apply step must stop at the shipped slice budget");
        CheckAssert.False(
            first.Complete,
            "one apply step must not finish a join-sized baseline");
        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            apply.PresentedWorldObjects.Count,
            "the presented set after one slice must equal the budget");
        CheckAssert.True(
            timer.ElapsedMilliseconds < 50,
            $"one join-apply slice must stay far below a 1000 ms hitch; took {timer.ElapsedMilliseconds} ms");
        var firstChanges = NetworkPresentationApply.ToChanges(first);
        CheckAssert.Equal(
            first.Applied,
            firstChanges.Count,
            "the window change list must match the budgeted slice");
        CheckAssert.True(
            firstChanges.All(change =>
                change.Kind == WorldObjectDeltaKind.Upsert &&
                change.State is not null &&
                change.ObjectId == change.State.ObjectId),
            "a first join slice must emit upserts the cave/construction observers can consume");
        CheckAssert.False(
            firstChanges.Any(change => change.Kind == WorldObjectDeltaKind.Remove),
            "a partial join slice must not emit removals");
        Console.WriteLine(
            $"join-apply-slice wall={timer.Elapsed.TotalMilliseconds:0.000}ms " +
            $"applied={first.Applied} complete={first.Complete}");

        var slices = 1;
        while (!apply.ApplyWorldObjects(client.State.WorldObjects).Complete)
            slices++;
        CheckAssert.True(
            slices >= 2,
            "a join-sized baseline must require multiple budgeted slices");
        CheckAssert.Equal(
            objectCount,
            apply.PresentedWorldObjects.Count,
            "later slices must finish the same client generation");

        var remaining = client.State.WorldObjects.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        var removedId = remaining.Keys.OrderBy(static id => id).First();
        var previous = remaining[removedId];
        remaining.Remove(removedId);
        NetworkPresentationSlice<NetworkWorldObjectState> reduced;
        do
        {
            reduced = apply.ApplyWorldObjects(remaining);
        } while (!reduced.Complete);
        var reducedChanges = NetworkPresentationApply.ToChanges(reduced);
        CheckAssert.True(
            reducedChanges.Any(change =>
                NetworkPresentationApply.MatchesExpectedRemove(
                    change,
                    removedId,
                    previous.ObjectRevision,
                    previous.ChunkRevision)),
            "a generation shrink must emit a Remove that cave Matches can accept");
        CheckAssert.False(
            apply.PresentedWorldObjects.ContainsKey(removedId),
            "the presented set must drop objects the new generation no longer contains");
    }

    private static void WorldObjectIngestDoesNotRestartWhenGenerationChangesMidPass()
    {
        const int objectCount = 160;
        var generation = Enumerable.Range(0, objectCount)
            .ToDictionary(
                index => Guid.Parse($"b1000000-0000-0000-0000-{index + 1:D12}"),
                index => new NetworkWorldObjectState(
                    Guid.Parse($"b1000000-0000-0000-0000-{index + 1:D12}"),
                    0, 0, 0, 1, 1, "large_rock",
                    index, 0, 0, 1, 1, false, "", 0,
                    WorldObjectGateState.None));
        var apply = new NetworkPresentationApply();
        var first = apply.ApplyWorldObjects(generation);
        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            first.Applied,
            "the first slice must consume the join budget");
        CheckAssert.False(first.Complete, "160 objects must not finish in one slice");

        // A live MMO replaces the WorldObjects dictionary on every delta.
        // Restarting here would ToArray() again from index 0 and never finish.
        var nextGeneration = generation.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        var second = apply.ApplyWorldObjects(nextGeneration);
        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            second.Applied,
            "a generation swap mid-pass must continue the in-flight copy");
        CheckAssert.Equal(
            0,
            second.Removals.Count,
            "a mid-pass swap must not emit removals against the unfinished set");
        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice * 2,
            apply.PresentedWorldObjects.Count,
            "the second slice must visit the next 64 objects, not the first 64 again");
        CheckAssert.False(
            second.Complete,
            "continuing the original pass must still have objects left");
    }

    private static async ValueTask
        BudgetedWorldObjectSlicesDriveObserversAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 160;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse($"a5000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2((index % 10) * .2f, (index / 10) * .2f)))
            .ToArray();
        await using var fixture = await LoopbackChecks.StartHostAsync(
            cancellationToken, seeds);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(client, "Observers", cancellationToken);
        await LoopbackChecks.EventuallySharedAsync(
            () => client.State.WorldObjects.Count >= objectCount,
            "the observer fixture did not receive the join-sized baseline",
            cancellationToken);

        var ingest = new NetworkPresentationApply();
        var dispatcher = new NetworkWorldObjectChangeApply();
        var observer = new RecordingWorldObjectObserver();
        var first = ingest.ApplyWorldObjects(client.State.WorldObjects);
        dispatcher.Apply(
            NetworkPresentationApply.ToChanges(first), observer);

        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            dispatcher.KnownObjectIds.Count,
            "the first poll slice must populate known object ids without the full baseline");
        CheckAssert.Equal(
            NetworkPresentationApply.MaximumWorldObjectsPerSlice,
            observer.Upserts.Count,
            "construction/cave upsert observers must fire once per sliced object");
        CheckAssert.Equal(
            0,
            observer.Removes.Count,
            "a partial join slice must not close containers or fire remove observers");
        CheckAssert.Equal(1, observer.SlicesApplied,
            "one poll step must apply exactly one change slice");

        NetworkPresentationSlice<NetworkWorldObjectState> slice;
        do
        {
            slice = ingest.ApplyWorldObjects(client.State.WorldObjects);
            dispatcher.Apply(
                NetworkPresentationApply.ToChanges(slice), observer);
        } while (!slice.Complete);
        CheckAssert.Equal(
            objectCount,
            dispatcher.KnownObjectIds.Count,
            "later slices must finish marking every baseline object");

        var target = client.State.WorldObjects.Values
            .OrderBy(static value => value.ObjectId)
            .First();
        var previousObjectRevision = target.ObjectRevision;
        var previousChunkRevision = target.ChunkRevision;
        var chunk = new NetworkWorldChunk(
            target.ChunkX, target.ChunkY, target.WorldLevel);
        var pickup = await SendAction(
            client,
            new PickUpWorldObjectAction(new WorldObjectReference(
                target.ObjectId,
                target.ChunkX,
                target.ChunkY,
                target.WorldLevel,
                target.ObjectRevision,
                target.ChunkRevision)),
            cancellationToken);
        CheckAssert.True(pickup.Accepted,
            $"the observer fixture pickup was rejected: {pickup.Detail}");
        await LoopbackChecks.EventuallySharedAsync(
            () => !client.State.WorldObjects.ContainsKey(target.ObjectId) &&
                  client.State.WorldChunkRevisions.TryGetValue(
                      chunk, out var revision) &&
                  revision > previousChunkRevision,
            "the shipped client did not apply the authoritative removal",
            cancellationToken);
        var currentChunk = client.State.WorldChunkRevisions[chunk];

        do
        {
            slice = ingest.ApplyWorldObjects(
                client.State.WorldObjects, client.State.WorldChunkRevisions);
            dispatcher.Apply(
                NetworkPresentationApply.ToChanges(slice), observer);
        } while (!slice.Complete);

        var removed = observer.Removes.Single(change =>
            change.ObjectId == target.ObjectId);
        CheckAssert.True(
            NetworkPresentationApply.MatchesExpectedRemove(
                removed,
                target.ObjectId,
                previousObjectRevision,
                currentChunk),
            "cave fill/restore Matches must succeed on the budgeted Remove");
        CheckAssert.True(
            observer.ClosedContainers.Contains(target.ObjectId),
            "a remove change must request the same container-close the window uses");
        CheckAssert.True(
            dispatcher.KnownObjectIds.Contains(target.ObjectId),
            "known ids retain removed objects so local chunk copies stay suppressed");
    }

    private sealed class RecordingWorldObjectObserver
        : INetworkWorldObjectChangeObserver
    {
        public List<NetworkWorldObjectChange> Upserts { get; } = [];
        public List<NetworkWorldObjectChange> Removes { get; } = [];
        public HashSet<Guid> ClosedContainers { get; } = [];
        public int SlicesApplied { get; private set; }

        public void OnRemoved(NetworkWorldObjectChange change)
        {
            Removes.Add(change);
            ClosedContainers.Add(change.ObjectId);
        }

        public void OnUpserted(
            NetworkWorldObjectChange change,
            NetworkWorldObjectState state)
        {
            _ = state;
            Upserts.Add(change);
        }

        public void OnSliceApplied(
            IReadOnlyList<NetworkWorldObjectChange> changes)
        {
            _ = changes;
            SlicesApplied++;
        }
    }

    private static async ValueTask
        HealthyClientAdvancesWhileLateJoinBlockedAsync(
            CancellationToken cancellationToken)
    {
        const int objectCount = 160;
        var seeds = Enumerable.Range(0, objectCount)
            .Select(index => new WorldObjectSeed(
                Guid.Parse($"a4000000-0000-0000-0000-{index + 1:D12}"),
                "large_rock",
                new Vector2((index % 8) * .1f, (index / 8) * .1f)))
            .ToArray();
        await using var fixture = await LoopbackChecks.StartHostAsync(
            cancellationToken, seeds);
        await using var healthy = new NetworkGameClient(TimeSpan.Zero);
        await fixture.ConnectAsync(healthy, "Healthy", cancellationToken);
        await LoopbackChecks.EventuallySharedAsync(
            () => healthy.State.Gameplay is not null &&
                  healthy.State.WorldObjects.Count >= objectCount,
            "the healthy client did not receive the large world bootstrap",
            cancellationToken);

        var writeBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockFirstWorldObject = 1;
        fixture.Server.BeforeOutboundWriteForTest = async (
            _, message, writerCancellation) =>
        {
            if (message is not WorldObjectStateMessage ||
                Interlocked.Exchange(ref blockFirstWorldObject, 0) == 0)
                return;
            writeBlocked.TrySetResult();
            await releaseWrite.Task.WaitAsync(writerCancellation)
                .ConfigureAwait(false);
        };

        var tickAtBlock = healthy.State.ServerTick;
        await using var late = new NetworkGameClient(TimeSpan.Zero);
        var lateJoin = fixture.ConnectAsync(late, "Late", cancellationToken);
        try
        {
            await writeBlocked.Task.WaitAsync(
                TimeSpan.FromSeconds(8), cancellationToken);
            var walk = await SendWalk(healthy, new(4, 0), cancellationToken);
            CheckAssert.True(walk.Accepted,
                $"a healthy walk was rejected during late join: {walk.Detail}");
            await LoopbackChecks.EventuallySharedAsync(
                () => healthy.State.ServerTick > tickAtBlock,
                "the healthy client stopped receiving advancing snapshots during late join",
                cancellationToken);
        }
        finally
        {
            releaseWrite.TrySetResult();
            fixture.Server.BeforeOutboundWriteForTest = null;
        }

        await lateJoin.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        CheckAssert.Equal(
            NetworkGameClientStatus.Connected,
            late.State.Status,
            "the late client must still complete after the healthy path stayed live");
    }

    private static async ValueTask
        DedicatedServerHandshakeAcceptsHostPortJoinAsync(
            CancellationToken cancellationToken)
    {
        await using var fixture = await LoopbackChecks.StartHostAsync(
            cancellationToken);
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        var accepted = await fixture.ConnectAsync(
            client, "PortJoin", cancellationToken);
        CheckAssert.True(accepted.WorldId != Guid.Empty, "handshake world id");
        CheckAssert.True(float.IsFinite(accepted.SpawnX), "handshake spawn");
        CheckAssert.True(accepted.Tick >= 0, "handshake tick");
        var handshakeTick = accepted.Tick;
        await LoopbackChecks.EventuallySharedAsync(
            () => client.State.ServerTick > handshakeTick,
            "a later snapshot tick must exceed the handshake tick",
            cancellationToken);
    }

    private static async Task<ActionResultMessage> SendAction(
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
            await client.SendActionAsync(payload, commandId, cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            client.ActionCompleted -= Handler;
        }
    }

    private static async Task<CommandResultMessage> SendWalk(
        NetworkGameClient client,
        Vector2 destination,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CommandResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NetworkCommandResultEventArgs args) =>
            completion.TrySetResult(args.Result);
        client.CommandCompleted += Handler;
        try
        {
            await client.SendWalkAsync(
                destination.X, destination.Y, 0, cancellationToken);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            client.CommandCompleted -= Handler;
        }
    }
}