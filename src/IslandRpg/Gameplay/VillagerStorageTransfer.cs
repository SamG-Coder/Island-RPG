namespace IslandRpg.Gameplay;

internal readonly record struct VillagerStorageTransferResult(
    string?[] Inventory,
    int ItemsMoved);

internal static class VillagerStorageTransfer
{
    public static VillagerStorageTransferResult DepositAll(
        ItemContainerState container,
        string?[] inventory,
        string ownerId,
        Func<string, bool>? retain = null)
    {
        var updated = PlayerInventory.Normalize(inventory);
        var moved = 0;
        for (var slot = 0; slot < updated.Length; slot++)
        {
            if (updated[slot] is not { } itemId ||
                retain?.Invoke(itemId) == true ||
                !container.TryAdd(itemId, ownerId: ownerId))
                continue;
            updated[slot] = null;
            moved++;
        }
        return new(updated, moved);
    }

    public static bool TryWithdrawFirst(
        ItemContainerState container,
        string?[] inventory,
        Func<string, bool> accepts,
        out string?[] updatedInventory,
        out string? itemId)
    {
        updatedInventory = PlayerInventory.Normalize(inventory);
        itemId = null;
        for (var slot = 0; slot < container.Items.Length; slot++)
        {
            if (container.Items[slot] is not { } candidate ||
                !accepts(candidate) ||
                !PlayerInventory.TryAdd(
                    updatedInventory, candidate, out var withItem))
                continue;
            var ownerId = container.OwnerIds[slot];
            if (!container.TryTake(slot, 1, out var removedItem))
                continue;
            if (!string.Equals(
                    removedItem, candidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (removedItem is not null)
                    container.TryAdd(
                        removedItem, ownerId: ownerId);
                itemId = null;
                continue;
            }
            itemId = removedItem;
            updatedInventory = withItem;
            return true;
        }
        itemId = null;
        return false;
    }

    public static bool IsWorkItemForRole(
        VillagerWorkRole role,
        string itemId)
    {
        if (!ItemCatalog.TryGet(itemId, out var item)) return false;
        return role switch
        {
            VillagerWorkRole.Food => item.HasTag(ItemTag.FishingNet),
            VillagerWorkRole.Wood =>
                item.HasTag(ItemTag.Axe) && item.WoodcuttingPower > 0,
            VillagerWorkRole.Crafting =>
                item.HasTag(ItemTag.Knife) ||
                item.HasTag(ItemTag.Hammer),
            VillagerWorkRole.Exploration =>
                item.HasTag(ItemTag.Pickaxe) && item.MiningPower > 0,
            _ => false
        };
    }

    public static bool HasWorkItem(
        VillagerWorkRole role,
        string?[] inventory) =>
        inventory.Any(itemId =>
            itemId is not null && IsWorkItemForRole(role, itemId));
}
