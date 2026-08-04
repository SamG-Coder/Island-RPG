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

internal sealed record CraftingIngredient(
    string ItemId,
    int Count,
    IReadOnlyList<string>? AlternativeItemIds = null)
{
    public bool Accepts(string? candidate)
    {
        if (string.Equals(
                candidate, ItemId,
                StringComparison.OrdinalIgnoreCase))
            return true;
        if (AlternativeItemIds is null) return false;
        for (var index = 0;
             index < AlternativeItemIds.Count;
             index++)
            if (string.Equals(
                    candidate,
                    AlternativeItemIds[index],
                    StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
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

    public static IReadOnlyList<CraftingIngredient> Outputs(
        CraftingRecipe recipe) =>
        recipe.InventorySteps is { Count: > 0 } steps
            ? steps[^1].Produces
            : [new(recipe.ResultItemId, 1)];

    public static bool IsReturnedIngredient(
        CraftingRecipe recipe, string itemId) =>
        recipe.Ingredients.Any(ingredient => ingredient.Accepts(itemId));

    public static readonly IReadOnlyList<CraftingRecipe> Recipes =
    new CraftingRecipe[]
    {
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
            "reinforced-fishing-net", ItemIds.ReinforcedFishingNet,
            CraftingCategory.Tools, 6, 65,
            [
                new(ItemIds.PrimitiveFishingNet, 1),
                new(ItemIds.Rope, 2, [ItemIds.SlimeGel])
            ],
            [
                "Double the primitive mesh with tightly twisted rope.",
                "Knot wooden weights around the reinforced perimeter."
            ],
            RequiredTools: [new(ItemTag.Knife, "knife")],
            RequiredStationItemId: ItemIds.Workbench),
        new(
            "advanced-fishing-net", ItemIds.AdvancedFishingNet,
            CraftingCategory.Tools, 12, 150,
            [
                new(ItemIds.ReinforcedFishingNet, 1),
                new(ItemIds.Rope, 2),
                new(ItemIds.IronBar, 1),
                new(ItemIds.SlimeCore, 1)
            ],
            [
                "Weave a dense second layer through the reinforced mesh.",
                "Hammer iron into compact weights and secure the edge knots."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")],
            RequiredStationItemId: ItemIds.SmithingAnvil),
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
            [new(
                ItemIds.Logs, 1,
                [
                    ItemIds.OakLogs,
                    ItemIds.PineLogs,
                    ItemIds.PalmLogs,
                    ItemIds.Bamboo
                ])],
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
                new(ItemIds.Coal, 1, [ItemIds.Charcoal])
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
                new(ItemIds.Coal, 2, [ItemIds.Charcoal])
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
            [
                new(ItemIds.IronBloom, 1),
                new(ItemIds.Coal, 1, [ItemIds.Charcoal])
            ],
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
            "bronze-axe", ItemIds.BronzeAxe,
            CraftingCategory.Tools, 8, 95,
            [new(ItemIds.BronzeBar, 1), new(ItemIds.Sticks, 1)],
            [
                "Hammer the bronze into a broad cutting edge.",
                "Fit the axe head securely onto a straight wooden handle."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "bronze-sickle", ItemIds.BronzeSickle,
            CraftingCategory.Tools, 9, 100,
            [new(ItemIds.BronzeBar, 1), new(ItemIds.Sticks, 1)],
            [
                "Draw the bronze into a curved harvesting blade.",
                "Fasten the blade to a short wooden grip."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        SmithingTool(
            "bronze-hammer", ItemIds.BronzeHammer, "hammer",
            ItemIds.BronzeBar, 8, 105),
        SmithingTool(
            "bronze-knife", ItemIds.BronzeKnife, "knife",
            ItemIds.BronzeBar, 8, 90),
        SmithingTool(
            "bronze-shovel", ItemIds.BronzeShovel, "shovel",
            ItemIds.BronzeBar, 9, 105),
        new(
            "cooking-pot", ItemIds.CookingPot,
            CraftingCategory.Furniture, 10, 120,
            [new(ItemIds.BronzeBar, 2)],
            [
                "Hammer two bronze bars into a deep cooking vessel.",
                "Form a sturdy handle and three stable feet.",
                "Place the pot close to a campfire before cooking."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.SmithingAnvil),
        new(
            "storage-chest", ItemIds.StorageChest,
            CraftingCategory.Furniture, 4, 85,
            [
                new(ItemIds.Plank, 6),
                new(ItemIds.Sticks, 2),
                new(ItemIds.Rope, 1)
            ],
            [
                "Join six planks into a deep rectangular box.",
                "Brace the corners and fit a curved wooden lid.",
                "Bind the chest securely before placing it."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.Workbench),
        new(
            "storage-barrel", ItemIds.StorageBarrel,
            CraftingCategory.Furniture, 6, 95,
            [
                new(ItemIds.Plank, 5),
                new(ItemIds.Rope, 2)
            ],
            [
                "Shape the planks into narrow barrel staves.",
                "Draw the staves tightly together with rope hoops.",
                "Fit a wooden base and lid before placing it."
            ],
            RequiredTools:
            [
                new(ItemTag.Hammer, "hammer")
            ],
            RequiredStationItemId: ItemIds.Workbench),
        new(
            "wooden-wall", ItemIds.WoodenWall,
            CraftingCategory.Furniture, 1, 90,
            [new(ItemIds.Logs, 1)],
            [
                "Trim a log into stout wall timbers.",
                "Mark the wall footprint before raising the frame.",
                "Build the timbers up until the wall is secure."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]),
        new(
            "wooden-fence", ItemIds.WoodenFence,
            CraftingCategory.Furniture, 1, 45,
            [new(ItemIds.Sticks, 3)],
            [
                "Mark the boundary line.",
                "Bind sharpened stakes into a light fence."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]),
        new(
            "stone-wall", ItemIds.StoneWall,
            CraftingCategory.Furniture, 6, 150,
            [new(ItemIds.LargeRock, 3)],
            [
                "Set broad stones into a stable foundation.",
                "Raise and secure the defensive wall."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]),
        new(
            "fortified-wooden-wall", ItemIds.FortifiedWoodenWall,
            CraftingCategory.Furniture, 4, 120,
            [new(ItemIds.Logs, 2)],
            [
                "Raise a close-set timber wall.",
                "Brace and protect its base with heavy boarding."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]),
        new(
            "fortified-wall", ItemIds.FortifiedWall,
            CraftingCategory.Furniture, 10, 240,
            [new(ItemIds.LargeRock, 5)],
            [
                "Lay a deep reinforced stone foundation.",
                "Raise the thick defensive courses and battlements."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")]),
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
        SmithingTool(
            "iron-hammer", ItemIds.IronHammer, "hammer",
            ItemIds.IronBar, 12, 170),
        SmithingTool(
            "iron-knife", ItemIds.IronKnife, "knife",
            ItemIds.IronBar, 12, 150),
        SmithingTool(
            "iron-shovel", ItemIds.IronShovel, "shovel",
            ItemIds.IronBar, 13, 175),
        SmithingTool(
            "iron-sickle", ItemIds.IronSickle, "sickle",
            ItemIds.IronBar, 13, 165),
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
            "stone-sickle", ItemIds.StoneSickle,
            CraftingCategory.Tools, 1, 40,
            [
                new(ItemIds.SharpenedRock, 1),
                new(ItemIds.Sticks, 1),
                new(ItemIds.PlantFibres, 1)
            ],
            [
                "Knapp the sharp stone into a curved harvesting edge.",
                "Lash the blade to a short wooden handle."
            ]),
        new(
            "portable-torch", ItemIds.PortableTorch,
            CraftingCategory.Tools, 2, 30,
            [
                new(ItemIds.Sticks, 1),
                new(ItemIds.PlantFibres, 2, [ItemIds.SlimeGel]),
                new(ItemIds.Charcoal, 1, [ItemIds.Coal])
            ],
            [
                "Wrap dry fibre tightly around one end of the stick.",
                "Work powdered fuel into the wrapping so it burns steadily."
            ]),
        new(
            "salted-fish", ItemIds.SaltedFish,
            CraftingCategory.Resources, 2, 24,
            [
                new(
                    ItemIds.CookedMinnows, 1,
                    [
                        ItemIds.CookedRiverPerch,
                        ItemIds.CookedSilverHerring,
                        ItemIds.CookedRedSnapper,
                        ItemIds.CookedOceanMackerel,
                        ItemIds.CookedBluefinTuna
                    ]),
                new(ItemIds.SaltCrystals, 1)
            ],
            [
                "Rub coarse salt over the cooked fish.",
                "Wrap it tightly so the salt draws out excess moisture."
            ]),
        new(
            "herbal-poultice", ItemIds.HerbalPoultice,
            CraftingCategory.Resources, 2, 28,
            [
                new(ItemIds.MedicinalHerbs, 2),
                new(ItemIds.PlantFibres, 1)
            ],
            [
                "Crush the medicinal leaves to release their oils.",
                "Wrap the herbs in clean fibre and bind the poultice firmly."
            ]),
        new(
            "gathering-basket", ItemIds.GatheringBasket,
            CraftingCategory.Tools, 2, 35,
            [new(ItemIds.PlantFibres, 6), new(ItemIds.Sticks, 2)],
            [
                "Split the fibres into flexible weaving strands.",
                "Weave them around a rigid stick rim and handles."
            ]),
        new(
            "open-pearl-oyster", ItemIds.Pearl,
            CraftingCategory.Resources, 1, 12,
            [new(ItemIds.PearlOysterShell, 1)],
            ["Open the oyster shell carefully and remove its pearl."],
            RequiredTools: [new(ItemTag.Knife, "knife")]),
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
            CraftingCategory.Furniture, 3, 76,
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
    }.Concat(HouseCatalog.Recipes).ToArray();

    private static CraftingRecipe SmithingTool(
        string id, string resultItemId, string toolName,
        string barItemId, int requiredLevel, int experience) =>
        new(
            id, resultItemId, CraftingCategory.Tools,
            requiredLevel, experience,
            [new(barItemId, 1), new(ItemIds.Sticks, 1)],
            [
                $"Hammer the metal into a balanced {toolName} head.",
                $"Fit and secure the {toolName} head to a wooden handle."
            ],
            RequiredTools: [new(ItemTag.Hammer, "hammer")],
            RequiredStationItemId: ItemIds.SmithingAnvil);

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

    public static SkillExperienceChange AwardExperience(
        int currentExperience, CraftingRecipe recipe,
        string?[]? inventory)
    {
        var power = BestRequiredToolPower(recipe, inventory);
        var experience = (int)MathF.Round(
            recipe.Experience * (1f + (power - 1) * .1f));
        return SkillService.AwardExperience(
            currentExperience, experience);
    }

    public static int BestRequiredToolPower(
        CraftingRecipe recipe, string?[]? inventory)
    {
        var power = 1;
        foreach (var tool in recipe.RequiredTools ?? [])
            power = Math.Max(power, tool.Tag switch
            {
                ItemTag.Hammer =>
                    PlayerInventory.BestHammer(inventory)?.HammerPower ?? 1,
                ItemTag.Knife =>
                    PlayerInventory.BestKnife(inventory)?.KnifePower ?? 1,
                _ => 1
            });
        return power;
    }

    public static RecipeAvailability Availability(
        CraftingRecipe recipe, int level, InventoryContainer inventory,
        bool requiredStationAvailable = true) =>
        CraftingService.TryCraftDetailed(
            recipe, level, inventory, out _, requiredStationAvailable) switch
        {
            CraftingService.CraftResult.Locked => RecipeAvailability.Locked,
            CraftingService.CraftResult.MissingResources =>
                RecipeAvailability.MissingResources,
            CraftingService.CraftResult.MissingStation =>
                RecipeAvailability.MissingStation,
            CraftingService.CraftResult.InventoryFull =>
                RecipeAvailability.InventoryFull,
            _ => RecipeAvailability.Ready
        };

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
            if (CountIngredient(inventory, ingredient) <
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

    public static int CountIngredient(
        string?[]? inventory,
        CraftingIngredient ingredient)
    {
        if (inventory is null) return 0;
        var count = 0;
        var length = Math.Min(inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
            if (ingredient.Accepts(inventory[slot]))
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
