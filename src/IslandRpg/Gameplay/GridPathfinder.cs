using OpenTK.Mathematics;
using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class GridPathfinder
{
    private static readonly (int X, int Y, float Cost)[] Neighbours =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 1.41421356f), (1, -1, 1.41421356f),
        (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)
    ];

    public static IReadOnlyList<Vector2> Find(
        long seed,
        Vector2 startPosition,
        Vector2 requestedTarget,
        int maximumVisited = 8192,
        CancellationToken cancellationToken = default,
        int worldLevel = (int)WorldLevel.Overworld)
    {
        var start = ((int)MathF.Floor(startPosition.X), (int)MathF.Floor(startPosition.Y));
        var goal = ((int)MathF.Floor(requestedTarget.X), (int)MathF.Floor(requestedTarget.Y));
        var caveContext = worldLevel == (int)WorldLevel.Underground
            ? new CaveHydrologyField.SamplingContext(seed)
            : null;
        var caveDensity = new Dictionary<(int X, int Y), float>();
        float Density(int x, int y)
        {
            if (caveDensity.TryGetValue((x, y), out var density))
                return density;
            density = caveContext!.Density(x + .5f, y + .5f);
            caveDensity[(x, y)] = density;
            return density;
        }
        if (!Passable(seed, goal.Item1, goal.Item2, worldLevel, Density)) return [];

        var frontier = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var costs = new Dictionary<(int X, int Y), float> { [start] = 0 };
        frontier.Enqueue(start, 0);
        var visited = 0;
        while (frontier.Count > 0 && visited++ < maximumVisited)
        {
            if ((visited & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            if (current == goal) return Reconstruct(current);
            foreach (var neighbour in Neighbours)
            {
                var next = (current.X + neighbour.X, current.Y + neighbour.Y);
                if (!Passable(seed, next.Item1, next.Item2, worldLevel, Density))
                    continue;
                if (neighbour.X != 0 && neighbour.Y != 0 &&
                    (!Passable(
                         seed, current.X + neighbour.X, current.Y,
                         worldLevel, Density) ||
                     !Passable(
                         seed, current.X, current.Y + neighbour.Y,
                         worldLevel, Density)))
                    continue;
                var slope = Math.Abs(
                    Height(seed, next.Item1, next.Item2, worldLevel, Density) -
                    Height(seed, current.X, current.Y, worldLevel, Density));
                if (slope > 4) continue;
                var nextCost = costs[current] + neighbour.Cost + slope * .32f;
                if (costs.TryGetValue(next, out var previous) && previous <= nextCost) continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var heuristic = Math.Max(Math.Abs(goal.Item1 - next.Item1),
                                         Math.Abs(goal.Item2 - next.Item2));
                frontier.Enqueue(next, nextCost + heuristic);
            }
        }
        return [];

        IReadOnlyList<Vector2> Reconstruct((int X, int Y) current)
        {
            var result = new List<Vector2>();
            while (current != start)
            {
                result.Add(new Vector2(current.X + .5f, current.Y + .5f));
                current = cameFrom[current];
            }
            result.Reverse();
            return result;
        }
    }

    private static bool Passable(
        long seed, int x, int y, int worldLevel,
        Func<int, int, float> density)
    {
        if (worldLevel == (int)WorldLevel.Underground)
            return density(x, y) >=
                CaveHydrologyField.Boundary;
        var biome = InfiniteWorldGenerator.BiomeAt(seed, x, y);
        return biome != Biome.DeepWater;
    }

    private static int Height(
        long seed, int x, int y, int worldLevel,
        Func<int, int, float> density) =>
        worldLevel == (int)WorldLevel.Underground
            ? (int)MathF.Round(UndergroundWorldGenerator.Height(density(x, y)))
            : InfiniteWorldGenerator.SampleSurfaceHeight(seed, x, y);
}
