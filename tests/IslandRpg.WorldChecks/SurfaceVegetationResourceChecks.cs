using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

internal static class SurfaceVegetationResourceChecks
{
    public static void Run()
    {
        DeterministicDescriptorsAndHarvestDefaults();
        NegativeCoordinatesAndSeamIdentity();
        SoloGeneratorAndVisualParity();
        UnsupportedLevelsAreEmpty();
    }

    private static void DeterministicDescriptorsAndHarvestDefaults()
    {
        const long seed = 88_421;
        var source = new SurfaceVegetationResourceDescriptorSource();
        var chunk = new WorldChunkKey(-1, 0, 0);
        var first = source.DescribeChunk(seed, chunk);
        var second = source.DescribeChunk(seed, chunk);

        Assert(first.Count >=
               SurfaceVegetationCatalog.MinimumCoastalFibreSourcesPerChunk,
            "the blocked-start fixture must expose guaranteed fibre resources");
        Assert(first.Count <=
               SurfaceVegetationCatalog.MaximumPlacementsPerChunk,
            "interactive vegetation must retain a strict per-chunk bound");
        Assert(first.SequenceEqual(second),
            "surface vegetation descriptors must be deterministic and ordered");

        foreach (var value in first)
        {
            Assert(value.Key.Kind is ResourceNodeKind.FibreShrub or
                    ResourceNodeKind.BerryBush,
                "only interactive fibre and berry vegetation may become nodes");
            Assert(value.InitialHealth == 0 && value.MaximumHealth == 0,
                "renewable vegetation must not masquerade as a damage resource");
            Assert(value.InitialRemaining == 1,
                "the procedural default must represent one ready harvest");
            Assert(value.RegrowthGameSeconds ==
                   (value.Key.Kind == ResourceNodeKind.FibreShrub
                       ? SurfaceVegetationCatalog.FibreRegrowthGameSeconds
                       : SurfaceVegetationCatalog.BerryRegrowthGameSeconds),
                "descriptor regrowth must preserve the solo cooldown policy");
            Assert(SurfaceVegetationCatalog.TryGetVisual(
                    value.Key.Variant, out var visual) &&
                   visual.ResourceKind == value.Key.Kind &&
                   visual.InitialRemaining == 1 &&
                   visual.RegrowthGameSeconds == value.RegrowthGameSeconds &&
                   !string.IsNullOrWhiteSpace(visual.GatherItemId),
                "every resource variant must decode its visual, reward and lifecycle");
        }
    }

    private static void NegativeCoordinatesAndSeamIdentity()
    {
        const long seed = -4_991_337;
        var source = new SurfaceVegetationResourceDescriptorSource();
        var leftChunk = new WorldChunkKey(-1, -1, 0);
        var rightChunk = new WorldChunkKey(0, -1, 0);
        var left = source.DescribeChunk(seed, leftChunk);
        var right = source.DescribeChunk(seed, rightChunk);

        Assert(left.All(value =>
                value.Position.X is >= -32 and < 0 &&
                value.Position.Y is >= -32 and < 0 &&
                (int)MathF.Floor(value.Position.X) == value.Key.SourceX &&
                (int)MathF.Floor(value.Position.Y) == value.Key.SourceY),
            "negative resources must retain floor-divided tile ownership");
        Assert(right.All(value =>
                value.Position.X is >= 0 and < 32 &&
                value.Position.Y is >= -32 and < 0 &&
                (int)MathF.Floor(value.Position.X) == value.Key.SourceX &&
                (int)MathF.Floor(value.Position.Y) == value.Key.SourceY),
            "the neighboring chunk must own its side of the zero seam");
        Assert(!left.Select(static value => value.Key)
                .Intersect(right.Select(static value => value.Key)).Any(),
            "adjacent chunks must not duplicate a vegetation address");

        var catalog = new ProceduralResourceCatalog(source);
        var firstLeft = catalog.DescribeChunk(seed, leftChunk);
        _ = catalog.DescribeChunk(seed, rightChunk);
        var repeatedLeft = catalog.DescribeChunk(seed, leftChunk);
        Assert(firstLeft.Select(static value => value.Id)
                .SequenceEqual(repeatedLeft.Select(static value => value.Id)),
            "neighbor generation must not change stable negative-coordinate IDs");
        Assert(firstLeft.All(value =>
            source.DescribeChunk(seed, leftChunk).Any(seedValue =>
                ProceduralResourceIdentity.ForVegetation(
                    seed,
                    leftChunk,
                    seedValue.Key.Kind,
                    seedValue.Key.SourceX,
                    seedValue.Key.SourceY,
                    seedValue.Key.Ordinal,
                    seedValue.Key.Variant) == value.Id)),
            "catalog IDs must derive only from the canonical vegetation address");
    }

    private static void SoloGeneratorAndVisualParity()
    {
        const long seed = 8_817_310;
        var source = new SurfaceVegetationResourceDescriptorSource();
        foreach (var chunkKey in new[]
                 {
                     new WorldChunkKey(0, 0, 0),
                     new WorldChunkKey(-2, 1, 0),
                     new WorldChunkKey(1, -2, 0),
                     new WorldChunkKey(-1, 0, 0)
                 })
        {
            var solo = InfiniteWorldGenerator.Generate(
                seed,
                new ChunkCoordinate(
                    chunkKey.X, chunkKey.Y, chunkKey.WorldLevel));
            var canonical = SurfaceVegetationCatalog.DescribeChunk(
                seed, chunkKey);
            Assert(canonical.Count == solo.Vegetation.Length,
                "canonical and solo generators must place every decoration equally");
            for (var index = 0; index < canonical.Count; index++)
            {
                var expected = canonical[index];
                var actual = solo.Vegetation[index];
                Assert(expected.Position.X == actual.X &&
                       expected.Position.Y == actual.Y &&
                       expected.Visual.GraphicName == actual.GraphicName &&
                       expected.Visual.FrameIndex == actual.FrameIndex &&
                       (WorldVegetationKind)expected.Visual.Kind == actual.Kind &&
                       expected.Visual.CanBecomeInstance ==
                           actual.CanBecomeInstance,
                    "solo vegetation must preserve canonical position and visual order");
            }

            var headless = source.DescribeChunk(seed, chunkKey);
            var gatherableSolo = canonical
                .Where(static value => value.Visual.ResourceKind.HasValue)
                .ToArray();
            Assert(headless.Count == gatherableSolo.Length,
                "headless descriptors must include every solo gatherable and no decoration");
            for (var index = 0; index < headless.Count; index++)
            {
                Assert(headless[index].Position ==
                       gatherableSolo[index].Position &&
                       headless[index].Key.Kind ==
                           gatherableSolo[index].Visual.ResourceKind &&
                       headless[index].Key.Variant ==
                           gatherableSolo[index].Visual.Variant,
                    "resource descriptors must preserve exact solo interaction targets");
            }
        }
    }

    private static void UnsupportedLevelsAreEmpty()
    {
        var source = new SurfaceVegetationResourceDescriptorSource();
        Assert(source.DescribeChunk(
                1, new WorldChunkKey(0, 0, -1)).Count == 0,
            "underground chunks must not receive surface vegetation resources");
        Assert(source.DescribeChunk(
                1, new WorldChunkKey(0, 0, 1)).Count == 0,
            "unknown world levels must fail closed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
