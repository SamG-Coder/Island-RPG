using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

internal static class SurfaceTreeResourceChecks
{
    public static void Run()
    {
        DeterministicDescriptors();
        NegativeChunkAndSeamOwnership();
        SoloGeneratorParity();
        UnsupportedLevelsAreEmpty();
    }

    private static void DeterministicDescriptors()
    {
        const long seed = 7_913_227;
        var source = new SurfaceTreeResourceDescriptorSource();
        var chunk = new WorldChunkKey(3, -4, 0);
        var first = source.DescribeChunk(seed, chunk);
        var second = source.DescribeChunk(seed, chunk);
        Assert(first.Count > 0,
            "the deterministic fixture must exercise generated trees");
        Assert(first.Count == second.Count,
            "surface tree generation must be deterministic");
        for (var index = 0; index < first.Count; index++)
        {
            Assert(first[index] == second[index],
                "surface tree descriptors must preserve stable order");
            Assert(SurfaceTreeCatalog.TryGetVisual(
                    first[index].Key.Variant, out var visual),
                "every generated tree variant must decode");
            Assert(visual.MaximumHealth == first[index].MaximumHealth,
                "tree health must come from its decoded visual family");
            Assert(visual.LogItemId.Length > 0 &&
                   visual.SeedItemId.Length > 0 &&
                   visual.FellingLogCount is >= 2 and <= 4,
                "tree variants must carry canonical reward policy");
        }
    }

    private static void NegativeChunkAndSeamOwnership()
    {
        const long seed = -91_227;
        var source = new SurfaceTreeResourceDescriptorSource();
        var leftChunk = new WorldChunkKey(-1, -1, 0);
        var rightChunk = new WorldChunkKey(0, -1, 0);
        var left = source.DescribeChunk(seed, leftChunk);
        var right = source.DescribeChunk(seed, rightChunk);

        Assert(left.All(value =>
                value.Position.X is >= -32 and < 0 &&
                value.Position.Y is >= -32 and < 0),
            "negative chunks must retain floor-divided ownership");
        Assert(right.All(value =>
                value.Position.X is >= 0 and < 32 &&
                value.Position.Y is >= -32 and < 0),
            "the neighboring chunk must own its side of the zero seam");
        Assert(!left.Select(value => value.Key)
                .Intersect(right.Select(value => value.Key)).Any(),
            "adjacent chunks must not duplicate seam trees");

        foreach (var tileY in Enumerable.Range(-32, 32))
        foreach (var tileX in new[] { -1, 0 })
        {
            var expected = SurfaceTreeCatalog.TryDescribeAt(
                seed, tileX, tileY, out var visual);
            var chunkValues = tileX < 0 ? left : right;
            var actual = chunkValues.SingleOrDefault(value =>
                value.Key.SourceX == tileX && value.Key.SourceY == tileY);
            Assert(expected == (actual is not null),
                "seam tiles must be generated independently of chunk order");
            if (expected)
            {
                Assert(actual!.Key.Variant == visual.Variant,
                    "seam tree variants must match direct tile sampling");
            }
        }
    }

    private static void SoloGeneratorParity()
    {
        const long seed = 8_817_310;
        var source = new SurfaceTreeResourceDescriptorSource();
        foreach (var chunkKey in new[]
                 {
                     new WorldChunkKey(0, 0, 0),
                     new WorldChunkKey(-2, 1, 0),
                     new WorldChunkKey(1, -2, 0)
                 })
        {
            var headless = source.DescribeChunk(seed, chunkKey);
            var solo = InfiniteWorldGenerator.Generate(
                seed,
                new ChunkCoordinate(
                    chunkKey.X, chunkKey.Y, chunkKey.WorldLevel));
            Assert(headless.Count > 0,
                "each parity fixture must exercise generated trees");
            Assert(headless.Count == solo.Trees.Length,
                "headless and solo generators must place the same tree count");
            for (var index = 0; index < headless.Count; index++)
            {
                var expected = headless[index];
                var actual = solo.Trees[index];
                Assert(expected.Position.X == actual.X + .5f &&
                       expected.Position.Y == actual.Y + .5f,
                    "tree descriptors must use the existing tile-center interaction point");
                Assert(SurfaceTreeCatalog.TryGetVisual(
                        expected.Key.Variant, out var visual) &&
                       visual.GraphicName == actual.GraphicName &&
                       visual.FrameIndex == actual.FrameIndex,
                    "headless variants must decode to the solo tree graphic and frame");
            }
        }
    }

    private static void UnsupportedLevelsAreEmpty()
    {
        var source = new SurfaceTreeResourceDescriptorSource();
        Assert(source.DescribeChunk(1, new WorldChunkKey(0, 0, -1)).Count == 0,
            "underground chunks must not receive surface trees");
        Assert(source.DescribeChunk(1, new WorldChunkKey(0, 0, 1)).Count == 0,
            "unknown world levels must fail closed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
