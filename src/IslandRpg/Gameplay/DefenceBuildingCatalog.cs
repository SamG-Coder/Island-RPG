namespace IslandRpg.Gameplay;

internal enum DefenceBuildingKind
{
    Outpost,
    WatchTower,
    GuardTower,
    Keep,
    BombardTower,
    Castle
}

internal sealed record DefenceBuildingDefinition(
    string ItemId,
    string Name,
    string Architecture,
    DefenceBuildingKind Kind,
    string GraphicName,
    short GraphicId,
    string ConstructionGraphicName,
    int FootprintWidth,
    int FootprintDepth,
    int MaximumHealth,
    int RequiredLevel,
    int LogCost,
    int RockCost);

internal static class DefenceBuildingCatalog
{
    private static readonly string[] Architectures =
    [
        "Central European", "East Asian", "Indian", "Middle Eastern",
        "Western European", "Expansion I", "Expansion II",
        "Expansion III", "Expansion IV", "Expansion V"
    ];

    private sealed record TowerTier(
        string Name,
        DefenceBuildingKind Kind,
        string GraphicPrefix,
        short[] GraphicIds,
        int Health,
        int Level,
        int Logs,
        int Rocks);

    private static readonly TowerTier[] TowerTiers =
    [
        new("Watch tower", DefenceBuildingKind.WatchTower, "WCTW1NNG",
            [4199, 4200, 4171, 4201, 4202, 7116, 8116, 9116, 10116, 11116],
            300, 4, 5, 2),
        new("Guard tower", DefenceBuildingKind.GuardTower, "WCTW2NNG",
            [2529, 2530, 2517, 2531, 2532, 7123, 8123, 9123, 10123, 11123],
            450, 6, 6, 5),
        new("Keep", DefenceBuildingKind.Keep, "WCTW3NNG",
            [2404, 2405, 2395, 2406, 2407, 7131, 8131, 9131, 10131, 11131],
            650, 8, 8, 8),
        new("Bombard tower", DefenceBuildingKind.BombardTower, "WCTW4NNG",
            [2412, 2413, 2398, 2414, 2415, 7139, 8139, 9139, 10139, 11139],
            800, 10, 10, 10)
    ];

    private static readonly (string Architecture, string Graphic, short Id)[]
        Castles =
    [
        ("Central European", "CSTL3NNE", 171),
        ("East Asian", "CSTL3NNF", 172),
        ("Indian", "CSTL3NNI", 177),
        ("Middle Eastern", "CSTL3NNM", 173),
        ("Western European", "CSTL3NNW", 174),
        ("African", "CSTL3NNW", 7633),
        ("Expansion I", "CSTL3NNX", 6747),
        ("Expansion II", "CSTL3NNX", 7747),
        ("Expansion III", "CSTL3NNX", 8747),
        ("Expansion IV", "CSTL3NNX", 9747),
        ("Expansion V", "CSTL3NNX", 10747)
    ];

    public static readonly IReadOnlyList<DefenceBuildingDefinition> All =
        CreateAll();

    private static readonly IReadOnlyDictionary<string,
        DefenceBuildingDefinition> ByItemId = All.ToDictionary(
            value => value.ItemId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> RequiredGraphics =>
        All.Select(value => value.GraphicName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsDefence(string itemId) =>
        ByItemId.ContainsKey(itemId);

    public static bool IsDefenceGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static DefenceBuildingDefinition Get(string itemId) =>
        ByItemId.TryGetValue(itemId, out var value)
            ? value
            : throw new KeyNotFoundException($"Unknown defence: {itemId}");

    public static IReadOnlyList<CraftingRecipe> Recipes => All.Select(value =>
        new CraftingRecipe(
            $"build-{value.ItemId}", value.ItemId,
            CraftingCategory.Furniture, value.RequiredLevel,
            100 + value.RequiredLevel * 20,
            new CraftingIngredient[]
            {
                new(ItemIds.Logs, value.LogCost),
                new(ItemIds.LargeRock, value.RockCost)
            }.Where(ingredient => ingredient.Count > 0).ToArray(),
            ["Mark the defensive footprint.", "Raise and secure the structure."],
            RequiredTools: [new(ItemTag.Hammer, "hammer")])).ToArray();

    private static DefenceBuildingDefinition[] CreateAll()
    {
        var result = new List<DefenceBuildingDefinition>
        {
            new(
                "defence_3223", "Outpost", "Early shelter",
                DefenceBuildingKind.Outpost, "WCTWX1NNG", 3223,
                "CNST1_NN", 1, 1, 180, 2, 3, 0)
        };
        foreach (var tier in TowerTiers)
        for (var index = 0; index < Architectures.Length; index++)
        {
            var suffix = index switch
            {
                0 => "E", 1 => "F", 2 => "I", 3 => "M", 4 => "W",
                _ => "X"
            };
            var id = tier.GraphicIds[index];
            result.Add(new(
                $"defence_{id}", $"{Architectures[index]} {tier.Name}",
                Architectures[index], tier.Kind,
                tier.GraphicPrefix + suffix, id,
                "CNST1_NN", 1, 1, tier.Health, tier.Level,
                tier.Logs, tier.Rocks));
        }
        result.AddRange(Castles.Select(value => new DefenceBuildingDefinition(
            $"defence_{value.Id}", $"{value.Architecture} castle",
            value.Architecture, DefenceBuildingKind.Castle,
            value.Graphic, value.Id, "CNST8_NN",
            4, 4, 2000, 12, 12, 12)));
        return result.ToArray();
    }
}
