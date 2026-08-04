namespace IslandRpg.Rendering;

using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

internal static class GateVisuals
{
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
        (Normalize(rotation) == 0
            ? "open"
            : $"r{Normalize(rotation)}-open");

    public static string ShadowAtlasKey(
        string itemId, bool open = false, int rotation = 0)
    {
        var gate = GateCatalog.Get(itemId);
        return $"GATE@{gate.GateGraphicId}#" +
               (Normalize(rotation) == 0
                   ? open ? "open-shadow" : "shadow"
                   : $"r{Normalize(rotation)}-" +
                     (open ? "open-shadow" : "shadow"));
    }

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
                var anchors = MetadataAnchors(gate, rotation);
                // Bake the whole gate into one frame in isometric depth order:
                // far/top end, gate span, then near/bottom end.
                composite = LayerGate(
                    centerFrame, sideWallFrame, anchors,
                    centerFirst: Normalize(rotation) == 2);
            }
            else composite = centerFrame;
            result.Add((AtlasKey(gate.ItemId, rotation), composite));
            if (orientation.SideWallGraphicId > 0 &&
                byId.TryGetValue(orientation.SideWallGraphicId, out var openSideWall))
            {
                var sideFrame = openSideWall.Sprite.Frames[0];
                var anchors = MetadataAnchors(gate, rotation);
                result.Add((OpenAtlasKey(gate.ItemId, rotation),
                    LayerGate(
                        openCenterFrame, sideFrame, anchors,
                        centerFirst: Normalize(rotation) == 2)));
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
                    var anchors = MetadataAnchors(gate, rotation);
                    result.Add((ShadowAtlasKey(gate.ItemId, rotation: rotation),
                        LayerGate(
                            centerShadow, side, anchors,
                            centerFirst: Normalize(rotation) == 2)));
                    result.Add((ShadowAtlasKey(
                        gate.ItemId, open: true, rotation: rotation),
                        LayerGate(
                            byId.TryGetValue(
                                    orientation.OpenGateShadowGraphicId,
                                    out var openGateShadow)
                                ? openGateShadow.Sprite.Frames[0]
                                : centerShadow,
                            side, anchors,
                            centerFirst: Normalize(rotation) == 2)));
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

    private static SpriteFrame LayerGate(
        SpriteFrame center,
        SpriteFrame side,
        GateAnchors anchors,
        bool centerFirst) =>
        centerFirst
            ? SpriteCompositor.LayerFrames(
                (center, 0, 0),
                (side, anchors.Far.X, anchors.Far.Y),
                (side, anchors.Near.X, anchors.Near.Y))
            : SpriteCompositor.LayerFrames(
                (side, anchors.Far.X, anchors.Far.Y),
                (center, 0, 0),
                (side, anchors.Near.X, anchors.Near.Y));

    private static int Normalize(int rotation) =>
        rotation < 0 ? 0 : rotation % 4;

    private static GateAnchors MetadataAnchors(
        GateDefinition gate, int rotation)
    {
        var geometry = GateCatalog.Geometry(gate, rotation);
        var annexes = geometry.AnnexOffsets;
        Vector2 first;
        Vector2 second;
        if (annexes.Count >= 2)
        {
            first = annexes[0];
            second = annexes[1];
        }
        else
        {
            // GTC/GTD are authored as diagonal square-clearance units and
            // contain no annex records. Derive their two ends from that DAT
            // clearance and orientation; this remains world metadata and
            // does not inspect or threshold any sprite pixels.
            var axis = Normalize(rotation) switch
            {
                2 => new Vector2(1, -1),
                3 => new Vector2(1, 1),
                1 => Vector2.UnitY,
                _ => Vector2.UnitX
            };
            axis.Normalize();
            var halfSpan = Math.Max(
                WorldPlacementGrid.CellSize,
                Math.Max(
                    geometry.PlacementClearance.X,
                    geometry.PlacementClearance.Y) - .5f);
            first = -axis * halfSpan;
            second = axis * halfSpan;
        }
        var firstOffset = ProjectAnnex(first);
        var secondOffset = ProjectAnnex(second);
        return firstOffset.Y < secondOffset.Y ||
               firstOffset.Y == secondOffset.Y && firstOffset.X <= secondOffset.X
            ? new(firstOffset, secondOffset)
            : new(secondOffset, firstOffset);
    }

    private static (int X, int Y) ProjectAnnex(Vector2 offset)
    {
        var projected = IsometricTerrainProjection.Project(
            offset.X, offset.Y, 0);
        return (
            (int)MathF.Round(projected.X),
            (int)MathF.Round(projected.Y));
    }

}
