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
        int maximumVisited = 65536,
        CancellationToken cancellationToken = default,
        int worldLevel = (int)WorldLevel.Overworld)
    {
        var start = (
            WorldPlacementGrid.Cell(startPosition.X),
            WorldPlacementGrid.Cell(startPosition.Y));
        var goal = (
            WorldPlacementGrid.Cell(requestedTarget.X),
            WorldPlacementGrid.Cell(requestedTarget.Y));
        var caveContext = worldLevel == (int)WorldLevel.Underground
            ? new CaveHydrologyField.SamplingContext(seed)
            : null;
        var caveDensity = new Dictionary<(int X, int Y), float>();
        float Density(int x, int y)
        {
            if (caveDensity.TryGetValue((x, y), out var density))
                return density;
            var center = WorldPlacementGrid.CellCenter(x, y);
            density = caveContext!.Density(center.X, center.Y);
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
                var nextCost = costs[current] +
                               neighbour.Cost * WorldPlacementGrid.CellSize +
                               slope * .32f;
                if (costs.TryGetValue(next, out var previous) && previous <= nextCost) continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var heuristic =
                    Math.Max(
                        Math.Abs(goal.Item1 - next.Item1),
                        Math.Abs(goal.Item2 - next.Item2)) *
                    WorldPlacementGrid.CellSize;
                frontier.Enqueue(next, nextCost + heuristic);
            }
        }
        return [];

        IReadOnlyList<Vector2> Reconstruct((int X, int Y) current)
        {
            var result = new List<Vector2>();
            while (current != start)
            {
                result.Add(WorldPlacementGrid.CellCenter(
                    current.X, current.Y));
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
        var center = WorldPlacementGrid.CellCenter(x, y);
        var biome = InfiniteWorldGenerator.BiomeAt(
            seed,
            (int)MathF.Floor(center.X),
            (int)MathF.Floor(center.Y));
        return biome != Biome.DeepWater;
    }

    private static int Height(
        long seed, int x, int y, int worldLevel,
        Func<int, int, float> density) =>
        worldLevel == (int)WorldLevel.Underground
            ? (int)MathF.Round(UndergroundWorldGenerator.Height(density(x, y)))
            : SampleSurfaceHeight(seed, x, y);

    private static int SampleSurfaceHeight(long seed, int x, int y)
    {
        var center = WorldPlacementGrid.CellCenter(x, y);
        return InfiniteWorldGenerator.SampleSurfaceHeight(
            seed,
            (int)MathF.Floor(center.X),
            (int)MathF.Floor(center.Y));
    }
}
