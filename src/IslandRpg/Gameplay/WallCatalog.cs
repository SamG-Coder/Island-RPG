namespace IslandRpg.Gameplay;

internal enum WallFamily
{
    Fence,
    Palisade,
    FortifiedPalisade,
    Stone,
    FortifiedStone
}

internal sealed record WallDefinition(
    string ItemId,
    int MaximumHealth,
    string RefundItemId,
    string Name = "Wall",
    string Architecture = "Common",
    WallFamily Family = WallFamily.Palisade,
    string GraphicName = "FENCEN1G",
    short GraphicId = 8501,
    string? ShadowGraphicName = "FENCEN0G",
    short ShadowGraphicId = 8500,
    bool UsesStoneConstructionStages = false,
    int RequiredLevel = 1,
    int LogCost = 0,
    int RockCost = 0);

internal static class WallCatalog
{
    private static readonly Dictionary<string, WallDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.WoodenFence] = new(
                ItemIds.WoodenFence, 70, ItemIds.Sticks,
                "Wooden fence", "Common", WallFamily.Fence,
                "FENCENNG", 8502, null, 0,
                RequiredLevel: 1),
            [ItemIds.WoodenWall] = new(
                ItemIds.WoodenWall, 120, ItemIds.Logs,
                "Wooden wall", "Common", WallFamily.Palisade,
                "FENCEN1G", 8501, "FENCEN0G", 8500,
                RequiredLevel: 1, LogCost: 1),
            [ItemIds.FortifiedWoodenWall] = new(
                ItemIds.FortifiedWoodenWall, 190, ItemIds.Logs,
                "Fortified wooden wall", "Common",
                WallFamily.FortifiedPalisade,
                "WALL1N1G", 605, "WALL1N0G", 604,
                RequiredLevel: 4, LogCost: 2),
            [ItemIds.StoneWall] = new(
                ItemIds.StoneWall, 260, ItemIds.LargeRock,
                "Western European stone wall", "Western European",
                WallFamily.Stone, "WALL2NNW", 2024,
                "WALL2N0W", 2016, true, 6, RockCost: 3),
            [ItemIds.FortifiedWall] = new(
                ItemIds.FortifiedWall, 420, ItemIds.LargeRock,
                "Western European fortified wall", "Western European",
                WallFamily.FortifiedStone, "WALL3NNW", 2036,
                "WALL3N0W", 2028, true, 9, RockCost: 5)
        };

    private static readonly (string Architecture, string Suffix,
        short StoneId, short StoneShadowId,
        short FortifiedId, short FortifiedShadowId)[] Variants =
    [
        ("Central European", "E", 2021, 2013, 2033, 2025),
        ("East Asian", "F", 2022, 2014, 2034, 2026),
        ("Middle Eastern", "M", 2023, 2015, 2035, 2027),
        ("Expansion I", "X", 7096, 7094, 7100, 7098),
        ("Expansion II", "X", 7390, 7388, 7394, 7392),
        ("Expansion III", "X", 8096, 8094, 8100, 8098),
        ("Expansion IV", "X", 9096, 9094, 9100, 9098),
        ("Expansion V", "X", 10096, 10094, 10100, 10098),
        ("Expansion VI", "X", 11096, 11094, 11100, 11098)
    ];

    static WallCatalog()
    {
        foreach (var value in Variants)
        {
            AddVariant(
                value.StoneId,
                $"{value.Architecture} stone wall", value.Architecture,
                WallFamily.Stone, $"WALL2NN{value.Suffix}",
                value.StoneId, $"WALL2N0{value.Suffix}",
                value.StoneShadowId, 260, 6, 3);
            AddVariant(
                value.FortifiedId,
                $"{value.Architecture} fortified wall", value.Architecture,
                WallFamily.FortifiedStone, $"WALL3NN{value.Suffix}",
                value.FortifiedId, $"WALL3N0{value.Suffix}",
                value.FortifiedShadowId, 420, 9, 5);
        }
    }

    public static IReadOnlyCollection<WallDefinition> All =>
        Definitions.Values.ToArray();

    public static bool IsWall(string itemId) =>
        Definitions.ContainsKey(itemId);

    public static WallDefinition Get(string itemId) =>
        Definitions.TryGetValue(itemId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown wall: {itemId}");

    public static IReadOnlyList<CraftingRecipe> VariantRecipes =>
        All.Where(value => value.ItemId.StartsWith(
                "wall_variant_", StringComparison.Ordinal))
            .Select(value => new CraftingRecipe(
                $"build-{value.ItemId}", value.ItemId,
                CraftingCategory.Furniture, value.RequiredLevel,
                80 + value.RequiredLevel * 15,
                [new(ItemIds.LargeRock, value.RockCost)],
                ["Mark the wall route.", "Raise and secure the wall."],
                RequiredTools: [new(ItemTag.Hammer, "hammer")]))
            .ToArray();

    private static void AddVariant(
        short id, string name, string architecture, WallFamily family,
        string graphic, short graphicId, string shadow, short shadowId,
        int health, int level, int rocks)
    {
        var itemId = $"wall_variant_{id}";
        Definitions[itemId] = new(
            itemId, health, ItemIds.LargeRock, name, architecture, family,
            graphic, graphicId, shadow, shadowId, true,
            level, RockCost: rocks);
    }
}
