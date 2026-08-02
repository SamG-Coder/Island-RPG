namespace IslandRpg.Gameplay;

[Flags]
internal enum ItemTag : long
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
    CookedFood = 1 << 11,
    BurntFood = 1 << 12,
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
    BerrySprite = 1 << 23,
    MetalToolSprite = 1 << 24,
    MetalMaterialSprite = 1 << 25,
    ProgressionSprite = 1 << 26,
    Sickle = 1 << 27,
    AdvancedToolSprite = 1 << 28,
    FishingNetUpgradeSprite = 1 << 29,
    Weapon = 1 << 30,
    PersonalGoalSprite = 1L << 31,
    CropSprite = 1L << 32,
    SlimeLootSprite = 1L << 33,
    SlimeCraftedSprite = 1L << 34,
    Medicine = 1L << 35
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
    int MiningPower = 0,
    int FarmingPower = 0,
    int DiggingPower = 0,
    int FishingPower = 0,
    int HammerPower = 0,
    int KnifePower = 0,
    bool CanStack = false)
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
    public const string BronzePickaxe = "bronze_pickaxe";
    public const string BronzeAxe = "bronze_axe";
    public const string BronzeSickle = "bronze_sickle";
    public const string BronzeHammer = "bronze_hammer";
    public const string IronHammer = "iron_hammer";
    public const string BronzeKnife = "bronze_knife";
    public const string IronKnife = "iron_knife";
    public const string BronzeShovel = "bronze_shovel";
    public const string IronShovel = "iron_shovel";
    public const string IronSickle = "iron_sickle";
    public const string IronPickaxe = "iron_pickaxe";
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
    public const string WildGrainSeeds = "wild_grain_seeds";
    public const string WildGrain = "wild_grain";
    public const string BeanSeeds = "bean_seeds";
    public const string Beans = "beans";
    public const string RootSeeds = "root_seeds";
    public const string EdibleRoots = "edible_roots";
    public const string PortableTorch = "portable_torch";
    public const string GatheringBasket = "gathering_basket";
    public const string Pearl = "pearl";
    public const string StoneSickle = "stone_sickle";
    public const string WildGrainCrop = "wild_grain_crop";
    public const string BeanCrop = "bean_crop";
    public const string RootCrop = "root_crop";
    public const string SharpenedRock = "sharpened_rock";
    public const string Coal = "coal";
    public const string TinOre = "tin_ore";
    public const string CopperOre = "copper_ore";
    public const string IronOre = "iron_ore";
    public const string BronzeBar = "bronze_bar";
    public const string IronBloom = "iron_bloom";
    public const string IronBar = "iron_bar";
    public const string Charcoal = "charcoal";
    public const string FishBerryStew = "fish_berry_stew";
    public const string WildBerries = "wild_berries";
    public const string TropicalBerries = "tropical_berries";
    public const string RoastedWildBerries = "roasted_wild_berries";
    public const string RoastedTropicalBerries = "roasted_tropical_berries";
    public const string BurntWildBerries = "burnt_wild_berries";
    public const string BurntTropicalBerries = "burnt_tropical_berries";
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
    public const string ReinforcedFishingNet = "reinforced_fishing_net";
    public const string AdvancedFishingNet = "advanced_fishing_net";
    public const string Workbench = "workbench";
    public const string Campfire = "campfire";
    public const string Bloomery = "bloomery";
    public const string SmithingAnvil = "smithing_anvil";
    public const string CookingPot = "cooking_pot";
    public const string StorageChest = "storage_chest";
    public const string StorageBarrel = "storage_barrel";
    public const string CaveHole = "cave_hole";
    public const string CaveEntrance = "cave_entrance";
    public const string DigSite = "dig_site";
    public const string ShallowHole = "shallow_hole";
    public const string TrainingDummy = "training_dummy";
    public const string LootBag = "loot_bag";
    public const string SlimeGel = "slime_gel";
    public const string SlimeCore = "slime_core";
    public const string SaltCrystals = "salt_crystals";
    public const string MedicinalHerbs = "medicinal_herbs";
    public const string SaltedFish = "salted_fish";
    public const string HerbalPoultice = "herbal_poultice";
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
                WoodcuttingPower: 3),
            [ItemIds.StoneHammer] = new(
                ItemIds.StoneHammer, "stone hammer", "Stone hammer",
                "A primitive hammer with a stone head lashed to a wooden handle.",
                0, Tags: ItemTag.Tool | ItemTag.Hammer |
                         ItemTag.StoneToolSprite,
                HammerPower: 1),
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
            [ItemIds.BronzePickaxe] = new(
                ItemIds.BronzePickaxe, "bronze pickaxe",
                "Bronze pickaxe",
                "A bronze-alloy pickaxe made from copper and tin.",
                0, Tags: ItemTag.Tool | ItemTag.Pickaxe |
                         ItemTag.MetalToolSprite,
                MiningPower: 2),
            [ItemIds.BronzeAxe] = new(
                ItemIds.BronzeAxe, "bronze axe", "Bronze axe",
                "A balanced bronze axe that cuts more effectively than stone.",
                0, Tags: ItemTag.Tool | ItemTag.Axe |
                         ItemTag.ProgressionSprite,
                WoodcuttingPower: 2),
            [ItemIds.BronzeSickle] = new(
                ItemIds.BronzeSickle, "bronze sickle", "Bronze sickle",
                "A curved bronze harvesting blade for gathering berries.",
                4, Tags: ItemTag.Tool | ItemTag.Sickle |
                         ItemTag.ProgressionSprite,
                FarmingPower: 1),
            [ItemIds.BronzeHammer] = MetalTool(
                ItemIds.BronzeHammer, "bronze hammer", "Bronze hammer",
                "A balanced bronze hammer for demanding workshop tasks.",
                0, ItemTag.Hammer, toolPower: 2),
            [ItemIds.IronHammer] = MetalTool(
                ItemIds.IronHammer, "iron hammer", "Iron hammer",
                "A durable iron hammer suited to advanced smithing.",
                1, ItemTag.Hammer, toolPower: 3),
            [ItemIds.BronzeKnife] = MetalTool(
                ItemIds.BronzeKnife, "bronze knife", "Bronze knife",
                "A keen bronze knife for precise cutting and carving.",
                2, ItemTag.Knife, toolPower: 2),
            [ItemIds.IronKnife] = MetalTool(
                ItemIds.IronKnife, "iron knife", "Iron knife",
                "A strong iron knife that holds a reliable edge.",
                3, ItemTag.Knife, toolPower: 3),
            [ItemIds.BronzeShovel] = MetalTool(
                ItemIds.BronzeShovel, "bronze shovel", "Bronze shovel",
                "A bronze shovel that cuts firmly into compact earth.",
                4, ItemTag.Shovel, diggingPower: 2),
            [ItemIds.IronShovel] = MetalTool(
                ItemIds.IronShovel, "iron shovel", "Iron shovel",
                "A reinforced iron shovel for difficult excavations.",
                5, ItemTag.Shovel, diggingPower: 3),
            [ItemIds.IronSickle] = MetalTool(
                ItemIds.IronSickle, "iron sickle", "Iron sickle",
                "A sharp iron sickle for efficient harvesting.",
                6, ItemTag.Sickle, farmingPower: 2),
            [ItemIds.IronPickaxe] = new(
                ItemIds.IronPickaxe, "iron pickaxe", "Iron pickaxe",
                "A strong forged iron pickaxe for breaking dense ore.",
                1, Tags: ItemTag.Tool | ItemTag.Pickaxe |
                         ItemTag.MetalToolSprite,
                MiningPower: 3),
            [ItemIds.StoneShovel] = new(
                ItemIds.StoneShovel, "stone shovel", "Stone shovel",
                "A broad stone blade lashed to a wooden shaft for digging.",
                4, Tags: ItemTag.Tool | ItemTag.Shovel |
                         ItemTag.StoneToolSprite,
                DiggingPower: 1),
            [ItemIds.StoneKnife] = new(
                ItemIds.StoneKnife, "stone knife", "Stone knife",
                "A primitive cutting tool with a stone blade bound to a wooden grip.",
                3, Tags: ItemTag.Tool | ItemTag.Knife | ItemTag.Weapon |
                         ItemTag.StoneToolSprite,
                KnifePower: 1),
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
            [ItemIds.SlimeGel] = new(
                ItemIds.SlimeGel, "slime gel", "Slime gel",
                "A cool, elastic residue left behind by a defeated slime.", 0,
                Tags: ItemTag.NaturalMaterial | ItemTag.SlimeLootSprite),
            [ItemIds.SlimeCore] = new(
                ItemIds.SlimeCore, "slime core", "Slime core",
                "A rare condensed core that once animated a slime.", 1,
                Tags: ItemTag.NaturalMaterial | ItemTag.SlimeLootSprite),
            [ItemIds.SaltCrystals] = new(
                ItemIds.SaltCrystals, "salt crystals", "Salt",
                "Coarse mineral salt crystallised inside a coastal slime.", 2,
                Tags: ItemTag.NaturalMaterial | ItemTag.SlimeLootSprite),
            [ItemIds.MedicinalHerbs] = new(
                ItemIds.MedicinalHerbs, "medicinal herbs", "Herbs",
                "A fragrant bundle of herbs preserved within a grass slime.", 3,
                Tags: ItemTag.NaturalMaterial | ItemTag.SlimeLootSprite |
                      ItemTag.Medicine),
            [ItemIds.SaltedFish] = new(
                ItemIds.SaltedFish, "salted fish", "Salted fish",
                "Cooked fish preserved with coarse salt for a sustaining meal.", 0,
                Tags: ItemTag.CookedFood | ItemTag.SlimeCraftedSprite),
            [ItemIds.HerbalPoultice] = new(
                ItemIds.HerbalPoultice, "herbal poultice", "Poultice",
                "Medicinal leaves wrapped in clean fibre for treating wounds.", 1,
                Tags: ItemTag.Medicine | ItemTag.SlimeCraftedSprite),
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
            [ItemIds.WildGrainSeeds] = PersonalGoal(
                ItemIds.WildGrainSeeds, "wild grain seeds", "Grain seed",
                "A pouch of hardy grain seed suitable for open soil.", 0,
                ItemTag.Seed),
            [ItemIds.WildGrain] = PersonalGoal(
                ItemIds.WildGrain, "wild grain", "Grain",
                "A tied sheaf of nutritious wild grain.", 1),
            [ItemIds.BeanSeeds] = PersonalGoal(
                ItemIds.BeanSeeds, "bean seeds", "Bean seed",
                "Mottled beans selected for planting.", 2, ItemTag.Seed),
            [ItemIds.Beans] = PersonalGoal(
                ItemIds.Beans, "beans", "Beans",
                "A handful of fresh, filling beans.", 3),
            [ItemIds.RootSeeds] = PersonalGoal(
                ItemIds.RootSeeds, "root seeds", "Root seed",
                "Seeds for a hardy edible root crop.", 4, ItemTag.Seed),
            [ItemIds.EdibleRoots] = PersonalGoal(
                ItemIds.EdibleRoots, "edible roots", "Roots",
                "Earthy roots that can be eaten raw.", 5),
            [ItemIds.PortableTorch] = PersonalGoal(
                ItemIds.PortableTorch, "portable torch", "Torch",
                "A resinous wrapped torch that illuminates dark places.", 6,
                ItemTag.Tool),
            [ItemIds.GatheringBasket] = PersonalGoal(
                ItemIds.GatheringBasket, "gathering basket", "Basket",
                "A woven basket that helps keep gathered materials together.",
                7, ItemTag.Tool),
            [ItemIds.Pearl] = PersonalGoal(
                ItemIds.Pearl, "pearl", "Pearl",
                "A rare luminous pearl prized by collectors.", 8),
            [ItemIds.StoneSickle] = new(
                ItemIds.StoneSickle, "stone sickle", "Stone sickle",
                "A curved knapped blade lashed to a wooden handle.", 9,
                Tags: ItemTag.Tool | ItemTag.Sickle |
                      ItemTag.PersonalGoalSprite,
                FarmingPower: 1),
            [ItemIds.WildGrainCrop] = Crop(
                ItemIds.WildGrainCrop, "wild grain crop", 0),
            [ItemIds.BeanCrop] = Crop(
                ItemIds.BeanCrop, "bean crop", 1),
            [ItemIds.RootCrop] = Crop(
                ItemIds.RootCrop, "root crop", 2),
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
                "Warm-coloured copper ore ready to smelt with tin.", 2,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.IronOre] = new(
                ItemIds.IronOre, "iron ore", "Iron ore",
                "Dense iron-rich ore with a rusty red surface.", 3,
                Tags: ItemTag.Mineral | ItemTag.NaturalMaterial |
                      ItemTag.MiningMaterial | ItemTag.MiningSprite),
            [ItemIds.BronzeBar] = new(
                ItemIds.BronzeBar, "bronze bar", "Bronze bar",
                "Copper and tin alloy cast into a workable bronze bar.", 0,
                Tags: ItemTag.Mineral | ItemTag.MetalMaterialSprite),
            [ItemIds.IronBloom] = new(
                ItemIds.IronBloom, "iron bloom", "Iron bloom",
                "A porous mass of iron and slag fresh from a charcoal smelt.", 1,
                Tags: ItemTag.Mineral | ItemTag.MetalMaterialSprite),
            [ItemIds.IronBar] = new(
                ItemIds.IronBar, "iron bar", "Iron bar",
                "An iron bloom reheated and hammered into a clean billet.", 2,
                Tags: ItemTag.Mineral | ItemTag.MetalMaterialSprite),
            [ItemIds.Charcoal] = new(
                ItemIds.Charcoal, "charcoal", "Charcoal",
                "Slow-burned wood carbon suitable for a bloomery.",
                3, Tags: ItemTag.MiningMaterial |
                         ItemTag.ProgressionSprite),
            [ItemIds.FishBerryStew] = new(
                ItemIds.FishBerryStew, "fish and berry stew", "Stew",
                "A nourishing stew of fresh fish and tart berries.",
                2, Tags: ItemTag.CookedFood |
                         ItemTag.ProgressionSprite),
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
            [ItemIds.RoastedWildBerries] = new(
                ItemIds.RoastedWildBerries, "roasted wild berries",
                "Roasted berries",
                "Wild berries softened and sweetened over a campfire.",
                0, Tags: ItemTag.Berry | ItemTag.BerrySprite |
                         ItemTag.CookedFood),
            [ItemIds.RoastedTropicalBerries] = new(
                ItemIds.RoastedTropicalBerries,
                "roasted tropical berries", "Roasted berries",
                "Golden berries caramelised over a campfire.",
                1, Tags: ItemTag.Berry | ItemTag.BerrySprite |
                         ItemTag.CookedFood),
            [ItemIds.BurntWildBerries] = new(
                ItemIds.BurntWildBerries, "burnt wild berries",
                "Burnt berries", "A bitter, blackened handful of berries.",
                0, Tags: ItemTag.Berry | ItemTag.BerrySprite |
                         ItemTag.CookedFood | ItemTag.BurntFood),
            [ItemIds.BurntTropicalBerries] = new(
                ItemIds.BurntTropicalBerries,
                "burnt tropical berries", "Burnt berries",
                "Golden berries scorched beyond usefulness.",
                1, Tags: ItemTag.Berry | ItemTag.BerrySprite |
                         ItemTag.CookedFood | ItemTag.BurntFood),
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
                      ItemTag.FibreNetSprite,
                FishingPower: 1),
            [ItemIds.ReinforcedFishingNet] = FishingNet(
                ItemIds.ReinforcedFishingNet,
                "reinforced fishing net", "Reinforced net",
                "A rope net reinforced for larger coastal fish.", 0, 2),
            [ItemIds.AdvancedFishingNet] = FishingNet(
                ItemIds.AdvancedFishingNet,
                "advanced fishing net", "Advanced net",
                "A dense net with iron weights for powerful ocean fish.",
                1, 3),
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
            [ItemIds.Bloomery] = new(
                ItemIds.Bloomery,
                "bloomery", "Bloomery",
                "A charcoal-fired clay shaft furnace for smelting metal.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.SmithingAnvil] = new(
                ItemIds.SmithingAnvil,
                "smithing anvil", "Anvil",
                "A heavy bronze-faced anvil for consolidating and forging metal.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.CookingPot] = new(
                ItemIds.CookingPot,
                "cooking pot", "Cooking pot",
                "A heavy bronze cooking pot. Place it close to a campfire.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.StorageChest] = new(
                ItemIds.StorageChest,
                "wooden chest", "Chest",
                "A reinforced wooden chest with room for many stored items.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.StorageBarrel] = new(
                ItemIds.StorageBarrel,
                "storage barrel", "Barrel",
                "A compact wooden barrel for persistent item storage.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.TrainingDummy] = new(
                ItemIds.TrainingDummy,
                "training dummy", "Training dummy",
                "A reinforced practice target available only through developer tools.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
            [ItemIds.LootBag] = new(
                ItemIds.LootBag,
                "loot bag", "Loot",
                "A dropped bag of spoils. It can only be emptied.",
                0, Droppable: false,
                Tags: ItemTag.PlaceableObject),
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

    static ItemCatalog()
    {
        foreach (var (id, item) in Items.ToArray())
            if (IsSlimeDropStackable(id))
                Items[id] = item with { CanStack = true };
    }

    private static bool IsSlimeDropStackable(string itemId) =>
        itemId is
            ItemIds.SlimeGel or
            ItemIds.SlimeCore or
            ItemIds.SaltCrystals or
            ItemIds.MedicinalHerbs;

    private static ItemDefinition Coastal(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.NaturalMaterial | ItemTag.CoastalSprite);

    private static ItemDefinition PersonalGoal(
        string id, string name, string caption, string examine, int cell,
        ItemTag tags = ItemTag.None) =>
        new(
            id, name, caption, examine, cell,
            Tags: tags | ItemTag.PersonalGoalSprite);

    private static ItemDefinition Crop(string id, string name, int cell) =>
        new(
            id, name, name,
            "A planted crop growing in worked soil.", cell,
            Droppable: false, Tags: ItemTag.CropSprite);

    private static ItemDefinition MetalTool(
        string id, string name, string caption, string examine, int cell,
        ItemTag toolTag, int farmingPower = 0, int diggingPower = 0,
        int toolPower = 0) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Tool | toolTag | ItemTag.AdvancedToolSprite |
                  (toolTag == ItemTag.Knife ? ItemTag.Weapon : ItemTag.None),
            FarmingPower: farmingPower,
            DiggingPower: diggingPower,
            HammerPower: toolTag == ItemTag.Hammer ? toolPower : 0,
            KnifePower: toolTag == ItemTag.Knife ? toolPower : 0);

    private static ItemDefinition FishingNet(
        string id, string name, string caption, string examine, int cell,
        int fishingPower) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Tool | ItemTag.FishingNet |
                  ItemTag.FishingNetUpgradeSprite,
            FishingPower: fishingPower);

    private static ItemDefinition Fish(
        string id, string name, string caption, string examine, int cell) =>
        new(id, name, caption, examine, cell, Tags: ItemTag.Fish);

    private static ItemDefinition CookedFish(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Fish | ItemTag.CookedFood);

    private static ItemDefinition BurntFish(
        string id, string name, string caption, string examine, int cell) =>
        new(
            id, name, caption, examine, cell,
            Tags: ItemTag.Fish | ItemTag.CookedFood | ItemTag.BurntFood);

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
