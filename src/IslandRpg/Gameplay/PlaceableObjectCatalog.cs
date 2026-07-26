using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal sealed record PlaceableObjectDefinition(
    string ItemId,
    string SpriteFile,
    float FootprintWidth,
    float FootprintDepth,
    float Height,
    int HotspotX,
    int HotspotY);

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
                HotspotY: 70)
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
    {
        var half = size * .5f;
        return MathF.Round(value - half) + half;
    }
}
