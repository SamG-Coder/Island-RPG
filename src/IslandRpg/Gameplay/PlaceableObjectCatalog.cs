using OpenTK.Mathematics;
using IslandRpg.World;

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
    bool ChromaKeyMagenta = false,
    float NavigationWidth = 0,
    float NavigationDepth = 0)
{
    public float GroundContactWidth =>
        NavigationWidth > 0 ? NavigationWidth : FootprintWidth;

    public float GroundContactDepth =>
        NavigationDepth > 0 ? NavigationDepth : FootprintDepth;
}

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
                HotspotY: 44,
                NavigationWidth: .9f,
                NavigationDepth: .5f),
            [ItemIds.Campfire] = new(
                ItemIds.Campfire,
                "campfire.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: .3f,
                HotspotX: 29,
                HotspotY: 54,
                NavigationWidth: .55f,
                NavigationDepth: .4f),
            [ItemIds.Bloomery] = new(
                ItemIds.Bloomery,
                "bloomery.png",
                FootprintWidth: 1.5f,
                FootprintDepth: 1.5f,
                Height: 1.5f,
                HotspotX: 58,
                HotspotY: 98,
                NavigationWidth: .65f,
                NavigationDepth: .5f),
            [ItemIds.SmithingAnvil] = new(
                ItemIds.SmithingAnvil,
                "anvil.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: .9f,
                HotspotX: 28,
                HotspotY: 48,
                RenderWidth: 56,
                RenderHeight: 52,
                NavigationWidth: .5f,
                NavigationDepth: .35f),
            [ItemIds.CookingPot] = new(
                ItemIds.CookingPot,
                "cooking-pot.png",
                FootprintWidth: .9f,
                FootprintDepth: .9f,
                Height: .65f,
                HotspotX: 25,
                HotspotY: 47,
                RenderWidth: 50,
                RenderHeight: 50,
                NavigationWidth: .45f,
                NavigationDepth: .35f),
            [ItemIds.StorageChest] = new(
                ItemIds.StorageChest,
                "storage-chest.png",
                FootprintWidth: 1.25f,
                FootprintDepth: .75f,
                Height: .8f,
                HotspotX: 30,
                HotspotY: 43,
                RenderWidth: 60,
                RenderHeight: 46,
                NavigationWidth: .6f,
                NavigationDepth: .3f),
            [ItemIds.StorageBarrel] = new(
                ItemIds.StorageBarrel,
                "storage-barrel.png",
                FootprintWidth: .75f,
                FootprintDepth: .75f,
                Height: 1,
                HotspotX: 31,
                HotspotY: 53,
                NavigationWidth: .35f,
                NavigationDepth: .3f),
            [ItemIds.WoodenWall] = new(
                ItemIds.WoodenWall,
                "wooden-wall.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: 1.8f,
                HotspotX: 48,
                HotspotY: 82,
                RenderWidth: 96,
                RenderHeight: 88,
                NavigationWidth: 1f,
                NavigationDepth: 1f),
            [ItemIds.WoodenFence] = new(
                ItemIds.WoodenFence,
                "wooden-fence.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: 1.5f,
                HotspotX: 48,
                HotspotY: 82,
                RenderWidth: 96,
                RenderHeight: 88,
                NavigationWidth: 1f,
                NavigationDepth: 1f),
            [ItemIds.StoneWall] = new(
                ItemIds.StoneWall,
                "stone-wall.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: 2.2f,
                HotspotX: 48,
                HotspotY: 96,
                RenderWidth: 96,
                RenderHeight: 104,
                NavigationWidth: 1f,
                NavigationDepth: 1f),
            [ItemIds.FortifiedWoodenWall] = new(
                ItemIds.FortifiedWoodenWall,
                "fortified-wooden-wall.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: 1.9f,
                HotspotX: 48,
                HotspotY: 86,
                RenderWidth: 96,
                RenderHeight: 92,
                NavigationWidth: 1f,
                NavigationDepth: 1f),
            [ItemIds.FortifiedWall] = new(
                ItemIds.FortifiedWall,
                "fortified-wall.png",
                FootprintWidth: 1,
                FootprintDepth: 1,
                Height: 2.4f,
                HotspotX: 48,
                HotspotY: 104,
                RenderWidth: 96,
                RenderHeight: 112,
                NavigationWidth: 1f,
                NavigationDepth: 1f),
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
                ChromaKeyMagenta: true,
                NavigationWidth: .45f,
                NavigationDepth: .3f),
            [ItemIds.LootBag] = new(
                ItemIds.LootBag,
                "loot-bag.png",
                FootprintWidth: .45f,
                FootprintDepth: .35f,
                Height: .45f,
                HotspotX: 18,
                HotspotY: 26,
                NavigationWidth: .05f,
                NavigationDepth: .05f)
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

    public static Vector2 GroundContactCenter(
        string itemId,
        Vector2 storedPosition)
    {
        if (!TryGet(itemId, out var definition))
            return storedPosition;
        // The sprite hotspot is authored at the front edge of its ground
        // base. Move back by half the projected navigation footprint so the
        // collision rectangle is centred beneath the pixels touching ground.
        var contactHalfDepthPixels =
            (definition.GroundContactWidth +
             definition.GroundContactDepth) * 12f;
        // Equal movement on both world axes projects to a vertical-only shift
        // of 48 pixels per world unit.
        var forward =
            (ProjectedFrontOffsetPixels(itemId) -
             contactHalfDepthPixels) / 48f;
        return storedPosition + new Vector2(forward, forward);
    }

    public static NavigationObstacle WallNavigationObstacle(
        WorldGroundObject value)
    {
        var center = GroundContactCenter(
            value.ItemId, new(value.X, value.Y)) +
            new Vector2(
                WorldPlacementGrid.CellSize,
                WorldPlacementGrid.CellSize);
        var frame = value.VisualFrame is >= 0 and < 5
            ? value.VisualFrame
            : ConstructionService.Angle(value);
        return frame switch
        {
            3 => new(center, 1f, .75f),
            4 => new(center, .75f, 1f),
            _ => new(center, 1f, 1f)
        };
    }

    public static Vector2 ClosestInteractionPoint(
        string itemId,
        Vector2 storedPosition,
        Vector2 actorPosition,
        float clearance = .32f)
    {
        var points = InteractionPoints(
            itemId, storedPosition, actorPosition, clearance);
        return points.Count > 0 ? points[0] : storedPosition;
    }

    public static IReadOnlyList<Vector2> InteractionPoints(
        string itemId,
        Vector2 storedPosition,
        Vector2 actorPosition,
        float clearance = .32f)
    {
        if (!TryGet(itemId, out var definition))
            return [storedPosition];

        var center = GroundContactCenter(itemId, storedPosition);
        var halfWidth = definition.GroundContactWidth * .5f + clearance;
        var halfDepth = definition.GroundContactDepth * .5f + clearance;
        var relative = actorPosition - center;
        var outsideX = MathF.Abs(relative.X) > halfWidth;
        var outsideY = MathF.Abs(relative.Y) > halfDepth;
        Vector2 closest;
        if (outsideX || outsideY)
        {
            closest = center + new Vector2(
                Math.Clamp(relative.X, -halfWidth, halfWidth),
                Math.Clamp(relative.Y, -halfDepth, halfDepth));
        }
        else
        {
            var left = relative.X + halfWidth;
            var right = halfWidth - relative.X;
            var top = relative.Y + halfDepth;
            var bottom = halfDepth - relative.Y;
            var nearest = MathF.Min(
                MathF.Min(left, right), MathF.Min(top, bottom));
            if (nearest == left) relative.X = -halfWidth;
            else if (nearest == right) relative.X = halfWidth;
            else if (nearest == top) relative.Y = -halfDepth;
            else relative.Y = halfDepth;
            closest = center + relative;
        }
        return new[]
            {
                closest,
                center + new Vector2(-halfWidth, -halfDepth),
                center + new Vector2(0, -halfDepth),
                center + new Vector2(halfWidth, -halfDepth),
                center + new Vector2(-halfWidth, 0),
                center + new Vector2(halfWidth, 0),
                center + new Vector2(-halfWidth, halfDepth),
                center + new Vector2(0, halfDepth),
                center + new Vector2(halfWidth, halfDepth)
            }
            .Distinct()
            .OrderBy(point => Vector2.DistanceSquared(
                actorPosition, point))
            .ToArray();
    }

    public static Vector2 SnapToGrid(
        string itemId, Vector2 target)
    {
        if (!TryGet(itemId, out var definition))
            return target;
        return new(
            SnapAxis(target.X, definition.FootprintWidth),
            SnapAxis(target.Y, definition.FootprintDepth));
    }

    public static Vector2 SnapBuildingToTile(Vector2 target) =>
        new(MathF.Floor(target.X) + .5f, MathF.Floor(target.Y) + .5f);

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

    public static float PlacementPadding(
        PlaceableObjectDefinition first,
        PlaceableObjectDefinition second) =>
        first.FootprintWidth <= 1 && first.FootprintDepth <= 1 &&
        second.FootprintWidth <= 1 && second.FootprintDepth <= 1
            ? 0
            : .08f;

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

internal static class BuildingTerrainPlacement
{
    public static bool IsSupported(
        int footprintTileCount,
        float lowestHeight,
        float highestHeight) =>
        footprintTileCount == 1 || highestHeight - lowestHeight <= 2;
}
