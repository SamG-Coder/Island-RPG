namespace IslandRpg.Gameplay;

internal readonly record struct ActorInventoryResult(
    bool Succeeded,
    string?[] Inventory,
    string? ItemId = null,
    string? Failure = null);

/// <summary>
/// Actor-neutral inventory mutations used by players and autonomous actors.
/// World-facing controllers own movement and presentation; all actors share
/// these recipes, food rules, capacity checks, and transfers.
/// </summary>
internal static class ActorActionService
{
    public static ActorInventoryResult Gather(
        string?[]? inventory, string itemId, int quantity)
    {
        var updated = PlayerInventory.Normalize(inventory);
        for (var count = 0; count < Math.Max(1, quantity); count++)
            if (!PlayerInventory.TryAdd(updated, itemId, out updated))
                return new(
                    count > 0, updated, itemId,
                    count == 0 ? "inventory_full" : null);
        return new(true, updated, itemId);
    }

    public static ActorInventoryResult Craft(
        string?[]? inventory,
        CraftingRecipe recipe,
        int craftingLevel,
        bool stationAvailable = false)
    {
        var result = CraftingService.TryCraftDetailed(
            recipe,
            craftingLevel,
            inventory,
            out var updated,
            stationAvailable);
        return result == CraftingService.CraftResult.Success
            ? new(true, updated, recipe.ResultItemId)
            : new(false, PlayerInventory.Normalize(inventory),
                Failure: result.ToString().ToLowerInvariant());
    }

    public static ActorInventoryResult Cook(
        string?[]? inventory,
        int slot,
        int cookingLevel,
        float roll)
    {
        var updated = PlayerInventory.Normalize(inventory);
        if ((uint)slot >= (uint)updated.Length ||
            updated[slot] is not { } raw ||
            !CookingSkill.CanCook(raw, cookingLevel))
            return new(false, updated, Failure: "not_cookable");
        var cooked = CookingSkill.Roll(raw, cookingLevel, roll);
        updated[slot] = cooked.ItemId;
        return new(true, updated, cooked.ItemId);
    }

    public static ActorInventoryResult CookStew(
        string?[]? inventory, int cookingLevel)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if (cookingLevel < StewCookingService.RequiredLevel)
            return new(false, unchanged, Failure: "level_locked");
        return StewCookingService.TryPrepare(
            unchanged,
            out var updated,
            out _,
            out _)
            ? new(true, updated, ItemIds.FishBerryStew)
            : new(false, unchanged, Failure: "missing_ingredients");
    }

    public static bool TryTransfer(
        string?[]? source,
        string?[]? destination,
        int sourceSlot,
        out string?[] updatedSource,
        out string?[] updatedDestination,
        out string? itemId)
    {
        updatedSource = PlayerInventory.Normalize(source);
        updatedDestination = PlayerInventory.Normalize(destination);
        itemId = null;
        if ((uint)sourceSlot >= (uint)updatedSource.Length ||
            updatedSource[sourceSlot] is not { } selected ||
            !PlayerInventory.TryAdd(
                updatedDestination, selected,
                out updatedDestination) ||
            !PlayerInventory.TryRemove(
                updatedSource, sourceSlot,
                out updatedSource))
            return false;
        itemId = selected;
        return true;
    }
}
