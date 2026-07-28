namespace IslandRpg.Gameplay;

internal static class CraftingService
{
    internal enum CraftResult
    {
        Success,
        Locked,
        MissingResources,
        MissingStation,
        InventoryFull
    }

    public static bool TryCraft(
        CraftingRecipe recipe,
        int craftingLevel,
        string?[]? items,
        out string?[] updated,
        bool requiredStationAvailable = true)
    {
        return TryCraftDetailed(
            recipe, craftingLevel, items, out updated,
            requiredStationAvailable) ==
               CraftResult.Success;
    }

    public static CraftResult TryCraftDetailed(
        CraftingRecipe recipe,
        int craftingLevel,
        string?[]? items,
        out string?[] updated,
        bool requiredStationAvailable = true)
    {
        updated = PlayerInventory.Normalize(items);
        if (craftingLevel < recipe.RequiredLevel)
            return CraftResult.Locked;
        if (recipe.RequiredStationItemId is not null &&
            !requiredStationAvailable)
            return CraftResult.MissingStation;
        var working = PlayerInventory.Normalize(updated);
        foreach (var tool in recipe.RequiredTools ?? [])
        {
            var held = working.Count(item =>
                item is not null &&
                ItemCatalog.Get(item).HasTag(tool.Tag));
            if (held < tool.Count)
                return CraftResult.MissingResources;
        }

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
                        ingredient.Accepts);
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
