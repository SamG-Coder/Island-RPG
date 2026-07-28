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

    public const string Coal = "CAVE_ORE_COAL";
    public const string Tin = "CAVE_ORE_TIN";
    public const string Copper = "CAVE_ORE_COPPER";
    public const string Iron = "CAVE_ORE_IRON";
    public const string Growth = "CAVE_GROWTH";

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

    public static int VariantCount(string name) => name switch
    {
        "STONM_NN" or "OREM_NN" or Coal or Tin or Copper or Iron => 7,
        "ROCKX_NN" or "ROCK2_NN" => 6,
        "ROCKF1_NN" => 4,
        "ROCKF2_NN" or "SKELA_NN" => 2,
        "ROCKF3_NN" => 1,
        "SKEL_NN" => 15,
        "RUINS_NN" => 3,
        Growth => 10,
        _ => 0
    };

    public static WorldVegetation[] Generate(
        long seed,
        ChunkCoordinate coordinate,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable)
        => CaveFeaturePlacement.Generate(
            seed, coordinate, tiles, renderable);

    internal static float Hash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)(seed + salt);
            value ^= (ulong)(long)x * 0x9E3779B185EBCA87UL;
            value ^= (ulong)(long)y * 0xC2B2AE3D27D4EB4FUL;
            value ^= value >> 29;
            value *= 0x165667B19E3779F9UL;
            value ^= value >> 32;
            return (value & 0xFFFFFF) / 16777216f;
        }
    }
}
