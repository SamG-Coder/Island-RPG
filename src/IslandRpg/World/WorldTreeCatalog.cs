namespace IslandRpg.World;

internal static class WorldTreeCatalog
{
    private static readonly IReadOnlyDictionary<string, int> VariantCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["FPAL_NN"] = 13,
            ["FPIN_NN"] = 9,
            ["FOAK_NN"] = 14,
            ["FJUN_NN"] = 13,
            ["FSNO_NN"] = 9,
            ["FBAM_NN"] = 4,
            ["FCAC_NN"] = 6
        };

    public static bool HasVariants(string graphicName) =>
        VariantCounts.ContainsKey(graphicName) ||
        VariantCounts.ContainsKey(VisibleName(graphicName));

    public static float SpawnChance(WorldBiome region, float elevation)
    {
        var chance = region switch
        {
            WorldBiome.Rainforest => .31f,
            WorldBiome.TemperateForest => .23f,
            WorldBiome.Taiga => .19f,
            WorldBiome.Wetland => .13f,
            WorldBiome.Savanna => .065f,
            WorldBiome.Alpine => .045f,
            WorldBiome.Coast => .012f,
            WorldBiome.Tundra => .025f,
            WorldBiome.Desert => .009f,
            _ => 0
        };
        return region == WorldBiome.Alpine
            ? chance * Math.Clamp((12f - elevation) / 4f, 0, 1)
            : chance;
    }

    public static int FrameCount(string graphicName) =>
        VariantCounts.GetValueOrDefault(VisibleName(graphicName), 1);

    public static int SelectFrame(
        long seed, int x, int y, string graphicName)
    {
        var count = FrameCount(graphicName);
        if (count == 1) return 0;
        return (int)(Hash(seed, x, y, 3137) * count) % count;
    }

    public static string SelectGraphic(
        long seed, IslandTile tile)
    {
        var roll = Hash(seed, tile.X, tile.Y, 137);
        var generic = GenericTree(
            Hash(seed, tile.X, tile.Y, 149));
        return tile.Region switch
        {
            WorldBiome.Coast => "FPAL_NN",
            WorldBiome.Savanna => roll < .62f
                ? "FPAL_NN"
                : generic,
            WorldBiome.Rainforest => roll switch
            {
                < .72f => "FJUN_NN",
                < .88f => "FBAM_NN",
                _ => generic
            },
            WorldBiome.TemperateForest => roll < .72f
                ? "FOAK_NN"
                : generic,
            WorldBiome.Wetland => roll switch
            {
                < .55f => "FBAM_NN",
                < .82f => "FJUN_NN",
                _ => generic
            },
            WorldBiome.Taiga => "FPIN_NN",
            WorldBiome.Tundra => "FSNO_NN",
            WorldBiome.Alpine => tile.Biome == Biome.Snow
                ? "FSNO_NN"
                : "FPIN_NN",
            WorldBiome.Desert => "FCAC_NN",
            _ => generic
        };
    }

    public static string AtlasKey(IslandTree tree) =>
        AtlasKey(tree.GraphicName, tree.FrameIndex);

    public static string AtlasKey(string graphicName, int frameIndex) =>
        FrameCount(graphicName) > 1
            ? $"{graphicName}#{Math.Clamp(frameIndex, 0, FrameCount(graphicName) - 1)}"
            : graphicName;

    private static string VisibleName(string graphicName) =>
        graphicName.EndsWith("_N0", StringComparison.OrdinalIgnoreCase)
            ? graphicName[..^2] + "NN"
            : graphicName;

    private static string GenericTree(float roll)
    {
        var variant = Math.Min((int)(roll * 12), 11);
        return $"TREE{(char)('A' + variant)}_NN";
    }

    private static float Hash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)seed ^
                        (ulong)(long)x * 0x9e3779b185ebca87UL ^
                        (ulong)(long)y * 0xc2b2ae3d27d4eb4fUL ^
                        (uint)salt;
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (value >> 40) / 16777216f;
        }
    }
}
