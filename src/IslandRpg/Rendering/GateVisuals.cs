namespace IslandRpg.Rendering;

using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class GateVisuals
{
    // Measured from the two tower bases in the authored GTAX foundation.
    // The Y component is essential: gates occupy the opposite isometric axis
    // from a screen-horizontal 3x1 building.
    private const int SideWallOffsetX = 70;
    private const int SideWallOffsetY = 35;

    public static IReadOnlyCollection<string> RequiredGraphics =>
        GateCatalog.RequiredGraphics;

    public static bool IsGateGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static string AtlasKey(string itemId) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#0";

    public static string OpenAtlasKey(string itemId) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#open";

    public static string ShadowAtlasKey(string itemId, bool open = false) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#" +
        (open ? "open-shadow" : "shadow");

    public static string Resolve(WorldGroundObject value)
    {
        if (ConstructionService.Stage(value) == ConstructionStage.Complete)
            return GateService.IsOpen(value)
                ? OpenAtlasKey(value.ItemId)
                : AtlasKey(value.ItemId);
        var gate = GateCatalog.Get(value.ItemId);
        var stage = ConstructionService.Stage(value);
        var frame = stage switch
        {
            ConstructionStage.Planned or ConstructionStage.Foundation => 0,
            ConstructionStage.Frame => 1,
            _ => 2
        };
        return $"{gate.ConstructionGraphicName}@" +
               $"{gate.ConstructionGraphicId}#{frame}";
    }

    public static string? ResolveShadow(WorldGroundObject value)
    {
        if (ConstructionService.Stage(value) == ConstructionStage.Complete)
            return ShadowAtlasKey(value.ItemId, GateService.IsOpen(value));
        var gate = GateCatalog.Get(value.ItemId);
        if (gate.ConstructionShadowGraphicId <= 0 ||
            gate.ConstructionShadowGraphicName is null)
            return null;
        var frame = ConstructionService.Stage(value) switch
        {
            ConstructionStage.Planned or ConstructionStage.Foundation => 0,
            ConstructionStage.Frame => 1,
            _ => 2
        };
        return $"{gate.ConstructionShadowGraphicName}@" +
               $"{gate.ConstructionShadowGraphicId}#{frame}";
    }

    public static IReadOnlyList<(string Key, SpriteFrame Frame)> CompositeFrames(
        IReadOnlyList<LoadedGraphic> assets)
    {
        var byId = assets.ToDictionary(
            value => value.Definition.GraphicId);
        var result = new List<(string, SpriteFrame)>();
        foreach (var gate in GateCatalog.All)
        {
            if (!byId.TryGetValue(gate.GateGraphicId, out var center))
                continue;
            var centerFrame = center.Sprite.Frames[0];
            SpriteFrame composite;
            if (gate.SideWallGraphicId > 0 &&
                byId.TryGetValue(gate.SideWallGraphicId, out var sideWall))
            {
                var sideWallFrame = sideWall.Sprite.Frames[0];
                // Bake the whole gate into one frame in isometric depth order:
                // far/top end, gate span, then near/bottom end.
                composite = SpriteCompositor.LayerFrames(
                    (sideWallFrame, SideWallOffsetX, -SideWallOffsetY),
                    (centerFrame, 0, 0),
                    (sideWallFrame, -SideWallOffsetX, SideWallOffsetY));
            }
            else composite = centerFrame;
            result.Add((AtlasKey(gate.ItemId), composite));
            if (gate.SideWallGraphicId > 0 &&
                byId.TryGetValue(gate.SideWallGraphicId, out var openSideWall))
            {
                result.Add((OpenAtlasKey(gate.ItemId),
                    SpriteCompositor.LayerFrames(
                        (openSideWall.Sprite.Frames[0],
                            SideWallOffsetX, -SideWallOffsetY),
                        (openSideWall.Sprite.Frames[0],
                            -SideWallOffsetX, SideWallOffsetY))));
            }
            else result.Add((OpenAtlasKey(gate.ItemId),
                new SpriteFrame(1, 1, 0, 0, new byte[4])));

            if (byId.TryGetValue(gate.GateShadowGraphicId, out var gateShadow))
            {
                var centerShadow = gateShadow.Sprite.Frames[0];
                if (gate.SideWallShadowGraphicId > 0 &&
                    byId.TryGetValue(
                        gate.SideWallShadowGraphicId, out var sideShadow))
                {
                    var side = sideShadow.Sprite.Frames[0];
                    result.Add((ShadowAtlasKey(gate.ItemId),
                        SpriteCompositor.LayerFrames(
                            (side, SideWallOffsetX, -SideWallOffsetY),
                            (centerShadow, 0, 0),
                            (side, -SideWallOffsetX, SideWallOffsetY))));
                    result.Add((ShadowAtlasKey(gate.ItemId, open: true),
                        SpriteCompositor.LayerFrames(
                            (side, SideWallOffsetX, -SideWallOffsetY),
                            (side, -SideWallOffsetX, SideWallOffsetY))));
                }
                else
                {
                    result.Add((ShadowAtlasKey(gate.ItemId), centerShadow));
                    result.Add((ShadowAtlasKey(gate.ItemId, open: true),
                        new SpriteFrame(1, 1, 0, 0, new byte[4])));
                }
            }
        }
        return result;
    }
}
