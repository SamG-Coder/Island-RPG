using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class CropService
{
    public const double GrowthGameSeconds = 4 * 60 * 60;

    public static bool TryHarvestItem(string seedItemId, out string itemId)
    {
        itemId = seedItemId switch
        {
            ItemIds.WildGrainSeeds => ItemIds.WildGrain,
            ItemIds.BeanSeeds => ItemIds.Beans,
            ItemIds.RootSeeds => ItemIds.EdibleRoots,
            _ => ""
        };
        return itemId.Length > 0;
    }

    public static bool TryCropItem(string seedItemId, out string itemId)
    {
        itemId = seedItemId switch
        {
            ItemIds.WildGrainSeeds => ItemIds.WildGrainCrop,
            ItemIds.BeanSeeds => ItemIds.BeanCrop,
            ItemIds.RootSeeds => ItemIds.RootCrop,
            _ => ""
        };
        return itemId.Length > 0;
    }

    public static WorldGroundObject Plant(
        string seedItemId, float x, float y, double gameSeconds,
        string? ownerId = null)
    {
        if (!TryHarvestItem(seedItemId, out var harvestItemId))
            throw new ArgumentException("The item is not a crop seed.",
                nameof(seedItemId));
        _ = TryCropItem(seedItemId, out var cropItemId);
        return new(
            Guid.NewGuid(), cropItemId, x, y,
            FuelItemId: harvestItemId,
            LitUntilGameSeconds: gameSeconds + GrowthGameSeconds,
            OwnerId: ownerId);
    }

    public static bool IsCrop(WorldGroundObject value) =>
        value.ItemId switch
        {
            ItemIds.WildGrainCrop => value.FuelItemId == ItemIds.WildGrain,
            ItemIds.BeanCrop => value.FuelItemId == ItemIds.Beans,
            ItemIds.RootCrop => value.FuelItemId == ItemIds.EdibleRoots,
            _ => false
        };

    public static bool IsReady(
        WorldGroundObject value, double gameSeconds) =>
        IsCrop(value) && gameSeconds >= value.LitUntilGameSeconds;

    public static int HarvestCount(string?[]? inventory) =>
        2 + FarmingSkill.GatheringBasketBonus(inventory);
}
