using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ResourceLifecycleChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("resource lifecycle separates tree health from sticks", () =>
        {
            var tree = Descriptor(
                ResourceNodeKind.Tree,
                initialHealth: 100,
                maximumHealth: 100,
                initialRemaining: 1);
            var initial = ResourceNodeStateRules.CreateDefault(tree);

            CheckAssert.False(initial.Depleted,
                "a generated tree must begin alive");
            CheckAssert.True(ResourceNodeStateRules.TryConsumeRemaining(
                    tree, initial, 1, 12, out var withoutSticks),
                "tree sticks must use the shared stock transition");
            CheckAssert.Equal(0, withoutSticks.Remaining,
                "the final loose stick must be consumed");
            CheckAssert.False(withoutSticks.Depleted,
                "zero loose sticks must not fell a healthy tree");
            CheckAssert.True(ResourceNodeStateRules.IsValid(
                    tree, withoutSticks),
                "a healthy tree with no sticks must remain valid sparse state");
        });

        checks.Add("resource lifecycle regrows renewable harvest stock", () =>
        {
            var berries = Descriptor(
                ResourceNodeKind.BerryBush,
                initialRemaining: 3,
                regrowthGameSeconds: 720);
            var initial = ResourceNodeStateRules.CreateDefault(berries);

            CheckAssert.True(ResourceNodeStateRules.TryConsumeRemaining(
                    berries, initial, 3, 100, out var depleted),
                "taking the final harvest must enter cooldown");
            CheckAssert.True(depleted.Depleted,
                "a bush with no remaining harvest must be depleted");
            CheckAssert.Equal(820d, depleted.ReadyAtGameSeconds,
                "the cooldown must derive from authoritative game time");
            CheckAssert.False(ResourceNodeStateRules.TryRegrow(
                    berries, depleted, 819.999, out _),
                "harvest stock must not regrow before its deadline");
            CheckAssert.True(ResourceNodeStateRules.TryRegrow(
                    berries, depleted, 820, out var regrown),
                "harvest stock must regrow exactly at its deadline");
            CheckAssert.Equal(3, regrown.Remaining,
                "regrowth must restore deterministic descriptor stock");
            CheckAssert.Equal((uint)2, regrown.NodeRevision,
                "depletion and regrowth must each advance the node revision");
            CheckAssert.False(regrown.Depleted,
                "regrown harvest stock must be available");
            CheckAssert.True(ResourceNodeStateRules.IsValid(
                    berries, regrown),
                "regrown state must remain checkpoint-safe");
        });

        checks.Add("resource lifecycle damages health backed mining nodes", () =>
        {
            var mining = Descriptor(
                ResourceNodeKind.MiningNode,
                initialHealth: 80,
                maximumHealth: 80);
            var initial = ResourceNodeStateRules.CreateDefault(mining);

            CheckAssert.True(ResourceNodeStateRules.TryApplyDamage(
                    mining, initial, 0, out var depleted),
                "lethal mining damage must use the health lifecycle");
            CheckAssert.True(depleted.Depleted,
                "a zero-health mining node must be depleted");
            CheckAssert.True(ResourceNodeStateRules.IsValid(
                    mining, depleted),
                "depleted mining state must remain checkpoint-safe");
            CheckAssert.False(ResourceNodeStateRules.TryRegrow(
                    mining, depleted, 10_000, out _),
                "non-renewable mining nodes must not silently regenerate");
        });

        checks.Add("resource lifecycle rejects cross-kind sparse state", () =>
        {
            var fibre = Descriptor(
                ResourceNodeKind.FibreShrub,
                initialRemaining: 2,
                regrowthGameSeconds: 300);
            var invalid = ResourceNodeStateRules.CreateDefault(fibre) with
            {
                Health = 1,
                Remaining = 0,
                Depleted = true,
                ReadyAtGameSeconds = 300
            };

            CheckAssert.False(ResourceNodeStateRules.IsValid(fibre, invalid),
                "remaining-backed resources must never carry health");
            CheckAssert.True(ResourceNodeStateRules.ActionTargets(
                    ResourceActionKind.GatherFibre,
                    ResourceNodeKind.FibreShrub),
                "fibre actions must map to fibre resources");
            CheckAssert.False(ResourceNodeStateRules.ActionTargets(
                    ResourceActionKind.GatherBerries,
                    ResourceNodeKind.FibreShrub),
                "resource action mapping must reject the wrong node kind");
        });

        checks.Add("resource authority validates renewable sparse checkpoints", () =>
        {
            const long seed = 991;
            var source = new FixedResourceSource(new(
                ProceduralResourceKey.Vegetation(
                    ResourceNodeKind.BerryBush, 2, 2, 0, 4),
                new Vector2(2.5f, 2.5f),
                InitialRemaining: 3,
                RegrowthGameSeconds: 720));
            var catalog = new ProceduralResourceCatalog(source);
            var descriptor = catalog.DescribeChunk(
                seed, new WorldChunkKey(0, 0, 0)).Single();
            var depleted = ResourceNodeStateRules.CreateDefault(descriptor) with
            {
                NodeRevision = 1,
                Remaining = 0,
                ReadyAtGameSeconds = 820,
                Depleted = true
            };
            var checkpoint = new AuthoritativeResourceTransactionsCheckpoint(
                [new ResourceChunkSparseState(
                    descriptor.Chunk, 1, [depleted])],
                []);
            var authority = new AuthoritativeResourceTransactions(
                seed, catalog);

            authority.RestoreCheckpoint(checkpoint);
            CheckAssert.Equal(depleted,
                authority.CaptureChunk(descriptor.Chunk).Nodes.Single(),
                "a renewable cooldown must survive sparse restore exactly");

            var invalidAuthority = new AuthoritativeResourceTransactions(
                seed, catalog);
            var invalid = checkpoint with
            {
                Chunks = [checkpoint.Chunks[0] with
                {
                    Nodes = [depleted with { ReadyAtGameSeconds = 0 }]
                }]
            };
            CheckAssert.Throws<InvalidDataException>(
                () => invalidAuthority.RestoreCheckpoint(invalid),
                "a renewable depleted node without a deadline must fail closed");
        });
    }

    private static ResourceNodeDescriptor Descriptor(
        ResourceNodeKind kind,
        int initialHealth = 0,
        int maximumHealth = 0,
        int initialRemaining = 0,
        double regrowthGameSeconds = 0) => new(
        new ResourceNodeId(Guid.NewGuid()),
        kind,
        new WorldChunkKey(0, 0, 0),
        new Vector2(2.5f, 2.5f),
        Variant: 0,
        initialHealth,
        maximumHealth,
        initialRemaining,
        regrowthGameSeconds);

    private sealed class FixedResourceSource(
        ProceduralResourceSeed value) :
        IProceduralResourceDescriptorSource
    {
        public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk) =>
            chunk == new WorldChunkKey(0, 0, 0) ? [value] : [];
    }
}
