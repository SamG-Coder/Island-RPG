using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused renewable-vegetation authority checks. The suite integrator should
/// register this class from Program.cs once all parallel slices have landed.
/// </summary>
internal static class VegetationResourceAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("resource authority gathers deterministic fibre and adventure XP", () =>
        {
            var fixture = Fixture(
                ResourceNodeKind.FibreShrub,
                inventory: new Dictionary<int, string>
                {
                    [0] = ItemIds.GatheringBasket
                });
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new GatherFibreTransaction(
                    fixture.Context, fixture.Reference, 1));

            CheckAssert.True(result.Accepted,
                "an in-range fibre harvest must commit");
            var gathered = result.RewardQuantity(ItemIds.PlantFibres);
            CheckAssert.True(gathered is >= 2 and <= 3,
                "fibre must roll one or two bundles plus its basket bonus");
            CheckAssert.Equal(0, result.Gameplay!.Value.FarmingExperience,
                "fibre gathering must not award farming experience");
            CheckAssert.Equal(
                AdventureService.AwardFromAction(0, gathered * 2).Experience,
                result.Gameplay.Value.AdventureExperience,
                "fibre must award solo-equivalent adventure experience");
            CheckAssert.True(result.NodeDelta!.Current.Depleted,
                "one fibre harvest must enter the renewable cooldown");
            CheckAssert.Equal(301d,
                result.NodeDelta.Current.ReadyAtGameSeconds,
                "fibre must use its canonical five-minute regrowth window");
        });

        checks.Add("resource authority validates exact sickles and berry rewards", () =>
        {
            var fixture = Fixture(
                ResourceNodeKind.BerryBush,
                inventory: new Dictionary<int, string>
                {
                    [0] = ItemIds.GatheringBasket,
                    [4] = ItemIds.StoneSickle
                });
            var wrong = fixture.Authority.Execute(
                fixture.Actor,
                new GatherBerriesTransaction(
                    fixture.Context, fixture.Reference, 3, 2));
            CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
                wrong.Status,
                "berry gathering must not silently choose a sickle from another slot");
            CheckAssert.Equal((uint)0,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "an invalid tool selection must not mutate the bush");

            var result = fixture.Authority.Execute(
                fixture.Actor,
                new GatherBerriesTransaction(
                    fixture.Context with { CommandId = Guid.NewGuid() },
                    fixture.Reference, 4, 2));
            CheckAssert.True(result.Accepted,
                "the exact usable sickle slot must be accepted");
            var gathered = result.RewardQuantity(fixture.ItemId);
            CheckAssert.True(gathered is >= 2 and <= 6,
                "berries must include the deterministic base, basket and optional sickle bonus");
            CheckAssert.Equal(18 * gathered,
                result.Gameplay!.Value.FarmingExperience,
                "farming XP must be based on berries actually carried");
            CheckAssert.Equal(
                AdventureService.AwardFromAction(0, 18 * gathered).Experience,
                result.Gameplay.Value.AdventureExperience,
                "berry farming XP gained must feed the adventure award");
            CheckAssert.Equal(722d,
                result.NodeDelta!.Current.ReadyAtGameSeconds,
                "berries must use their canonical twelve-minute regrowth window");
        });

        checks.Add("renewable harvest carries partial yields and rejects zero capacity atomically", () =>
        {
            var partialItems = Enumerable.Range(0, PlayerInventory.Capacity - 1)
                .ToDictionary(static slot => slot,
                    static slot => slot == 0
                        ? ItemIds.GatheringBasket
                        : ItemIds.LargeRock);
            var partial = Fixture(
                ResourceNodeKind.FibreShrub,
                inventory: partialItems);
            var accepted = partial.Authority.Execute(
                partial.Actor,
                new GatherFibreTransaction(
                    partial.Context, partial.Reference, 1));
            CheckAssert.True(accepted.Accepted,
                "a harvest must commit when at least one rolled item fits");
            CheckAssert.Equal(1,
                accepted.RewardQuantity(ItemIds.PlantFibres),
                "only the one fibre bundle which fits may be carried");
            CheckAssert.True(!string.IsNullOrWhiteSpace(accepted.Detail),
                "partial harvest overflow must be observable");

            var fullItems = Enumerable.Range(0, PlayerInventory.Capacity)
                .ToDictionary(static slot => slot,
                    static _ => ItemIds.LargeRock);
            var full = Fixture(
                ResourceNodeKind.FibreShrub,
                inventory: fullItems);
            var rejected = full.Authority.Execute(
                full.Actor,
                new GatherFibreTransaction(
                    full.Context, full.Reference, 1));
            CheckAssert.Equal(ResourceTransactionStatus.InventoryFull,
                rejected.Status,
                "a harvest with zero capacity must reject");
            CheckAssert.Equal((uint)0,
                full.Authority.CaptureChunkRevision(full.Chunk),
                "zero-capacity rejection must not mutate or start cooldown");

            var room = full.Actor with
            {
                Gameplay = Gameplay()
            };
            var retry = full.Authority.Execute(
                room,
                new GatherFibreTransaction(
                    full.Context with { CommandId = Guid.NewGuid() },
                    full.Reference, 1));
            CheckAssert.True(retry.Accepted,
                "an inventory rejection must not consume authoritative cadence");
        });

        checks.Add("due vegetation regrowth and harvest publish one atomic revision jump", () =>
        {
            var fixture = Fixture(ResourceNodeKind.FibreShrub);
            var first = fixture.Authority.Execute(
                fixture.Actor,
                new GatherFibreTransaction(
                    fixture.Context, fixture.Reference, 1));
            var nextActor = fixture.Actor with
            {
                Gameplay = first.Gameplay!.Value
            };
            var nextContext = fixture.Context with
            {
                CommandId = Guid.NewGuid(),
                ExpectedActorRevision = first.ActorRevision,
                ExpectedInventoryRevision = first.InventoryRevision
            };
            var nextReference = fixture.Reference with
            {
                ExpectedNodeRevision = 1,
                ExpectedResourceChunkRevision = 1
            };
            var early = fixture.Authority.Execute(
                nextActor,
                new GatherFibreTransaction(
                    nextContext, nextReference, 300.999));
            CheckAssert.Equal(ResourceTransactionStatus.Depleted,
                early.Status,
                "a renewable node must remain depleted before its deadline");
            CheckAssert.Equal((uint)1,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "an early observation must not materialize regrowth");

            var due = fixture.Authority.Execute(
                nextActor,
                new GatherFibreTransaction(
                    nextContext with { CommandId = Guid.NewGuid() },
                    nextReference, 301));
            CheckAssert.True(due.Accepted,
                "a command at the exact deadline must regrow and harvest");
            CheckAssert.Equal((uint)1,
                due.NodeDelta!.Previous.NodeRevision,
                "the atomic delta must begin at the client's depleted revision");
            CheckAssert.Equal((uint)3,
                due.NodeDelta.Current.NodeRevision,
                "regrowth and harvest must each advance the node revision");
            CheckAssert.Equal((uint)1,
                due.ChunkDelta!.Value.PreviousRevision,
                "the atomic chunk delta must begin at the referenced revision");
            CheckAssert.Equal((uint)3,
                due.ChunkDelta.Value.CurrentRevision,
                "regrowth and harvest must each advance the chunk revision");
            CheckAssert.True(due.NodeDelta.Current.Depleted,
                "the due resource must begin a fresh cooldown after harvest");
            CheckAssert.Equal(601d,
                due.NodeDelta.Current.ReadyAtGameSeconds,
                "the next fibre deadline must be based on accepted server time");
        });

        checks.Add("renewable catch-up remains deterministic across checkpoint restore", () =>
        {
            var fixture = Fixture(
                ResourceNodeKind.BerryBush,
                actorId: new ActorId(Guid.Parse(
                    "7b000000-0000-0000-0000-000000000001")));
            var first = fixture.Authority.Execute(
                fixture.Actor,
                new GatherBerriesTransaction(
                    fixture.Context, fixture.Reference, -1, 10));
            var checkpoint = fixture.Authority.CaptureCheckpoint();
            var actor = fixture.Actor with { Gameplay = first.Gameplay!.Value };
            var context = fixture.Context with
            {
                CommandId = Guid.NewGuid(),
                ExpectedActorRevision = first.ActorRevision,
                ExpectedInventoryRevision = first.InventoryRevision
            };
            var reference = fixture.Reference with
            {
                ExpectedNodeRevision = 1,
                ExpectedResourceChunkRevision = 1
            };

            var left = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);
            var right = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);
            left.RestoreCheckpoint(checkpoint);
            right.RestoreCheckpoint(checkpoint);
            var leftResult = left.Execute(
                actor,
                new GatherBerriesTransaction(
                    context, reference, -1, 730));
            var rightResult = right.Execute(
                actor,
                new GatherBerriesTransaction(
                    context with { CommandId = Guid.NewGuid() },
                    reference, -1, 730));

            CheckAssert.True(leftResult.Accepted && rightResult.Accepted,
                "both restored authorities must process due catch-up");
            CheckAssert.SequenceEqual(leftResult.Rewards, rightResult.Rewards,
                "persisted action ordinal must reproduce the same catch-up yield");
            CheckAssert.Equal(leftResult.NodeDelta!.Current,
                rightResult.NodeDelta!.Current,
                "catch-up lifecycle state must be restart deterministic");
            CheckAssert.Equal((uint)3,
                left.CaptureChunkRevision(fixture.Chunk),
                "the restored chunk must retain both catch-up revisions");
            CheckAssert.Equal(1,
                left.CaptureCheckpoint().ActorCadences.Length,
                "catch-up must preserve one bounded actor/action cadence");
        });

        checks.Add("resource session separates real cadence from accelerated regrowth", async _ =>
        {
            var fixture = Fixture(ResourceNodeKind.FibreShrub);
            var session = new AuthoritativeWorldSession(
                identitySource: new FixedIdentitySource(),
                resourceTransactions: fixture.Authority);
            var connection = ClientConnectionId.New();
            var joinTask = session.EnqueueJoinAsync(new(
                connection, "Clock Tester", fixture.Actor.Position));
            session.Drain();
            var join = await joinTask;

            var firstTask = session.EnqueueIntentAsync(new(
                connection,
                join.Identity.PlayerId,
                1,
                new GatherFibreIntent(
                    Guid.NewGuid(),
                    join.Gameplay.Inventory.Revision,
                    join.Gameplay.ActorRevision,
                    fixture.Reference)));
            session.Drain();
            var first = await firstTask;
            CheckAssert.True(first.Accepted,
                "the first server-time fibre harvest must commit");
            var firstResource = first.ResourceTransaction!;
            CheckAssert.Equal(
                AuthoritativeWorldTime.FromElapsedRealSeconds(0) + 300,
                firstResource.NodeDelta!.Current.ReadyAtGameSeconds,
                "renewable readiness must be stamped in accelerated world time");
            CheckAssert.Equal(.75,
                fixture.Authority.CaptureCheckpoint()
                    .ActorCadences.Single().ReadyAtGameSeconds,
                "the persisted interaction cadence must remain elapsed real time");

            var depletedReference = new ResourceNodeReference(
                firstResource.NodeDelta.Current.Id,
                firstResource.NodeDelta.Current.Chunk,
                firstResource.NodeDelta.Current.NodeRevision,
                firstResource.ChunkDelta!.Value.CurrentRevision);
            var immediateTask = session.EnqueueIntentAsync(new(
                connection,
                join.Identity.PlayerId,
                2,
                new GatherFibreIntent(
                    Guid.NewGuid(),
                    first.InventoryRevision,
                    first.ActorRevision,
                    depletedReference)));
            session.Drain();
            var immediate = await immediateTask;
            CheckAssert.Equal(IntentStatus.ResourceCadenceLocked,
                immediate.Status,
                "accelerated world time must not bypass a real-time action cadence");

            for (var tick = 0; tick < 45; tick++) session.Tick();
            var earlyTask = session.EnqueueIntentAsync(new(
                connection,
                join.Identity.PlayerId,
                3,
                new GatherFibreIntent(
                    Guid.NewGuid(),
                    first.InventoryRevision,
                    first.ActorRevision,
                    depletedReference)));
            session.Drain();
            var early = await earlyTask;
            CheckAssert.Equal(IntentStatus.ResourceDepleted, early.Status,
                "the real cadence may elapse before the accelerated five-minute regrowth deadline");

            for (var tick = 45; tick < 300; tick++) session.Tick();
            var currentGameplay = session.CaptureSnapshot().Actors
                .Single(actor => actor.PlayerId == join.Identity.PlayerId)
                .Gameplay;
            var dueTask = session.EnqueueIntentAsync(new(
                connection,
                join.Identity.PlayerId,
                4,
                new GatherFibreIntent(
                    Guid.NewGuid(),
                    currentGameplay.Inventory.Revision,
                    currentGameplay.ActorRevision,
                    depletedReference)));
            session.Drain();
            var due = await dueTask;
            CheckAssert.True(due.Accepted,
                "five real seconds must advance the renewable node by five game minutes");
            CheckAssert.Equal(
                AuthoritativeWorldTime.FromElapsedRealSeconds(5) + 300,
                due.ResourceTransaction!.NodeDelta!.Current
                    .ReadyAtGameSeconds,
                "the next renewable deadline must remain in the world-time domain");
        });

        checks.Add("resource session replays vegetation receipts after restart", async _ =>
        {
            var fixture = Fixture(ResourceNodeKind.FibreShrub);
            var sessionId = new SessionId(Guid.NewGuid());
            var identities = new FixedIdentitySource();
            var session = new AuthoritativeWorldSession(
                identitySource: identities,
                sessionId: sessionId,
                resourceTransactions: fixture.Authority);
            var firstConnection = ClientConnectionId.New();
            var joinTask = session.EnqueueJoinAsync(new(
                firstConnection, "Fibre Tester", fixture.Actor.Position));
            session.Drain();
            var join = await joinTask;
            var commandId = Guid.NewGuid();
            var intent = new GatherFibreIntent(
                commandId,
                join.Gameplay.Inventory.Revision,
                join.Gameplay.ActorRevision,
                fixture.Reference);
            var actionTask = session.EnqueueIntentAsync(new(
                firstConnection, join.Identity.PlayerId, 1, intent));
            session.Drain();
            var action = await actionTask;
            CheckAssert.True(action.Accepted,
                "the initial vegetation intent must commit");
            var checkpoint = session.CaptureCheckpoint();

            var restoredAuthority = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);
            var restored = new AuthoritativeWorldSession(
                identitySource: new FixedIdentitySource(),
                sessionId: sessionId,
                resourceTransactions: restoredAuthority);
            restored.RestoreCheckpoint(checkpoint);
            var secondConnection = ClientConnectionId.New();
            var reconnectTask = restored.EnqueueReconnectAsync(new(
                secondConnection,
                join.Identity.PlayerId,
                join.ReconnectToken));
            restored.Drain();
            var reconnect = await reconnectTask;
            CheckAssert.True(reconnect.Accepted,
                "the persisted actor must reconnect with its private token");
            var replayTask = restored.EnqueueIntentAsync(new(
                secondConnection, join.Identity.PlayerId,
                reconnect.NextCommandSequence, intent));
            restored.Drain();
            var replay = await replayTask;

            CheckAssert.True(replay.Duplicate,
                "a persisted command receipt must replay without re-harvesting");
            CheckAssert.Equal((uint)1,
                restoredAuthority.CaptureChunkRevision(fixture.Chunk),
                "receipt replay must leave the resource at its persisted revision");
            CheckAssert.Equal(action.InventoryRevision,
                replay.InventoryRevision,
                "receipt replay must preserve exact inventory state");
        });

        checks.Add(
            "in-range fibre gather still commits after the PresentSkill windup",
            async _ =>
        {
            var fixture = Fixture(ResourceNodeKind.FibreShrub);
            var session = new AuthoritativeWorldSession(
                identitySource: new FixedIdentitySource(),
                resourceTransactions: fixture.Authority);
            var connection = ClientConnectionId.New();
            var joinTask = session.EnqueueJoinAsync(new(
                connection, "Windup Tester", fixture.Actor.Position));
            session.Drain();
            var join = await joinTask;
            var presentTask = session.EnqueueIntentAsync(new(
                connection, join.Identity.PlayerId, 1,
                new PresentSkillIntent(EntityAction.Gather)));
            session.Drain();
            CheckAssert.True((await presentTask).Accepted,
                "an in-range gather must publish Gather before the mutation");

            for (var tick = 0; tick < ActorSkillStance.OneShotTicks; tick++)
                session.Tick();
            CheckAssert.Equal(
                EntityAction.Idle,
                ActorSkillStance.UnpackAction(
                    session.CaptureSnapshot().Actors.Single().AnimationState),
                "the published gather clip must expire before the late commit");

            var actor = session.CaptureSnapshot().Actors.Single();
            var gatherTask = session.EnqueueIntentAsync(new(
                connection, join.Identity.PlayerId, 2,
                new GatherFibreIntent(
                    Guid.NewGuid(),
                    actor.Gameplay.Inventory.Revision,
                    actor.Gameplay.ActorRevision,
                    fixture.Reference)));
            session.Drain();
            var gather = await gatherTask;
            CheckAssert.True(gather.Accepted,
                "starting in range must still yield the item after the windup");
            CheckAssert.True(
                gather.ResourceTransaction?.Rewards.Any(value =>
                    value.Quantity > 0) == true,
                "the late in-range fibre commit must award the gathered item");
            CheckAssert.Equal(
                EntityAction.Idle,
                ActorSkillStance.UnpackAction(
                    session.CaptureSnapshot().Actors.Single().AnimationState),
                "the late fibre commit must not start a second Gather clip");
        });

        checks.Add("vegetation intent fingerprints include action and exact tool slot", () =>
        {
            var fixture = Fixture(ResourceNodeKind.BerryBush);
            var commandId = Guid.NewGuid();
            var bare = new GatherBerriesIntent(
                commandId, 1, 1, fixture.Reference, -1);
            var sickle = bare with { ToolInventorySlot = 4 };
            var fibre = new GatherFibreIntent(
                commandId, 1, 1, fixture.Reference);
            CheckAssert.False(
                GameplayIntentFingerprint.Create(bare) ==
                GameplayIntentFingerprint.Create(sickle),
                "the selected sickle slot must bind the idempotency receipt");
            CheckAssert.False(
                GameplayIntentFingerprint.Create(bare) ==
                GameplayIntentFingerprint.Create(fibre),
                "different vegetation actions must never share a fingerprint");
        });
    }

    private static VegetationFixture Fixture(
        ResourceNodeKind kind,
        Dictionary<int, string>? inventory = null,
        ActorId? actorId = null)
    {
        const long worldSeed = 934_771;
        var chunk = new WorldChunkKey(0, 0, 0);
        var visual = Enumerable.Range(0, 8 << 5)
            .Select(variant => SurfaceVegetationCatalog.TryGetVisual(
                    variant, out var candidate)
                ? candidate
                : default)
            .First(candidate => candidate.ResourceKind == kind);
        var source = new FixedResourceSource([
            new(
                ProceduralResourceKey.Vegetation(
                    kind, 2, 2, 0, visual.Variant),
                new Vector2(2.5f, 2.5f),
                InitialRemaining: visual.InitialRemaining,
                RegrowthGameSeconds: visual.RegrowthGameSeconds)
        ]);
        var catalog = new ProceduralResourceCatalog(source);
        var descriptor = catalog.DescribeChunk(worldSeed, chunk).Single();
        var gameplay = Gameplay(inventory);
        var identity = actorId ?? new ActorId(Guid.Parse(
            "7a000000-0000-0000-0000-000000000001"));
        var actor = new WorldTransactionActorInput(
            identity, new Vector2(2.4f, 2.4f), 0, gameplay);
        var context = new WorldTransactionContext(
            Guid.NewGuid(), identity,
            gameplay.ActorRevision, gameplay.Inventory.Revision);
        return new(
            worldSeed,
            chunk,
            catalog,
            new AuthoritativeResourceTransactions(worldSeed, catalog),
            actor,
            context,
            new ResourceNodeReference(descriptor.Id, chunk, 0, 0),
            visual.GatherItemId!);
    }

    private static PlayerGameplaySnapshot Gameplay(
        IReadOnlyDictionary<int, string>? items = null)
    {
        var slots = Enumerable.Range(0, PlayerInventory.Capacity)
            .Select(slot => items is not null &&
                            items.TryGetValue(slot, out var itemId)
                ? new InventorySlotSnapshot(slot, itemId, 1)
                : new InventorySlotSnapshot(slot, null, 0))
            .ToImmutableArray();
        return new(
            1, 100, 100, 0, 0, 0,
            new PlayerInventorySnapshot(1, slots));
    }

    private sealed class FixedResourceSource(
        IReadOnlyList<ProceduralResourceSeed> values) :
        IProceduralResourceDescriptorSource
    {
        public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk) =>
            chunk == new WorldChunkKey(0, 0, 0) ? values : [];
    }

    private sealed class FixedIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "7c000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse(
                "7d000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() =>
            new("vegetation-resource-authority-secret");
    }

    private sealed record VegetationFixture(
        long WorldSeed,
        WorldChunkKey Chunk,
        IResourceDescriptorResolver Catalog,
        AuthoritativeResourceTransactions Authority,
        WorldTransactionActorInput Actor,
        WorldTransactionContext Context,
        ResourceNodeReference Reference,
        string ItemId);
}
