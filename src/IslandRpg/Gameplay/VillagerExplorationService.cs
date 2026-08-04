using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

/// <summary>
/// Plans short, observable exploration legs. The caller reassesses the world
/// after every leg instead of sending an actor directly across unknown ground.
/// </summary>
internal static class VillagerExplorationService
{
    public const float LegDistance = 7;
    public const int OpeningScoutLegs = 4;

    public static Vector2 NextLeg(
        long worldSeed,
        Vector2 position,
        Vector2 sectorTarget,
        int worldLevel)
    {
        return BestForwardFrontier(
            worldSeed, position, sectorTarget, worldLevel);
    }

    public static bool MadeProgress(Vector2 position, Vector2 waypoint) =>
        Vector2.DistanceSquared(position, waypoint) >= 1.5f * 1.5f;

    public static Vector2 LegFromRoute(
        Vector2 position,
        IReadOnlyList<Vector2> route)
    {
        if (route.Count == 0) return position;
        var legDistanceSquared = LegDistance * LegDistance;
        foreach (var waypoint in route)
            if (Vector2.DistanceSquared(position, waypoint) >=
                legDistanceSquared)
                return waypoint;
        return route[^1];
    }

    private static Vector2 BestForwardFrontier(
        long worldSeed,
        Vector2 position,
        Vector2 sectorTarget,
        int worldLevel)
    {
        var heading = sectorTarget - position;
        if (heading.LengthSquared <= .01f) heading = Vector2.UnitX;
        else heading = Vector2.Normalize(heading);
        var best = position;
        var bestScore = 0f;
        ReadOnlySpan<float> turns =
        [0, .35f, -.35f, .7f, -.7f, 1.05f, -1.05f, 1.4f, -1.4f];
        foreach (var turn in turns)
        {
            var cosine = MathF.Cos(turn);
            var sine = MathF.Sin(turn);
            var direction = new Vector2(
                heading.X * cosine - heading.Y * sine,
                heading.X * sine + heading.Y * cosine);
            var candidate = WorldLevelNavigation.ReachableWalkableTarget(
                worldSeed,
                position,
                position + direction * LegDistance,
                worldLevel,
                maximumRadius: 2);
            var displacement = candidate - position;
            var forward = Vector2.Dot(displacement, heading);
            if (forward <= .5f) continue;
            var score = forward * 3 + displacement.Length;
            if (score <= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        return best;
    }
}
