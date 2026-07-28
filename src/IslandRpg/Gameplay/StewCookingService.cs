namespace IslandRpg.Gameplay;

internal static class StewCookingService
{
    public const int RequiredLevel = 5;
    public const int Experience = 55;
    public const float CampfireRange = 2.25f;

    public static bool HasIngredients(string?[]? inventory) =>
        FindIngredientSlot(inventory, IsRawFish) >= 0 &&
        FindIngredientSlot(inventory, IsRawBerry) >= 0;

    public static bool TryPrepare(
        string?[]? inventory,
        out string?[] updated,
        out string fishItemId,
        out string berryItemId)
    {
        updated = PlayerInventory.Normalize(inventory);
        fishItemId = "";
        berryItemId = "";
        var fishSlot = FindIngredientSlot(updated, IsRawFish);
        var berrySlot = FindIngredientSlot(updated, IsRawBerry);
        if (fishSlot < 0 || berrySlot < 0) return false;
        fishItemId = updated[fishSlot]!;
        berryItemId = updated[berrySlot]!;
        updated[fishSlot] = ItemIds.FishBerryStew;
        updated[berrySlot] = null;
        return true;
    }

    private static int FindIngredientSlot(
        string?[]? inventory,
        Func<string, bool> predicate)
    {
        if (inventory is null) return -1;
        var length = Math.Min(
            inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
            if (inventory[slot] is { } itemId &&
                predicate(itemId))
                return slot;
        return -1;
    }

    private static bool IsRawFish(string itemId) =>
        ItemCatalog.Get(itemId).HasTag(ItemTag.Fish) &&
        CookingSkill.TryProfile(itemId, out _);

    private static bool IsRawBerry(string itemId) =>
        ItemCatalog.Get(itemId).HasTag(ItemTag.Berry) &&
        CookingSkill.TryProfile(itemId, out _);
}
