namespace IslandRpg.Gameplay;

internal static class PlayerInventory
{
    public const int Capacity = 28;

    public static string?[] CreateStartingInventory()
    {
        var inventory = new string?[Capacity];
        inventory[0] = ItemIds.Axe;
        return inventory;
    }

    public static string?[] Normalize(string?[]? items)
    {
        var normalized = new string?[Capacity];
        if (items is not null)
            Array.Copy(items, normalized, Math.Min(items.Length, Capacity));
        return normalized;
    }

    public static int Count(string?[]? items) =>
        items?.Take(Capacity).Count(item => item is not null) ?? 0;

    public static bool IsFull(string?[]? items) => Count(items) >= Capacity;

    public static bool HasAxe(string?[]? items) =>
        items?.Any(item =>
            item is not null &&
            ItemCatalog.Get(item).HasTag(ItemTag.Axe)) == true;

    public static bool CanDrop(string itemId) =>
        ItemCatalog.Get(itemId).Droppable;

    public static bool TryBreakRock(
        string?[]? items, int toolSlot, int targetSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            updated[toolSlot] != ItemIds.LargeRock)
            return false;
        var result = updated[targetSlot] switch
        {
            ItemIds.LargeRock => ItemIds.MediumRock,
            ItemIds.MediumRock => ItemIds.SmallRocks,
            _ => null
        };
        if (result is null) return false;
        var emptySlot = Array.FindIndex(updated, item => item is null);
        if (emptySlot < 0) return false;
        updated[targetSlot] = result;
        updated[emptySlot] = result;
        return true;
    }

    public static bool TrySharpenRock(
        string?[]? items, int toolSlot, int targetSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            updated[toolSlot] != ItemIds.MediumRock ||
            updated[targetSlot] != ItemIds.MediumRock)
            return false;
        updated[toolSlot] = null;
        updated[targetSlot] = ItemIds.SharpenedRock;
        return true;
    }

    public static bool TryCraftAxe(
        string?[]? items, int toolSlot, int targetSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            updated[toolSlot] != ItemIds.SharpenedRock ||
            updated[targetSlot] != ItemIds.Sticks)
            return false;
        updated[toolSlot] = null;
        updated[targetSlot] = ItemIds.Axe;
        return true;
    }

    public static bool TryCarvePlank(
        string?[]? items, int toolSlot, int targetSlot,
        float toolDestructionRoll, out string?[] updated,
        out bool toolDestroyed)
    {
        updated = Normalize(items);
        toolDestroyed = false;
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            updated[toolSlot] != ItemIds.SharpenedRock ||
            !ItemCatalog.Get(updated[targetSlot] ?? string.Empty)
                .HasTag(ItemTag.Log))
            return false;
        updated[targetSlot] = ItemIds.Plank;
        toolDestroyed = toolDestructionRoll < .25f;
        if (toolDestroyed)
            updated[toolSlot] = null;
        return true;
    }

    public static bool TrySwap(
        string?[]? items, int source, int target, out string?[] updated)
    {
        updated = Normalize(items);
        if (source == target ||
            (uint)source >= Capacity ||
            (uint)target >= Capacity ||
            updated[source] is null)
            return false;
        (updated[source], updated[target]) =
            (updated[target], updated[source]);
        return true;
    }

    public static bool TryAdd(
        string?[]? items, string itemId, out string?[] updated)
    {
        updated = Normalize(items);
        var emptySlot = Array.FindIndex(updated, item => item is null);
        if (emptySlot < 0)
        {
            return false;
        }
        updated[emptySlot] = itemId;
        return true;
    }

    public static bool TryRemove(
        string?[]? items, int slot, out string?[] updated)
    {
        updated = Normalize(items);
        if ((uint)slot >= Capacity || updated[slot] is null)
            return false;
        updated[slot] = null;
        return true;
    }
}
