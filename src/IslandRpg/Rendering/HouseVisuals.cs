namespace IslandRpg.Rendering;

using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class HouseVisuals
{
    private static readonly ConstructionVisualDefinition Construction =
        ConstructionVisualCatalog.ForFootprint(2, 2);

    public static IReadOnlyCollection<string> RequiredGraphics =>
        HouseCatalog.RequiredGraphics
            .Concat(ConstructionVisualCatalog.RequiredGraphics)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsHouseGraphic(string graphicName) =>
        HouseCatalog.IsHouseGraphic(graphicName) ||
        ConstructionVisualCatalog.IsConstructionGraphic(graphicName);

    public static string AtlasKey(string itemId)
    {
        var house = HouseCatalog.Get(itemId);
        return $"{house.GraphicName}@{house.GraphicId}#{house.Frame}";
    }

    public static string Resolve(WorldGroundObject value)
    {
        var stage = ConstructionService.Stage(value);
        if (stage == ConstructionStage.Complete)
            return AtlasKey(value.ItemId);

        // AoE supplies three authored angles/stages. Houses deliberately use
        // two readable construction phases: the sparse footprint and the
        // raised worksite shown immediately before the finished house.
        var constructionFrame = stage is
            ConstructionStage.Planned or ConstructionStage.Foundation
                ? 0
                : 2;
        return Construction.AtlasKey(constructionFrame);
    }
}
