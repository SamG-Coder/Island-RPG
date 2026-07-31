namespace IslandRpg.Gameplay;

/// <summary>
/// Defines small, role-specific inventory targets so autonomous work gathers
/// prerequisites for a concrete tool instead of stockpiling every material.
/// </summary>
internal static class VillagerWorkSupplyPlanner
{
    public static bool NeedsFibre(VillagerState villager)
    {
        var count = Count(villager.Inventory, ItemIds.PlantFibres);
        return villager.WorkRole switch
        {
            VillagerWorkRole.Food
                when PlayerInventory.BestFishingNet(
                    villager.Inventory) is null =>
                count < (villager.Inventory.Contains(ItemIds.Rope) ? 6 : 3),
            VillagerWorkRole.Crafting
                when !HasTag(villager.Inventory, ItemTag.Knife) =>
                count < 1,
            _ => false
        };
    }

    public static bool NeedsSticks(VillagerState villager)
    {
        if (Count(villager.Inventory, ItemIds.Sticks) >= 1)
            return false;
        return villager.WorkRole switch
        {
            VillagerWorkRole.Wood =>
                PlayerInventory.BestAxe(villager.Inventory) is null,
            VillagerWorkRole.Crafting =>
                HasTag(villager.Inventory, ItemTag.Knife) &&
                !HasTag(villager.Inventory, ItemTag.Hammer),
            VillagerWorkRole.Exploration =>
                PlayerInventory.BestPickaxe(villager.Inventory) is null,
            _ => false
        };
    }

    private static int Count(string?[] inventory, string itemId) =>
        inventory.Count(value => value == itemId);

    private static bool HasTag(
        string?[] inventory,
        ItemTag tag) =>
        inventory.Any(itemId =>
            itemId is not null &&
            ItemCatalog.TryGet(itemId, out var item) &&
            item.HasTag(tag));
}
