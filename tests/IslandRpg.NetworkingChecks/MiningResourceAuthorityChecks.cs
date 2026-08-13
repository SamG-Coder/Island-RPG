using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused underground-mining authority checks. Register from Program.cs
/// alongside the other resource transaction suites.
/// </summary>
internal static class MiningResourceAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("resource authority mines with exact selected pickaxe", () =>
        {
            var fixture = Fixture(
                maximumHealth: 85,
                inventory: new Dictionary<int, string>
                {
                    [2] = ItemIds.StonePickaxe,
                    [7] = ItemIds.IronPickaxe
                });
            var wrong = fixture.Authority.Execute(
                fixture.Actor,
                new MineResourceTransaction(
                    fixture.Context, fixture.Reference, 1, 2));
            CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
                wrong.Status,
                "mining must not silently choose a pickaxe from another slot");
            CheckAssert.Equal((uint)0,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "an invalid exact slot must not mutate the node or cadence");

            var result = fixture.Authority.Execute(
                fixture.Actor,
                new MineResourceTransaction(
                    fixture.Context with { CommandId = Guid.NewGuid() },
                    fixture.Reference, 7, 2));
            CheckAssert.True(result.Accepted,
                "the exact iron-pickaxe slot must be accepted");
            CheckAssert.False(result.ToolWorn,
                "mining must preserve solo parity where pickaxes do not wear");
        });

        checks.Add("resource authority mining matches solo damage and XP", () =>
        {
            var fixture = FindFixture(static result => result.Hit &&
                result.NodeDelta?.Current.Depleted == false,
                maximumHealth: 85);
            var result = fixture.Result;
            CheckAssert.True(result.Damage is >= 4 and <= 9,
                "a level-one stone pickaxe must use the canonical 4-9 damage range");
            CheckAssert.Equal(result.Damage,
                result.Gameplay!.Value.MiningExperience,
                "non-final hits must award actual damage as Mining XP");
            CheckAssert.Equal(
                AdventureService.AwardFromAction(0, result.Damage).Experience,
                result.Gameplay.Value.AdventureExperience,
                "Mining XP gained must feed canonical Adventure XP");
            CheckAssert.Equal(0, result.Rewards.Length,
                "non-final mining hits must not grant the completion reward");
            CheckAssert.Equal((uint)1,
                result.NodeDelta!.Current.NodeRevision,
                "a hit must advance the sparse node exactly once");
            CheckAssert.Equal((uint)1,
                result.ChunkDelta!.Value.CurrentRevision,
                "a hit must advance the resource chunk exactly once");
        });

        checks.Add("resource authority mining final hit grants configured reward", () =>
        {
            var fixture = FindFixture(static result => result.Hit &&
                result.NodeDelta?.Current.Depleted == true,
                maximumHealth: 1);
            var result = fixture.Result;
            CheckAssert.Equal(1, result.Damage,
                "a final strike may deal only the node's remaining health");
            CheckAssert.Equal(29,
                result.Gameplay!.Value.MiningExperience,
                "tin completion must award one damage plus 28 completion XP");
            CheckAssert.Equal(1,
                result.RewardQuantity(ItemIds.TinOre),
                "a depleted tin node must grant exactly one tin ore");
            CheckAssert.True(result.NodeDelta!.Current.Depleted,
                "the final hit must persist permanent depletion");

            var noReward = FindFixture(static result => result.Hit &&
                result.NodeDelta?.Current.Depleted == true,
                maximumHealth: 1,
                variant: UndergroundMiningVariant.JaggedRock);
            CheckAssert.Equal(0, noReward.Result.Rewards.Length,
                "destructible formations without configured loot must award no item");
            CheckAssert.Equal(41,
                noReward.Result.Gameplay!.Value.MiningExperience,
                "no-loot formations must still award damage and completion XP");
        });

        checks.Add("mining reward capacity rejects before cadence or damage", () =>
        {
            var fullItems = Enumerable.Range(0, PlayerInventory.Capacity)
                .ToDictionary(static slot => slot,
                    static _ => ItemIds.LargeRock);
            fullItems[4] = ItemIds.StonePickaxe;
            var fixture = Fixture(
                maximumHealth: 85,
                inventory: fullItems);
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new MineResourceTransaction(
                    fixture.Context, fixture.Reference, 4, 2));
            CheckAssert.Equal(ResourceTransactionStatus.InventoryFull,
                result.Status,
                "reward-bearing nodes must preserve solo's capacity precondition");
            CheckAssert.Equal((uint)0,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "capacity rejection must not damage the mining node");
            CheckAssert.Equal(0,
                fixture.Authority.CaptureCheckpoint().ActorCadences.Length,
                "capacity rejection must not consume mining cadence");

            var formation = Fixture(
                maximumHealth: 135,
                inventory: fullItems,
                variant: UndergroundMiningVariant.JaggedRock);
            var formationResult = formation.Authority.Execute(
                formation.Actor,
                new MineResourceTransaction(
                    formation.Context, formation.Reference, 4, 2));
            CheckAssert.True(formationResult.Accepted,
                "no-reward formations must remain mineable with a full inventory");
        });

        checks.Add("mining misses commit cadence but no public mutation", () =>
        {
            var fixture = FindFixture(static result => !result.Hit,
                maximumHealth: 85);
            var result = fixture.Result;
            CheckAssert.True(result.Accepted,
                "a valid authoritative miss is an accepted gameplay result");
            CheckAssert.Equal(0, result.Damage,
                "a miss must deal no damage");
            CheckAssert.True(result.NodeDelta is null &&
                             result.ChunkDelta is null,
                "a miss must not publish a fake resource mutation");
            CheckAssert.Equal(fixture.Fixture.Actor.Gameplay.ActorRevision,
                result.ActorRevision,
                "a miss must not invent actor progression revisions");
            var locked = fixture.Fixture.Authority.Execute(
                fixture.Fixture.Actor,
                new MineResourceTransaction(
                    fixture.Fixture.Context with { CommandId = Guid.NewGuid() },
                    fixture.Fixture.Reference, fixture.Fixture.ToolSlot,
                    2.5));
            CheckAssert.Equal(ResourceTransactionStatus.CadenceLocked,
                locked.Status,
                "accepted misses must still enforce server-owned cadence");
            CheckAssert.Equal(1UL,
                fixture.Fixture.Authority.CaptureCheckpoint()
                    .ActorCadences.Single().ActionOrdinal,
                "a miss must persist its deterministic roll ordinal");
        });

        checks.Add("mining remains deterministic across checkpoint restore", () =>
        {
            var first = FindFixture(static result => result.Hit,
                maximumHealth: 85);
            var checkpoint = first.Fixture.Authority.CaptureCheckpoint();
            var actor = first.Fixture.Actor with
            {
                Gameplay = first.Result.Gameplay!.Value
            };
            var context = first.Fixture.Context with
            {
                CommandId = Guid.NewGuid(),
                ExpectedActorRevision = first.Result.ActorRevision,
                ExpectedInventoryRevision = first.Result.InventoryRevision
            };
            var reference = first.Fixture.Reference with
            {
                ExpectedNodeRevision = 1,
                ExpectedResourceChunkRevision = 1
            };
            var left = new AuthoritativeResourceTransactions(
                first.Fixture.WorldSeed, first.Fixture.Catalog);
            var right = new AuthoritativeResourceTransactions(
                first.Fixture.WorldSeed, first.Fixture.Catalog);
            left.RestoreCheckpoint(checkpoint);
            right.RestoreCheckpoint(checkpoint);
            var leftResult = left.Execute(
                actor,
                new MineResourceTransaction(
                    context, reference, first.Fixture.ToolSlot, 4));
            var rightResult = right.Execute(
                actor,
                new MineResourceTransaction(
                    context with { CommandId = Guid.NewGuid() },
                    reference, first.Fixture.ToolSlot, 4));

            CheckAssert.Equal(leftResult.Hit, rightResult.Hit,
                "restored mining must reproduce the same next accuracy roll");
            CheckAssert.Equal(leftResult.Damage, rightResult.Damage,
                "restored mining must reproduce the same next damage roll");
            AssertGameplayEqual(
                leftResult.Gameplay!.Value,
                rightResult.Gameplay!.Value,
                "restored mining must reproduce the same progression state");
            CheckAssert.SequenceEqual(leftResult.Rewards, rightResult.Rewards,
                "restored mining must reproduce completion rewards");
        });

        checks.Add("resource session replays mining receipts after restart", async _ =>
        {
            var fixture = FindFixture(static result => result.Hit,
                maximumHealth: 85).Fixture;
            // Use a fresh aggregate because finding the deterministic actor
            // above consumed the trial fixture.
            fixture = Fixture(
                maximumHealth: 85,
                actorId: fixture.Actor.ActorId);
            var sessionId = new SessionId(Guid.NewGuid());
            var session = new AuthoritativeWorldSession(
                identitySource: new FixedIdentitySource(
                    fixture.Actor.ActorId),
                sessionId: sessionId,
                resourceTransactions: fixture.Authority);
            var connection = ClientConnectionId.New();
            var joinTask = session.EnqueueJoinAsync(new(
                connection,
                "Mining Tester",
                fixture.Actor.Position,
                [new InitialInventoryItem(ItemIds.StonePickaxe)],
                SpawnWorldLevel: -1));
            session.Drain();
            var join = await joinTask;
            var commandId = Guid.NewGuid();
            var intent = new MineResourceIntent(
                commandId,
                join.Gameplay.Inventory.Revision,
                join.Gameplay.ActorRevision,
                fixture.Reference,
                ToolInventorySlot: 0);
            var actionTask = session.EnqueueIntentAsync(new(
                connection, join.Identity.PlayerId, 1, intent));
            session.Drain();
            var action = await actionTask;
            CheckAssert.True(action.Accepted,
                "the initial session mining intent must commit");
            var checkpoint = session.CaptureCheckpoint();

            var restoredAuthority = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);
            var restored = new AuthoritativeWorldSession(
                identitySource: new FixedIdentitySource(
                    fixture.Actor.ActorId),
                sessionId: sessionId,
                resourceTransactions: restoredAuthority);
            restored.RestoreCheckpoint(checkpoint);
            var reconnectConnection = ClientConnectionId.New();
            var reconnectTask = restored.EnqueueReconnectAsync(new(
                reconnectConnection,
                join.Identity.PlayerId,
                join.ReconnectToken));
            restored.Drain();
            var reconnect = await reconnectTask;
            var replayTask = restored.EnqueueIntentAsync(new(
                reconnectConnection,
                join.Identity.PlayerId,
                reconnect.NextCommandSequence,
                intent));
            restored.Drain();
            var replay = await replayTask;

            CheckAssert.True(reconnect.Accepted && replay.Duplicate,
                "a persisted mining command must reconnect and replay its receipt");
            CheckAssert.True(replay.ResourceTransaction is null,
                "durable replay must not expose a stale transaction delta as new public state");
            CheckAssert.Equal(action.Status, replay.Status,
                "durable mining receipt replay must preserve its accepted outcome");
            CheckAssert.Equal(action.ActorRevision, replay.ActorRevision,
                "durable mining receipt replay must preserve private actor state");
            CheckAssert.Equal(
                action.ResourceTransaction?.ChunkDelta?.CurrentRevision ?? 0,
                restoredAuthority.CaptureChunkRevision(fixture.Chunk),
                "receipt replay must not strike the mining node twice");
        });

        checks.Add("mining intent fingerprint binds exact tool and action", () =>
        {
            var fixture = Fixture(maximumHealth: 85);
            var commandId = Guid.NewGuid();
            var first = new MineResourceIntent(
                commandId, 1, 1, fixture.Reference, 0);
            var second = first with { ToolInventorySlot = 1 };
            var tree = new StrikeTreeIntent(
                commandId, 1, 1, fixture.Reference, 0);
            CheckAssert.False(
                GameplayIntentFingerprint.Create(first) ==
                GameplayIntentFingerprint.Create(second),
                "the selected pickaxe slot must bind mining idempotency");
            CheckAssert.False(
                GameplayIntentFingerprint.Create(first) ==
                GameplayIntentFingerprint.Create(tree),
                "mining and tree actions must never share a receipt fingerprint");
        });
    }

    private static (MiningFixture Fixture, ResourceTransactionResult Result)
        FindFixture(
            Predicate<ResourceTransactionResult> predicate,
            int maximumHealth,
            UndergroundMiningVariant variant = UndergroundMiningVariant.Tin)
    {
        for (var attempt = 1; attempt <= 512; attempt++)
        {
            var actorId = new ActorId(Guid.Parse(
                $"8a000000-0000-0000-0000-{attempt:D12}"));
            var fixture = Fixture(maximumHealth, actorId: actorId,
                variant: variant);
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new MineResourceTransaction(
                    fixture.Context, fixture.Reference,
                    fixture.ToolSlot, 2));
            if (predicate(result)) return (fixture, result);
        }
        throw new InvalidOperationException(
            "A deterministic mining fixture matching the requested roll was not found.");
    }

    private static MiningFixture Fixture(
        int maximumHealth,
        IReadOnlyDictionary<int, string>? inventory = null,
        ActorId? actorId = null,
        UndergroundMiningVariant variant = UndergroundMiningVariant.Tin)
    {
        const long worldSeed = 841_773;
        var chunk = new WorldChunkKey(0, 0, -1);
        if (!UndergroundMiningCatalog.TryGetVisual(
                (int)variant, out var visual))
            throw new InvalidOperationException("Unknown mining fixture variant.");
        var source = new FixedResourceSource([
            new(
                ProceduralResourceKey.Mining(2, 2, 0, (int)variant),
                new Vector2(2.5f, 2.5f),
                InitialHealth: maximumHealth,
                MaximumHealth: visual.MaximumHealth)
        ]);
        var catalog = new ProceduralResourceCatalog(source);
        var descriptor = catalog.DescribeChunk(worldSeed, chunk).Single();
        var gameplay = Gameplay(inventory);
        var identity = actorId ?? new ActorId(Guid.Parse(
            "8b000000-0000-0000-0000-000000000001"));
        var actor = new WorldTransactionActorInput(
            identity, new Vector2(2.4f, 2.4f), -1, gameplay);
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
            ToolSlot: inventory?.FirstOrDefault(value =>
                    value.Value == ItemIds.StonePickaxe).Key ?? 0);
    }

    private static PlayerGameplaySnapshot Gameplay(
        IReadOnlyDictionary<int, string>? items = null)
    {
        items ??= new Dictionary<int, string>
        {
            [0] = ItemIds.StonePickaxe
        };
        var slots = Enumerable.Range(0, PlayerInventory.Capacity)
            .Select(slot => items.TryGetValue(slot, out var itemId)
                ? new InventorySlotSnapshot(slot, itemId, 1)
                : new InventorySlotSnapshot(slot, null, 0))
            .ToImmutableArray();
        return new(
            1, 100, 100, 0, 0, 0,
            new PlayerInventorySnapshot(1, slots));
    }

    private static void AssertGameplayEqual(
        PlayerGameplaySnapshot expected,
        PlayerGameplaySnapshot actual,
        string message)
    {
        CheckAssert.Equal(expected.ActorRevision, actual.ActorRevision,
            message);
        CheckAssert.Equal(expected.Inventory.Revision,
            actual.Inventory.Revision, message);
        CheckAssert.SequenceEqual(expected.Inventory.Slots,
            actual.Inventory.Slots, message);
        CheckAssert.Equal(expected.MiningExperience,
            actual.MiningExperience, message);
        CheckAssert.Equal(expected.AdventureExperience,
            actual.AdventureExperience, message);
    }

    private sealed class FixedResourceSource(
        IReadOnlyList<ProceduralResourceSeed> values) :
        IProceduralResourceDescriptorSource
    {
        public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk) =>
            chunk == new WorldChunkKey(0, 0, -1) ? values : [];
    }

    private sealed class FixedIdentitySource(ActorId actorId) :
        ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "8c000000-0000-0000-0000-000000000001")),
            actorId);

        public ReconnectToken CreateReconnectToken() =>
            new("mining-resource-authority-secret");
    }

    private sealed record MiningFixture(
        long WorldSeed,
        WorldChunkKey Chunk,
        IResourceDescriptorResolver Catalog,
        AuthoritativeResourceTransactions Authority,
        WorldTransactionActorInput Actor,
        WorldTransactionContext Context,
        ResourceNodeReference Reference,
        int ToolSlot);
}
