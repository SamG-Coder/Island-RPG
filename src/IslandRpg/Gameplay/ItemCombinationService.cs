namespace IslandRpg.Gameplay;

/// <summary>
/// Resolves an explicit "use this item on that item" gesture against the
/// crafting catalogue. Inventory mutation remains owned by CraftingService.
/// </summary>
internal static class ItemCombinationService
{
    public static CraftingRecipe? FindRecipe(
        string firstItemId, string secondItemId,
        IEnumerable<CraftingRecipe>? recipes = null)
    {
        foreach (var recipe in recipes ?? CraftingSkill.Recipes)
        {
            if (recipe.RequiredStationItemId is not null) continue;
            if (MatchesIngredients(recipe, firstItemId, secondItemId) ||
                MatchesToolAndIngredient(recipe, firstItemId, secondItemId))
                return recipe;
        }
        return null;
    }

    private static bool MatchesIngredients(
        CraftingRecipe recipe, string firstItemId, string secondItemId)
    {
        if (recipe.RequiredTools is { Count: > 0 } ||
            recipe.Ingredients.Sum(value => value.Count) != 2)
            return false;
        return CanAssignPair(
            recipe.Ingredients, firstItemId, secondItemId);
    }

    private static bool MatchesToolAndIngredient(
        CraftingRecipe recipe, string firstItemId, string secondItemId)
    {
        if (recipe.RequiredTools is not { Count: 1 } tools ||
            tools[0].Count != 1 ||
            recipe.Ingredients.Count != 1 ||
            recipe.Ingredients[0].Count != 1)
            return false;
        return IsTool(firstItemId, tools[0]) &&
               recipe.Ingredients[0].Accepts(secondItemId) ||
               IsTool(secondItemId, tools[0]) &&
               recipe.Ingredients[0].Accepts(firstItemId);
    }

    private static bool CanAssignPair(
        IReadOnlyList<CraftingIngredient> ingredients,
        string firstItemId, string secondItemId)
    {
        var requirements = ingredients
            .SelectMany(value => Enumerable.Repeat(value, value.Count))
            .ToArray();
        return requirements.Length == 2 &&
               (requirements[0].Accepts(firstItemId) &&
                requirements[1].Accepts(secondItemId) ||
                requirements[0].Accepts(secondItemId) &&
                requirements[1].Accepts(firstItemId));
    }

    private static bool IsTool(
        string itemId, CraftingToolRequirement requirement) =>
        ItemCatalog.Get(itemId).HasTag(requirement.Tag);
}
