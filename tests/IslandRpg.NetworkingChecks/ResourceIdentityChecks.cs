using System.Globalization;
using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class ResourceIdentityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("resource identity is deterministic and domain separated", () =>
        {
            var chunk = new WorldChunkKey(-2, 3, 0);
            var key = ProceduralResourceKey.Vegetation(
                ResourceNodeKind.BerryBush, -61, 101, 7, 12);
            var first = ProceduralResourceIdentity.Derive(913_227, chunk, key);
            var second = ProceduralResourceIdentity.Derive(913_227, chunk, key);

            CheckAssert.False(first.IsEmpty,
                "valid procedural identities must never be empty");
            CheckAssert.Equal(first, second,
                "identical canonical inputs must produce the same identity");
            CheckAssert.False(first == ProceduralResourceIdentity.Derive(
                    913_228, chunk, key),
                "world seed must domain-separate resource identities");
            CheckAssert.False(first == ProceduralResourceIdentity.Derive(
                    913_227, chunk, key with { Ordinal = 8 }),
                "generator ordinal must domain-separate resource identities");
            CheckAssert.False(first == ProceduralResourceIdentity.Derive(
                    913_227, chunk, key with
                    {
                        Kind = ResourceNodeKind.FibreShrub
                    }),
                "resource kind must domain-separate identities");
        });

        checks.Add("resource identity is culture independent", () =>
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentUICulture =
                    CultureInfo.GetCultureInfo("fr-FR");
                var french = ProceduralResourceIdentity.ForFish(
                    long.MinValue + 81, 0, -1, -33, 4);

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                CultureInfo.CurrentUICulture =
                    CultureInfo.GetCultureInfo("ar-SA");
                var arabic = ProceduralResourceIdentity.ForFish(
                    long.MinValue + 81, 0, -1, -33, 4);

                CheckAssert.Equal(french, arabic,
                    "binary fixed-endian identity input must ignore culture");
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        });

        checks.Add("resource identity rejects out-of-bounds addresses", () =>
        {
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => ProceduralResourceIdentity.Derive(
                    1,
                    new WorldChunkKey(0, 0, 0),
                    ProceduralResourceKey.Tree(32, 0)),
                "a source coordinate outside the claimed chunk must fail");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => ProceduralResourceIdentity.Derive(
                    1,
                    new WorldChunkKey(0, 0, 0),
                    new ProceduralResourceKey(
                        ResourceNodeKind.MiningNode, 0, 0, -1, 0)),
                "negative generator ordinals must fail");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => ProceduralResourceIdentity.ForTree(
                    1, 0, 1_000_001, 0),
                "source coordinates outside the authoritative world bound must fail");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => ProceduralResourceIdentity.ForTree(
                    1, 0, ProceduralResourceIdentity.MaximumCoordinate, 0),
                "the exclusive positive world boundary must fail");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => ProceduralResourceIdentity.ForTree(1, 1, 0, 0),
                "unsupported world levels must fail before generation");
        });

        checks.Add("resource catalog resolves only authority-derived identities", () =>
        {
            var chunk = new WorldChunkKey(2, -1, 0);
            var key = ProceduralResourceKey.Tree(68, -3, 4);
            var source = new FixedResourceSource([
                new ProceduralResourceSeed(
                    key, new Vector2(68.5f, -2.5f), 125, 125, 3)
            ]);
            var catalog = new ProceduralResourceCatalog(source);
            var descriptor = catalog.DescribeChunk(551, chunk).Single();
            var reference = new ResourceNodeReference(
                descriptor.Id, chunk, 0, 0);

            CheckAssert.True(catalog.TryResolve(551, reference, out var resolved),
                "catalog must resolve an identity it derived from generator output");
            CheckAssert.Equal(descriptor, resolved,
                "resolution must return the canonical generated descriptor");
            CheckAssert.False(catalog.TryResolve(
                    551,
                    reference with
                    {
                        Id = ProceduralResourceIdentity.ForTree(
                            551, 0, 70, -3, 4)
                    },
                    out _),
                "a different but structurally valid identity must not resolve");
            CheckAssert.False(catalog.TryResolve(
                    551,
                    reference with { Chunk = new(3, -1, 0) },
                    out _),
                "an identity must not resolve against a forged chunk");
            CheckAssert.False(catalog.TryResolve(
                    551,
                    reference with { Id = ResourceNodeId.Empty },
                    out _),
                "an empty wire identity must not resolve");
            var generatedChunks = source.DescribeCount;
            CheckAssert.True(catalog.TryResolve(
                    551, reference, out _),
                "a repeated canonical claim must still resolve");
            CheckAssert.Equal(generatedChunks, source.DescribeCount,
                "repeated claims must reuse the bounded canonical chunk index");
        });

        checks.Add("resource catalog bounds generator output", () =>
        {
            var chunk = new WorldChunkKey(0, 0, 0);
            var invalidPosition = new ProceduralResourceCatalog(
                new FixedResourceSource([
                    new ProceduralResourceSeed(
                        ProceduralResourceKey.Tree(0, 0),
                        new Vector2(float.NaN, 0), 100, 100)
                ]));
            CheckAssert.Throws<InvalidOperationException>(
                () => invalidPosition.DescribeChunk(1, chunk),
                "non-finite generated positions must fail closed");

            var duplicate = new ProceduralResourceSeed(
                ProceduralResourceKey.Tree(0, 0),
                new Vector2(.5f, .5f), 100, 100);
            var duplicateCatalog = new ProceduralResourceCatalog(
                new FixedResourceSource([duplicate, duplicate]));
            CheckAssert.Throws<InvalidOperationException>(
                () => duplicateCatalog.DescribeChunk(1, chunk),
                "duplicate canonical keys must fail instead of aliasing nodes");

            var limited = new ProceduralResourceCatalog(
                new FixedResourceSource([duplicate, duplicate with
                {
                    Key = ProceduralResourceKey.Tree(1, 0),
                    Position = new Vector2(1.5f, .5f)
                }]),
                new ProceduralResourceCatalogLimits
                {
                    MaximumNodesPerChunk = 1
                });
            CheckAssert.Throws<InvalidOperationException>(
                () => limited.DescribeChunk(1, chunk),
                "oversized generator output must be rejected before indexing");

            var invalidDefaults = new ProceduralResourceCatalog(
                new FixedResourceSource([
                    duplicate with { MaximumHealth = 99 }
                ]));
            CheckAssert.Throws<InvalidOperationException>(
                () => invalidDefaults.DescribeChunk(1, chunk),
                "initial health above maximum health must be rejected");

            CheckAssert.SequenceEqual(
                Array.Empty<ResourceNodeDescriptor>(),
                invalidDefaults.DescribeChunk(
                    1, new WorldChunkKey(int.MaxValue, 0, 0)),
                "out-of-bounds chunk claims must fail closed before generation");
            CheckAssert.SequenceEqual(
                Array.Empty<ResourceNodeDescriptor>(),
                invalidDefaults.DescribeChunk(
                    1,
                    new WorldChunkKey(
                        ProceduralResourceIdentity.MaximumCoordinate /
                        WorldChunkKey.Size,
                        0,
                        0)),
                "a partially out-of-bounds positive boundary chunk must fail closed");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => new ProceduralResourceCatalog(
                    new FixedResourceSource([]),
                    new ProceduralResourceCatalogLimits
                    {
                        MaximumWorldCoordinate =
                            ProceduralResourceIdentity.MaximumCoordinate + 1f
                    }),
                "catalog bounds must not exceed canonical identity bounds");
        });

        checks.Add("resource action cadence validates authoritative time", () =>
        {
            var cadence = new ResourceActionCadence(.75);
            CheckAssert.False(cadence.IsReady(4.74, 4.75),
                "an action must not resolve before its server deadline");
            CheckAssert.True(cadence.IsReady(4.75, 4.75),
                "an action may resolve exactly on its server deadline");
            CheckAssert.Equal(8.25, cadence.NextReadyAt(7.5),
                "the next deadline must derive from server time and cadence");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => new ResourceActionCadence(0),
                "zero cadence must not disable authoritative rate limiting");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => cadence.NextReadyAt(double.NaN),
                "non-finite authoritative time must be rejected");
            CheckAssert.Throws<ArgumentOutOfRangeException>(
                () => cadence.NextReadyAt(double.MaxValue),
                "cadence deadline overflow must be rejected");
            CheckAssert.Throws<InvalidOperationException>(
                () => default(ResourceActionCadence).IsReady(1, 0),
                "an uninitialized cadence must not bypass rate limiting");
            CheckAssert.Throws<InvalidOperationException>(
                () => default(ResourceActionCadence).NextReadyAt(1),
                "an uninitialized cadence must not produce a deadline");
        });
    }

    private sealed class FixedResourceSource(
        IReadOnlyList<ProceduralResourceSeed> values) :
        IProceduralResourceDescriptorSource
    {
        public int DescribeCount { get; private set; }

        private readonly WorldChunkKey? _chunk = values.Count == 0
            ? null
            : WorldChunkKey.At(values[0].Position, 0);

        public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk)
        {
            DescribeCount++;
            return _chunk == chunk ? values : [];
        }
    }
}
