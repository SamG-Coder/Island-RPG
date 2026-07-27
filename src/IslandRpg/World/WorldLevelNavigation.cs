using OpenTK.Mathematics;

namespace IslandRpg.World;

/// <summary>
/// Resolves safe positions consistently for loading, level transitions and
/// developer-map travel.
/// </summary>
internal static class WorldLevelNavigation
{
    public static Vector2 NearestWalkable(
        long seed,
        Vector2 destination,
        Vector2 fallback,
        int level,
        int maximumRadius = 24)
    {
        var originX = (int)MathF.Floor(destination.X);
        var originY = (int)MathF.Floor(destination.Y);
        var caveContext = level == (int)WorldLevel.Underground
            ? new CaveHydrologyField.SamplingContext(seed)
            : null;
        for (var radius = 0; radius <= maximumRadius; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                continue;
            var tileX = originX + x;
            var tileY = originY + y;
            if (!IsWalkable(seed, tileX, tileY, level, caveContext))
                continue;
            return new(tileX + .5f, tileY + .5f);
        }
        return fallback;
    }

    public static bool IsWalkable(
        long seed,
        int tileX,
        int tileY,
        int level,
        CaveHydrologyField.SamplingContext? caveContext = null)
    {
        if (level == (int)WorldLevel.Underground)
        {
            caveContext ??=
                new CaveHydrologyField.SamplingContext(seed);
            return caveContext.Density(tileX + .5f, tileY + .5f) >=
                   CaveHydrologyField.Boundary;
        }

        var biome = InfiniteWorldGenerator.BiomeAt(seed, tileX, tileY);
        return biome is not (
            Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows);
    }
}
