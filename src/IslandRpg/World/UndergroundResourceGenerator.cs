using IslandRpg.Resources;

namespace IslandRpg.World;

internal static class UndergroundResourceGenerator
{
    public static readonly string[] RequiredGraphicNames =
    [
        "STONM_NN", "STONM_N0",
        "GOLDM_NN", "GOLDM_N0",
        "OREM_NN",
        "ROCKX_NN", "ROCK2_NN",
        "ROCKF1_NN", "ROCKF2_NN", "ROCKF3_NN",
        "SKEL_NN", "SKELA_NN", "RUINS_NN"
    ];

    public const string Coal = UndergroundMiningCatalog.CoalGraphic;
    public const string Tin = UndergroundMiningCatalog.TinGraphic;
    public const string Copper = UndergroundMiningCatalog.CopperGraphic;
    public const string Iron = UndergroundMiningCatalog.IronGraphic;
    public const string Growth = UndergroundMiningCatalog.GrowthGraphic;

    public static bool IsResourceGraphic(string name) =>
        name is "STONM_NN" or "OREM_NN" or
            "ROCKX_NN" or "ROCK2_NN" or
            "ROCKF1_NN" or "ROCKF2_NN" or "ROCKF3_NN" or
            "SKEL_NN" or "SKELA_NN" or "RUINS_NN" or
            Coal or Tin or Copper or Iron or Growth;

    public static string? ShadowGraphic(string name) => name switch
    {
        "STONM_NN" => "STONM_N0",
        Coal or Tin or Copper or Iron => "GOLDM_N0",
        _ => null
    };

    public static int VariantCount(string name) =>
        UndergroundMiningCatalog.VariantCount(name);

    public static WorldVegetation[] Generate(
        long seed,
        ChunkCoordinate coordinate,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable)
        => CaveFeaturePlacement.Generate(
            seed, coordinate, tiles, renderable);

    internal static float Hash(long seed, int x, int y, int salt) =>
        UndergroundMiningCatalog.UnitHash(seed, x, y, salt);
}
