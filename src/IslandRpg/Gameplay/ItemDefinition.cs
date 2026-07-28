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
    CoastalSprite = 1 << 9,
    Fish = 1 << 10,
    CookedFish = 1 << 11,
    BurntFish = 1 << 12,
    FishingNet = 1 << 13,
    FibreNetSprite = 1 << 14,
    PlaceableObject = 1 << 15,
    Hammer = 1 << 16,
    Knife = 1 << 17,
    Shovel = 1 << 18,
    Pickaxe = 1 << 19,
    MiningMaterial = 1 << 20,
    MiningSprite = 1 << 21,
    Berry = 1 << 22,
    BerrySprite = 1 << 23
}

internal sealed record ItemDefinition(
    string Id,
    string Name,
    string Caption,
    string Examine,
    int? SpriteCell,
    bool Droppable = true,
    ItemTag Tags = ItemTag.None,
    int WoodcuttingPower = 0,
    int MiningPower = 0)
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
    public const string StoneShovel = "stone_shovel";
    public const string StoneKnife = "stone_knife";
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
    public const string TinOre = "tin_ore";
    public const string CopperOre = "copper_ore";
    public const string IronOre = "iron_ore";
    public const string WildBerries = "wild_berries";
    public const string TropicalBerries = "tropical_berries";
    public const string ClamShell = "clam_shell";
    public const string CockleShell = "cockle_shell";
    public const string SpiralShell = "spiral_shell";
    public const string ScallopShell = "scallop_shell";
    public const string MoonShell = "moon_shell";
    public const string ConchShell = "conch_shell";
    public const string CowrieShell = "cowrie_shell";
    public const string PearlOysterShell = "pearl_oyster_shell";
    public const string Seaweed = "seaweed";
    public const string RawMinnows = "raw_minnows";
    public const string RawRiverPerch = "raw_river_perch";
    public const string RawSilverHerring = "raw_silver_herring";
    public const string RawRedSnapper = "raw_red_snapper";
    public const string RawOceanMackerel = "raw_ocean_mackerel";
    public const string RawBluefinTuna = "raw_bluefin_tuna";
    public const string CookedMinnows = "cooked_minnows";
    public const string CookedRiverPerch = "cooked_river_perch";
    public const string CookedSilverHerring = "cooked_silver_herring";
    public const string CookedRedSnapper = "cooked_red_snapper";
    public const string CookedOceanMackerel = "cooked_ocean_mackerel";
    public const string CookedBluefinTuna = "cooked_bluefin_tuna";
    public const string BurntMinnows = "burnt_minnows";
    public const string BurntRiverPerch = "burnt_river_perch";
    public const string BurntSilverHerring = "burnt_silver_herring";
    public const string BurntRedSnapper = "burnt_red_snapper";
    public const string BurntOceanMackerel = "burnt_ocean_mackerel";
    public const string BurntBluefinTuna = "burnt_bluefin_tuna";
    public const string PlantFibres = "plant_fibres";
    public const string Rope = "rope";
    public const string Dirt = "dirt";
    public const string Sand = "sand";
    public const string PrimitiveFishingNet = "primitive_fishing_net";
    public const string Workbench = "workbench";
    public const string Campfire = "campfire";
    public const string CaveHole = "cave_hole";
    public const string CaveEntrance = "cave_entrance";
    public const string DigSite = "dig_site";
    public const string ShallowHole = "shallow_hole";
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
                0, Tags: ItemTag.Tool | ItemTag.Hammer |
                         ItemTag.StoneToolSprite),
            [ItemIds.StoneAxe] = new(
                ItemIds.StoneAxe, "stone axe", "Stone axe",
                "A primitive axe with a sharp stone head lashed to a wooden handle.",
                1, Tags: ItemTag.Axe | ItemTag.Tool |
                         ItemTag.StoneToolSprite,
                WoodcuttingPower: 1),
            [ItemIds.StonePickaxe] = new(
                ItemIds.StonePickaxe, "stone pickaxe", "Stone pickaxe",
                "A primitive pickaxe with a pointed stone head lashed to a wooden handle.",
                2, Tags: ItemTag.Tool | ItemTag.Pickaxe |
                         ItemTag.StoneToolSprite,
                MiningPower: 1),
            [ItemIds.StoneShovel] = new(
                ItemIds.StoneShovel, "stone shovel", "Stone shovel",
                "A broad stone blade lashed to a wooden shaft for digging.",
                4, Tags: ItemTag.Tool | ItemTag.Shovel |
                         ItemTag.StoneToolSprite),
            [ItemIds.StoneKnife] = new(
                ItemIds.StoneKnife, "stone knife", "Stone knife",
                "A primitive cutting tool with a stone blade bound to a wooden grip.",
                3, Tags: ItemTag.Tool | ItemTag.Knife |
                         ItemTag.StoneToolSprite),
            [ItemIds.BluntStoneHammer] = new(
                ItemIds.BluntStoneHammer, "blunt stone hammer", "Blunt hammer",
                "A stone hammer with worn, blunt working edges.", 0,
                Tags: ItemTag.Tool | ItemTag.Hammer |
                      ItemTag.StoneToolSprite),
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
                "Dense black coal that burns with strong heat.", 0,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.TinOre] = new(
                ItemIds.TinOre, "tin ore", "Tin ore",
                "Pale tin-bearing ore broken from an underground deposit.", 1,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.CopperOre] = new(
                ItemIds.CopperOre, "copper ore", "Copper ore",
                "Warm-coloured copper ore ready for future smelting.", 2,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.IronOre] = new(
                ItemIds.IronOre, "iron ore", "Iron ore",
                "Dense iron-rich ore with a rusty red surface.", 3,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.WildBerries] = new(
                ItemIds.WildBerries, "wild berries", "Wild berries",
                "A fresh handful of tart berries gathered from a temperate bush.",
                0, Tags: ItemTag.NaturalMaterial |
                         ItemTag.Berry | ItemTag.BerrySprite),
            [ItemIds.TropicalBerries] = new(
                ItemIds.TropicalBerries, "tropical berries",
                "Tropical berries",
                "A sweet handful of golden berries gathered in a warm climate.",
                1, Tags: ItemTag.NaturalMaterial |
                         ItemTag.Berry | ItemTag.BerrySprite),
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
                "Fresh seaweed cast onto the beach by the tide.", 8),
            [ItemIds.RawMinnows] = Fish(
                ItemIds.RawMinnows, "raw minnows", "Minnows",
                "A small netful of fresh shore minnows.", 0),
            [ItemIds.RawRiverPerch] = Fish(
                ItemIds.RawRiverPerch, "raw river perch", "Perch",
                "A fresh perch caught in inland water.", 2),
            [ItemIds.RawSilverHerring] = Fish(
                ItemIds.RawSilverHerring, "raw silver herring", "Herring",
                "A bright silver herring from coastal water.", 4),
            [ItemIds.RawRedSnapper] = Fish(
                ItemIds.RawRedSnapper, "raw red snapper", "Snapper",
                "A colourful red snapper caught in warm shallows.", 6),
            [ItemIds.RawOceanMackerel] = Fish(
                ItemIds.RawOceanMackerel, "raw ocean mackerel", "Mackerel",
                "A strong ocean mackerel caught in a casting net.", 8),
            [ItemIds.RawBluefinTuna] = Fish(
                ItemIds.RawBluefinTuna, "raw bluefin tuna", "Tuna",
                "A rare bluefin tuna from deep open water.", 10),
            [ItemIds.CookedMinnows] = CookedFish(
                ItemIds.CookedMinnows, "cooked minnows", "Minnows",
                "A small netful of cooked shore minnows.", 1),
            [ItemIds.CookedRiverPerch] = CookedFish(
                ItemIds.CookedRiverPerch, "cooked river perch", "Perch",
                "A river perch cooked to a golden brown.", 3),
            [ItemIds.CookedSilverHerring] = CookedFish(
                ItemIds.CookedSilverHerring, "cooked silver herring", "Herring",
                "A silver herring with crisp, cooked skin.", 5),
            [ItemIds.CookedRedSnapper] = CookedFish(
                ItemIds.CookedRedSnapper, "cooked red snapper", "Snapper",
                "A red snapper roasted until golden.", 7),
            [ItemIds.CookedOceanMackerel] = CookedFish(
                ItemIds.CookedOceanMackerel, "cooked ocean mackerel", "Mackerel",
                "A richly browned ocean mackerel.", 9),
            [ItemIds.CookedBluefinTuna] = CookedFish(
                ItemIds.CookedBluefinTuna, "cooked bluefin tuna", "Tuna",
                "A substantial bluefin tuna cooked through.", 11),
            [ItemIds.BurntMinnows] = BurntFish(
                ItemIds.BurntMinnows, "burnt minnows", "Burnt",
                "A burnt netful of minnows.", 1),
            [ItemIds.BurntRiverPerch] = BurntFish(
                ItemIds.BurntRiverPerch, "burnt river perch", "Burnt",
                "A burnt river perch ruined by too much heat.", 3),
            [ItemIds.BurntSilverHerring] = BurntFish(
                ItemIds.BurntSilverHerring, "burnt silver herring", "Burnt",
                "A badly burnt silver herring.", 5),
            [ItemIds.BurntRedSnapper] = BurntFish(
                ItemIds.BurntRedSnapper, "burnt red snapper", "Burnt",
                "A burnt red snapper cooked far beyond saving.", 7),
            [ItemIds.BurntOceanMackerel] = BurntFish(
                ItemIds.BurntOceanMackerel, "burnt ocean mackerel", "Burnt",
                "A burnt ocean mackerel.", 9),
            [ItemIds.BurntBluefinTuna] = BurntFish(
                ItemIds.BurntBluefinTuna, "burnt bluefin tuna", "Burnt",
                "A rare bluefin tuna burnt beyond saving.", 11),
            [ItemIds.PlantFibres] = new(
                ItemIds.PlantFibres, "plant fibres", "Fibres",
                "Stripped plant fibres suitable for weaving.", 0,
                Tags: ItemTag.NaturalMaterial |
                      ItemTag.FibreNetSprite),
            [ItemIds.Rope] = new(
                ItemIds.Rope, "rope", "Rope",
                "A strong rope twisted from plant fibres.",
                7, Tags: ItemTag.NaturalMaterial |
                         ItemTag.StoneToolSprite),
            [ItemIds.Dirt] = new(
                ItemIds.Dirt, "dirt", "Dirt",
                "Freshly excavated soil.",
                12, Tags: ItemTag.NaturalMaterial |
                          ItemTag.StoneToolSprite),
            [ItemIds.Sand] = new(
                ItemIds.Sand, "sand", "Sand",
                "A mound of loose excavated sand.",
                13, Tags: ItemTag.NaturalMaterial |
                          ItemTag.StoneToolSprite),
            [ItemIds.PrimitiveFishingNet] = new(
                ItemIds.PrimitiveFishingNet,
                "primitive fishing net", "Fishing net",
                "A simple hand-woven net for catching fish.", 1,
                Tags: ItemTag.Tool | ItemTag.FishingNet |
                      ItemTag.FibreNetSprite),
            [ItemIds.Workbench] = new(
                ItemIds.Workbench,
                "workbench", "Workbench",
                "A sturdy woodworking bench. Place it on clear, level ground.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.Campfire] = new(
                ItemIds.Campfire,
                "campfire", "Campfire",
                "An unlit stone fire ring. Add a log before lighting it.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject)
            ,
            [ItemIds.CaveHole] = new(
                ItemIds.CaveHole, "dug hole", "Hole",
                "A test hole opening into a cave below.",
                9, Droppable: false, Tags: ItemTag.StoneToolSprite),
            [ItemIds.CaveEntrance] = new(
                ItemIds.CaveEntrance, "roped cave entrance", "Cave",
                "A secured rope descends into the cave below.",
                9, Droppable: false, Tags: ItemTag.StoneToolSprite),
            [ItemIds.DigSite] = new(
                ItemIds.DigSite, "excavation", "Dig site",
                "A partially excavated hole.",
                8, Droppable: false, Tags: ItemTag.StoneToolSprite),
            [ItemIds.ShallowHole] = new(
                ItemIds.ShallowHole, "shallow hole", "Hole",
                "A completed hole with a visible bottom.",
                8, Droppable: false, Tags: ItemTag.StoneToolSprite)
        };

    private static ItemDefinition Coastal(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.NaturalMaterial | ItemTag.CoastalSprite);

    private static ItemDefinition Fish(
        string id, string name, string caption, string examine, int cell) =>
        new(id, name, caption, examine, cell, Tags: ItemTag.Fish);

    private static ItemDefinition CookedFish(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Fish | ItemTag.CookedFish);

    private static ItemDefinition BurntFish(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Fish | ItemTag.CookedFish | ItemTag.BurntFish);

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
