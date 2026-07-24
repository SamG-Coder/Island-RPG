namespace IslandRpg.Gameplay;

internal static class PlayerInventory
{
    public const int Capacity = 28;

    public static int Count(string[]? items) =>
        Math.Min(items?.Length ?? 0, Capacity);

    public static bool IsFull(string[]? items) => Count(items) >= Capacity;

    public static bool TryAdd(
        string[]? items, string itemId, out string[] updated)
    {
        var current = items?.Take(Capacity).ToArray() ?? [];
        if (current.Length >= Capacity)
        {
            updated = current;
            return false;
        }
        updated = [.. current, itemId];
        return true;
    }
}
