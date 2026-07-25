namespace IslandRpg.Gameplay;

[Flags]
internal enum ItemTag
{
    None = 0,
    Axe = 1 << 0,
    Log = 1 << 1,
    WoodcuttingMaterial = 1 << 2,
    NaturalMaterial = 1 << 3
}

internal sealed record ItemDefinition(
    string Id,
    string Name,
    string Caption,
    string Examine,
    int? SpriteCell,
    bool Droppable = true,
    ItemTag Tags = ItemTag.None)
{
    public bool HasTag(ItemTag tag) => (Tags & tag) == tag;
}

internal static class ItemIds
{
    public const string Axe = "axe";
    public const string Logs = "logs";
    public const string OakLogs = "oak_logs";
    public const string PineLogs = "pine_logs";
    public const string PalmLogs = "palm_logs";
    public const string Bamboo = "bamboo";
    public const string WoodChips = "wood_chips";
    public const string Plank = "plank";
    public const string Sticks = "sticks";
    public const string LargeRock = "large_rock";
    public const string MediumRock = "medium_rock";
    public const string SmallRocks = "small_rocks";
}

internal static class ItemCatalog
{
    private static readonly Dictionary<string, ItemDefinition> Items =
        new Dictionary<string, ItemDefinition>(
            StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.Logs] = new(
                ItemIds.Logs, "logs", "Logs",
                "Logs cut from a tree.", 0,
                Tags: ItemTag.Log | ItemTag.WoodcuttingMaterial),
            [ItemIds.OakLogs] = new(
                ItemIds.OakLogs, "oak logs", "Oak",
                "Logs cut from a sturdy oak tree.", 1,
                Tags: ItemTag.Log | ItemTag.WoodcuttingMaterial),
            [ItemIds.PineLogs] = new(
                ItemIds.PineLogs, "pine logs", "Pine",
                "Fresh pine logs with a sharp woodland scent.", 2,
                Tags: ItemTag.Log | ItemTag.WoodcuttingMaterial),
            [ItemIds.PalmLogs] = new(
                ItemIds.PalmLogs, "palm logs", "Palm",
                "Fibrous logs cut from a palm tree.", 3,
                Tags: ItemTag.Log | ItemTag.WoodcuttingMaterial),
            [ItemIds.Bamboo] = new(
                ItemIds.Bamboo, "bamboo", "Bamb",
                "A strong, lightweight length of bamboo.", 4,
                Tags: ItemTag.Log | ItemTag.WoodcuttingMaterial),
            [ItemIds.Axe] = new(
                ItemIds.Axe, "axe", "Axe",
                "A sturdy axe for chopping down trees.", 5,
                Droppable: false, Tags: ItemTag.Axe),
            [ItemIds.WoodChips] = new(
                ItemIds.WoodChips, "wood chips", "Chips",
                "Small chips left over from worked timber.", 6,
                Tags: ItemTag.WoodcuttingMaterial),
            [ItemIds.Plank] = new(
                ItemIds.Plank, "plank", "Plank",
                "A prepared wooden plank.", 7,
                Tags: ItemTag.WoodcuttingMaterial),
            [ItemIds.Sticks] = new(
                ItemIds.Sticks, "sticks", "Sticks",
                "A pair of dry fallen sticks.", 0,
                Tags: ItemTag.NaturalMaterial),
            [ItemIds.LargeRock] = new(
                ItemIds.LargeRock, "large rock", "Large",
                "A heavy rock, useful for breaking other rocks.", 1,
                Tags: ItemTag.NaturalMaterial),
            [ItemIds.MediumRock] = new(
                ItemIds.MediumRock, "medium rock", "Medium",
                "A medium-sized piece of broken rock.", 2,
                Tags: ItemTag.NaturalMaterial),
            [ItemIds.SmallRocks] = new(
                ItemIds.SmallRocks, "small rocks", "Pebbles",
                "A handful of small rocks and pebbles.", 3,
                Tags: ItemTag.NaturalMaterial)
        };

    public static IReadOnlyCollection<ItemDefinition> All =>
        Items.Values;

    public static bool TryGet(
        string itemId, out ItemDefinition definition) =>
        Items.TryGetValue(itemId, out definition!);

    public static ItemDefinition Get(string itemId)
    {
        if (TryGet(itemId, out var definition))
            return definition;
        var name = itemId.Replace('_', ' ').Trim();
        if (name.Length == 0) name = "unknown item";
        return new(
            itemId, name, name,
            $"An unfamiliar item called {name}.", null);
    }
}
