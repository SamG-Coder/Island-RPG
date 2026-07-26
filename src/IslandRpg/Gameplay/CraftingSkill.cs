namespace IslandRpg.Gameplay;

internal enum CraftingCategory
{
    All,
    Tools,
    Resources
}

internal enum RecipeAvailability
{
    Locked,
    MissingResources,
    InventoryFull,
    Ready
}

internal sealed record CraftingIngredient(string ItemId, int Count);

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
    IReadOnlyList<CraftingInventoryStep>? InventorySteps = null);

internal static class CraftingSkill
{
    public const int MaximumLevel = SkillService.MaximumLevel;

    public static readonly IReadOnlyList<CraftingRecipe> Recipes =
    [
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
            [new(ItemIds.SharpenedRock, 1), new(ItemIds.Logs, 1)],
            [
                "Use a sharpened rock on any type of log.",
                "Carve along the grain until the log becomes a plank."
            ]),
        new(
            "stone-hammer", ItemIds.StoneHammer,
            CraftingCategory.Tools, 3, 30,
            [new(ItemIds.MediumRock, 1), new(ItemIds.Sticks, 1)],
            [
                "Place a medium rock against the end of the sticks.",
                "Fasten the stone firmly to create a hammer head."
            ]),
        new(
            "stone-axe", ItemIds.StoneAxe,
            CraftingCategory.Tools, 4, 40,
            [new(ItemIds.SharpenedRock, 1), new(ItemIds.Sticks, 1)],
            [
                "Place the sharpened rock against the sticks.",
                "Lash the sharp stone firmly to create an axe."
            ]),
        new(
            "stone-pickaxe", ItemIds.StonePickaxe,
            CraftingCategory.Tools, 6, 60,
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
            ])
    ];

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
        CraftingRecipe recipe, int level, string?[]? inventory)
    {
        return CraftingService.TryCraftDetailed(
            recipe, level, inventory, out _) switch
        {
            CraftingService.CraftResult.Success =>
                RecipeAvailability.Ready,
            CraftingService.CraftResult.Locked =>
                RecipeAvailability.Locked,
            CraftingService.CraftResult.InventoryFull =>
                RecipeAvailability.InventoryFull,
            _ => RecipeAvailability.MissingResources
        };
    }
}
