using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal sealed record MiningNodeDefinition(
    string GraphicName,
    string DisplayName,
    int MaximumHealth,
    string? RewardItemId,
    int CompletionExperience);

internal static class MiningNodeCatalog
{
    private static readonly Dictionary<string, MiningNodeDefinition> Nodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [UndergroundResourceGenerator.Coal] = new(
                UndergroundResourceGenerator.Coal,
                "coal deposit", 75, ItemIds.Coal, 24),
            [UndergroundResourceGenerator.Tin] = new(
                UndergroundResourceGenerator.Tin,
                "tin deposit", 85, ItemIds.TinOre, 28),
            [UndergroundResourceGenerator.Copper] = new(
                UndergroundResourceGenerator.Copper,
                "copper deposit", 100, ItemIds.CopperOre, 34),
            [UndergroundResourceGenerator.Iron] = new(
                UndergroundResourceGenerator.Iron,
                "iron deposit", 125, ItemIds.IronOre, 42),
            ["STONM_NN"] = new(
                "STONM_NN", "stone deposit", 95, ItemIds.LargeRock, 26),
            ["OREM_NN"] = new(
                "OREM_NN", "stone outcrop", 80, ItemIds.LargeRock, 22),
            ["ROCKX_NN"] = new(
                "ROCKX_NN", "jagged rock", 135, null, 40),
            ["ROCK2_NN"] = new(
                "ROCK2_NN", "rock formation", 180, null, 55),
            ["ROCKF1_NN"] = new(
                "ROCKF1_NN", "layered rock", 150, null, 46),
            ["ROCKF2_NN"] = new(
                "ROCKF2_NN", "stone pillar", 210, null, 65),
            ["ROCKF3_NN"] = new(
                "ROCKF3_NN", "massive stone formation", 320, null, 95)
        };

    public static bool TryGet(
        WorldVegetation vegetation,
        out MiningNodeDefinition definition) =>
        Nodes.TryGetValue(vegetation.GraphicName, out definition!);
}
