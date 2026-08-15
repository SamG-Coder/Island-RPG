namespace IslandRpg.Rendering;

internal readonly record struct ItemSpriteSheetDefinition(
    string FileName,
    int CellCount,
    int CellSize = 32,
    int Columns = 0)
{
    public int EffectiveColumns => Columns > 0 ? Columns : CellCount;
    public int Width => EffectiveColumns * CellSize;
    public int Height =>
        (int)Math.Ceiling(CellCount / (double)EffectiveColumns) * CellSize;
}

internal static class ItemSpriteSheetCatalog
{
    public static readonly ItemSpriteSheetDefinition AdvancedTools =
        new("metal-tools-progression.png", 7);

    public static readonly ItemSpriteSheetDefinition FishingNetUpgrades =
        new("fishing-net-upgrades.png", 2);

    public static readonly ItemSpriteSheetDefinition PersonalGoals =
        new("personal-goals-items.png", 10, Columns: 5);
    public static readonly ItemSpriteSheetDefinition Crops =
        new("planted-crops.png", 3);
    public static readonly ItemSpriteSheetDefinition SlimeLoot =
        new("slime-loot-items.png", 4);
    public static readonly ItemSpriteSheetDefinition SlimeCrafted =
        new("slime-crafted-items.png", 2);
    public static readonly ItemSpriteSheetDefinition Buckets =
        new("bucket-items.png", 3);
}
