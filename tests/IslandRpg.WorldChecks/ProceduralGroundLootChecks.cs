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
            "Procedural ground-loot checks passed: catalog IDs match generated rocks and sticks.");
    }

    private static void CatalogMatchesSoloChunkGenerator()
    {
        const long seed = 67;
        var compared = 0;
        for (var chunkY = -2; chunkY <= 2; chunkY++)
        for (var chunkX = -2; chunkX <= 2; chunkX++)
        {
            var coordinate = new ChunkCoordinate(chunkX, chunkY);
            var generated = InfiniteWorldGenerator.Generate(seed, coordinate);
            var catalog = ProceduralGroundLootCatalog.DescribeChunk(
                seed, new WorldChunkKey(chunkX, chunkY, 0));
            var generatedCore = generated.GroundObjects
                .Where(IsCatalogItem)
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            var catalogIds = catalog
                .Select(value => value.Id)
                .OrderBy(value => value)
                .ToArray();
            Assert(
                generatedCore.SequenceEqual(catalogIds),
                "procedural ground loot must use the same stable IDs as solo chunk generation");
            compared += generatedCore.Length;
        }

        Assert(compared > 0,
            "the fixture must include generated sticks or rocks");
    }

    private static bool IsCatalogItem(WorldGroundObject value) =>
        value.ItemId is ItemIds.Sticks or ItemIds.LargeRock or
            ItemIds.WildGrainSeeds or ItemIds.BeanSeeds or ItemIds.RootSeeds;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
