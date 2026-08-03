namespace IslandRpg.Gameplay;

internal enum VillagerCollectionRoute : byte
{
    Ground,
    TreeLogs,
    TreeSticks,
    Forage,
    Fish,
    Mine
}

/// <summary>
/// Maps a concrete collection commitment to the world capability that can
/// actually produce it. This prevents a generic "gather" decision from
/// satisfying unrelated needs indefinitely while a promise is active.
/// </summary>
internal static class VillagerCollectionRouteService
{
    public static VillagerCollectionRoute For(string itemId)
    {
        if (!ItemCatalog.TryGet(itemId, out var item))
            return VillagerCollectionRoute.Ground;
        if (item.HasTag(ItemTag.Log))
            return VillagerCollectionRoute.TreeLogs;
        if (itemId == ItemIds.Sticks)
            return VillagerCollectionRoute.TreeSticks;
        if (itemId == ItemIds.PlantFibres || item.HasTag(ItemTag.Berry))
            return VillagerCollectionRoute.Forage;
        if (item.HasTag(ItemTag.Fish))
            return VillagerCollectionRoute.Fish;
        if (item.HasTag(ItemTag.MiningMaterial))
            return VillagerCollectionRoute.Mine;
        return VillagerCollectionRoute.Ground;
    }

    public static bool HasRequiredTool(
        VillagerCollectionRoute route,
        string?[] inventory) => route switch
    {
        VillagerCollectionRoute.TreeLogs =>
            PlayerInventory.BestAxe(inventory) is not null,
        VillagerCollectionRoute.Fish =>
            PlayerInventory.BestFishingNet(inventory) is not null,
        VillagerCollectionRoute.Mine =>
            PlayerInventory.BestPickaxe(inventory) is not null,
        _ => true
    };
}
