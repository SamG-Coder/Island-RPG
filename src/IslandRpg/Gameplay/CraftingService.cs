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
            if (inventory.Count(itemId =>
                    ItemCatalog.Get(itemId).HasTag(tool.Tag)) < tool.Count)
                return CraftResult.MissingResources;

        var working = inventory.Clone();
        var steps = recipe.InventorySteps ??
        [
            new CraftingInventoryStep(
                recipe.Ingredients,
                [new(recipe.ResultItemId, 1)])
        ];
        foreach (var step in steps)
        {
            if (!TryConsumePreferredIngredients(working, step.Consumes))
                return CraftResult.MissingResources;
            foreach (var product in step.Produces)
                if (!(ItemCatalog.TryGet(product.ItemId, out _)
                        ? working.TryAdd(product.ItemId, product.Count)
                        : working.TryAddTransient(
                            product.ItemId, product.Count)))
                    return CraftResult.InventoryFull;
        }
        updated = working;
        return CraftResult.Success;
    }

    public static CraftResult TryConsumeForPlacement(
        CraftingRecipe recipe,
        int craftingLevel,
        InventoryContainer inventory,
        out InventoryContainer updated,
        bool requiredStationAvailable = true,
        int placements = 1)
    {
        updated = inventory.Clone();
        if (craftingLevel < recipe.RequiredLevel)
            return CraftResult.Locked;
        if (recipe.RequiredStationItemId is not null &&
            !requiredStationAvailable)
            return CraftResult.MissingStation;
        foreach (var tool in recipe.RequiredTools ?? [])
            if (inventory.Count(itemId =>
                    ItemCatalog.Get(itemId).HasTag(tool.Tag)) < tool.Count)
                return CraftResult.MissingResources;

        if (placements <= 0) return CraftResult.Success;
        var working = inventory.Clone();
        for (var placement = 0; placement < placements; placement++)
            if (!TryConsumePreferredIngredients(working, recipe.Ingredients))
                return CraftResult.MissingResources;
        updated = working;
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
            if (!TryConsumePreferredIngredients(working, step.Consumes))
                return CraftResult.MissingResources;

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

    /// <summary>
    /// Consumes every recipe's named material before considering substitutes.
    /// This both preserves more valuable alternatives and prevents an earlier
    /// flexible requirement from stealing a later requirement's primary item.
    /// </summary>
    private static bool TryConsumePreferredIngredients(
        InventoryContainer inventory,
        IReadOnlyList<CraftingIngredient> ingredients)
    {
        var remaining = new int[ingredients.Count];
        for (var index = 0; index < ingredients.Count; index++)
        {
            var ingredient = ingredients[index];
            remaining[index] = ingredient.Count - TakeUpTo(
                inventory, ingredient.ItemId, ingredient.Count);
        }
        for (var index = 0; index < ingredients.Count; index++)
        {
            var ingredient = ingredients[index];
            foreach (var alternative in ingredient.AlternativeItemIds ?? [])
                remaining[index] -= TakeUpTo(
                    inventory, alternative, remaining[index]);
            if (remaining[index] > 0) return false;
        }
        return true;
    }

    private static bool TryConsumePreferredIngredients(
        string?[] inventory,
        IReadOnlyList<CraftingIngredient> ingredients)
    {
        var remaining = new int[ingredients.Count];
        for (var index = 0; index < ingredients.Count; index++)
        {
            var ingredient = ingredients[index];
            remaining[index] = ingredient.Count - TakeUpTo(
                inventory, ingredient.ItemId, ingredient.Count);
        }
        for (var index = 0; index < ingredients.Count; index++)
        {
            var ingredient = ingredients[index];
            foreach (var alternative in ingredient.AlternativeItemIds ?? [])
                remaining[index] -= TakeUpTo(
                    inventory, alternative, remaining[index]);
            if (remaining[index] > 0) return false;
        }
        return true;
    }

    private static int TakeUpTo(
        InventoryContainer inventory, string itemId, int maximum)
    {
        var count = Math.Min(maximum, inventory.Count(itemId));
        return count > 0 && inventory.TryTake(
            candidate => candidate.Equals(
                itemId, StringComparison.OrdinalIgnoreCase), count)
            ? count
            : 0;
    }

    private static int TakeUpTo(
        string?[] inventory, string itemId, int maximum)
    {
        var taken = 0;
        for (var slot = 0;
             slot < inventory.Length && taken < maximum;
             slot++)
        {
            if (!string.Equals(
                    inventory[slot], itemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            inventory[slot] = null;
            taken++;
        }
        return taken;
    }
}
