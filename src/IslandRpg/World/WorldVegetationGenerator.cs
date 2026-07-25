namespace IslandRpg.World;

internal static class WorldVegetationGenerator
{
    public static readonly string[] RequiredGraphicNames =
    [
        "PLANTS",
        "BUSH_NN", "BUSH_N0",
        "BUSH2_NN", "BUSH2_N0",
        "BUSH3_NN", "BUSH3_N0",
        "FORAG_NN", "FORAGM_NN"
    ];

    public static bool IsVegetationGraphic(string name) =>
        name.Equals("PLANTS", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("BUSH", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FORAG", StringComparison.OrdinalIgnoreCase);

    private sealed record VegetationProfile(
        string GraphicName,
        int FrameCount,
        WorldVegetationKind Kind,
        bool CanBecomeInstance,
        float PatchScale,
        Func<WorldBiome, float> HabitatChance);

    private static readonly VegetationProfile[] Profiles =
    [
        new(
            "PLANTS", 5, WorldVegetationKind.Plant, false, 4.5f,
            region => region switch
            {
                WorldBiome.TemperateGrassland => .105f,
                WorldBiome.TemperateForest => .075f,
                WorldBiome.Rainforest => .090f,
                WorldBiome.Wetland => .085f,
                WorldBiome.Savanna => .055f,
                WorldBiome.Taiga => .030f,
                _ => 0
            }),
        new(
            "BUSH_NN", 2, WorldVegetationKind.Shrub, false, 8f,
            region => region switch
            {
                WorldBiome.TemperateForest => .018f,
                WorldBiome.Rainforest => .024f,
                WorldBiome.Wetland => .016f,
                _ => 0
            }),
        new(
            "BUSH2_NN", 18, WorldVegetationKind.Shrub, false, 7f,
            region => region switch
            {
                WorldBiome.TemperateForest => .038f,
                WorldBiome.Rainforest => .042f,
                WorldBiome.TemperateGrassland => .020f,
                WorldBiome.Savanna => .018f,
                WorldBiome.Taiga => .022f,
                WorldBiome.Tundra => .008f,
                _ => 0
            }),
        new(
            "BUSH3_NN", 9, WorldVegetationKind.FloweringShrub, false, 6f,
            region => region switch
            {
                WorldBiome.Tundra => .030f,
                WorldBiome.Alpine => .022f,
                WorldBiome.Taiga => .012f,
                _ => 0
            }),
        new(
            "FORAG_NN", 4, WorldVegetationKind.BerryBush, true, 9f,
            region => region switch
            {
                WorldBiome.TemperateForest => .018f,
                WorldBiome.Wetland => .015f,
                WorldBiome.Taiga => .011f,
                WorldBiome.TemperateGrassland => .007f,
                _ => 0
            }),
        new(
            "FORAGM_NN", 4, WorldVegetationKind.BerryBush, true, 9f,
            region => region switch
            {
                WorldBiome.Rainforest => .015f,
                WorldBiome.Savanna => .010f,
                WorldBiome.Coast => .006f,
                _ => 0
            })
    ];

    public static WorldVegetation[] Generate(
        long seed,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyCollection<IslandTree> trees)
    {
        const int maximumPerChunk = 96;
        var treeTiles = trees
            .Select(tree => (tree.X, tree.Y))
            .ToHashSet();
        var candidates = new List<(float Priority, WorldVegetation Value)>();
        foreach (var tile in tiles)
        {
            if (treeTiles.Contains((tile.X, tile.Y)) ||
                IsWater(tile.Biome) ||
                IsSand(tile.Biome) ||
                Relief(tile) > 2)
                continue;

            var treeInfluence = NearbyTreeInfluence(
                tile.X, tile.Y, treeTiles);
            foreach (var profile in Profiles)
            {
                if (profile.GraphicName.Equals(
                        "BUSH3_NN",
                        StringComparison.OrdinalIgnoreCase) &&
                    tile.Biome != Biome.Snow)
                    continue;
                var chance = profile.HabitatChance(tile.Region);
                if (chance <= 0) continue;
                var patch = PatchValue(
                    seed, tile.X, tile.Y,
                    profile.PatchScale,
                    1709 + Array.IndexOf(Profiles, profile) * 97);
                // Dense local colonies with sparse satellite recruits.
                var colony = MathF.Pow(patch, 2.2f) * 2.8f + .12f;
                var edgeFactor = profile.Kind switch
                {
                    WorldVegetationKind.BerryBush =>
                        .72f + treeInfluence * .75f,
                    WorldVegetationKind.Shrub or
                        WorldVegetationKind.FloweringShrub =>
                        .82f + treeInfluence * .55f,
                    _ => 1f
                };
                var roll = Hash(
                    seed, tile.X, tile.Y,
                    2003 + Array.IndexOf(Profiles, profile) * 101);
                if (roll >= chance * colony * edgeFactor)
                    continue;

                var frameRoll = Hash(
                    seed, tile.X, tile.Y,
                    2309 + Array.IndexOf(Profiles, profile) * 103);
                var frame = SelectFrame(profile, tile, frameRoll);
                var x = tile.X + .12f +
                        Hash(seed, tile.X, tile.Y, 2551) * .76f;
                var y = tile.Y + .12f +
                        Hash(seed, tile.X, tile.Y, 2557) * .76f;
                candidates.Add((
                    Hash(
                        seed, tile.X, tile.Y,
                        2801 + Array.IndexOf(Profiles, profile) * 107),
                    new(
                        x, y,
                        profile.GraphicName,
                        frame,
                        profile.Kind,
                        profile.CanBecomeInstance)));
                break;
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Priority)
            .Take(maximumPerChunk)
            .Select(candidate => candidate.Value)
            .ToArray();
    }

    private static float NearbyTreeInfluence(
        int x, int y, HashSet<(int X, int Y)> trees)
    {
        var nearby = 0;
        for (var offsetY = -2; offsetY <= 2; offsetY++)
        for (var offsetX = -2; offsetX <= 2; offsetX++)
            if (trees.Contains((x + offsetX, y + offsetY)))
                nearby++;
        // Highest at a broken forest edge; dense canopy suppresses shrubs.
        return nearby switch
        {
            0 => .25f,
            <= 3 => 1f,
            <= 7 => .65f,
            _ => .30f
        };
    }

    private static int SelectFrame(
        VegetationProfile profile, IslandTile tile, float roll)
    {
        // BUSH2's final six authored variants carry snow. Keep those tied to
        // actual snow material rather than the broader regional climate.
        if (profile.GraphicName.Equals(
                "BUSH2_NN", StringComparison.OrdinalIgnoreCase))
        {
            const int snowFrameStart = 12;
            const int snowFrameCount = 6;
            return tile.Biome == Biome.Snow
                ? snowFrameStart + FrameIndex(roll, snowFrameCount)
                : FrameIndex(roll, snowFrameStart);
        }

        return FrameIndex(roll, profile.FrameCount);
    }

    private static int FrameIndex(float roll, int count) =>
        Math.Min((int)(roll * count), count - 1);

    private static float PatchValue(
        long seed, int x, int y, float scale, int salt)
    {
        var scaledX = x / scale;
        var scaledY = y / scale;
        var x0 = (int)MathF.Floor(scaledX);
        var y0 = (int)MathF.Floor(scaledY);
        var fx = Smooth(scaledX - x0);
        var fy = Smooth(scaledY - y0);
        var north = Lerp(
            Hash(seed, x0, y0, salt),
            Hash(seed, x0 + 1, y0, salt), fx);
        var south = Lerp(
            Hash(seed, x0, y0 + 1, salt),
            Hash(seed, x0 + 1, y0 + 1, salt), fx);
        return Lerp(north, south, fy);
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

    private static bool IsWater(Biome biome) =>
        biome is Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows;

    private static bool IsSand(Biome biome) =>
        biome is Biome.Beach or Biome.DesertSand;

    private static int Relief(IslandTile tile) =>
        Math.Max(
            Math.Max(tile.North, tile.East),
            Math.Max(tile.South, tile.West)) -
        Math.Min(
            Math.Min(tile.North, tile.East),
            Math.Min(tile.South, tile.West));

    private static float Smooth(float value) =>
        value * value * (3 - 2 * value);

    private static float Lerp(float left, float right, float amount) =>
        left + (right - left) * amount;
}
