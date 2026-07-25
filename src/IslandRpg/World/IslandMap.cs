namespace IslandRpg.World;

internal enum Biome
{
    DeepWater, ShallowWater, RiverWater, MangroveShallows,
    Beach, Grassland, DryGrass, Mud,
    Forest, JungleFloor, Highland, Rock,
    Tundra, Snow, DesertSand, CrackedEarth
}
internal enum WorldBiome
{
    Ocean, Coast, River, Wetland, TemperateGrassland, TemperateForest,
    Rainforest, Savanna, Desert, Taiga, Tundra, Alpine
}

internal sealed record IslandTile(
    int X, int Y, Biome Biome, byte North, byte East, byte South, byte West,
    WorldBiome Region = WorldBiome.Ocean);
internal sealed record IslandTree(
    int X, int Y, string GraphicName, int FrameIndex = 0);

internal sealed class IslandMap
{
    public const int Size = 120; // Classic "normal" random-map scale.
    public required IReadOnlyList<IslandTile> Tiles { get; init; }
    public required IReadOnlyList<IslandTree> Trees { get; init; }
}

internal static class IslandGenerator
{
    public static IslandMap Generate(int seed = 2187)
    {
        var random = new Random(seed);
        var heights = new byte[IslandMap.Size + 1, IslandMap.Size + 1];
        for (var y = 0; y <= IslandMap.Size; y++)
        for (var x = 0; x <= IslandMap.Size; x++)
        {
            var nx = (x - IslandMap.Size / 2f) / (IslandMap.Size * .43f);
            var ny = (y - IslandMap.Size / 2f) / (IslandMap.Size * .39f);
            var warp = MathF.Sin(x * .17f + seed) * .055f + MathF.Sin(y * .113f) * .045f +
                       MathF.Sin((x + y) * .071f) * .04f;
            var land = 1f - MathF.Sqrt(nx * nx + ny * ny) + warp;
            heights[x, y] = (byte)Math.Clamp((int)MathF.Floor((land - .06f) * 8f), 0, 6);
        }
        var surfaceHeights = (byte[,])heights.Clone();
        for (var y = 0; y <= IslandMap.Size; y++)
        for (var x = 0; x <= IslandMap.Size; x++)
            if (surfaceHeights[x, y] <= 2) surfaceHeights[x, y] = 0;

        var tiles = new List<IslandTile>(IslandMap.Size * IslandMap.Size);
        var trees = new List<IslandTree>();
        for (var y = 0; y < IslandMap.Size; y++)
        for (var x = 0; x < IslandMap.Size; x++)
        {
            var average = (heights[x, y] + heights[x + 1, y] +
                           heights[x + 1, y + 1] + heights[x, y + 1]) / 4f;
            var n = surfaceHeights[x, y];
            var e = surfaceHeights[x + 1, y];
            var s = surfaceHeights[x + 1, y + 1];
            var w = surfaceHeights[x, y + 1];
            var moisture = (MathF.Sin(x * .29f) + MathF.Cos(y * .23f) + MathF.Sin((x-y)*.11f)) / 3f;
            var biome = average switch
            {
                < .62f => Biome.DeepWater,
                < .85f => Biome.ShallowWater,
                < 1.45f => Biome.Beach,
                > 4.8f => Biome.Rock,
                > 3.6f => Biome.Highland,
                _ when moisture > -.05f => Biome.Forest,
                _ => Biome.Grassland
            };
            tiles.Add(new(x, y, biome, n, e, s, w));

            var chance = biome == Biome.Forest ? .22 : biome == Biome.Highland ? .07 : biome == Biome.Beach ? .018 : 0;
            if (random.NextDouble() < chance)
            {
                var graphic = biome switch
                {
                    Biome.Beach => "FPAL_NN",
                    Biome.Highland => "FPIN_NN",
                    _ => $"TREE{(char)('A' + random.Next(0, 12))}_NN"
                };
                trees.Add(new(x, y, graphic));
            }
        }
        return new() { Tiles = tiles, Trees = trees };
    }
}
