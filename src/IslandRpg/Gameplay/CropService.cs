using System.Numerics;
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

    public static Vector2 TileCenter(Vector2 position) => new(
        MathF.Floor(position.X) + .5f,
        MathF.Floor(position.Y) + .5f);

    public static bool IsTileCenter(Vector2 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) &&
        position == TileCenter(position);

    public static WorldGroundObject Plant(
        Guid objectId,
        string seedItemId, float x, float y, double gameSeconds,
        string? ownerId = null)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException(
                "A stable crop object identity is required.",
                nameof(objectId));
        if (!float.IsFinite(x) || !float.IsFinite(y))
            throw new ArgumentOutOfRangeException(
                nameof(x), "Crop coordinates must be finite.");
        if (!double.IsFinite(gameSeconds) || gameSeconds < 0 ||
            gameSeconds > double.MaxValue - GrowthGameSeconds)
            throw new ArgumentOutOfRangeException(
                nameof(gameSeconds),
                "Crop planting time must be a finite non-negative value.");
        if (!TryHarvestItem(seedItemId, out var harvestItemId))
            throw new ArgumentException("The item is not a crop seed.",
                nameof(seedItemId));
        _ = TryCropItem(seedItemId, out var cropItemId);
        return new(
            objectId, cropItemId, x, y,
            FuelItemId: harvestItemId,
            LitUntilGameSeconds: gameSeconds + GrowthGameSeconds,
            OwnerId: ownerId);
    }

    // Retained for the local single-player adapter. Authoritative callers use
    // the overload above and supply an identity derived from the command.
    public static WorldGroundObject Plant(
        string seedItemId, float x, float y, double gameSeconds,
        string? ownerId = null) =>
        Plant(Guid.NewGuid(), seedItemId, x, y, gameSeconds, ownerId);

    public static bool IsCropItem(string itemId) => itemId is
        ItemIds.WildGrainCrop or ItemIds.BeanCrop or ItemIds.RootCrop;

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
        IsCrop(value) && double.IsFinite(gameSeconds) &&
        gameSeconds >= 0 && gameSeconds >= value.LitUntilGameSeconds;

    public static bool HasValidPersistentState(WorldGroundObject value) =>
        !IsCropItem(value.ItemId) ||
        IsCrop(value) && double.IsFinite(value.LitUntilGameSeconds) &&
        value.LitUntilGameSeconds >= GrowthGameSeconds;

    public static int HarvestCount(bool hasGatheringBasket) =>
        2 + (hasGatheringBasket ? 1 : 0);

    public static int HarvestCount(string?[]? inventory) =>
        HarvestCount(FarmingSkill.GatheringBasketBonus(inventory) > 0);
}
