using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum BucketWaterKind
{
    None,
    Fresh,
    Sea
}

internal static class BucketService
{
    public const float FillRange = 2.4f;

    public static bool IsEmpty(string itemId) => itemId == ItemIds.Bucket;

    public static bool IsFilled(string itemId) =>
        itemId is ItemIds.BucketOfWater or ItemIds.BucketOfSeawater;

    public static bool IsBucket(string itemId) =>
        IsEmpty(itemId) || IsFilled(itemId);

    public static string FilledItemId(BucketWaterKind kind) => kind switch
    {
        BucketWaterKind.Fresh => ItemIds.BucketOfWater,
        BucketWaterKind.Sea => ItemIds.BucketOfSeawater,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), "Only fresh or sea water can fill a bucket.")
    };

    public static string DisplayName(BucketWaterKind kind) => kind switch
    {
        BucketWaterKind.Fresh => "water",
        BucketWaterKind.Sea => "seawater",
        _ => "water"
    };

    public static BucketWaterKind ClassifyAt(
        long worldSeed, int worldLevel, int tileX, int tileY)
    {
        if (worldLevel < 0)
            return ClassifyUnderground(
                ProceduralUndergroundTerrain.MaterialAt(
                    worldSeed, tileX, tileY));

        return ClassifySurface(
            ProceduralSurfaceTerrain.ClassifyAt(
                worldSeed, tileX, tileY).Material);
    }

    public static BucketWaterKind ClassifySurface(
        ProceduralSurfaceTerrain.Material material) => material switch
    {
        ProceduralSurfaceTerrain.Material.DeepWater or
            ProceduralSurfaceTerrain.Material.ShallowWater =>
            BucketWaterKind.Sea,
        ProceduralSurfaceTerrain.Material.RiverWater or
            ProceduralSurfaceTerrain.Material.MangroveShallows =>
            BucketWaterKind.Fresh,
        _ => BucketWaterKind.None
    };

    public static BucketWaterKind ClassifyUnderground(
        ProceduralUndergroundTerrain.Material material) => material switch
    {
        ProceduralUndergroundTerrain.Material.ShallowWater or
            ProceduralUndergroundTerrain.Material.RiverWater =>
            BucketWaterKind.Fresh,
        _ => BucketWaterKind.None
    };

    public static Vector2 TileCenter(float x, float y) => new(
        MathF.Floor(x) + .5f,
        MathF.Floor(y) + .5f);
}
