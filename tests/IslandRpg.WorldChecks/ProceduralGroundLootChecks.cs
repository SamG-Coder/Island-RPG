using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

internal static class ProceduralGroundLootChecks
{
    public static void Run()
    {
        CatalogMatchesSoloChunkGenerator();
        Console.WriteLine(
            "Procedural ground-loot checks passed: catalog IDs match every generated pickup item.");
    }

    private static void CatalogMatchesSoloChunkGenerator()
    {
        const long seed = 67;
        var compared = 0;
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        for (var chunkY = -8; chunkY <= 8; chunkY++)
        for (var chunkX = -8; chunkX <= 8; chunkX++)
        {
            var coordinate = new ChunkCoordinate(chunkX, chunkY);
            var generated = InfiniteWorldGenerator.Generate(seed, coordinate);
            var chunk = new WorldChunkKey(chunkX, chunkY, 0);
            var inland = ProceduralGroundLootCatalog.DescribeChunk(seed, chunk);
            var coastal = ProceduralCoastalLootCatalog.DescribeChunk(seed, chunk);
            var generatedInland = generated.GroundObjects
                .Where(IsInlandCatalogItem)
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            var inlandIds = inland
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            Assert(
                generatedInland.SequenceEqual(inlandIds),
                "inland ground loot must use the same stable IDs as solo chunk generation");
            var generatedCoastal = generated.GroundObjects
                .Where(value => ProceduralCoastalLootCatalog.IsCoastal(value.ItemId))
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            var coastalIds = coastal
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            Assert(
                generatedCoastal.SequenceEqual(coastalIds),
                "coastal collectibles must use the same stable IDs as solo chunk generation");
            compared += generatedInland.Length + generatedCoastal.Length;
            foreach (var value in generated.GroundObjects)
                seenItems.Add(value.ItemId);
        }

        Assert(compared > 0,
            "the fixture must include generated portable ground loot");
        foreach (var itemId in ProceduralGroundLootCatalog.PortableItemIds)
        {
            Assert(seenItems.Contains(itemId),
                $"the fixture must include generated {itemId}");
        }
        Assert(
            seenItems.Any(ProceduralCoastalLootCatalog.IsCoastal),
            "the fixture must include generated coastal collectibles");
    }

    private static bool IsInlandCatalogItem(WorldGroundObject value) =>
        ProceduralGroundLootCatalog.PortableItemIds.Contains(value.ItemId);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
