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
        InventoryContainer inventory,
        out InventoryContainer updated,
        bool requiredStationAvailable = true)
    {
        updated = inventory.Clone();
        if (craftingLevel < recipe.RequiredLevel)
            return CraftResult.Locked;
        if (recipe.RequiredStationItemId is not null &&
            !requiredStationAvailable)
            return CraftResult.MissingStation;
        foreach (var tool in recipe.RequiredTools ?? [])
            if (updated.Count(itemId =>
                    ItemCatalog.Get(itemId).HasTag(tool.Tag)) < tool.Count)
                return CraftResult.MissingResources;

        var steps = recipe.InventorySteps ??
        [
            new CraftingInventoryStep(
                recipe.Ingredients,
                [new(recipe.ResultItemId, 1)])
        ];
        foreach (var step in steps)
        {
            foreach (var ingredient in step.Consumes)
                if (!updated.TryTake(ingredient.Accepts, ingredient.Count))
                    return CraftResult.MissingResources;
            foreach (var product in step.Produces)
                if (!(ItemCatalog.TryGet(product.ItemId, out _)
                        ? updated.TryAdd(product.ItemId, product.Count)
                        : updated.TryAddTransient(
                            product.ItemId, product.Count)))
                    return CraftResult.InventoryFull;
        }
        return CraftResult.Success;
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
