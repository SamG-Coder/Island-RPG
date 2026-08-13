using System.Globalization;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

internal static class UndergroundMiningResourceChecks
{
    public static void Run()
    {
        DeterministicDescriptorsAndPolicy();
        SoloGeneratorParity();
        TypedIdentityIsCultureIndependent();
        LegacySaveKeyUpgrades();
        UnsupportedLevelsAreEmpty();
    }

    private static void DeterministicDescriptorsAndPolicy()
    {
        const long seed = 4_071_992;
        var source = new UndergroundMiningResourceDescriptorSource();
        var chunk = FindChunkWithMining(source, seed);
        var first = source.DescribeChunk(seed, chunk);
        var second = source.DescribeChunk(seed, chunk);

        Assert(first.Count > 0,
            "the mining fixture must contain a mineable cave node");
        Assert(first.SequenceEqual(second),
            "mining descriptors must be deterministic and stably ordered");
        Assert(first.All(value =>
                value.Key.Kind == ResourceNodeKind.MiningNode &&
                value.Key.SourceX == (int)MathF.Floor(value.Position.X) &&
                value.Key.SourceY == (int)MathF.Floor(value.Position.Y) &&
                value.InitialHealth == value.MaximumHealth &&
                value.InitialRemaining == 0 &&
                value.RegrowthGameSeconds == 0 &&
                UndergroundMiningCatalog.TryGetVisual(
                    value.Key.Variant, out var visual) &&
                visual.MaximumHealth == value.MaximumHealth &&
                visual.CompletionExperience > 0),
            "mining nodes must carry canonical coordinates, visuals, health, rewards and XP policy");
    }

    private static void SoloGeneratorParity()
    {
        const long seed = 4_071_992;
        var source = new UndergroundMiningResourceDescriptorSource();
        var chunkKey = FindChunkWithMining(source, seed);
        var headless = source.DescribeChunk(seed, chunkKey);
        var solo = InfiniteWorldGenerator.Generate(
            seed,
            new ChunkCoordinate(
                chunkKey.X, chunkKey.Y, chunkKey.WorldLevel));
        var soloMining = solo.Vegetation
            .Select((value, ordinal) => (Value: value, Ordinal: ordinal))
            .Where(value => UndergroundMiningCatalog.TryGetVisual(
                value.Value.GraphicName, out _))
            .ToArray();

        Assert(headless.Count == soloMining.Length,
            "headless and solo cave generation must expose the same mineable count");
        for (var index = 0; index < headless.Count; index++)
        {
            var expected = headless[index];
            var actual = soloMining[index];
            Assert(expected.Position.X == actual.Value.X &&
                   expected.Position.Y == actual.Value.Y &&
                   expected.Key.Ordinal == actual.Ordinal &&
                   UndergroundMiningCatalog.TryGetVisual(
                       expected.Key.Variant, out var visual) &&
                   visual.GraphicName == actual.Value.GraphicName,
                "headless mining identity must retain the exact solo feature position, ordinal and visual");
        }
    }

    private static void TypedIdentityIsCultureIndependent()
    {
        const long seed = -8_351_772;
        var source = new UndergroundMiningResourceDescriptorSource();
        var chunk = FindChunkWithMining(source, seed);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = new ProceduralResourceCatalog(source)
                .DescribeChunk(seed, chunk)
                .Select(value => value.Id)
                .ToArray();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            var arabic = new ProceduralResourceCatalog(source)
                .DescribeChunk(seed, chunk)
                .Select(value => value.Id)
                .ToArray();
            Assert(french.SequenceEqual(arabic),
                "typed coordinate/ordinal mining IDs must ignore process culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static void UnsupportedLevelsAreEmpty()
    {
        var source = new UndergroundMiningResourceDescriptorSource();
        Assert(source.DescribeChunk(1, new WorldChunkKey(0, 0, 0)).Count == 0,
            "surface chunks must not receive cave mining resources");
        Assert(source.DescribeChunk(1, new WorldChunkKey(0, 0, 1)).Count == 0,
            "unknown levels must fail closed");
    }

    private static void LegacySaveKeyUpgrades()
    {
        const long seed = 4_071_992;
        var source = new UndergroundMiningResourceDescriptorSource();
        var chunkKey = FindChunkWithMining(source, seed);
        var chunk = InfiniteWorldGenerator.Generate(
            seed,
            new ChunkCoordinate(
                chunkKey.X, chunkKey.Y, chunkKey.WorldLevel));
        var index = Array.FindIndex(chunk.Vegetation, value =>
            UndergroundMiningCatalog.TryGetVisual(
                value.GraphicName, out _));
        Assert(index >= 0, "the legacy fixture must contain a mining node");
        var node = chunk.Vegetation[index];
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var legacy = $"vegetation:{node.X:0.000}:{node.Y:0.000}";
            chunk.MiningStates.Add(new(legacy, 1, 2));
            WorldMiningIdentity.UpgradeLegacyKeys(chunk);
            Assert(chunk.MiningStates.Single().StableKey ==
                   WorldMiningIdentity.StableKey(node, index),
                "culture-sensitive legacy save keys must upgrade to the typed invariant key");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static WorldChunkKey FindChunkWithMining(
        UndergroundMiningResourceDescriptorSource source,
        long seed)
    {
        for (var y = -3; y <= 3; y++)
        for (var x = -3; x <= 3; x++)
        {
            var chunk = new WorldChunkKey(x, y, -1);
            if (source.DescribeChunk(seed, chunk).Count > 0) return chunk;
        }
        throw new InvalidOperationException(
            "Unable to locate a deterministic mining fixture.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
