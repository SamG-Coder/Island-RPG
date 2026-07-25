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
    Ready
}

internal sealed record CraftingIngredient(string ItemId, int Count);

internal sealed record CraftingRecipe(
    string Id,
    string ResultItemId,
    CraftingCategory Category,
    int RequiredLevel,
    int Experience,
    IReadOnlyList<CraftingIngredient> Ingredients,
    IReadOnlyList<string> Steps);

internal static class CraftingSkill
{
    public const int MaximumLevel = 20;

    public static readonly IReadOnlyList<CraftingRecipe> Recipes =
    [
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
            ])
    ];

    public static int LevelForExperience(int experience)
    {
        experience = Math.Max(0, experience);
        for (var level = MaximumLevel; level > 1; level--)
            if (experience >= ExperienceForLevel(level))
                return level;
        return 1;
    }

    public static int ExperienceForLevel(int level)
    {
        level = Math.Clamp(level, 1, MaximumLevel);
        var rank = level - 1;
        return 50 * rank * rank + 25 * rank;
    }

    public static int ExperienceToNextLevel(int experience)
    {
        var level = LevelForExperience(experience);
        return level >= MaximumLevel
            ? 0
            : ExperienceForLevel(level + 1) - Math.Max(0, experience);
    }

    public static RecipeAvailability Availability(
        CraftingRecipe recipe, int level, string?[]? inventory)
    {
        if (level < recipe.RequiredLevel)
            return RecipeAvailability.Locked;
        foreach (var ingredient in recipe.Ingredients)
        {
            var held = inventory?.Count(
                item => string.Equals(
                    item, ingredient.ItemId,
                    StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (held < ingredient.Count)
                return RecipeAvailability.MissingResources;
        }
        return RecipeAvailability.Ready;
    }
}
