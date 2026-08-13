using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused resource-authority checks. Registration is intentionally left to
/// the milestone integrator so this parallel slice does not edit Program.cs.
/// </summary>
internal static class ResourceAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("resource authority gathers one tree stick atomically", () =>
        {
            var fixture = Fixture();
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new GatherTreeStickTransaction(
                    fixture.Context,
                    fixture.Reference,
                    1));

            CheckAssert.True(result.Accepted,
                "a valid in-range gather must commit");
            CheckAssert.Equal(1, result.RewardQuantity(ItemIds.Sticks),
                "the authoritative reward must contain one stick");
            CheckAssert.Equal(2, result.NodeDelta!.Current.Remaining,
                "one and only one procedural stick must be consumed");
            CheckAssert.Equal((uint)1,
                result.ChunkDelta!.Value.CurrentRevision,
                "the sparse resource chunk must advance exactly once");
            CheckAssert.Equal(1,
                result.Gameplay!.Value.Inventory.Slots.Count(
                    value => value.ItemId == ItemIds.Sticks),
                "the actor inventory must receive exactly one stick");
        });

        checks.Add("resource authority rejects stale and rapid commands", () =>
        {
            var fixture = Fixture();
            var first = fixture.Authority.Execute(
                fixture.Actor,
                new GatherTreeStickTransaction(
                    fixture.Context, fixture.Reference, 1));
            var rapid = fixture.Authority.Execute(
                fixture.Actor with { Gameplay = first.Gameplay!.Value },
                new GatherTreeStickTransaction(
                    fixture.Context with
                    {
                        CommandId = Guid.NewGuid(),
                        ExpectedActorRevision = first.ActorRevision,
                        ExpectedInventoryRevision = first.InventoryRevision
                    },
                    fixture.Reference with
                    {
                        ExpectedNodeRevision = 1,
                        ExpectedResourceChunkRevision = 1
                    },
                    1.1));
            CheckAssert.Equal(ResourceTransactionStatus.CadenceLocked,
                rapid.Status,
                "server time must rate-limit a second gather");

            var stale = fixture.Authority.Execute(
                fixture.Actor with { Gameplay = first.Gameplay.Value },
                new GatherTreeStickTransaction(
                    fixture.Context with
                    {
                        CommandId = Guid.NewGuid(),
                        ExpectedActorRevision = first.ActorRevision,
                        ExpectedInventoryRevision = first.InventoryRevision
                    },
                    fixture.Reference,
                    2));
            CheckAssert.Equal(ResourceTransactionStatus.StaleNodeRevision,
                stale.Status,
                "an exact stale node reference must not gather again");
        });

        checks.Add("resource authority requires a usable axe", () =>
        {
            var fixture = Fixture();
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new StrikeTreeTransaction(
                    fixture.Context, fixture.Reference, 0, 2));
            CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
                result.Status,
                "tree damage must reject actors without an axe");
            CheckAssert.Equal((uint)0,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "a rejected strike must not dirty the sparse chunk");
        });

        checks.Add("tree misses advance actor revision exactly once", () =>
        {
            (ResourceFixture Fixture,
                WorldTransactionActorInput Actor,
                ResourceTransactionResult Result)? selected = null;
            for (var attempt = 1; attempt <= 512; attempt++)
            {
                var trialFixture = Fixture(new ActorId(Guid.Parse(
                    $"6c000000-0000-0000-0000-{attempt:D12}")));
                var inventory = trialFixture.Actor.Gameplay.Inventory.Slots
                    .Select(value => value.Slot == 4
                        ? new InventorySlotSnapshot(
                            value.Slot, ItemIds.StoneAxe, 1)
                        : value)
                    .ToImmutableArray();
                var trialActor = trialFixture.Actor with
                {
                    Gameplay = trialFixture.Actor.Gameplay with
                    {
                        Inventory = trialFixture.Actor.Gameplay.Inventory with
                        {
                            Slots = inventory
                        }
                    }
                };
                var result = trialFixture.Authority.Execute(
                    trialActor,
                    new StrikeTreeTransaction(
                        trialFixture.Context, trialFixture.Reference, 4, 2));
                if (result.Accepted && !result.Hit && !result.ToolWorn)
                {
                    selected = (trialFixture, trialActor, result);
                    break;
                }
            }

            CheckAssert.True(selected is not null,
                "a deterministic clean tree miss fixture must exist");
            var (fixture, actor, miss) = selected!.Value;
            CheckAssert.Equal(actor.Gameplay.ActorRevision + 1,
                miss.ActorRevision,
                "an accepted tree miss must advance actor revision once");
            CheckAssert.Equal(actor.Gameplay.Inventory.Revision,
                miss.InventoryRevision,
                "a clean tree miss must not advance inventory revision");
            CheckAssert.True(miss.NodeDelta is null &&
                             miss.ChunkDelta is null,
                "a tree miss must not publish a fake resource mutation");

            var currentActor = actor with
            {
                Gameplay = miss.Gameplay!.Value
            };
            var staleReplay = fixture.Authority.Execute(
                currentActor,
                new StrikeTreeTransaction(
                    fixture.Context with
                    {
                        CommandId = Guid.NewGuid(),
                        ExpectedInventoryRevision = miss.InventoryRevision
                    },
                    fixture.Reference, 4, 4));
            CheckAssert.Equal(ResourceTransactionStatus.StaleActorRevision,
                staleReplay.Status,
                "an evicted tree-miss replay must fail its old actor revision");
            CheckAssert.Equal(1UL,
                fixture.Authority.CaptureCheckpoint()
                    .ActorCadences.Single().ActionOrdinal,
                "a stale tree-miss replay must not consume another ordinal");
        });

        checks.Add("resource attempt overflow rejects before mutation", () =>
        {
            var fixture = Fixture();
            var slots = fixture.Actor.Gameplay.Inventory.Slots
                .Select(value => value.Slot == 4
                    ? new InventorySlotSnapshot(
                        value.Slot, ItemIds.StoneAxe, 1)
                    : value)
                .ToImmutableArray();
            var gameplay = fixture.Actor.Gameplay with
            {
                ActorRevision = uint.MaxValue,
                Inventory = fixture.Actor.Gameplay.Inventory with
                {
                    Slots = slots
                }
            };
            var actor = fixture.Actor with { Gameplay = gameplay };
            var result = fixture.Authority.Execute(
                actor,
                new StrikeTreeTransaction(
                    fixture.Context with
                    {
                        ExpectedActorRevision = uint.MaxValue
                    },
                    fixture.Reference, 4, 2));

            CheckAssert.Equal(ResourceTransactionStatus.InvalidCommand,
                result.Status,
                "an accepted attempt that cannot advance actor revision must reject");
            CheckAssert.Equal((uint)0,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "revision overflow must reject before resource mutation");
            CheckAssert.Equal(0,
                fixture.Authority.CaptureCheckpoint().ActorCadences.Length,
                "revision overflow must reject before cadence consumption");
        });

        checks.Add("resource authority uses the exact axe slot and tree reward", () =>
        {
            var fixture = Fixture();
            var inventory = fixture.Actor.Gameplay.Inventory.Slots
                .Select(value => value.Slot == 4
                    ? new InventorySlotSnapshot(
                        value.Slot, ItemIds.StoneAxe, 1)
                    : value)
                .ToImmutableArray();
            var actor = fixture.Actor with
            {
                Gameplay = fixture.Actor.Gameplay with
                {
                    Inventory = fixture.Actor.Gameplay.Inventory with
                    {
                        Slots = inventory
                    }
                }
            };
            var wrong = fixture.Authority.Execute(
                actor,
                new StrikeTreeTransaction(
                    fixture.Context, fixture.Reference, 0, 2));
            CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
                wrong.Status,
                "the authority must not silently choose an axe from another slot");

            var hit = fixture.Authority.Execute(
                actor,
                new StrikeTreeTransaction(
                    fixture.Context with { CommandId = Guid.NewGuid() },
                    fixture.Reference, 4, 2));
            CheckAssert.True(hit.Accepted,
                "the selected usable axe slot must be accepted");
            CheckAssert.Equal(
                actor.Gameplay.ActorRevision + 1,
                hit.ActorRevision,
                "an accepted tree strike must advance actor revision exactly once");
            if (!hit.Rewards.IsDefaultOrEmpty)
            {
                var descriptor = fixture.Catalog.DescribeChunk(
                    fixture.WorldSeed, fixture.Chunk).Single();
                CheckAssert.True(SurfaceTreeCatalog.TryGetVisual(
                        descriptor.Variant, out var visual),
                    "the fixture must have a canonical surface-tree visual");
                CheckAssert.True(hit.RewardQuantity(visual.LogItemId) > 0,
                    "wood rewards must follow the authoritative tree family");
            }
        });

        checks.Add("resource authority fells trees with a full inventory", () =>
        {
            var (fixture, actor, result) = ExecuteFelling(
                availableRewardSlots: 0);

            CheckAssert.True(result.Accepted && result.Hit,
                "inventory capacity must not roll back an accepted tree hit");
            CheckAssert.True(result.NodeDelta!.Current.Depleted,
                "a lethal strike must fell the tree even when no logs fit");
            CheckAssert.Equal(0, result.Rewards.Length,
                "the receipt must report no carried reward when inventory is full");
            CheckAssert.True(!string.IsNullOrWhiteSpace(result.Detail),
                "the receipt must explain that cut wood was left behind");
            CheckAssert.True(
                result.Gameplay!.Value.WoodcuttingExperience >
                actor.Gameplay.WoodcuttingExperience,
                "felling experience must commit independently of rewards");
            CheckAssert.Equal((uint)1,
                fixture.Authority.CaptureChunkRevision(fixture.Chunk),
                "the felled node must advance its sparse chunk exactly once");
        });

        checks.Add("resource authority carries partial felling rewards once", () =>
        {
            var (fixture, actor, result) = ExecuteFelling(
                availableRewardSlots: 1);
            var descriptor = fixture.Catalog.DescribeChunk(
                fixture.WorldSeed, fixture.Chunk).Single();
            CheckAssert.True(SurfaceTreeCatalog.TryGetVisual(
                    descriptor.Variant, out var visual),
                "the felling fixture must use a canonical tree visual");

            CheckAssert.True(result.Accepted && result.Hit,
                "the partially carried felling strike must be accepted");
            CheckAssert.True(result.NodeDelta is not null,
                "the partially carried felling strike must expose a node delta");
            CheckAssert.True(result.NodeDelta!.Current.Depleted,
                "the partially carried felling strike must still commit");
            CheckAssert.Equal(1,
                result.RewardQuantity(visual.LogItemId),
                "only the one log which fits may be reported as carried");
            CheckAssert.True(!string.IsNullOrWhiteSpace(result.Detail),
                "partial overflow must be observable to the adapter");

            var repeated = fixture.Authority.Execute(
                actor with { Gameplay = result.Gameplay!.Value },
                new StrikeTreeTransaction(
                    fixture.Context with
                    {
                        CommandId = Guid.NewGuid(),
                        ExpectedActorRevision = result.ActorRevision,
                        ExpectedInventoryRevision = result.InventoryRevision
                    },
                    fixture.Reference with
                    {
                        ExpectedNodeRevision = 1,
                        ExpectedResourceChunkRevision = 1
                    },
                    ToolInventorySlot: fixture.ToolSlot,
                    GameSeconds: 5));
            CheckAssert.Equal(ResourceTransactionStatus.Depleted,
                repeated.Status,
                "a felled tree must never grant its overflow a second time");
            CheckAssert.Equal(0, repeated.Rewards.Length,
                "revisiting a felled tree must not duplicate rewards");
        });

        checks.Add("resource authority persists sparse state and cadence", () =>
        {
            var fixture = Fixture();
            var result = fixture.Authority.Execute(
                fixture.Actor,
                new GatherTreeStickTransaction(
                    fixture.Context, fixture.Reference, 3));
            var checkpoint = fixture.Authority.CaptureCheckpoint();
            CheckAssert.Equal(1, checkpoint.Chunks.Length,
                "only a changed resource chunk should be persisted");
            CheckAssert.Equal(1, checkpoint.Chunks[0].Nodes.Length,
                "only a changed procedural node should be persisted");
            CheckAssert.Equal(1, checkpoint.ActorCadences.Length,
                "the deterministic action ordinal and cadence must persist");

            var restored = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);
            restored.RestoreCheckpoint(checkpoint);
            CheckAssert.Equal((uint)1,
                restored.CaptureChunkRevision(fixture.Chunk),
                "resource chunk revisions must survive restart");
            var replay = restored.Execute(
                fixture.Actor with { Gameplay = result.Gameplay!.Value },
                new GatherTreeStickTransaction(
                    fixture.Context with
                    {
                        CommandId = Guid.NewGuid(),
                        ExpectedActorRevision = result.ActorRevision,
                        ExpectedInventoryRevision = result.InventoryRevision
                    },
                    fixture.Reference with
                    {
                        ExpectedNodeRevision = 1,
                        ExpectedResourceChunkRevision = 1
                    },
                    3.1));
            CheckAssert.Equal(ResourceTransactionStatus.CadenceLocked,
                replay.Status,
                "restart must not reset authoritative gathering cadence");
        });

        checks.Add("resource authority rejects malformed checkpoints atomically", () =>
        {
            var fixture = Fixture();
            _ = fixture.Authority.Execute(
                fixture.Actor,
                new GatherTreeStickTransaction(
                    fixture.Context, fixture.Reference, 3));
            var checkpoint = fixture.Authority.CaptureCheckpoint();
            var invalidChunk = checkpoint.Chunks[0] with
            {
                ResourceChunkRevision = 9
            };
            var invalid = checkpoint with
            {
                Chunks = [invalidChunk]
            };
            var restored = new AuthoritativeResourceTransactions(
                fixture.WorldSeed, fixture.Catalog);

            CheckAssert.Throws<InvalidDataException>(
                () => restored.RestoreCheckpoint(invalid),
                "a chunk revision inconsistent with sparse mutations must fail");
            CheckAssert.Equal((uint)0,
                restored.CaptureChunkRevision(fixture.Chunk),
                "failed restore validation must leave authority pristine");
            CheckAssert.Equal(0,
                restored.CaptureCheckpoint().Chunks.Length,
                "failed restore must not retain partial sparse nodes");
            restored.RestoreCheckpoint(checkpoint);
            CheckAssert.Equal((uint)1,
                restored.CaptureChunkRevision(fixture.Chunk),
                "a valid checkpoint must still restore after rejection");
        });

        checks.Add("resource authority resolves final-stick seeds deterministically", () =>
        {
            ResourceFixture? selected = null;
            ResourceTransactionResult? expected = null;
            for (var attempt = 0; attempt < 512 && expected is null; attempt++)
            {
                var fixture = Fixture(
                    new ActorId(Guid.Parse(
                        $"6a000000-0000-0000-0000-{attempt + 1:D12}")),
                    initialSticks: 1);
                var result = fixture.Authority.Execute(
                    fixture.Actor,
                    new GatherTreeStickTransaction(
                        fixture.Context, fixture.Reference, 1));
                if (result.Rewards.Length > 1)
                {
                    selected = fixture;
                    expected = result;
                }
            }
            if (selected is null || expected is null)
                throw new InvalidOperationException(
                    "A deterministic seed-reward fixture was not found.");

            var replayAuthority = new AuthoritativeResourceTransactions(
                selected.WorldSeed, selected.Catalog);
            var replay = replayAuthority.Execute(
                selected.Actor,
                new GatherTreeStickTransaction(
                    selected.Context, selected.Reference, 1));
            CheckAssert.SequenceEqual(expected.Rewards, replay.Rewards,
                "the same actor/node/ordinal must produce the same seed rewards");
            CheckAssert.True(replay.Rewards.Skip(1).Single().Quantity is 1 or 2,
                "final-stick seed outcomes must produce one or two seeds");
        });

        checks.Add("resource session preserves idempotent gather receipts", async _ =>
        {
            var fixture = Fixture();
            var identities = new DeterministicIdentitySource();
            var session = new AuthoritativeWorldSession(
                identitySource: identities,
                sessionId: new SessionId(Guid.NewGuid()),
                resourceTransactions: fixture.Authority);
            var connection = ClientConnectionId.New();
            var joinTask = session.EnqueueJoinAsync(new(
                connection, "Resource Tester", fixture.Actor.Position));
            session.Drain();
            var join = await joinTask;
            var commandId = Guid.NewGuid();
            var intent = new GatherTreeStickIntent(
                commandId,
                join.Gameplay.Inventory.Revision,
                join.Gameplay.ActorRevision,
                fixture.Reference);
            var firstTask = session.EnqueueIntentAsync(new(
                connection, join.Identity.PlayerId, 1, intent));
            session.Drain();
            var first = await firstTask;
            var duplicateTask = session.EnqueueIntentAsync(new(
                connection, join.Identity.PlayerId, 2, intent));
            session.Drain();
            var duplicate = await duplicateTask;

            CheckAssert.True(first.Accepted,
                "the first session resource command must commit");
            CheckAssert.True(duplicate.Duplicate,
                "a duplicate command identifier must replay its receipt");
            CheckAssert.Equal(first.InventoryRevision,
                duplicate.InventoryRevision,
                "idempotent replay must not gather a second item");
        });
    }

    private static ResourceFixture Fixture(
        ActorId? actorIdOverride = null,
        int initialSticks = 3,
        int? initialHealth = null)
    {
        const long seed = 887_123;
        var chunk = new WorldChunkKey(0, 0, 0);
        var visual = Enumerable.Range(0, 19 << 5)
            .Select(variant => SurfaceTreeCatalog.TryGetVisual(
                    variant, out var candidate)
                ? candidate
                : default)
            .First(candidate => candidate.MaximumHealth > 0 &&
                                candidate.LogItemId != ItemIds.Logs);
        var source = new FixedResourceSource([
            new(
                ProceduralResourceKey.Tree(2, 2, variant: visual.Variant),
                new Vector2(2.5f, 2.5f),
                InitialHealth: initialHealth ?? visual.MaximumHealth,
                MaximumHealth: visual.MaximumHealth,
                InitialRemaining: initialSticks)
        ]);
        var catalog = new ProceduralResourceCatalog(source);
        var descriptor = catalog.DescribeChunk(seed, chunk).Single();
        var reference = new ResourceNodeReference(
            descriptor.Id, chunk, 0, 0);
        var gameplay = Gameplay();
        var actorId = actorIdOverride ?? new ActorId(Guid.NewGuid());
        var actor = new WorldTransactionActorInput(
            actorId, new Vector2(2.3f, 2.3f), 0, gameplay);
        var context = new WorldTransactionContext(
            Guid.NewGuid(), actorId,
            gameplay.ActorRevision, gameplay.Inventory.Revision);
        return new(
            seed,
            chunk,
            catalog,
            new AuthoritativeResourceTransactions(seed, catalog),
            actor,
            context,
            reference);
    }

    private static PlayerGameplaySnapshot Gameplay()
    {
        var slots = Enumerable.Range(0, PlayerInventory.Capacity)
            .Select(static slot => new InventorySlotSnapshot(
                slot, null, 0))
            .ToImmutableArray();
        return new(
            1, 100, 100, 0, 0, 0,
            new PlayerInventorySnapshot(1, slots));
    }

    private static (ResourceFixture Fixture,
        WorldTransactionActorInput Actor,
        ResourceTransactionResult Result) ExecuteFelling(
        int availableRewardSlots)
    {
        if (availableRewardSlots is < 0 or > 2)
            throw new ArgumentOutOfRangeException(
                nameof(availableRewardSlots));
        for (var attempt = 0; attempt < 512; attempt++)
        {
            var fixture = Fixture(
                new ActorId(Guid.Parse(
                    $"7a000000-0000-0000-0000-{attempt + 1:D12}")),
                initialHealth: 1);
            var empty = Enumerable.Range(
                    PlayerInventory.Capacity - availableRewardSlots,
                    availableRewardSlots)
                .ToHashSet();
            var slots = fixture.Actor.Gameplay.Inventory.Slots
                .Select(value => empty.Contains(value.Slot)
                        ? value
                        : value.Slot == 4
                            ? new InventorySlotSnapshot(
                                value.Slot, ItemIds.StoneAxe, 1)
                        : new InventorySlotSnapshot(
                            value.Slot, ItemIds.LargeRock, 1))
                .ToImmutableArray();
            var axeSlot = empty.Contains(4) ? 3 : 4;
            slots = slots
                .Select(value => value.Slot == axeSlot
                    ? new InventorySlotSnapshot(
                        value.Slot, ItemIds.StoneAxe, 1)
                    : value)
                .ToImmutableArray();
            if (empty.Contains(4) && axeSlot != 4)
            {
                slots = slots
                    .Select(value => value.Slot == 4
                        ? new InventorySlotSnapshot(
                            value.Slot, ItemIds.LargeRock, 1)
                        : value)
                    .ToImmutableArray();
            }
            var actor = fixture.Actor with
            {
                Gameplay = fixture.Actor.Gameplay with
                {
                    Inventory = fixture.Actor.Gameplay.Inventory with
                    {
                        Slots = slots
                    }
                }
            };
            var result = fixture.Authority.Execute(
                actor,
                new StrikeTreeTransaction(
                    fixture.Context,
                    fixture.Reference,
                    ToolInventorySlot: axeSlot,
                    GameSeconds: 2));
            if (result.Accepted && result.Hit &&
                result.NodeDelta?.Current.Depleted == true)
                return (fixture with { ToolSlot = axeSlot }, actor, result);
        }

        throw new InvalidOperationException(
            "A deterministic felling fixture was not found.");
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

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "5a000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse(
                "5b000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() =>
            new("resource-authority-check-secret");
    }

    private sealed record ResourceFixture(
        long WorldSeed,
        WorldChunkKey Chunk,
        IResourceDescriptorResolver Catalog,
        AuthoritativeResourceTransactions Authority,
        WorldTransactionActorInput Actor,
        WorldTransactionContext Context,
        ResourceNodeReference Reference,
        int ToolSlot = 4);
}
