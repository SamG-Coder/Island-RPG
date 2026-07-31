namespace IslandRpg.Rendering;

internal readonly record struct ItemSpriteSheetDefinition(
    string FileName,
    int CellCount,
    int CellSize = 32)
{
    public int Width => CellCount * CellSize;
    public int Height => CellSize;
}

internal static class ItemSpriteSheetCatalog
{
    public static readonly ItemSpriteSheetDefinition AdvancedTools =
        new("metal-tools-progression.png", 7);

    public static readonly ItemSpriteSheetDefinition FishingNetUpgrades =
        new("fishing-net-upgrades.png", 2);
}
