namespace IslandRpg.Rendering;

using IslandRpg.Gameplay;

internal static class HouseVisuals
{
    public static IReadOnlyCollection<string> RequiredGraphics =>
        HouseCatalog.RequiredGraphics;

    public static bool IsHouseGraphic(string graphicName) =>
        HouseCatalog.IsHouseGraphic(graphicName);

    public static string AtlasKey(string itemId)
    {
        var house = HouseCatalog.Get(itemId);
        return $"{house.GraphicName}@{house.GraphicId}#{house.Frame}";
    }
}
