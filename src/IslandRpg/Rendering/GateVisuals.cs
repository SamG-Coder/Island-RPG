namespace IslandRpg.Rendering;

using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class GateVisuals
{
    private readonly record struct PixelPoint(float X, float Y);
    private readonly record struct GateAnchors(
        (int X, int Y) Far, (int X, int Y) Near);

    public static IReadOnlyCollection<string> RequiredGraphics =>
        GateCatalog.RequiredGraphics;

    public static bool IsGateGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static string AtlasKey(string itemId, int rotation = 0) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#" +
        (Normalize(rotation) == 0 ? "0" : $"r{Normalize(rotation)}");

    public static string OpenAtlasKey(string itemId, int rotation = 0) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#" +
        (Normalize(rotation) == 0 ? "open" : $"r{Normalize(rotation)}-open");

    public static string ShadowAtlasKey(
        string itemId, bool open = false, int rotation = 0) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#" +
        (Normalize(rotation) == 0
            ? open ? "open-shadow" : "shadow"
            : $"r{Normalize(rotation)}-" +
              (open ? "open-shadow" : "shadow"));

    public static string Resolve(WorldGroundObject value)
    {
        if (ConstructionService.Stage(value) == ConstructionStage.Complete)
            return GateService.IsOpen(value)
                ? OpenAtlasKey(value.ItemId, value.VisualFrame)
                : AtlasKey(value.ItemId, value.VisualFrame);
        var gate = GateCatalog.Get(value.ItemId);
        var orientation = GateCatalog.Orientation(gate, value.VisualFrame);
        var stage = ConstructionService.Stage(value);
        var frame = stage switch
        {
            ConstructionStage.Planned or ConstructionStage.Foundation => 0,
            ConstructionStage.Frame => 1,
            _ => 2
        };
        return $"{orientation.ConstructionGraphicName}@" +
               $"{orientation.ConstructionGraphicId}#{frame}";
    }

    public static string? ResolveShadow(WorldGroundObject value)
    {
        if (ConstructionService.Stage(value) == ConstructionStage.Complete)
            return ShadowAtlasKey(
                value.ItemId, GateService.IsOpen(value), value.VisualFrame);
        var gate = GateCatalog.Get(value.ItemId);
        var orientation = GateCatalog.Orientation(gate, value.VisualFrame);
        if (orientation.ConstructionShadowGraphicId <= 0 ||
            orientation.ConstructionShadowGraphicName is null)
            return null;
        var frame = ConstructionService.Stage(value) switch
        {
            ConstructionStage.Planned or ConstructionStage.Foundation => 0,
            ConstructionStage.Frame => 1,
            _ => 2
        };
        return $"{orientation.ConstructionShadowGraphicName}@" +
               $"{orientation.ConstructionShadowGraphicId}#{frame}";
    }

    public static IReadOnlyList<(string Key, SpriteFrame Frame)> CompositeFrames(
        IReadOnlyList<LoadedGraphic> assets)
    {
        var byId = assets.ToDictionary(
            value => value.Definition.GraphicId);
        var result = new List<(string, SpriteFrame)>();
        foreach (var gate in GateCatalog.All)
        foreach (var rotation in Enumerable.Range(0, 4))
        {
            var orientation = GateCatalog.Orientation(gate, rotation);
            if (!byId.TryGetValue(orientation.GateGraphicId, out var center))
                continue;
            var centerFrame = center.Sprite.Frames[0];
            var openCenterFrame = byId.TryGetValue(
                    orientation.OpenGateGraphicId, out var openCenter)
                ? openCenter.Sprite.Frames[0]
                : centerFrame;
            SpriteFrame composite;
            if (orientation.SideWallGraphicId > 0 &&
                byId.TryGetValue(orientation.SideWallGraphicId, out var sideWall))
            {
                var sideWallFrame = sideWall.Sprite.Frames[0];
                var anchors = byId.TryGetValue(
                        orientation.ConstructionGraphicId,
                        out var construction)
                    ? FoundationAnchors(
                        construction.Sprite.Frames[0], sideWallFrame)
                    : FallbackAnchors(rotation);
                // Bake the whole gate into one frame in isometric depth order:
                // far/top end, gate span, then near/bottom end.
                composite = SpriteCompositor.LayerFrames(
                    (sideWallFrame, anchors.Far.X, anchors.Far.Y),
                    (centerFrame, 0, 0),
                    (sideWallFrame, anchors.Near.X, anchors.Near.Y));
            }
            else composite = centerFrame;
            result.Add((AtlasKey(gate.ItemId, rotation), composite));
            if (orientation.SideWallGraphicId > 0 &&
                byId.TryGetValue(orientation.SideWallGraphicId, out var openSideWall))
            {
                var sideFrame = openSideWall.Sprite.Frames[0];
                var anchors = byId.TryGetValue(
                        orientation.ConstructionGraphicId,
                        out var construction)
                    ? FoundationAnchors(
                        construction.Sprite.Frames[0], sideFrame)
                    : FallbackAnchors(rotation);
                result.Add((OpenAtlasKey(gate.ItemId, rotation),
                    SpriteCompositor.LayerFrames(
                        (sideFrame, anchors.Far.X, anchors.Far.Y),
                        (openCenterFrame, 0, 0),
                        (sideFrame, anchors.Near.X, anchors.Near.Y))));
            }
            else result.Add((OpenAtlasKey(gate.ItemId, rotation), openCenterFrame));

            if (byId.TryGetValue(orientation.GateShadowGraphicId, out var gateShadow))
            {
                var centerShadow = gateShadow.Sprite.Frames[0];
                if (orientation.SideWallShadowGraphicId > 0 &&
                    byId.TryGetValue(
                        orientation.SideWallShadowGraphicId, out var sideShadow))
                {
                    var side = sideShadow.Sprite.Frames[0];
                    var normalSide = byId[orientation.SideWallGraphicId]
                        .Sprite.Frames[0];
                    var anchors = byId.TryGetValue(
                            orientation.ConstructionGraphicId,
                            out var construction)
                        ? FoundationAnchors(
                            construction.Sprite.Frames[0], normalSide)
                        : FallbackAnchors(rotation);
                    result.Add((ShadowAtlasKey(gate.ItemId, rotation: rotation),
                        SpriteCompositor.LayerFrames(
                            (side, anchors.Far.X, anchors.Far.Y),
                            (centerShadow, 0, 0),
                            (side, anchors.Near.X, anchors.Near.Y))));
                    result.Add((ShadowAtlasKey(
                        gate.ItemId, open: true, rotation: rotation),
                        SpriteCompositor.LayerFrames(
                            (side, anchors.Far.X, anchors.Far.Y),
                            (byId.TryGetValue(
                                    orientation.OpenGateShadowGraphicId,
                                    out var openGateShadow)
                                ? openGateShadow.Sprite.Frames[0]
                                : centerShadow, 0, 0),
                            (side, anchors.Near.X, anchors.Near.Y))));
                }
                else
                {
                    result.Add((ShadowAtlasKey(gate.ItemId, rotation: rotation), centerShadow));
                    result.Add((ShadowAtlasKey(
                        gate.ItemId, open: true, rotation: rotation),
                        byId.TryGetValue(
                            orientation.OpenGateShadowGraphicId,
                            out var openGateShadow)
                            ? openGateShadow.Sprite.Frames[0]
                            : centerShadow));
                }
            }
        }
        return result;
    }

    private static int Normalize(int rotation) =>
        rotation < 0 ? 0 : rotation % 4;

    private static GateAnchors FoundationAnchors(
        SpriteFrame foundation, SpriteFrame tower)
    {
        var towerBand = TeamBandPoints(tower);
        var foundationBand = TeamBandPoints(foundation);
        if (towerBand.Count == 0 || foundationBand.Count < 2)
            return FallbackAnchors(0);

        var source = Centroid(towerBand);
        var (first, second) = SplitIntoTwoClusters(foundationBand);
        var firstOffset = Offset(first, foundation, source, tower);
        var secondOffset = Offset(second, foundation, source, tower);
        return firstOffset.Y < secondOffset.Y ||
               firstOffset.Y == secondOffset.Y && firstOffset.X <= secondOffset.X
            ? new(firstOffset, secondOffset)
            : new(secondOffset, firstOffset);
    }

    private static List<PixelPoint> TeamBandPoints(SpriteFrame frame)
    {
        var result = new List<PixelPoint>();
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var index = (y * frame.Width + x) * 4;
            if (frame.Rgba[index + 3] < 48) continue;
            var red = frame.Rgba[index];
            var green = frame.Rgba[index + 1];
            var blue = frame.Rgba[index + 2];
            if (blue > 70 && blue > red * 1.25f && blue > green * 1.18f)
                result.Add(new(x, y));
        }
        return result;
    }

    private static (PixelPoint First, PixelPoint Second) SplitIntoTwoClusters(
        IReadOnlyList<PixelPoint> points)
    {
        var minX = points.MinBy(value => value.X);
        var maxX = points.MaxBy(value => value.X);
        var minY = points.MinBy(value => value.Y);
        var maxY = points.MaxBy(value => value.Y);
        var horizontal = maxX.X - minX.X >= maxY.Y - minY.Y;
        var first = horizontal ? minX : minY;
        var second = horizontal ? maxX : maxY;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var firstPoints = new List<PixelPoint>();
            var secondPoints = new List<PixelPoint>();
            foreach (var point in points)
            {
                var firstDistance = DistanceSquared(point, first);
                var secondDistance = DistanceSquared(point, second);
                (firstDistance <= secondDistance ? firstPoints : secondPoints)
                    .Add(point);
            }
            if (firstPoints.Count > 0) first = Centroid(firstPoints);
            if (secondPoints.Count > 0) second = Centroid(secondPoints);
        }
        return (first, second);
    }

    private static PixelPoint Centroid(IReadOnlyList<PixelPoint> points) =>
        new(points.Average(value => value.X), points.Average(value => value.Y));

    private static float DistanceSquared(PixelPoint left, PixelPoint right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return x * x + y * y;
    }

    private static (int X, int Y) Offset(
        PixelPoint target, SpriteFrame foundation,
        PixelPoint source, SpriteFrame tower) =>
        ((int)MathF.Round(
             target.X - foundation.HotspotX -
             (source.X - tower.HotspotX)),
         (int)MathF.Round(
             target.Y - foundation.HotspotY -
             (source.Y - tower.HotspotY)));

    private static GateAnchors FallbackAnchors(int rotation)
    {
        const int x = 70;
        const int y = 35;
        var first = Normalize(rotation) switch
        {
            1 => (-x, -y),
            2 => (x, -y),
            3 => (x, y),
            _ => (x, -y)
        };
        return new(first, (-first.Item1, -first.Item2));
    }
}
