using OpenTK.Mathematics;
using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal readonly record struct NavigationObstacle(
    Vector2 Center,
    float Width,
    float Depth)
{
    public bool Contains(Vector2 point, float clearance = .18f) =>
        MathF.Abs(point.X - Center.X) <
            Width * .5f + clearance &&
        MathF.Abs(point.Y - Center.Y) <
            Depth * .5f + clearance;
}

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
        int worldLevel = (int)WorldLevel.Overworld,
        IReadOnlyList<NavigationObstacle>? obstacles = null)
    {
        var start = (
            WorldPlacementGrid.Cell(startPosition.X),
            WorldPlacementGrid.Cell(startPosition.Y));
        var requestedGoal = (
            WorldPlacementGrid.Cell(requestedTarget.X),
            WorldPlacementGrid.Cell(requestedTarget.Y));
        var caveContext = worldLevel == (int)WorldLevel.Underground
            ? new CaveHydrologyField.SamplingContext(seed)
            : null;
        var caveDensity = new Dictionary<(int X, int Y), float>();
        var passability = new Dictionary<(int X, int Y), bool>();
        var heights = new Dictionary<(int X, int Y), int>();
        float Density(int x, int y)
        {
            if (caveDensity.TryGetValue((x, y), out var density))
                return density;
            var center = WorldPlacementGrid.CellCenter(x, y);
            density = caveContext!.Density(center.X, center.Y);
            caveDensity[(x, y)] = density;
            return density;
        }
        bool CanPass(int x, int y)
        {
            if (passability.TryGetValue((x, y), out var value))
                return value;
            value = Passable(
                seed, x, y, worldLevel, Density, obstacles);
            passability[(x, y)] = value;
            return value;
        }
        int CellHeight(int x, int y)
        {
            if (heights.TryGetValue((x, y), out var value))
                return value;
            value = Height(seed, x, y, worldLevel, Density);
            heights[(x, y)] = value;
            return value;
        }
        var exactTarget = PassablePoint(
            seed, requestedTarget, worldLevel, caveContext, obstacles);
        var goal = ResolveGoal(
            seed,
            requestedGoal,
            requestedTarget,
            worldLevel,
            CanPass);
        if (goal is null)
            return [];
        var resolvedGoal = goal.Value;

        var frontier = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var costs = new Dictionary<(int X, int Y), float> { [start] = 0 };
        frontier.Enqueue(start, 0);
        var visited = 0;
        while (frontier.Count > 0 && visited++ < maximumVisited)
        {
            if ((visited & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            if (current == resolvedGoal) return Reconstruct(current);
            foreach (var neighbour in Neighbours)
            {
                var next = (current.X + neighbour.X, current.Y + neighbour.Y);
                if (!CanPass(next.Item1, next.Item2))
                    continue;
                if (neighbour.X != 0 && neighbour.Y != 0 &&
                    (!CanPass(current.X + neighbour.X, current.Y) ||
                     !CanPass(current.X, current.Y + neighbour.Y)))
                    continue;
                var slope = Math.Abs(
                    CellHeight(next.Item1, next.Item2) -
                    CellHeight(current.X, current.Y));
                if (slope > 4) continue;
                var nextCost = costs[current] +
                               neighbour.Cost * WorldPlacementGrid.CellSize +
                               slope * .32f;
                if (costs.TryGetValue(next, out var previous) && previous <= nextCost) continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var deltaX = Math.Abs(resolvedGoal.Item1 - next.Item1);
                var deltaY = Math.Abs(resolvedGoal.Item2 - next.Item2);
                var diagonal = Math.Min(deltaX, deltaY);
                var straight = Math.Max(deltaX, deltaY) - diagonal;
                var heuristic =
                    (diagonal * 1.41421356f + straight) *
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
            if (exactTarget)
            {
                if (result.Count == 0)
                    result.Add(requestedTarget);
                else
                    result[^1] = requestedTarget;
            }
            return result;
        }
    }

    private static (int X, int Y)? ResolveGoal(
        long seed,
        (int X, int Y) requestedGoal,
        Vector2 requestedTarget,
        int worldLevel,
        Func<int, int, bool> passable)
    {
        const int searchRadius = 12;
        (int X, int Y)? nearest = null;
        var nearestDistance = float.MaxValue;
        for (var y = -searchRadius; y <= searchRadius; y++)
        for (var x = -searchRadius; x <= searchRadius; x++)
        {
            var candidate = (
                requestedGoal.X + x,
                requestedGoal.Y + y);
            if (!passable(candidate.Item1, candidate.Item2))
                continue;
            var center = WorldPlacementGrid.CellCenter(
                candidate.Item1, candidate.Item2);
            var distance = (center - requestedTarget).LengthSquared;
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }

    private static bool PassablePoint(
        long seed,
        Vector2 point,
        int worldLevel,
        CaveHydrologyField.SamplingContext? caveContext,
        IReadOnlyList<NavigationObstacle>? obstacles)
    {
        if (obstacles is not null)
            foreach (var obstacle in obstacles)
                if (obstacle.Contains(point))
                    return false;
        if (worldLevel == (int)WorldLevel.Underground)
            return caveContext!.Density(point.X, point.Y) >=
                CaveHydrologyField.Boundary;
        return InfiniteWorldGenerator.BiomeAt(
            seed,
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y)) != Biome.DeepWater;
    }

    private static bool Passable(
        long seed, int x, int y, int worldLevel,
        Func<int, int, float> density,
        IReadOnlyList<NavigationObstacle>? obstacles)
    {
        var center = WorldPlacementGrid.CellCenter(x, y);
        if (obstacles is not null)
            foreach (var obstacle in obstacles)
                if (obstacle.Contains(center))
                    return false;
        if (worldLevel == (int)WorldLevel.Underground)
            return density(x, y) >=
                CaveHydrologyField.Boundary;
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
