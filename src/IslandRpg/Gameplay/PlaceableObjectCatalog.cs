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
    float NavigationDepth = 0,
    int RotationCount = 1)
{
    public float GroundContactWidth =>
        NavigationWidth > 0 ? NavigationWidth : FootprintWidth;

    public float GroundContactDepth =>
        NavigationDepth > 0 ? NavigationDepth : FootprintDepth;

    public int NormalizeRotation(int rotation) =>
        RotationCount <= 1 || rotation < 0
            ? 0
            : rotation % RotationCount;

    public (float Width, float Depth) Footprint(int rotation)
    {
        var normalized = NormalizeRotation(rotation);
        return normalized % 2 == 0
            ? (FootprintWidth, FootprintDepth)
            : (FootprintDepth, FootprintWidth);
    }

    public (float Width, float Depth) GroundContact(int rotation)
    {
        var normalized = NormalizeRotation(rotation);
        return normalized % 2 == 0
            ? (GroundContactWidth, GroundContactDepth)
            : (GroundContactDepth, GroundContactWidth);
    }
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

    static PlaceableObjectCatalog()
    {
        foreach (var house in HouseCatalog.All)
            Definitions[house.ItemId] = new(
                house.ItemId,
                $"{house.ItemId}.png",
                FootprintWidth: 2,
                FootprintDepth: 2,
                Height: 2.5f,
                HotspotX: 64,
                HotspotY: 104,
                RenderWidth: 128,
                RenderHeight: 112,
                NavigationWidth: 1.65f,
                NavigationDepth: 1.65f);
        foreach (var defence in DefenceBuildingCatalog.All)
            Definitions[defence.ItemId] = new(
                defence.ItemId,
                $"{defence.ItemId}.png",
                FootprintWidth: defence.FootprintWidth,
                FootprintDepth: defence.FootprintDepth,
                Height: defence.Kind == DefenceBuildingKind.Castle ? 5 : 3.5f,
                HotspotX: defence.Kind == DefenceBuildingKind.Castle ? 180 : 64,
                HotspotY: defence.Kind == DefenceBuildingKind.Castle ? 260 : 150,
                NavigationWidth: defence.FootprintWidth * .85f,
                NavigationDepth: defence.FootprintDepth * .85f);
        foreach (var wall in WallCatalog.All.Where(value =>
                     value.ItemId.StartsWith(
                         "wall_variant_", StringComparison.Ordinal)))
            Definitions[wall.ItemId] = new(
                wall.ItemId, $"{wall.ItemId}.png",
                FootprintWidth: 1, FootprintDepth: 1, Height: 2.2f,
                HotspotX: 48, HotspotY: 96,
                RenderWidth: 96, RenderHeight: 104,
                NavigationWidth: 1, NavigationDepth: 1);
        foreach (var gate in GateCatalog.All)
            Definitions[gate.ItemId] = new(
                gate.ItemId, $"{gate.ItemId}.png",
                // The shared placement rule uses this authored four-by-one
                // span for axial gates and reserves four-by-four for the two
                // diagonal orientations.
                FootprintWidth: 4, FootprintDepth: 1, Height: 3,
                HotspotX: 96, HotspotY: 150,
                NavigationWidth: 4, NavigationDepth: 1,
                RotationCount: 4);
    }

    public static IReadOnlyCollection<PlaceableObjectDefinition> All =>
        Definitions.Values;

    public static bool TryGet(
        string itemId, out PlaceableObjectDefinition definition) =>
        Definitions.TryGetValue(itemId, out definition!);

    public static bool IsPlaceable(string itemId) =>
        Definitions.ContainsKey(itemId);

    public static int RotationCount(string itemId) =>
        TryGet(itemId, out var definition) ? definition.RotationCount : 1;

    public static int NormalizeRotation(string itemId, int rotation) =>
        TryGet(itemId, out var definition)
            ? definition.NormalizeRotation(rotation)
            : 0;

    public static (float Width, float Depth) PlacementFootprint(
        PlaceableObjectDefinition definition,
        int rotation)
    {
        if (PlaceableWorldObjectRules.TryGetCollision(
                definition.ItemId, out var authoritativeDefinition))
        {
            var size = PlaceableWorldObjectRules.PlacementFootprint(
                authoritativeDefinition, rotation);
            return (size.X, size.Y);
        }

        return definition.Footprint(rotation);
    }

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
        return WallNavigationObstacleAt(center, frame);
    }

    private static NavigationObstacle WallNavigationObstacleAt(
        Vector2 center, int frame)
    {
        return frame switch
        {
            // Screen-horizontal and screen-vertical wall artwork spans a
            // complete diagonal world tile. Use a rotated, narrow strip so
            // the collision follows the visible wall base end to end.
            3 => new(center, 1.42f, .5f, -MathF.PI * .25f),
            4 => new(center, 1.42f, .5f, MathF.PI * .25f),
            _ => new(center, 1f, 1f)
        };
    }

    public static IReadOnlyList<NavigationObstacle> GateNavigationObstacles(
        WorldGroundObject value, bool includeMiddle)
    {
        var rotation = NormalizeRotation(value.ItemId, value.VisualFrame);
        var gate = GateCatalog.Get(value.ItemId);
        var geometry = GateGeometryCatalog.Geometry(gate, rotation);
        var size = geometry.CollisionRadius * 2;
        var center = GateRenderedGroundCenter(value);
        if (rotation is 2 or 3)
            return AxialWoodenGateNavigationObstacles(
                center, rotation, includeMiddle);
        var (axis, length, depth, collisionRotation) =
            GateCollisionLayout(rotation, size);
        var endLength = Math.Min(depth, length * .25f);
        var endOffset = (length - endLength) * .5f;
        var firstCenter = center + axis *
            -endOffset;
        var secondCenter = center + axis * endOffset;
        var result = new List<NavigationObstacle>(includeMiddle ? 3 : 2)
        {
            new(firstCenter, endLength, depth, collisionRotation),
            new(secondCenter, endLength, depth, collisionRotation)
        };
        if (includeMiddle)
        {
            // AoE stores one full collision radius for the closed building.
            // Splitting that authored span into two ends and a middle lets
            // an opened gate remove only its passage without inventing a
            // second footprint from sprite pixels.
            result.Add(new(
                center,
                Math.Max(WorldPlacementGrid.CellSize,
                    length - endLength * 2),
                depth,
                collisionRotation));
        }
        return result;
    }

    private static Vector2 GateRenderedGroundCenter(WorldGroundObject value)
    {
        // GroundObjectWorld renders placeable sprites at their authored front
        // edge by adding ProjectedFrontOffsetPixels. Gate collision is made
        // from several independently placed parts, so starting those parts at
        // the persisted entity centre leaves every obstacle behind the bases
        // visible on screen. Convert that exact render offset back into world
        // coordinates before applying the gate's tower/annex offsets.
        var frontOffset = ProjectedFrontOffsetPixels(value.ItemId) / 48f;
        return new Vector2(value.X + frontOffset, value.Y + frontOffset);
    }

    private static IReadOnlyList<NavigationObstacle>
        AxialWoodenGateNavigationObstacles(
            Vector2 center, int rotation, bool includeMiddle)
    {
        var vertical = rotation == 3;
        var axis = Vector2.Normalize(vertical
            ? new Vector2(1, 1)
            : new Vector2(1, -1));
        var middleFrame = vertical ? 4 : 3;
        var result = new List<NavigationObstacle>(includeMiddle ? 4 : 2)
        {
            // Reuse the exact wall collision mapping: frame 2 is the single
            // wooden tower, while frames 3 and 4 are the horizontal and
            // vertical wall sections used by their matching gate artwork.
            WallNavigationObstacleAt(center - axis * 1.5f, 2),
            WallNavigationObstacleAt(center + axis * 1.5f, 2)
        };
        if (includeMiddle)
        {
            result.Insert(1,
                WallNavigationObstacleAt(
                    center - axis * .5f, middleFrame));
            result.Insert(2,
                WallNavigationObstacleAt(
                    center + axis * .5f, middleFrame));
        }
        return result;
    }

    private static (Vector2 Axis, float Length, float Depth, float Rotation)
        GateCollisionLayout(int rotation, Vector2 size)
    {
        if (size.X > size.Y + .001f)
            return (Vector2.UnitX, size.X, size.Y, 0);
        if (size.Y > size.X + .001f)
            return (Vector2.UnitY, size.Y, size.X, MathF.PI * .5f);

        // The C/D gate records use a square collision radius. Preserve the
        // DAT size while orienting its openable span with the visual gate.
        var axis = (rotation & 3) switch
        {
            2 => new(1, -1),
            3 => new(1, 1),
            _ => Vector2.UnitX
        };
        axis.Normalize();
        return (axis, size.X, size.Y,
            MathF.Atan2(axis.Y, axis.X));
    }

    public static IReadOnlyList<NavigationObstacle> NavigationObstacles(
        WorldGroundObject value, bool includeGateMiddle = true)
    {
        if (GateCatalog.IsGate(value.ItemId))
            return GateNavigationObstacles(value, includeGateMiddle);
        if (WallCatalog.IsWall(value.ItemId))
            return [WallNavigationObstacle(value)];
        if (!TryGet(value.ItemId, out var definition))
            return [];
        var contact = definition.GroundContact(value.VisualFrame);
        return
        [
            new(
                GroundContactCenter(
                    value.ItemId, new(value.X, value.Y)),
                contact.Width,
                contact.Depth)
        ];
    }

    private static Vector2 RotateQuarter(Vector2 value, int rotation) =>
        (rotation & 3) switch
        {
            1 => new(-value.Y, value.X),
            2 => new(-value.X, -value.Y),
            3 => new(value.Y, -value.X),
            _ => value
        };

    public static Vector2 ClosestInteractionPoint(
        string itemId,
        Vector2 storedPosition,
        Vector2 actorPosition,
        float clearance = .32f,
        int rotation = 0)
    {
        var points = InteractionPoints(
            itemId, storedPosition, actorPosition, clearance, rotation);
        return points.Count > 0 ? points[0] : storedPosition;
    }

    public static IReadOnlyList<Vector2> InteractionPoints(
        string itemId,
        Vector2 storedPosition,
        Vector2 actorPosition,
        float clearance = .32f,
        int rotation = 0)
    {
        if (!TryGet(itemId, out var definition))
            return [storedPosition];

        var center = GroundContactCenter(itemId, storedPosition);
        var contact = GateCatalog.IsGate(itemId)
            ? GateGeometryCatalog.Geometry(
                GateCatalog.Get(itemId), rotation).CollisionSize
            : new Vector2(
                definition.GroundContact(rotation).Width,
                definition.GroundContact(rotation).Depth);
        var halfWidth = contact.X * .5f + clearance;
        var halfDepth = contact.Y * .5f + clearance;
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
        int firstRotation,
        PlaceableObjectDefinition second,
        Vector2 secondCenter,
        int secondRotation,
        float padding = .08f) =>
        Overlaps(
            PlacementFootprint(first, firstRotation), firstCenter,
            PlacementFootprint(second, secondRotation), secondCenter,
            padding);

    public static bool Overlaps(
        PlaceableObjectDefinition first,
        Vector2 firstCenter,
        PlaceableObjectDefinition second,
        Vector2 secondCenter,
        float padding = .08f) =>
        Overlaps(first, firstCenter, 0, second, secondCenter, 0, padding);

    private static bool Overlaps(
        (float Width, float Depth) first,
        Vector2 firstCenter,
        (float Width, float Depth) second,
        Vector2 secondCenter,
        float padding) =>
        MathF.Abs(firstCenter.X - secondCenter.X) <
            (first.Width + second.Width) * .5f + padding &&
        MathF.Abs(firstCenter.Y - secondCenter.Y) <
            (first.Depth + second.Depth) * .5f + padding;

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
        float padding = 0,
        int rotation = 0)
    {
        var footprint = PlacementFootprint(definition, rotation);
        return
        MathF.Abs(point.X - center.X) <
            footprint.Width * .5f + padding &&
        MathF.Abs(point.Y - center.Y) <
            footprint.Depth * .5f + padding;
    }

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
