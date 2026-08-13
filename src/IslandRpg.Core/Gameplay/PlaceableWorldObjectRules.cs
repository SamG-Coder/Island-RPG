using System.Numerics;
using IslandRpg.Navigation;
using IslandRpg.Resources;

namespace IslandRpg.Gameplay;

/// <summary>
/// Headless placement policy for inventory-backed furniture. Rendering owns
/// sprites and previews; the authority owns the accepted item set, footprint,
/// snapping, rotation, terrain samples, and overlap decisions.
/// </summary>
public readonly record struct PlaceableWorldObjectDefinition(
    string ItemId,
    float FootprintWidth,
    float FootprintDepth,
    int RotationCount = 1,
    float NavigationWidth = 0,
    float NavigationDepth = 0)
{
    public float GroundContactWidth =>
        NavigationWidth > 0 ? NavigationWidth : FootprintWidth;

    public float GroundContactDepth =>
        NavigationDepth > 0 ? NavigationDepth : FootprintDepth;

    public int NormalizeRotation(int rotation) =>
        RotationCount <= 1 || rotation < 0 ? 0 : rotation % RotationCount;

    public Vector2 Footprint(int rotation)
    {
        var normalized = NormalizeRotation(rotation);
        return normalized % 2 == 0
            ? new(FootprintWidth, FootprintDepth)
            : new(FootprintDepth, FootprintWidth);
    }

    public Vector2 GroundContact(int rotation)
    {
        var normalized = NormalizeRotation(rotation);
        return normalized % 2 == 0
            ? new(GroundContactWidth, GroundContactDepth)
            : new(GroundContactDepth, GroundContactWidth);
    }
}

public readonly record struct NavigationObstacleBounds(
    Vector2 Minimum,
    Vector2 Maximum)
{
    public Vector2 Center => (Minimum + Maximum) * .5f;

    public Vector2 Size => Maximum - Minimum;

    public bool Intersects(
        NavigationObstacleBounds other, float padding = 0) =>
        Minimum.X < other.Maximum.X + padding &&
        Maximum.X + padding > other.Minimum.X &&
        Minimum.Y < other.Maximum.Y + padding &&
        Maximum.Y + padding > other.Minimum.Y;
}

public static class PlaceableWorldObjectRules
{
    public const float CraftingStationInteractionRange = 3.5f;
    private static readonly Dictionary<string, PlaceableWorldObjectDefinition>
        Definitions = new(StringComparer.OrdinalIgnoreCase)
        {
            [ItemIds.Workbench] = new(
                ItemIds.Workbench, 2f, 1f,
                NavigationWidth: .9f, NavigationDepth: .5f),
            [ItemIds.Campfire] = new(
                ItemIds.Campfire, 1f, 1f,
                NavigationWidth: .55f, NavigationDepth: .4f),
            [ItemIds.Bloomery] = new(
                ItemIds.Bloomery, 1.5f, 1.5f,
                NavigationWidth: .65f, NavigationDepth: .5f),
            [ItemIds.SmithingAnvil] = new(
                ItemIds.SmithingAnvil, 1f, 1f,
                NavigationWidth: .5f, NavigationDepth: .35f),
            [ItemIds.CookingPot] = new(
                ItemIds.CookingPot, .9f, .9f,
                NavigationWidth: .45f, NavigationDepth: .35f),
            [ItemIds.StorageChest] = new(
                ItemIds.StorageChest, 1.25f, .75f,
                NavigationWidth: .6f, NavigationDepth: .3f),
            [ItemIds.StorageBarrel] = new(
                ItemIds.StorageBarrel, .75f, .75f,
                NavigationWidth: .35f, NavigationDepth: .3f),
        };
    private static readonly Dictionary<string, PlaceableWorldObjectDefinition>
        CollisionDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly float MaximumCollisionHalfExtentValue;
    private static readonly float MaximumPlacementHalfExtentValue;

    static PlaceableWorldObjectRules()
    {
        foreach (var definition in Definitions.Values)
            CollisionDefinitions.Add(definition.ItemId, definition);
        foreach (var wall in WallCatalog.All)
            CollisionDefinitions[wall.ItemId] = new(
                wall.ItemId, 1f, 1f, 5, 1f, 1f);
        foreach (var house in HouseCatalog.All)
            CollisionDefinitions[house.ItemId] = new(
                house.ItemId, 2f, 2f, 1, 1.65f, 1.65f);
        foreach (var defence in DefenceBuildingCatalog.All)
            CollisionDefinitions[defence.ItemId] = new(
                defence.ItemId,
                defence.FootprintWidth,
                defence.FootprintDepth,
                1,
                defence.FootprintWidth * .85f,
                defence.FootprintDepth * .85f);
        foreach (var gate in GateCatalog.All)
            CollisionDefinitions[gate.ItemId] = new(
                gate.ItemId, 4f, 1f, 4, 4f, 1f);
        MaximumCollisionHalfExtentValue = CollisionDefinitions.Values.Max(
            definition => Enumerable.Range(0, definition.RotationCount)
                .Select(rotation => CollisionBounds(CollisionObstacles(
                    definition, Vector2.Zero, rotation)))
                .Max(bounds => Math.Max(
                    Math.Max(MathF.Abs(bounds.Minimum.X),
                        MathF.Abs(bounds.Maximum.X)),
                    Math.Max(MathF.Abs(bounds.Minimum.Y),
                        MathF.Abs(bounds.Maximum.Y)))));
        MaximumPlacementHalfExtentValue = CollisionDefinitions.Values.Max(
            definition => Enumerable.Range(0, definition.RotationCount)
                .Select(rotation => PlacementFootprint(
                    definition, rotation))
                .Max(size => Math.Max(size.X, size.Y) * .5f));
    }

    public static IReadOnlyCollection<PlaceableWorldObjectDefinition> All =>
        Definitions.Values;

    public static bool TryGet(
        string itemId, out PlaceableWorldObjectDefinition definition) =>
        Definitions.TryGetValue(itemId, out definition);

    /// <summary>
    /// Resolves collision geometry for carried furniture and material-gated
    /// construction. This is deliberately separate from <see cref="TryGet"/>
    /// so a constructible result ID never becomes an inventory-placeable item.
    /// </summary>
    public static bool TryGetCollision(
        string itemId, out PlaceableWorldObjectDefinition definition) =>
        CollisionDefinitions.TryGetValue(itemId, out definition);

    public static float MaximumCollisionHalfExtent =>
        MaximumCollisionHalfExtentValue;

    public static float MaximumPlacementHalfExtent =>
        MaximumPlacementHalfExtentValue;

    public static bool IsPlaceable(string itemId) =>
        Definitions.ContainsKey(itemId);

    public static Vector2 Snap(string itemId, Vector2 position)
    {
        if (!TryGet(itemId, out var definition)) return position;
        return new(
            WorldPlacementGrid.SnapWithFootprint(
                position.X, definition.FootprintWidth),
            WorldPlacementGrid.SnapWithFootprint(
                position.Y, definition.FootprintDepth));
    }

    public static bool IsSnapped(string itemId, Vector2 position) =>
        TryGet(itemId, out _) && Snap(itemId, position) == position;

    /// <summary>
    /// Returns the complete authored area reserved while placing an object.
    /// This is intentionally distinct from navigation collision: a sprite may
    /// have a smaller shifted ground contact while its entire construction
    /// footprint still needs clear, level terrain. Diagonal gates reserve the
    /// same four-by-four clearance used by the client placement preview.
    /// </summary>
    public static Vector2 PlacementFootprint(
        PlaceableWorldObjectDefinition definition,
        int rotation)
    {
        var normalized = definition.NormalizeRotation(rotation);
        if (GateCatalog.IsGate(definition.ItemId) && normalized is 2 or 3)
        {
            var span = Math.Max(
                definition.FootprintWidth, definition.FootprintDepth);
            return new(span, span);
        }
        return definition.Footprint(normalized);
    }

    public static NavigationObstacleBounds PlacementBounds(
        PlaceableWorldObjectDefinition definition,
        Vector2 center,
        int rotation)
    {
        var half = PlacementFootprint(definition, rotation) * .5f;
        return new(center - half, center + half);
    }

    public static float PlacementPadding(
        PlaceableWorldObjectDefinition first,
        PlaceableWorldObjectDefinition second) =>
        first.FootprintWidth <= 1 && first.FootprintDepth <= 1 &&
        second.FootprintWidth <= 1 && second.FootprintDepth <= 1
            ? 0
            : .08f;

    public static bool PlacementOverlaps(
        PlaceableWorldObjectDefinition first,
        Vector2 firstCenter,
        int firstRotation,
        PlaceableWorldObjectDefinition second,
        Vector2 secondCenter,
        int secondRotation,
        float padding = .08f)
    {
        var firstSize = PlacementFootprint(first, firstRotation);
        var secondSize = PlacementFootprint(second, secondRotation);
        return MathF.Abs(firstCenter.X - secondCenter.X) <
                   (firstSize.X + secondSize.X) * .5f + padding &&
               MathF.Abs(firstCenter.Y - secondCenter.Y) <
                   (firstSize.Y + secondSize.Y) * .5f + padding;
    }

    public static bool Overlaps(
        PlaceableWorldObjectDefinition first,
        Vector2 firstCenter,
        int firstRotation,
        PlaceableWorldObjectDefinition second,
        Vector2 secondCenter,
        int secondRotation,
        float padding = .08f) => PlacementOverlaps(
            first,
            firstCenter,
            firstRotation,
            second,
            secondCenter,
            secondRotation,
            padding);

    public static bool PlacementContains(
        PlaceableWorldObjectDefinition definition,
        Vector2 center,
        int rotation,
        Vector2 point,
        float padding = 0)
    {
        var size = PlacementFootprint(definition, rotation);
        return MathF.Abs(point.X - center.X) < size.X * .5f + padding &&
               MathF.Abs(point.Y - center.Y) < size.Y * .5f + padding;
    }

    public static bool Contains(
        PlaceableWorldObjectDefinition definition,
        Vector2 center,
        int rotation,
        Vector2 point,
        float padding = 0) => PlacementContains(
            definition, center, rotation, point, padding);

    public static IReadOnlyList<NavigationObstacle> CollisionObstacles(
        PlaceableWorldObjectDefinition definition,
        Vector2 storedCenter,
        int rotation,
        bool openGate = false)
    {
        if (GateCatalog.IsGate(definition.ItemId))
            return GateCollisionObstacles(
                definition, storedCenter, rotation, !openGate);
        if (WallCatalog.IsWall(definition.ItemId))
            return
            [
                WallCollisionObstacle(
                    GroundContactCenter(definition, storedCenter) +
                    new Vector2(
                        WorldPlacementGrid.CellSize,
                        WorldPlacementGrid.CellSize),
                    rotation)
            ];

        var contact = definition.GroundContact(rotation);
        return
        [
            new NavigationObstacle(
                GroundContactCenter(definition, storedCenter),
                contact.X,
                contact.Y)
        ];
    }

    public static NavigationObstacleBounds CollisionBounds(
        IReadOnlyList<NavigationObstacle> obstacles)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        if (obstacles.Count == 0)
            throw new ArgumentException(
                "At least one collision obstacle is required.",
                nameof(obstacles));
        var minimum = new Vector2(float.MaxValue, float.MaxValue);
        var maximum = new Vector2(float.MinValue, float.MinValue);
        foreach (var obstacle in obstacles)
        {
            var half = obstacle.AxisAlignedHalfExtents();
            minimum = Vector2.Min(minimum, obstacle.Center - half);
            maximum = Vector2.Max(maximum, obstacle.Center + half);
        }
        return new(minimum, maximum);
    }

    public static NavigationObstacleBounds CollisionBounds(
        PlaceableWorldObjectDefinition definition,
        Vector2 storedCenter,
        int rotation,
        bool openGate = false) => CollisionBounds(CollisionObstacles(
            definition, storedCenter, rotation, openGate));

    public static bool Overlaps(
        NavigationObstacle first,
        NavigationObstacle second,
        float padding = 0)
    {
        if (!float.IsFinite(padding) || padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding));
        var firstAxes = Axes(first.RotationRadians);
        var secondAxes = Axes(second.RotationRadians);
        var delta = second.Center - first.Center;
        foreach (var axis in new[]
                 {
                     firstAxes.X, firstAxes.Y,
                     secondAxes.X, secondAxes.Y
                 })
        {
            var distance = MathF.Abs(Vector2.Dot(delta, axis));
            var firstRadius = ProjectionRadius(first, firstAxes, axis);
            var secondRadius = ProjectionRadius(second, secondAxes, axis);
            if (distance >= firstRadius + secondRadius + padding)
                return false;
        }
        return true;
    }

    public static bool Overlaps(
        IReadOnlyList<NavigationObstacle> first,
        IReadOnlyList<NavigationObstacle> second,
        float padding = 0) => first.Any(firstObstacle =>
        second.Any(secondObstacle => Overlaps(
            firstObstacle, secondObstacle, padding)));

    private static Vector2 GroundContactCenter(
        PlaceableWorldObjectDefinition definition,
        Vector2 storedCenter)
    {
        var projectedFrontOffset =
            (definition.FootprintWidth + definition.FootprintDepth) * 12f;
        var contactHalfDepth =
            (definition.GroundContactWidth +
             definition.GroundContactDepth) * 12f;
        var forward = (projectedFrontOffset - contactHalfDepth) / 48f;
        return storedCenter + new Vector2(forward, forward);
    }

    private static NavigationObstacle WallCollisionObstacle(
        Vector2 center, int frame) => frame switch
        {
            3 => new(center, 1.42f, .5f, -MathF.PI * .25f),
            4 => new(center, 1.42f, .5f, MathF.PI * .25f),
            _ => new(center, 1f, 1f)
        };

    private static IReadOnlyList<NavigationObstacle> GateCollisionObstacles(
        PlaceableWorldObjectDefinition definition,
        Vector2 storedCenter,
        int rotation,
        bool includeMiddle)
    {
        rotation = definition.NormalizeRotation(rotation);
        var frontOffset =
            (definition.FootprintWidth + definition.FootprintDepth) * 12f /
            48f;
        var center = storedCenter + new Vector2(frontOffset, frontOffset);
        if (rotation is 2 or 3)
        {
            var vertical = rotation == 3;
            var axis = Vector2.Normalize(vertical
                ? new Vector2(1, 1)
                : new Vector2(1, -1));
            var middleFrame = vertical ? 4 : 3;
            var diagonal = new List<NavigationObstacle>(
                includeMiddle ? 4 : 2)
            {
                WallCollisionObstacle(center - axis * 1.5f, 2),
                WallCollisionObstacle(center + axis * 1.5f, 2)
            };
            if (includeMiddle)
            {
                diagonal.Insert(1, WallCollisionObstacle(
                    center - axis * .5f, middleFrame));
                diagonal.Insert(2, WallCollisionObstacle(
                    center + axis * .5f, middleFrame));
            }
            return diagonal;
        }

        var horizontal = rotation == 0;
        var axisDirection = horizontal ? Vector2.UnitX : Vector2.UnitY;
        var collisionRotation = horizontal ? 0 : MathF.PI * .5f;
        const float length = 4f;
        const float depth = 1f;
        const float endLength = 1f;
        const float endOffset = 1.5f;
        var result = new List<NavigationObstacle>(includeMiddle ? 3 : 2)
        {
            new(center - axisDirection * endOffset,
                endLength, depth, collisionRotation),
            new(center + axisDirection * endOffset,
                endLength, depth, collisionRotation)
        };
        if (includeMiddle)
            result.Add(new(center, length - endLength * 2,
                depth, collisionRotation));
        return result;
    }

    private static (Vector2 X, Vector2 Y) Axes(float rotation)
    {
        var cosine = MathF.Cos(rotation);
        var sine = MathF.Sin(rotation);
        return (new(cosine, sine), new(-sine, cosine));
    }

    private static float ProjectionRadius(
        NavigationObstacle obstacle,
        (Vector2 X, Vector2 Y) axes,
        Vector2 axis) =>
        obstacle.Width * .5f * MathF.Abs(Vector2.Dot(axes.X, axis)) +
        obstacle.Depth * .5f * MathF.Abs(Vector2.Dot(axes.Y, axis));

    public static bool IsSupportedTerrain(
        PlaceableWorldObjectDefinition definition,
        Vector2 center,
        int rotation,
        int worldLevel,
        IWorldNavigationQuery navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (!navigation.SupportsWorldLevel(worldLevel)) return false;
        var size = PlacementFootprint(definition, rotation);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
            !float.IsFinite(size.X) || !float.IsFinite(size.Y) ||
            size.X <= 0 || size.Y <= 0)
            return false;

        // Validate in double precision before converting client-authored
        // coordinates to integers. Extreme but finite floats otherwise wrap at
        // the inclusive loop bounds and can monopolize the authority thread.
        var minimumEdgeX = (double)center.X - size.X * .5d;
        var maximumEdgeX = (double)center.X + size.X * .5d;
        var minimumEdgeY = (double)center.Y - size.Y * .5d;
        var maximumEdgeY = (double)center.Y + size.Y * .5d;
        if (minimumEdgeX < ProceduralResourceIdentity.MinimumCoordinate ||
            minimumEdgeY < ProceduralResourceIdentity.MinimumCoordinate ||
            maximumEdgeX > ProceduralResourceIdentity.MaximumCoordinate ||
            maximumEdgeY > ProceduralResourceIdentity.MaximumCoordinate)
            return false;

        var minimumX = (int)Math.Floor(minimumEdgeX + .001d);
        var maximumX = (int)Math.Ceiling(maximumEdgeX - .001d) - 1;
        var minimumY = (int)Math.Floor(minimumEdgeY + .001d);
        var maximumY = (int)Math.Ceiling(maximumEdgeY - .001d) - 1;
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        var count = 0;
        for (var y = minimumY; y <= maximumY; y++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var sample = new Vector2(x + .5f, y + .5f);
            if (!navigation.CanStandAt(sample, worldLevel) ||
                navigation.IsWading(sample, worldLevel))
                return false;
            foreach (var vertex in new[]
                     {
                         new Vector2(x, y),
                         new Vector2(x + 1, y),
                         new Vector2(x + 1, y + 1),
                         new Vector2(x, y + 1)
                     })
            {
                var height = navigation.HeightAt(vertex, worldLevel);
                if (!float.IsFinite(height)) return false;
                lowest = Math.Min(lowest, height);
                highest = Math.Max(highest, height);
            }
            count++;
        }
        return count > 0 && highest - lowest <= 2f;
    }
}
