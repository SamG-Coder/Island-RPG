namespace IslandRpg.Gameplay;

internal static class PlayerInventory
{
    public const int Capacity = 28;
    public const string AxeItemId = "axe";

    public static string?[] CreateStartingInventory()
    {
        var inventory = new string?[Capacity];
        inventory[0] = AxeItemId;
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
        items?.Any(item => item is not null && item.Equals(
            AxeItemId, StringComparison.OrdinalIgnoreCase)) == true;

    public static bool CanDrop(string itemId) =>
        !itemId.Equals(AxeItemId, StringComparison.OrdinalIgnoreCase);

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
