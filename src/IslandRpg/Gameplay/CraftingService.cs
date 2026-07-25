namespace IslandRpg.Gameplay;

internal static class CraftingService
{
    internal enum CraftResult
    {
        Success,
        Locked,
        MissingResources,
        InventoryFull
    }

    public static bool TryCraft(
        CraftingRecipe recipe,
        int craftingLevel,
        string?[]? items,
        out string?[] updated)
    {
        return TryCraftDetailed(
            recipe, craftingLevel, items, out updated) ==
               CraftResult.Success;
    }

    public static CraftResult TryCraftDetailed(
        CraftingRecipe recipe,
        int craftingLevel,
        string?[]? items,
        out string?[] updated)
    {
        updated = PlayerInventory.Normalize(items);
        if (craftingLevel < recipe.RequiredLevel)
            return CraftResult.Locked;
        var working = PlayerInventory.Normalize(updated);

        var steps = recipe.InventorySteps ??
        [
            new CraftingInventoryStep(
                recipe.Ingredients,
                [new(recipe.ResultItemId, 1)])
        ];
        foreach (var step in steps)
        {
            foreach (var ingredient in step.Consumes)
                for (var count = 0; count < ingredient.Count; count++)
                {
                    var slot = Array.FindIndex(
                        working,
                        item => string.Equals(
                            item, ingredient.ItemId,
                            StringComparison.OrdinalIgnoreCase));
                    if (slot < 0) return CraftResult.MissingResources;
                    working[slot] = null;
                }

            foreach (var product in step.Produces)
                for (var count = 0; count < product.Count; count++)
                {
                    var slot = Array.FindIndex(working, item => item is null);
                    if (slot < 0) return CraftResult.InventoryFull;
                    working[slot] = product.ItemId;
                }
        }

        updated = working;
        return CraftResult.Success;
    }
}
