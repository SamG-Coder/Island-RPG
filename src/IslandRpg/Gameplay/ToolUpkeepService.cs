namespace IslandRpg.Gameplay;

/// <summary>
/// Owns reusable tool wear and maintenance mutations for every actor.
/// </summary>
internal static class ToolUpkeepService
{
    public static bool TryBluntStoneTool(
        string?[]? items, string toolId, float roll,
        out string?[] updated)
    {
        updated = PlayerInventory.Normalize(items);
        if (roll >= .01f) return false;
        var slot = Array.FindIndex(updated, item => item == toolId);
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
        updated = PlayerInventory.Normalize(items);
        if (smallRocksSlot == toolSlot ||
            (uint)smallRocksSlot >= PlayerInventory.Capacity ||
            (uint)toolSlot >= PlayerInventory.Capacity ||
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
}
