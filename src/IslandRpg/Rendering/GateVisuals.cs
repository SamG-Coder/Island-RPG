namespace IslandRpg.Rendering;

using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class GateVisuals
{
    private const int SideWallOffset = 70;

    public static IReadOnlyCollection<string> RequiredGraphics =>
        GateCatalog.RequiredGraphics;

    public static bool IsGateGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static string AtlasKey(string itemId) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#0";

    public static string OpenAtlasKey(string itemId) =>
        $"GATE@{GateCatalog.Get(itemId).GateGraphicId}#open";

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
                composite = SpriteCompositor.LayerFrames(
                    (sideWallFrame, -SideWallOffset, 0),
                    (centerFrame, 0, 0),
                    (sideWallFrame, SideWallOffset, 0));
            }
            else composite = centerFrame;
            result.Add((AtlasKey(gate.ItemId), composite));
            if (gate.SideWallGraphicId > 0 &&
                byId.TryGetValue(gate.SideWallGraphicId, out var openSideWall))
            {
                result.Add((OpenAtlasKey(gate.ItemId),
                    SpriteCompositor.LayerFrames(
                        (openSideWall.Sprite.Frames[0], -SideWallOffset, 0),
                        (openSideWall.Sprite.Frames[0], SideWallOffset, 0))));
            }
            else result.Add((OpenAtlasKey(gate.ItemId),
                new SpriteFrame(1, 1, 0, 0, new byte[4])));
        }
        return result;
    }
}
