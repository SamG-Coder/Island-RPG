using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal sealed record PlaceableObjectDefinition(
    string ItemId,
    string SpriteFile,
    float FootprintWidth,
    float FootprintDepth,
    float Height,
    int HotspotX,
    int HotspotY,
    int RenderWidth = 0,
    int RenderHeight = 0,
    bool ChromaKeyMagenta = false);

internal static class PlaceableObjectCatalog
{
    private static readonly Dictionary<string, PlaceableObjectDefinition>
        Definitions = new(StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.Workbench] = new(
                ItemIds.Workbench,
                "workbench.png",
                FootprintWidth: 2,
                FootprintDepth: 1,
                Height: 1,
                HotspotX: 51,
                HotspotY: 44),
            [ItemIds.Campfire] = new(
                ItemIds.Campfire,
                "campfire.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: .3f,
                HotspotX: 29,
                HotspotY: 54),
            [ItemIds.Bloomery] = new(
                ItemIds.Bloomery,
                "bloomery.png",
                FootprintWidth: 1.5f,
                FootprintDepth: 1.5f,
                Height: 1.5f,
                HotspotX: 58,
                HotspotY: 98),
            [ItemIds.SmithingAnvil] = new(
                ItemIds.SmithingAnvil,
                "anvil.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: .9f,
                HotspotX: 28,
                HotspotY: 48,
                RenderWidth: 56,
                RenderHeight: 52),
            [ItemIds.CookingPot] = new(
                ItemIds.CookingPot,
                "cooking-pot.png",
                FootprintWidth: .9f,
                FootprintDepth: .9f,
                Height: .65f,
                HotspotX: 25,
                HotspotY: 47,
                RenderWidth: 50,
                RenderHeight: 50),
            [ItemIds.StorageChest] = new(
                ItemIds.StorageChest,
                "storage-chest.png",
                FootprintWidth: 1.25f,
                FootprintDepth: .75f,
                Height: .8f,
                HotspotX: 30,
                HotspotY: 43,
                RenderWidth: 60,
                RenderHeight: 46),
            [ItemIds.StorageBarrel] = new(
                ItemIds.StorageBarrel,
                "storage-barrel.png",
                FootprintWidth: .75f,
                FootprintDepth: .75f,
                Height: 1,
                HotspotX: 31,
                HotspotY: 53),
            [ItemIds.TrainingDummy] = new(
                ItemIds.TrainingDummy,
                Path.Combine("Combat", "training-dummy-source.png"),
                FootprintWidth: 1.2f,
                FootprintDepth: 1.2f,
                Height: 1.8f,
                HotspotX: 36,
                HotspotY: 69,
                RenderWidth: 72,
                RenderHeight: 72,
                ChromaKeyMagenta: true)
        };

    public static IReadOnlyCollection<PlaceableObjectDefinition> All =>
        Definitions.Values;

    public static bool TryGet(
        string itemId, out PlaceableObjectDefinition definition) =>
        Definitions.TryGetValue(itemId, out definition!);

    public static bool IsPlaceable(string itemId) =>
        Definitions.ContainsKey(itemId);

    public static float ProjectedFrontOffsetPixels(string itemId) =>
        TryGet(itemId, out var definition)
            ? (definition.FootprintWidth +
               definition.FootprintDepth) * 12f
            : 0;

    public static Vector2 SnapToGrid(
        string itemId, Vector2 target)
    {
        if (!TryGet(itemId, out var definition))
            return target;
        return new(
            SnapAxis(target.X, definition.FootprintWidth),
            SnapAxis(target.Y, definition.FootprintDepth));
    }

    public static bool Overlaps(
        PlaceableObjectDefinition first,
        Vector2 firstCenter,
        PlaceableObjectDefinition second,
        Vector2 secondCenter,
        float padding = .08f) =>
        MathF.Abs(firstCenter.X - secondCenter.X) <
            (first.FootprintWidth + second.FootprintWidth) * .5f +
            padding &&
        MathF.Abs(firstCenter.Y - secondCenter.Y) <
            (first.FootprintDepth + second.FootprintDepth) * .5f +
            padding;

    public static bool ContainsPoint(
        PlaceableObjectDefinition definition,
        Vector2 center,
        Vector2 point,
        float padding = 0) =>
        MathF.Abs(point.X - center.X) <
            definition.FootprintWidth * .5f + padding &&
        MathF.Abs(point.Y - center.Y) <
            definition.FootprintDepth * .5f + padding;

    private static float SnapAxis(float value, float size)
        => WorldPlacementGrid.SnapWithFootprint(value, size);
}
