namespace IslandRpg.Gameplay;

internal sealed record HouseDefinition(
    string ItemId,
    string Architecture,
    string GraphicName,
    short GraphicId,
    int Frame,
    int Tier,
    int MaximumHealth,
    int RequiredLevel,
    int LogCost,
    int RockCost)
{
    public string Name => $"{Architecture} house {Frame + 1}";
}

internal static class HouseCatalog
{
    private static readonly (string Architecture, string Graphic, short Id,
        int Tier)[] GraphicSets =
    [
        ("Early shelter", "HOUS1NNG", 2197, 1),
        ("Central European", "HOUS2NNE", 2206, 2),
        ("East Asian", "HOUS2NNF", 2207, 2),
        ("Indian", "HOUS2NNI", 9202, 2),
        ("Middle Eastern", "HOUS2NNM", 2208, 2),
        ("Western European", "HOUS2NNW", 2209, 2),
        ("Expansion I", "HOUS2NNX", 6909, 2),
        ("Expansion II", "HOUS2NNX", 7909, 2),
        ("Expansion III", "HOUS2NNX", 8909, 2),
        ("Expansion IV", "HOUS2NNX", 9909, 2),
        ("Expansion V", "HOUS2NNX", 10909, 2),
        ("Advanced Central European", "HOUS3NNE", 2220, 3),
        ("Advanced East Asian", "HOUS3NNF", 2221, 3),
        ("Advanced Indian", "HOUS3NNI", 2226, 3),
        ("Advanced Middle Eastern", "HOUS3NNM", 2222, 3),
        ("Advanced Western European", "HOUS3NNW", 2223, 3),
        ("Advanced Expansion I", "HOUS3NNX", 6916, 3),
        ("Advanced Expansion II", "HOUS3NNX", 7916, 3),
        ("Advanced Expansion III", "HOUS3NNX", 8916, 3),
        ("Advanced Expansion IV", "HOUS3NNX", 9916, 3),
        ("Advanced Expansion V", "HOUS3NNX", 10916, 3)
    ];

    public static readonly IReadOnlyList<HouseDefinition> All =
        GraphicSets.SelectMany(set => Enumerable.Range(0, 3).Select(frame =>
        {
            var level = set.Tier switch { 1 => 2, 2 => 4, _ => 7 };
            var logs = set.Tier switch { 1 => 3, 2 => 5, _ => 6 };
            var rocks = set.Tier switch { 1 => 0, 2 => 2, _ => 3 };
            return new HouseDefinition(
                $"house_{set.Id}_{frame}",
                set.Architecture, set.Graphic, set.Id, frame, set.Tier,
                set.Tier switch { 1 => 180, 2 => 260, _ => 340 },
                level, logs, rocks);
        })).ToArray();

    private static readonly IReadOnlyDictionary<string, HouseDefinition>
        ByItemId = All.ToDictionary(
            value => value.ItemId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> RequiredGraphics =>
        GraphicSets.Select(value => value.Graphic)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsHouse(string itemId) => ByItemId.ContainsKey(itemId);

    public static bool IsHouseGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static HouseDefinition Get(string itemId) =>
        ByItemId.TryGetValue(itemId, out var value)
            ? value
            : throw new KeyNotFoundException($"Unknown house: {itemId}");

    public static IReadOnlyList<CraftingRecipe> Recipes => All.Select(value =>
    {
        var ingredients = new List<CraftingIngredient>
        {
            new(ItemIds.Logs, value.LogCost)
        };
        if (value.RockCost > 0)
            ingredients.Add(new(ItemIds.LargeRock, value.RockCost));
        return new CraftingRecipe(
            $"build-{value.ItemId}", value.ItemId,
            CraftingCategory.Furniture, value.RequiredLevel,
            80 + value.Tier * 45,
            ingredients,
            ["Mark the house footprint.", "Raise and secure the dwelling."],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]);
    }).ToArray();
}
