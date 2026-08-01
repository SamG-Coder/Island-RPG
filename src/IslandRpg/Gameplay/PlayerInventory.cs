namespace IslandRpg.Gameplay;

internal static class PlayerInventory
{
    public const int Capacity = 28;

    public static string?[] CreateStartingInventory()
    {
        return new string?[Capacity];
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

    public static int Count(string?[]? items, string itemId) =>
        items?.Take(Capacity).Count(item => string.Equals(
            item, itemId, StringComparison.OrdinalIgnoreCase)) ?? 0;

    public static int AddedCount(
        string?[]? before, string?[]? after, string itemId) =>
        Math.Max(0, Count(after, itemId) - Count(before, itemId));

    public static bool IsFull(string?[]? items) => Count(items) >= Capacity;

    public static ItemDefinition? BestAxe(string?[]? items) =>
        items?
            .Where(item => item is not null)
            .Select(item => ItemCatalog.Get(item!))
            .Where(item =>
                item.HasTag(ItemTag.Tool) &&
                item.HasTag(ItemTag.Axe) &&
                item.WoodcuttingPower > 0)
            .OrderByDescending(item => item.WoodcuttingPower)
            .FirstOrDefault();

    public static bool HasAxe(string?[]? items) => BestAxe(items) is not null;

    public static bool HasAnyAxe(string?[]? items) =>
        items?
            .Where(item => item is not null)
            .Select(item => ItemCatalog.Get(item!))
            .Any(item =>
                item.HasTag(ItemTag.Tool) &&
                item.HasTag(ItemTag.Axe)) ?? false;

    public static ItemDefinition? BestPickaxe(string?[]? items) =>
        items?
            .Where(item => item is not null)
            .Select(item => ItemCatalog.Get(item!))
            .Where(item =>
                item.HasTag(ItemTag.Tool) &&
                item.HasTag(ItemTag.Pickaxe) &&
                item.MiningPower > 0)
            .OrderByDescending(item => item.MiningPower)
            .FirstOrDefault();

    public static ItemDefinition? BestFishingNet(string?[]? items) =>
        BestPoweredTool(items, ItemTag.FishingNet,
            item => item.FishingPower);

    public static ItemDefinition? BestShovel(string?[]? items) =>
        BestPoweredTool(items, ItemTag.Shovel,
            item => item.DiggingPower);

    public static ItemDefinition? BestHammer(string?[]? items) =>
        BestPoweredTool(items, ItemTag.Hammer,
            item => item.HammerPower);

    public static ItemDefinition? BestKnife(string?[]? items) =>
        BestPoweredTool(items, ItemTag.Knife,
            item => item.KnifePower);

    public static ItemDefinition? BestSickle(string?[]? items)
    {
        ItemDefinition? best = null;
        if (items is null) return null;
        var length = Math.Min(items.Length, Capacity);
        for (var slot = 0; slot < length; slot++)
        {
            if (items[slot] is not { } itemId) continue;
            var item = ItemCatalog.Get(itemId);
            if (!item.HasTag(ItemTag.Tool) ||
                !item.HasTag(ItemTag.Sickle) ||
                item.FarmingPower <= (best?.FarmingPower ?? 0))
                continue;
            best = item;
        }
        return best;
    }

    private static ItemDefinition? BestPoweredTool(
        string?[]? items, ItemTag tag,
        Func<ItemDefinition, int> power) =>
        items?
            .Take(Capacity)
            .Where(itemId => itemId is not null)
            .Select(itemId => ItemCatalog.Get(itemId!))
            .Where(item =>
                item.HasTag(ItemTag.Tool) &&
                item.HasTag(tag) &&
                power(item) > 0)
            .OrderByDescending(power)
            .FirstOrDefault();

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
            updated[toolSlot] is not { } toolId ||
            toolId != ItemIds.LargeRock &&
            !(ItemCatalog.Get(toolId).HasTag(ItemTag.Hammer) &&
              ItemCatalog.Get(toolId).HammerPower > 0))
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

    public static bool TryCraftStoneAxe(
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
        updated[targetSlot] = ItemIds.StoneAxe;
        return true;
    }

    public static bool TryCraftStoneKnife(
        string?[]? items, int firstSlot, int secondSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (firstSlot == secondSlot ||
            (uint)firstSlot >= Capacity ||
            (uint)secondSlot >= Capacity)
            return false;
        var first = updated[firstSlot];
        var second = updated[secondSlot];
        if (!((first == ItemIds.SharpenedRock &&
               second == ItemIds.PlantFibres) ||
              (first == ItemIds.PlantFibres &&
               second == ItemIds.SharpenedRock)))
            return false;
        updated[firstSlot] = null;
        updated[secondSlot] = ItemIds.StoneKnife;
        return true;
    }

    public static bool TryCraftStoneHammer(
        string?[]? items, int toolSlot, int targetSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            updated[toolSlot] != ItemIds.MediumRock ||
            updated[targetSlot] != ItemIds.Sticks)
            return false;
        updated[toolSlot] = null;
        updated[targetSlot] = ItemIds.StoneHammer;
        return true;
    }

    public static bool TryBluntStoneTool(
        string?[]? items, string toolId, float roll,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (roll >= .01f) return false;
        var slot = Array.FindIndex(
            updated, item => item == toolId);
        if (slot < 0) return false;
        updated[slot] = toolId switch
        {
            ItemIds.StoneAxe => ItemIds.BluntStoneAxe,
            ItemIds.StoneHammer => ItemIds.BluntStoneHammer,
            _ => updated[slot]
        };
        return updated[slot] != toolId;
    }

    public static bool TrySharpenStoneTool(
        string?[]? items, int smallRocksSlot, int toolSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (smallRocksSlot == toolSlot ||
            (uint)smallRocksSlot >= Capacity ||
            (uint)toolSlot >= Capacity ||
            updated[smallRocksSlot] != ItemIds.SmallRocks)
            return false;
        var sharpened = updated[toolSlot] switch
        {
            ItemIds.BluntStoneAxe => ItemIds.StoneAxe,
            ItemIds.BluntStoneHammer => ItemIds.StoneHammer,
            _ => null
        };
        if (sharpened is null) return false;
        updated[smallRocksSlot] = null;
        updated[toolSlot] = sharpened;
        return true;
    }

    public static bool TryCarvePlank(
        string?[]? items, int toolSlot, int targetSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if (toolSlot == targetSlot ||
            (uint)toolSlot >= Capacity ||
            (uint)targetSlot >= Capacity ||
            !ItemCatalog.Get(updated[toolSlot] ?? string.Empty)
                .HasTag(ItemTag.Knife) ||
            !ItemCatalog.Get(updated[targetSlot] ?? string.Empty)
                .HasTag(ItemTag.Log))
            return false;
        updated[targetSlot] = ItemIds.Plank;
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

    public static bool TryAddAtPreferredSlot(
        string?[]? items,
        string itemId,
        int preferredSlot,
        out string?[] updated)
    {
        updated = Normalize(items);
        if ((uint)preferredSlot < Capacity &&
            updated[preferredSlot] is null)
        {
            updated[preferredSlot] = itemId;
            return true;
        }
        var emptySlot = Array.FindIndex(
            updated, item => item is null);
        if (emptySlot < 0) return false;
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
