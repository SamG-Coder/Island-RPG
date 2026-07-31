namespace IslandRpg.Gameplay;

internal static class ItemDescriptionService
{
    public static string Describe(ItemDefinition item)
    {
        var stats = new List<string>(7);
        Add(stats, "Woodcutting", item.WoodcuttingPower);
        Add(stats, "Mining", item.MiningPower);
        Add(stats, "Farming", item.FarmingPower);
        Add(stats, "Digging", item.DiggingPower);
        Add(stats, "Fishing", item.FishingPower);
        Add(stats, "Hammer", item.HammerPower);
        Add(stats, "Knife", item.KnifePower);
        return stats.Count == 0
            ? item.Examine
            : $"{item.Examine} {string.Join(" · ", stats)}.";
    }

    private static void Add(List<string> stats, string name, int power)
    {
        if (power > 0) stats.Add($"{name} power: {power}");
    }
}
