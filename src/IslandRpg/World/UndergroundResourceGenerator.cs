namespace IslandRpg.World;

internal static class UndergroundResourceGenerator
{
    public static readonly string[] RequiredGraphicNames =
    [
        "STONM_NN", "STONM_N0",
        "GOLDM_NN", "GOLDM_N0",
        "ROCKX_NN"
    ];

    public const string Coal = "CAVE_ORE_COAL";
    public const string Tin = "CAVE_ORE_TIN";
    public const string Copper = "CAVE_ORE_COPPER";
    public const string Iron = "CAVE_ORE_IRON";

    public static bool IsResourceGraphic(string name) =>
        name is "STONM_NN" or "ROCKX_NN" or
            Coal or Tin or Copper or Iron;

    public static string? ShadowGraphic(string name) => name switch
    {
        "STONM_NN" => "STONM_N0",
        Coal or Tin or Copper or Iron => "GOLDM_N0",
        _ => null
    };

    public static WorldVegetation[] Generate(
        long seed,
        ChunkCoordinate coordinate,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable,
        IReadOnlyList<float> density)
    {
        const int maximumNodes = 6;
        var result = new List<WorldVegetation>(maximumNodes);
        foreach (var tile in tiles)
        {
            if (result.Count >= maximumNodes) break;
            var localX = tile.X - coordinate.X * WorldChunk.Size;
            var localY = tile.Y - coordinate.Y * WorldChunk.Size;
            if (!renderable[localY * WorldChunk.Size + localX] ||
                tile.Biome is Biome.ShallowWater or Biome.RiverWater)
                continue;
            var roll = Hash(seed, tile.X, tile.Y, 9049);
            if (roll > .012f) continue;
            if (!HasClearFloor(localX, localY, density))
                continue;
            var kind = Hash(seed, tile.X, tile.Y, 12161);
            var graphic = kind switch
            {
                < .20f => "STONM_NN",
                < .36f => Coal,
                < .51f => Tin,
                < .66f => Copper,
                < .81f => Iron,
                _ => "ROCKX_NN"
            };
            result.Add(new(
                tile.X + .5f,
                tile.Y + .5f,
                graphic,
                0,
                WorldVegetationKind.Shrub,
                false));
        }
        return result.ToArray();
    }

    private static bool HasClearFloor(
        int x,
        int y,
        IReadOnlyList<float> density)
    {
        const float margin = .08f;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        for (var offsetX = -1; offsetX <= 1; offsetX++)
        {
            var sampleX = (x + offsetX) *
                UndergroundWorldGenerator.SamplesPerTile + 2;
            var sampleY = (y + offsetY) *
                UndergroundWorldGenerator.SamplesPerTile + 2;
            if (sampleX < 0 || sampleY < 0 ||
                sampleX >= UndergroundWorldGenerator.DensityStride ||
                sampleY >= UndergroundWorldGenerator.DensityStride ||
                density[
                    sampleY * UndergroundWorldGenerator.DensityStride +
                    sampleX] <
                CaveHydrologyField.Boundary + margin)
                return false;
        }
        return true;
    }

    private static float Hash(long seed, int x, int y, int salt)
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
