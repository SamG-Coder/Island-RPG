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
        var found = false;
        var best = fallback;
        var bestDistanceSquared = float.MaxValue;
        var bestTieBreak = ulong.MaxValue;
        for (var y = -maximumRadius; y <= maximumRadius; y++)
        for (var x = -maximumRadius; x <= maximumRadius; x++)
        {
            var tileX = originX + x;
            var tileY = originY + y;
            if (!IsWalkable(seed, tileX, tileY, level, caveContext))
                continue;
            var candidate = new Vector2(tileX + .5f, tileY + .5f);
            var distanceSquared =
                Vector2.DistanceSquared(destination, candidate);
            var tieBreak = CoordinateHash(seed, tileX, tileY);
            if (distanceSquared > bestDistanceSquared + .0001f ||
                MathF.Abs(distanceSquared - bestDistanceSquared) <= .0001f &&
                tieBreak >= bestTieBreak)
                continue;
            found = true;
            best = candidate;
            bestDistanceSquared = distanceSquared;
            bestTieBreak = tieBreak;
        }
        return found ? best : fallback;
    }

    public static Vector2 ReachableWalkableTarget(
        long seed,
        Vector2 origin,
        Vector2 destination,
        int level,
        int maximumRadius = 2)
    {
        var safeDestination = NearestWalkable(
            seed, destination, origin, level, maximumRadius);
        var displacement = safeDestination - origin;
        var distance = displacement.Length;
        if (distance <= .001f) return origin;

        var steps = Math.Max(1, (int)MathF.Ceiling(distance / .25f));
        var lastReachable = origin;
        for (var step = 1; step <= steps; step++)
        {
            var candidate = Vector2.Lerp(
                origin, safeDestination, step / (float)steps);
            if (!IsWalkable(
                    seed,
                    (int)MathF.Floor(candidate.X),
                    (int)MathF.Floor(candidate.Y),
                    level))
                break;
            lastReachable = candidate;
        }
        return lastReachable;
    }

    public static Vector2 ReachableExplorationTarget(
        long seed,
        Vector2 origin,
        Vector2 preferred,
        int level,
        float searchDistance = 8)
    {
        var best = ReachableWalkableTarget(
            seed, origin, preferred, level, maximumRadius: 3);
        var bestDistanceSquared = Vector2.DistanceSquared(origin, best);
        var start = (int)(CoordinateHash(
            seed,
            (int)MathF.Floor(origin.X),
            (int)MathF.Floor(origin.Y)) % 16);
        for (var step = 0; step < 16; step++)
        {
            var angle = (start + step) / 16f * MathF.Tau;
            var destination = origin + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * searchDistance;
            var candidate = ReachableWalkableTarget(
                seed, origin, destination, level, maximumRadius: 3);
            var distanceSquared = Vector2.DistanceSquared(origin, candidate);
            if (distanceSquared <= bestDistanceSquared + .0001f) continue;
            best = candidate;
            bestDistanceSquared = distanceSquared;
        }
        return best;
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

    private static ulong CoordinateHash(long seed, int x, int y)
    {
        var value = unchecked(
            (ulong)seed ^
            (ulong)(uint)x * 0x9E3779B185EBCA87UL ^
            (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ value >> 31;
    }
}
