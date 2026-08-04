namespace IslandRpg.Rendering;

internal enum ConstructionFootprintClass
{
    SingleTile,
    House,
    MediumBuilding,
    LargeBuilding,
    VeryLargeBuilding,
    Monument,
    Waterfront
}

internal sealed record ConstructionVisualDefinition(
    string GraphicName,
    short GraphicId,
    int SlpId,
    int FootprintWidth,
    int FootprintDepth,
    ConstructionFootprintClass Classification)
{
    public const int FrameCount = 3;

    public string AtlasKey(int frame) =>
        $"{GraphicName}@{GraphicId}#{Math.Clamp(frame, 0, FrameCount - 1)}";
}

internal static class ConstructionVisualCatalog
{
    public static readonly IReadOnlyList<ConstructionVisualDefinition> All =
    [
        new("CNST1_NN", 118, 236, 1, 1,
            ConstructionFootprintClass.SingleTile),
        new("CNST2_NN", 119, 237, 2, 2,
            ConstructionFootprintClass.House),
        new("CNST3_NN", 120, 238, 3, 3,
            ConstructionFootprintClass.MediumBuilding),
        new("CNST4_NN", 121, 239, 4, 4,
            ConstructionFootprintClass.LargeBuilding),
        new("CNST8_NN", 123, 241, 5, 5,
            ConstructionFootprintClass.VeryLargeBuilding),
        new("CNST12_NN", 124, 243, 8, 8,
            ConstructionFootprintClass.Monument),
        new("CNSTD_NN", 4248, 4397, 3, 3,
            ConstructionFootprintClass.Waterfront)
    ];

    private static readonly IReadOnlyDictionary<string,
        ConstructionVisualDefinition> ByName = All.ToDictionary(
            value => value.GraphicName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> RequiredGraphics =>
        All.Select(value => value.GraphicName).ToArray();

    public static bool IsConstructionGraphic(string graphicName) =>
        ByName.ContainsKey(graphicName);

    public static ConstructionVisualDefinition ForFootprint(
        int width, int depth, bool waterfront = false)
    {
        if (waterfront)
            return All.Single(value =>
                value.Classification == ConstructionFootprintClass.Waterfront);

        var squareSize = Math.Max(width, depth);
        return All.Where(value =>
                value.Classification != ConstructionFootprintClass.Waterfront)
            .OrderBy(value => Math.Abs(value.FootprintWidth - squareSize))
            .ThenBy(value => value.FootprintWidth)
            .First();
    }
}
