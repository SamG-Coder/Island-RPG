namespace IslandRpg.Rendering;

using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class DefenceBuildingVisuals
{
    public static IReadOnlyCollection<string> RequiredGraphics =>
        DefenceBuildingCatalog.RequiredGraphics;

    public static bool IsDefenceGraphic(string graphicName) =>
        DefenceBuildingCatalog.IsDefenceGraphic(graphicName);

    public static string AtlasKey(string itemId)
    {
        var value = DefenceBuildingCatalog.Get(itemId);
        return $"{value.GraphicName}@{value.GraphicId}#0";
    }

    public static string Resolve(WorldGroundObject value)
    {
        if (ConstructionService.Stage(value) == ConstructionStage.Complete)
            return AtlasKey(value.ItemId);
        var definition = DefenceBuildingCatalog.Get(value.ItemId);
        var construction = ConstructionVisualCatalog.All.Single(candidate =>
            candidate.GraphicName.Equals(
                definition.ConstructionGraphicName,
                StringComparison.OrdinalIgnoreCase));
        var stage = ConstructionService.Stage(value);
        return construction.AtlasKey(stage is
            ConstructionStage.Planned or ConstructionStage.Foundation ? 0 : 2);
    }
}
