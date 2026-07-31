using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class FishingBoatTravel
{
    private static readonly (int X, int Y, float Cost)[] Neighbours =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 1.41421356f), (1, -1, 1.41421356f),
        (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)
    ];

    public static bool IsNavigable(Biome biome) =>
        biome is Biome.DeepWater or Biome.ShallowWater or Biome.RiverWater;

    public static Vector2 FindInitialPosition(long seed, Vector2 origin)
    {
        var centerX = (int)MathF.Floor(origin.X);
        var centerY = (int)MathF.Floor(origin.Y);
        for (var radius = 1; radius <= 96; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Abs(x) != radius && Math.Abs(y) != radius) continue;
            var tileX = centerX + x;
            var tileY = centerY + y;
            if (InfiniteWorldGenerator.BiomeAt(seed, tileX, tileY) !=
                Biome.ShallowWater)
                continue;
            if (!HasLandNeighbour(seed, tileX, tileY)) continue;
            return new(tileX + .5f, tileY + .5f);
        }
        return origin;
    }

    public static IReadOnlyList<Vector2> FindPath(
        long seed,
        Vector2 start,
        Vector2 requestedTarget,
        int maximumVisited = 65536)
    {
        var startCell = Cell(start);
        var requestedGoal = Cell(requestedTarget);
        var goal = ResolveGoal(seed, requestedGoal);
        if (goal is null || !Passable(seed, startCell)) return [];

        var frontier = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var costs = new Dictionary<(int X, int Y), float>
        {
            [startCell] = 0
        };
        frontier.Enqueue(startCell, 0);
        var visited = 0;
        while (frontier.Count > 0 && visited++ < maximumVisited)
        {
            var current = frontier.Dequeue();
            if (current == goal.Value)
                return Reconstruct(current);
            foreach (var neighbour in Neighbours)
            {
                var next = (
                    current.X + neighbour.X,
                    current.Y + neighbour.Y);
                if (!Passable(seed, next)) continue;
                if (neighbour.X != 0 && neighbour.Y != 0 &&
                    (!Passable(seed, (next.Item1, current.Y)) ||
                     !Passable(seed, (current.X, next.Item2))))
                    continue;
                var nextCost = costs[current] + neighbour.Cost;
                if (costs.TryGetValue(next, out var oldCost) &&
                    oldCost <= nextCost)
                    continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var heuristic = Math.Max(
                    Math.Abs(goal.Value.X - next.Item1),
                    Math.Abs(goal.Value.Y - next.Item2));
                frontier.Enqueue(next, nextCost + heuristic);
            }
        }
        return [];

        IReadOnlyList<Vector2> Reconstruct((int X, int Y) current)
        {
            var result = new List<Vector2>();
            while (current != startCell)
            {
                result.Add(Center(current));
                current = cameFrom[current];
            }
            result.Reverse();
            if (result.Count > 0 &&
                Cell(requestedTarget) == goal.Value &&
                IsNavigable(BiomeAt(seed, requestedTarget)))
                result[^1] = requestedTarget;
            return result;
        }
    }

    public static bool CanDisembark(
        long seed, Vector2 boatPosition, Vector2 target) =>
        !IsNavigable(BiomeAt(seed, target)) &&
        (boatPosition - target).Length <= 2.4f;

    public static Vector2? FindDisembarkLanding(
        long seed,
        Vector2 boatPosition,
        Vector2 requestedTarget)
    {
        const int radius = 3;
        var center = Cell(boatPosition);
        Vector2? best = null;
        var bestScore = float.MaxValue;
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            var candidate = Center((center.Item1 + x, center.Item2 + y));
            if (!CanDisembark(seed, boatPosition, candidate))
                continue;
            var score = (candidate - requestedTarget).LengthSquared;
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        return best;
    }

    private static (int X, int Y)? ResolveGoal(
        long seed, (int X, int Y) requested)
    {
        const int radius = 5;
        (int X, int Y)? nearest = null;
        var nearestDistance = int.MaxValue;
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            var candidate = (requested.X + x, requested.Y + y);
            if (!Passable(seed, candidate)) continue;
            var distance = x * x + y * y;
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }

    private static bool HasLandNeighbour(long seed, int x, int y)
    {
        foreach (var offset in Neighbours)
            if (!IsNavigable(InfiniteWorldGenerator.BiomeAt(
                    seed, x + offset.X, y + offset.Y)))
                return true;
        return false;
    }

    private static bool Passable(long seed, (int X, int Y) cell) =>
        IsNavigable(InfiniteWorldGenerator.BiomeAt(
            seed, cell.X, cell.Y));

    private static Biome BiomeAt(long seed, Vector2 point) =>
        InfiniteWorldGenerator.BiomeAt(
            seed,
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y));

    private static (int X, int Y) Cell(Vector2 point) =>
        ((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));

    private static Vector2 Center((int X, int Y) cell) =>
        new(cell.X + .5f, cell.Y + .5f);
}
