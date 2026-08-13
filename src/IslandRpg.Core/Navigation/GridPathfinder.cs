using System.Numerics;

namespace IslandRpg.Navigation;

public static class ActionPathSearchPolicy
{
    public const int MaximumVisited = 16_384;
    public const float AlternativeApproachDistance = 12f;

    public static bool ShouldTryAlternativeApproach(
        in Vector2 start,
        in Vector2 target) =>
        Vector2.DistanceSquared(start, target) <=
        AlternativeApproachDistance * AlternativeApproachDistance;
}

/// <summary>
/// Deterministic quarter-cell A*. Terrain data is supplied by a headless query,
/// keeping this reusable by the client, simulation and dedicated server.
/// </summary>
public static class GridPathfinder
{
    private static readonly (int X, int Y, float Cost)[] Neighbours =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 1.41421356f), (1, -1, 1.41421356f),
        (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)
    ];

    public static IReadOnlyList<Vector2> Find(
        IWorldNavigationQuery world,
        Vector2 startPosition,
        Vector2 requestedTarget,
        int maximumVisited = 65_536,
        CancellationToken cancellationToken = default,
        int worldLevel = (int)NavigationWorldLevel.Overworld,
        IReadOnlyList<NavigationObstacle>? obstacles = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (maximumVisited <= 0) return [];

        // An actor may begin within an interaction target's conservative
        // footprint. Ignore only those containing obstacles so the next path
        // can escape; every route starting outside still treats them as solid.
        var routeObstacles = obstacles is null
            ? null
            : obstacles.Where(obstacle =>
                    !obstacle.Contains(startPosition))
                .ToArray();
        var start = (
            X: WorldPlacementGrid.Cell(startPosition.X),
            Y: WorldPlacementGrid.Cell(startPosition.Y));
        var requestedGoal = (
            X: WorldPlacementGrid.Cell(requestedTarget.X),
            Y: WorldPlacementGrid.Cell(requestedTarget.Y));
        var passability = new Dictionary<(int X, int Y), bool>();
        var heights = new Dictionary<(int X, int Y), float>();

        bool CanPass(int x, int y)
        {
            if (passability.TryGetValue((x, y), out var value))
                return value;
            var center = WorldPlacementGrid.CellCenter(x, y);
            value = !Contains(routeObstacles, center) &&
                    world.CanStandAt(center, worldLevel);
            passability[(x, y)] = value;
            return value;
        }

        float CellHeight(int x, int y)
        {
            if (heights.TryGetValue((x, y), out var value))
                return value;
            value = world.HeightAt(
                WorldPlacementGrid.CellCenter(x, y), worldLevel);
            heights[(x, y)] = value;
            return value;
        }

        var exactTarget = !Contains(routeObstacles, requestedTarget) &&
                          world.CanStandAt(requestedTarget, worldLevel);
        var goal = ResolveGoal(
            requestedGoal,
            requestedTarget,
            CanPass);
        if (goal is null) return [];

        var resolvedStart = CanPass(start.X, start.Y)
            ? start
            : ResolveGoal(start, startPosition, CanPass);
        if (resolvedStart is null) return [];

        var searchStart = resolvedStart.Value;
        var resolvedGoal = goal.Value;
        if (HasDirectRoute(searchStart, resolvedGoal))
        {
            return
            [
                exactTarget
                    ? requestedTarget
                    : WorldPlacementGrid.CellCenter(
                        resolvedGoal.X, resolvedGoal.Y)
            ];
        }

        var frontier = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var costs = new Dictionary<(int X, int Y), float>
        {
            [searchStart] = 0
        };
        frontier.Enqueue(searchStart, 0);

        var visited = 0;
        while (frontier.Count > 0 && visited++ < maximumVisited)
        {
            if ((visited & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            if (current == resolvedGoal)
                return Reconstruct(current);

            foreach (var neighbour in Neighbours)
            {
                var next = (
                    X: current.X + neighbour.X,
                    Y: current.Y + neighbour.Y);
                if (!CanPass(next.X, next.Y)) continue;
                if (neighbour.X != 0 && neighbour.Y != 0 &&
                    (!CanPass(current.X + neighbour.X, current.Y) ||
                     !CanPass(current.X, current.Y + neighbour.Y)))
                    continue;
                var slope = MathF.Abs(
                    CellHeight(next.X, next.Y) -
                    CellHeight(current.X, current.Y));
                if (slope > 4) continue;
                var nextCost = costs[current] +
                               neighbour.Cost * WorldPlacementGrid.CellSize +
                               slope * .32f;
                if (costs.TryGetValue(next, out var previous) &&
                    previous <= nextCost)
                    continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var deltaX = Math.Abs(resolvedGoal.X - next.X);
                var deltaY = Math.Abs(resolvedGoal.Y - next.Y);
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
            while (current != searchStart)
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


        bool HasDirectRoute(
            (int X, int Y) from,
            (int X, int Y) to)
        {
            var x = from.X;
            var y = from.Y;
            var deltaX = Math.Abs(to.X - from.X);
            var deltaY = Math.Abs(to.Y - from.Y);
            var stepX = from.X < to.X ? 1 : -1;
            var stepY = from.Y < to.Y ? 1 : -1;
            var error = deltaX - deltaY;
            while (x != to.X || y != to.Y)
            {
                var previousX = x;
                var previousY = y;
                var doubledError = error * 2;
                if (doubledError > -deltaY)
                {
                    error -= deltaY;
                    x += stepX;
                }
                if (doubledError < deltaX)
                {
                    error += deltaX;
                    y += stepY;
                }

                if (!CanPass(x, y)) return false;
                if (x != previousX && y != previousY &&
                    (!CanPass(x, previousY) ||
                     !CanPass(previousX, y)))
                    return false;
                if (MathF.Abs(
                        CellHeight(x, y) -
                        CellHeight(previousX, previousY)) > 4)
                    return false;
            }
            return true;
        }
    }

    public static bool CanStandAt(
        IWorldNavigationQuery world,
        Vector2 point,
        int worldLevel,
        IReadOnlyList<NavigationObstacle>? obstacles = null) =>
        !Contains(obstacles, point) &&
        world.CanStandAt(point, worldLevel);

    private static (int X, int Y)? ResolveGoal(
        (int X, int Y) requestedGoal,
        Vector2 requestedTarget,
        Func<int, int, bool> passable)
    {
        const int searchRadius = 12;
        (int X, int Y)? nearest = null;
        var nearestDistance = float.MaxValue;
        for (var y = -searchRadius; y <= searchRadius; y++)
        for (var x = -searchRadius; x <= searchRadius; x++)
        {
            var candidate = (
                X: requestedGoal.X + x,
                Y: requestedGoal.Y + y);
            if (!passable(candidate.X, candidate.Y)) continue;
            var center = WorldPlacementGrid.CellCenter(
                candidate.X, candidate.Y);
            var distance = Vector2.DistanceSquared(
                center, requestedTarget);
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }

    private static bool Contains(
        IReadOnlyList<NavigationObstacle>? obstacles,
        Vector2 point)
    {
        if (obstacles is null) return false;
        foreach (var obstacle in obstacles)
            if (obstacle.Contains(point))
                return true;
        return false;
    }
}
