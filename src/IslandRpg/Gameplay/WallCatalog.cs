namespace IslandRpg.Gameplay;

internal sealed record WallDefinition(
    string ItemId,
    int MaximumHealth,
    string RefundItemId);

internal static class WallCatalog
{
    private static readonly IReadOnlyDictionary<string, WallDefinition>
        Definitions = new Dictionary<string, WallDefinition>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.WoodenFence] = new(
                ItemIds.WoodenFence, 70, ItemIds.Sticks),
            [ItemIds.WoodenWall] = new(
                ItemIds.WoodenWall, 120, ItemIds.Logs),
            [ItemIds.StoneWall] = new(
                ItemIds.StoneWall, 260, ItemIds.LargeRock),
            [ItemIds.FortifiedWall] = new(
                ItemIds.FortifiedWall, 420, ItemIds.LargeRock)
        };

    public static IReadOnlyCollection<WallDefinition> All =>
        Definitions.Values.ToArray();

    public static bool IsWall(string itemId) =>
        Definitions.ContainsKey(itemId);

    public static WallDefinition Get(string itemId) =>
        Definitions.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown wall: {itemId}");
}
