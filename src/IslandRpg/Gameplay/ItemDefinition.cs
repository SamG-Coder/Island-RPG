namespace IslandRpg.Gameplay;

[Flags]
internal enum ItemTag
{
    None = 0,
    Axe = 1 << 0,
    Log = 1 << 1,
    WoodcuttingMaterial = 1 << 2,
    NaturalMaterial = 1 << 3,
    Tool = 1 << 4,
    Seed = 1 << 5,
    Mineral = 1 << 6,
    SupplementalSprite = 1 << 7,
    StoneToolSprite = 1 << 8,
    CoastalSprite = 1 << 9
}

internal sealed record ItemDefinition(
    string Id,
    string Name,
    string Caption,
    string Examine,
    int? SpriteCell,
    bool Droppable = true,
    ItemTag Tags = ItemTag.None,
    int WoodcuttingPower = 0)
{
    public bool HasTag(ItemTag tag) => (Tags & tag) == tag;
}

internal static class ItemIds
{
    public const string IronAxe = "axe";
    public const string Axe = IronAxe;
    public const string StoneAxe = "stone_axe";
    public const string StoneHammer = "stone_hammer";
    public const string StonePickaxe = "stone_pickaxe";
    public const string BluntStoneAxe = "blunt_stone_axe";
    public const string BluntStoneHammer = "blunt_stone_hammer";
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
    public const string TreeSeeds = "tree_seeds";
    public const string PalmSeeds = "palm_seeds";
    public const string PineSeeds = "pine_seeds";
    public const string OakSeeds = "oak_seeds";
    public const string JungleTreeSeeds = "jungle_tree_seeds";
    public const string SnowTreeSeeds = "snow_tree_seeds";
    public const string BambooSeeds = "bamboo_seeds";
    public const string CactusSeeds = "cactus_seeds";
    public const string SharpenedRock = "sharpened_rock";
    public const string Coal = "coal";
    public const string ClamShell = "clam_shell";
    public const string CockleShell = "cockle_shell";
    public const string SpiralShell = "spiral_shell";
    public const string ScallopShell = "scallop_shell";
    public const string MoonShell = "moon_shell";
    public const string ConchShell = "conch_shell";
    public const string CowrieShell = "cowrie_shell";
    public const string PearlOysterShell = "pearl_oyster_shell";
    public const string Seaweed = "seaweed";
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
            [ItemIds.IronAxe] = new(
                ItemIds.IronAxe, "iron axe", "Iron axe",
                "A sturdy iron axe for chopping down trees.", 5,
                Tags: ItemTag.Axe | ItemTag.Tool,
                WoodcuttingPower: 2),
            [ItemIds.StoneHammer] = new(
                ItemIds.StoneHammer, "stone hammer", "Stone hammer",
                "A primitive hammer with a stone head lashed to a wooden handle.",
                0, Tags: ItemTag.Tool | ItemTag.StoneToolSprite),
            [ItemIds.StoneAxe] = new(
                ItemIds.StoneAxe, "stone axe", "Stone axe",
                "A primitive axe with a sharp stone head lashed to a wooden handle.",
                1, Tags: ItemTag.Axe | ItemTag.Tool |
                         ItemTag.StoneToolSprite,
                WoodcuttingPower: 1),
            [ItemIds.StonePickaxe] = new(
                ItemIds.StonePickaxe, "stone pickaxe", "Stone pickaxe",
                "A primitive pickaxe with a pointed stone head lashed to a wooden handle.",
                2, Tags: ItemTag.Tool | ItemTag.StoneToolSprite),
            [ItemIds.BluntStoneHammer] = new(
                ItemIds.BluntStoneHammer, "blunt stone hammer", "Blunt hammer",
                "A stone hammer with worn, blunt working edges.", 0,
                Tags: ItemTag.Tool | ItemTag.StoneToolSprite),
            [ItemIds.BluntStoneAxe] = new(
                ItemIds.BluntStoneAxe, "blunt stone axe", "Blunt axe",
                "A stone axe too blunt to chop effectively.", 1,
                Tags: ItemTag.Axe | ItemTag.Tool |
                      ItemTag.StoneToolSprite),
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
                Tags: ItemTag.NaturalMaterial),
            [ItemIds.TreeSeeds] = new(
                ItemIds.TreeSeeds, "tree seeds", "Tree seed",
                "Seeds from a common deciduous tree.", 0,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.PalmSeeds] = new(
                ItemIds.PalmSeeds, "palm seeds", "Palm seed",
                "Fibrous seeds that can grow into palm trees.", 1,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.PineSeeds] = new(
                ItemIds.PineSeeds, "pine seeds", "Pine seed",
                "Winged seeds released from a pine cone.", 2,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.OakSeeds] = new(
                ItemIds.OakSeeds, "oak seeds", "Oak seed",
                "Healthy acorns suitable for growing oak trees.", 3,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.JungleTreeSeeds] = new(
                ItemIds.JungleTreeSeeds, "jungle tree seeds", "Jungle seed",
                "Richly coloured seeds from a tropical tree.", 4,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.SnowTreeSeeds] = new(
                ItemIds.SnowTreeSeeds, "snow tree seeds", "Snow seed",
                "Hardy seeds from a tree adapted to frozen climates.", 5,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.BambooSeeds] = new(
                ItemIds.BambooSeeds, "bamboo seeds", "Bamboo seed",
                "A tied cluster of grains for growing bamboo.", 6,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.CactusSeeds] = new(
                ItemIds.CactusSeeds, "cactus seeds", "Cactus seed",
                "Tiny dark seeds collected from a cactus pod.", 7,
                Tags: ItemTag.Seed | ItemTag.SupplementalSprite),
            [ItemIds.SharpenedRock] = new(
                ItemIds.SharpenedRock, "sharpened rock", "Sharp rock",
                "A stone deliberately knapped to form a sharp edge.", 8,
                Tags: ItemTag.Tool | ItemTag.NaturalMaterial |
                      ItemTag.SupplementalSprite),
            [ItemIds.Coal] = new(
                ItemIds.Coal, "coal", "Coal",
                "Dense black coal that burns with strong heat.", 9,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.SupplementalSprite),
            [ItemIds.ClamShell] = Coastal(
                ItemIds.ClamShell, "clam shell", "Clam",
                "A common shell washed smooth by the tide.", 0),
            [ItemIds.CockleShell] = Coastal(
                ItemIds.CockleShell, "cockle shell", "Cockle",
                "A strongly ribbed shell found along sandy shores.", 1),
            [ItemIds.SpiralShell] = Coastal(
                ItemIds.SpiralShell, "spiral shell", "Spiral",
                "A tapered shell with a delicate natural spiral.", 2),
            [ItemIds.ScallopShell] = Coastal(
                ItemIds.ScallopShell, "scallop shell", "Scallop",
                "A colourful fan-shaped shell.", 3),
            [ItemIds.MoonShell] = Coastal(
                ItemIds.MoonShell, "moon shell", "Moon",
                "A smooth, luminous shell with a curled centre.", 4),
            [ItemIds.ConchShell] = Coastal(
                ItemIds.ConchShell, "conch shell", "Conch",
                "A large and uncommon spined conch shell.", 5),
            [ItemIds.CowrieShell] = Coastal(
                ItemIds.CowrieShell, "cowrie shell", "Cowrie",
                "A polished shell prized for its unusual shape.", 6),
            [ItemIds.PearlOysterShell] = Coastal(
                ItemIds.PearlOysterShell, "pearl oyster shell", "Oyster",
                "A very rare oyster shell still holding a pearl.", 7),
            [ItemIds.Seaweed] = Coastal(
                ItemIds.Seaweed, "seaweed", "Seaweed",
                "Fresh seaweed cast onto the beach by the tide.", 8)
        };

    private static ItemDefinition Coastal(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.NaturalMaterial | ItemTag.CoastalSprite);

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
