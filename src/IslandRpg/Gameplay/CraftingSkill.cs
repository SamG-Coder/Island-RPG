namespace IslandRpg.Gameplay;

internal enum CraftingCategory
{
    All,
    Tools,
    Furniture,
    Resources
}

internal enum RecipeAvailability
{
    Locked,
    MissingResources,
    MissingStation,
    InventoryFull,
    Ready
}

internal sealed record CraftingIngredient(string ItemId, int Count);
internal sealed record CraftingToolRequirement(
    ItemTag Tag, string Name, int Count = 1);

internal sealed record CraftingInventoryStep(
    IReadOnlyList<CraftingIngredient> Consumes,
    IReadOnlyList<CraftingIngredient> Produces);

internal sealed record CraftingRecipe(
    string Id,
    string ResultItemId,
    CraftingCategory Category,
    int RequiredLevel,
    int Experience,
    IReadOnlyList<CraftingIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<CraftingInventoryStep>? InventorySteps = null,
    IReadOnlyList<CraftingToolRequirement>? RequiredTools = null,
    string? RequiredStationItemId = null);

internal static class CraftingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;

    public static readonly IReadOnlyList<CraftingRecipe> Recipes =
    [
        new(
            "rope", ItemIds.Rope,
            CraftingCategory.Resources, 1, 15,
            [new(ItemIds.PlantFibres, 3)],
            [
                "Separate the plant fibres into long strands.",
                "Twist the strands together into a strong rope."
            ]),
        new(
            "primitive-fishing-net", ItemIds.PrimitiveFishingNet,
            CraftingCategory.Tools, 2, 25,
            [new(ItemIds.PlantFibres, 6)],
            [
                "Separate the plant fibres into long strands.",
                "Twist and weave the strands into an open mesh.",
                "Knot the edges to finish a primitive casting net."
            ]),
        new(
            "medium-rock", ItemIds.MediumRock,
            CraftingCategory.Resources, 1, 8,
            [new(ItemIds.LargeRock, 2)],
            [
                "Hold one large rock as the striking stone.",
                "Break the other large rock into two medium rocks."
            ],
            [
                new(
                    [new(ItemIds.LargeRock, 2)],
                    [
                        new(ItemIds.LargeRock, 1),
                        new(ItemIds.MediumRock, 2)
                    ])
            ]),
        new(
            "small-rocks", ItemIds.SmallRocks,
            CraftingCategory.Resources, 1, 8,
            [new(ItemIds.MediumRock, 2)],
            [
                "Hold one medium rock as the striking stone.",
                "Break the other medium rock into two piles of small rocks."
            ],
            [
                new(
                    [new(ItemIds.MediumRock, 2)],
                    [
                        new(ItemIds.MediumRock, 1),
                        new(ItemIds.SmallRocks, 2)
                    ])
            ]),
        new(
            "sharpened-rock", ItemIds.SharpenedRock,
            CraftingCategory.Resources, 1, 15,
            [new(ItemIds.MediumRock, 2)],
            [
                "Place two medium rocks together.",
                "Strike their edges until one forms a sharp cutting edge."
            ]),
        new(
            "plank", ItemIds.Plank,
            CraftingCategory.Resources, 2, 20,
            [new(ItemIds.Logs, 1)],
            [
                "Use a knife on any type of log.",
                "Carve along the grain until the log becomes a plank."
            ],
            RequiredTools:
            [
                new(ItemTag.Knife, "knife")
            ]),
        new(
            "stone-knife", ItemIds.StoneKnife,
            CraftingCategory.Tools, 1, 20,
            [
                new(ItemIds.PlantFibres, 1),
                new(ItemIds.SharpenedRock, 1)
            ],
            [
                "Wrap the plant fibres around one end of the sharp rock.",
                "Bind the fibres tightly to form a safe grip."
            ]),
        new(
            "stone-hammer", ItemIds.StoneHammer,
            CraftingCategory.Tools, 1, 30,
            [new(ItemIds.MediumRock, 1), new(ItemIds.Sticks, 1)],
            [
                "Place a medium rock against the end of the sticks.",
                "Fasten the stone firmly to create a hammer head."
            ]),
        new(
            "stone-axe", ItemIds.StoneAxe,
            CraftingCategory.Tools, 1, 40,
            [new(ItemIds.SharpenedRock, 1), new(ItemIds.Sticks, 1)],
            [
                "Place the sharpened rock against the sticks.",
                "Lash the sharp stone firmly to create an axe."
            ]),
        new(
            "stone-pickaxe", ItemIds.StonePickaxe,
            CraftingCategory.Tools, 1, 60,
            [
                new(ItemIds.SharpenedRock, 1),
                new(ItemIds.MediumRock, 1),
                new(ItemIds.Sticks, 1)
            ],
            [
                "Use the sharpened rock to shape the medium rock into a pick head.",
                "Fit the shaped head across the top of the sticks.",
                "Lash the head tightly to create a stone pickaxe."
            ],
            [
                new(
                    [
                        new(ItemIds.SharpenedRock, 1),
                        new(ItemIds.MediumRock, 1)
                    ],
                    [new("stone_pickaxe_head", 1)]),
                new(
                    [
                        new("stone_pickaxe_head", 1),
                        new(ItemIds.Sticks, 1)
                    ],
                    [new(ItemIds.StonePickaxe, 1)])
            ]),
        new(
            "bloomery", ItemIds.Bloomery,
            CraftingCategory.Furniture, 5, 90,
            [
                new(ItemIds.Dirt, 6),
                new(ItemIds.SmallRocks, 4),
                new(ItemIds.Sticks, 2)
            ],
            [
                "Mix damp earth around a stable stone furnace base.",
                "Build a narrow shaft with a lower opening for air and slag.",
                "Dry the clay bloomery before placing it on level ground."
            ],
            RequiredStationItemId: ItemIds.Workbench),
        new(
            "bronze-bar", ItemIds.BronzeBar,
            CraftingCategory.Resources, 6, 70,
            [
                new(ItemIds.CopperOre, 2),
                new(ItemIds.TinOre, 1),
                new(ItemIds.Coal, 1)
            ],
            [
                "Heat copper and tin together with coal in a crucible.",
                "Stir the molten alloy and skim away the waste.",
                "Pour the bronze into a bar mould and let it cool."
            ],
            RequiredStationItemId: ItemIds.Bloomery),
        new(
            "smithing-anvil", ItemIds.SmithingAnvil,
            CraftingCategory.Furniture, 7, 110,
            [
                new(ItemIds.BronzeBar, 2),
                new(ItemIds.Plank, 2)
            ],
            [
                "Cast the bronze into a broad, heavy hammering block.",
                "Fit the metal securely onto a thick timber base.",
                "Place the smithing anvil on clear, level ground."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.Workbench),
        new(
            "iron-bloom", ItemIds.IronBloom,
            CraftingCategory.Resources, 10, 100,
            [
                new(ItemIds.IronOre, 3),
                new(ItemIds.Coal, 2)
            ],
            [
                "Feed iron ore and coal into a forced-air clay furnace.",
                "Keep the furnace hot while the ore reduces to metallic iron.",
                "Extract the porous iron bloom from the surrounding slag."
            ],
            RequiredStationItemId: ItemIds.Bloomery),
        new(
            "iron-bar", ItemIds.IronBar,
            CraftingCategory.Resources, 11, 80,
            [new(ItemIds.IronBloom, 1), new(ItemIds.Coal, 1)],
            [
                "Reheat the iron bloom in a charcoal hearth.",
                "Hammer the bloom repeatedly to drive out trapped slag.",
                "Consolidate the clean iron into a workable bar."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "bronze-pickaxe", ItemIds.BronzePickaxe,
            CraftingCategory.Tools, 8, 110,
            [
                new(ItemIds.BronzeBar, 1),
                new(ItemIds.Sticks, 1)
            ],
            [
                "Reheat the bronze bar and shape it into a curved pick head.",
                "Fit and secure the bronze head to the wooden handle."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "iron-pickaxe", ItemIds.IronPickaxe,
            CraftingCategory.Tools, 12, 180,
            [
                new(ItemIds.IronBar, 1),
                new(ItemIds.Sticks, 1)
            ],
            [
                "Reheat and hammer the iron bar into a strong pick head.",
                "Fit and secure the iron head to the wooden handle."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "iron-axe", ItemIds.IronAxe,
            CraftingCategory.Tools, 12, 165,
            [
                new(ItemIds.IronBar, 1),
                new(ItemIds.Sticks, 1)
            ],
            [
                "Reheat, hammer and sharpen the iron bar into an axe head.",
                "Fit and secure the iron head to the wooden handle."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "stone-shovel", ItemIds.StoneShovel,
            CraftingCategory.Tools, 1, 45,
            [
                new(ItemIds.SharpenedRock, 1),
                new(ItemIds.Sticks, 1),
                new(ItemIds.PlantFibres, 1)
            ],
            [
                "Chip the sharpened rock into a broad shovel blade.",
                "Lash the blade firmly to the wooden shaft."
            ]),
        new(
            "campfire", ItemIds.Campfire,
            CraftingCategory.Furniture, 1, 25,
            [new(ItemIds.SmallRocks, 3)],
            [
                "Select several handfuls of small stones.",
                "Arrange the stones into a stable circular fire ring.",
                "Leave the centre clear for fuel."
            ]),
        new(
            "workbench", ItemIds.Workbench,
            CraftingCategory.Furniture, 3, 75,
            [
                new(ItemIds.Plank, 4),
                new(ItemIds.Sticks, 2)
            ],
            [
                "Lay four planks together to form a broad working surface.",
                "Shape the sticks into sturdy trestle supports.",
                "Use a hammer to fasten the top and brace the frame."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ])
    ];

    private static readonly IReadOnlyDictionary<
        CraftingCategory, IReadOnlyList<CraftingRecipe>>
        RecipesByCategory =
            Enum.GetValues<CraftingCategory>()
                .ToDictionary(
                    category => category,
                    category => (IReadOnlyList<CraftingRecipe>)
                        (category == CraftingCategory.All
                            ? Recipes
                            : Recipes.Where(recipe =>
                                    recipe.Category == category)
                                .ToArray()));
    private static readonly IReadOnlyDictionary<
        (CraftingCategory Category, string StationItemId),
        IReadOnlyList<CraftingRecipe>> RecipesByStationAndCategory =
            Recipes
                .Where(recipe => recipe.RequiredStationItemId is not null)
                .Select(recipe => recipe.RequiredStationItemId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(stationItemId =>
                    Enum.GetValues<CraftingCategory>().Select(category =>
                        new
                        {
                            Key = (category, stationItemId),
                            Recipes = (IReadOnlyList<CraftingRecipe>)Recipes
                                .Where(recipe =>
                                    string.Equals(
                                        recipe.RequiredStationItemId,
                                        stationItemId,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    (category == CraftingCategory.All ||
                                     recipe.Category == category))
                                .ToArray()
                        }))
                .ToDictionary(entry => entry.Key, entry => entry.Recipes);

    public static IReadOnlyList<CraftingRecipe> RecipesFor(
        CraftingCategory category) =>
        RecipesByCategory[category];

    public static IReadOnlyList<CraftingRecipe> RecipesFor(
        CraftingCategory category,
        string stationItemId) =>
        RecipesByStationAndCategory.TryGetValue(
            (category, stationItemId), out var recipes)
            ? recipes
            : [];

    public static int LevelForExperience(int experience) =>
        SkillService.LevelForExperience(experience);

    public static int ExperienceForLevel(int level) =>
        SkillService.ExperienceForLevel(level);

    public static int ExperienceToNextLevel(int experience) =>
        SkillService.ExperienceToNextLevel(experience);

    public static SkillExperienceChange AwardExperience(
        int currentExperience, CraftingRecipe recipe) =>
        SkillService.AwardExperience(
            currentExperience, recipe.Experience);

    public static RecipeAvailability Availability(
        CraftingRecipe recipe, int level, string?[]? inventory,
        bool requiredStationAvailable = true)
    {
        if (level < recipe.RequiredLevel)
            return RecipeAvailability.Locked;

        var tools = recipe.RequiredTools;
        if (tools is not null)
            for (var index = 0; index < tools.Count; index++)
            {
                var tool = tools[index];
                if (CountItemsWithTag(inventory, tool.Tag) < tool.Count)
                    return RecipeAvailability.MissingResources;
            }

        for (var index = 0; index < recipe.Ingredients.Count; index++)
        {
            var ingredient = recipe.Ingredients[index];
            if (CountItem(inventory, ingredient.ItemId) <
                ingredient.Count)
                return RecipeAvailability.MissingResources;
        }
        if (recipe.RequiredStationItemId is not null &&
            !requiredStationAvailable)
            return RecipeAvailability.MissingStation;

        var occupied = OccupiedSlots(inventory);
        var steps = recipe.InventorySteps;
        if (steps is null)
            occupied += 1 - IngredientTotal(recipe.Ingredients);
        else
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                occupied -= IngredientTotal(step.Consumes);
                occupied += IngredientTotal(step.Produces);
                if (occupied > PlayerInventory.Capacity)
                    return RecipeAvailability.InventoryFull;
            }

        return occupied > PlayerInventory.Capacity
            ? RecipeAvailability.InventoryFull
            : RecipeAvailability.Ready;
    }

    private static int OccupiedSlots(string?[]? inventory)
    {
        if (inventory is null) return 0;
        var count = 0;
        var length = Math.Min(inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
            if (inventory[slot] is not null)
                count++;
        return count;
    }

    private static int IngredientTotal(
        IReadOnlyList<CraftingIngredient> ingredients)
    {
        var count = 0;
        for (var index = 0; index < ingredients.Count; index++)
            count += ingredients[index].Count;
        return count;
    }

    private static int CountItem(string?[]? inventory, string itemId)
    {
        if (inventory is null) return 0;
        var count = 0;
        var length = Math.Min(inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
            if (string.Equals(
                    inventory[slot], itemId,
                    StringComparison.OrdinalIgnoreCase))
                count++;
        return count;
    }

    private static int CountItemsWithTag(
        string?[]? inventory, ItemTag tag)
    {
        if (inventory is null) return 0;
        var count = 0;
        var length = Math.Min(inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
        {
            var itemId = inventory[slot];
            if (itemId is not null &&
                ItemCatalog.Get(itemId).HasTag(tag))
                count++;
        }
        return count;
    }
}
