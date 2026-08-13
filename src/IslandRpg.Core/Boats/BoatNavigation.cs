using System.Numerics;
using IslandRpg.World;

namespace IslandRpg.Boats;

/// <summary>
/// Minimal headless water query. Boats intentionally cannot enter mangrove
/// shallows: this preserves the established solo deep/shallow/river policy.
/// </summary>
public interface IBoatNavigationQuery
{
    bool IsNavigable(Vector2 point);

    bool IsLanding(Vector2 point);

    bool IsInitialMooring(Vector2 point);
}

public sealed class ProceduralBoatNavigationQuery(long seed) :
    IBoatNavigationQuery
{
    public long Seed { get; } = seed;

    public bool IsNavigable(Vector2 point)
    {
        if (!IsFinite(point)) return false;
        var material = TerrainAt(point);
        return material is ProceduralSurfaceTerrain.Material.DeepWater or
            ProceduralSurfaceTerrain.Material.ShallowWater or
            ProceduralSurfaceTerrain.Material.RiverWater;
    }

    public bool IsLanding(Vector2 point) =>
        IsFinite(point) && !IsNavigable(point);

    public bool IsInitialMooring(Vector2 point) =>
        IsFinite(point) &&
        TerrainAt(point) == ProceduralSurfaceTerrain.Material.ShallowWater;

    private ProceduralSurfaceTerrain.Material TerrainAt(Vector2 point) =>
        ProceduralSurfaceTerrain.ClassifyAt(
            Seed,
            (int)MathF.Floor(point.X),
            (int)MathF.Floor(point.Y)).Material;

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}

/// <summary>
/// Deterministic tile-scale A* for boats. The client supplies only a target;
/// the authority resolves and bounds the route itself.
/// </summary>
public static class BoatRoutePlanner
{
    public const int MaximumVisited = 65_536;
    public const int GoalSearchRadius = 5;

    private static readonly (int X, int Y, float Cost)[] Neighbours =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 1.41421356f), (1, -1, 1.41421356f),
        (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)
    ];

    public static IReadOnlyList<Vector2> Find(
        IBoatNavigationQuery query,
        Vector2 start,
        Vector2 requestedTarget,
        int maximumVisited = MaximumVisited,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsFinite(start) || !IsFinite(requestedTarget) ||
            maximumVisited <= 0)
            return [];
        maximumVisited = Math.Min(maximumVisited, MaximumVisited);

        var startCell = Cell(start);
        var requestedGoal = Cell(requestedTarget);
        var passability = new Dictionary<(int X, int Y), bool>();
        bool Passable((int X, int Y) cell)
        {
            if (passability.TryGetValue(cell, out var passable))
                return passable;
            passable = query.IsNavigable(Center(cell));
            passability.Add(cell, passable);
            return passable;
        }

        var goal = ResolveGoal(requestedGoal, Passable);
        if (goal is null || !Passable(startCell)) return [];

        // Most travel is across uninterrupted water. Walking every open tile
        // through A* wastes allocations and authority-thread time, so prove a
        // corner-safe supercover line first and collapse it to one waypoint.
        // The same visit bound applies to this fast path.
        var exactTarget = requestedGoal == goal.Value &&
                          query.IsNavigable(requestedTarget)
            ? requestedTarget
            : Center(goal.Value);
        if (HasDirectRoute(
                start, exactTarget, Passable, maximumVisited,
                cancellationToken))
            return startCell == goal.Value && start == exactTarget
                ? []
                : [exactTarget];

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
            if ((visited & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            if (current == goal.Value)
                return Reconstruct(current);
            foreach (var neighbour in Neighbours)
            {
                var next = (
                    X: current.X + neighbour.X,
                    Y: current.Y + neighbour.Y);
                if (!Passable(next)) continue;
                if (neighbour.X != 0 && neighbour.Y != 0 &&
                    (!Passable((next.X, current.Y)) ||
                     !Passable((current.X, next.Y))))
                    continue;
                var nextCost = costs[current] + neighbour.Cost;
                if (costs.TryGetValue(next, out var previous) &&
                    previous <= nextCost)
                    continue;
                costs[next] = nextCost;
                cameFrom[next] = current;
                var heuristic = Math.Max(
                    Math.Abs(goal.Value.X - next.X),
                    Math.Abs(goal.Value.Y - next.Y));
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
            if (result.Count > 0 && requestedGoal == goal.Value &&
                query.IsNavigable(requestedTarget))
                result[^1] = requestedTarget;
            return result;
        }
    }

    private static (int X, int Y)? ResolveGoal(
        (int X, int Y) requested,
        Func<(int X, int Y), bool> passable)
    {
        if (passable(requested)) return requested;
        (int X, int Y)? nearest = null;
        var nearestDistance = int.MaxValue;
        for (var y = -GoalSearchRadius; y <= GoalSearchRadius; y++)
        for (var x = -GoalSearchRadius; x <= GoalSearchRadius; x++)
        {
            var candidate = (requested.X + x, requested.Y + y);
            if (!passable(candidate)) continue;
            var distance = x * x + y * y;
            if (distance >= nearestDistance) continue;
            nearest = candidate;
            nearestDistance = distance;
        }
        return nearest;
    }

    private static bool HasDirectRoute(
        Vector2 start,
        Vector2 target,
        Func<(int X, int Y), bool> passable,
        int maximumVisited,
        CancellationToken cancellationToken)
    {
        var current = Cell(start);
        var goal = Cell(target);
        if (!passable(current)) return false;
        if (current == goal) return true;
        var deltaX = target.X - start.X;
        var deltaY = target.Y - start.Y;
        // A supercover line can enter at most this many new cells. Rejecting
        // it here preserves the caller's total route-work contract.
        if ((long)Math.Abs(goal.X - current.X) +
            Math.Abs(goal.Y - current.Y) + 1 > maximumVisited)
            return false;

        var stepX = Math.Sign(deltaX);
        var stepY = Math.Sign(deltaY);
        var absoluteX = MathF.Abs(deltaX);
        var absoluteY = MathF.Abs(deltaY);
        var tDeltaX = absoluteX == 0
            ? double.PositiveInfinity
            : 1d / absoluteX;
        var tDeltaY = absoluteY == 0
            ? double.PositiveInfinity
            : 1d / absoluteY;
        var tMaxX = stepX switch
        {
            > 0 => (current.X + 1d - start.X) / absoluteX,
            < 0 => (start.X - current.X) / absoluteX,
            _ => double.PositiveInfinity
        };
        var tMaxY = stepY switch
        {
            > 0 => (current.Y + 1d - start.Y) / absoluteY,
            < 0 => (start.Y - current.Y) / absoluteY,
            _ => double.PositiveInfinity
        };
        var visited = 1;
        while (current != goal)
        {
            if ((visited++ & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (tMaxX < tMaxY)
            {
                current.X += stepX;
                tMaxX += tDeltaX;
                if (!passable(current)) return false;
                continue;
            }
            if (tMaxY < tMaxX)
            {
                current.Y += stepY;
                tMaxY += tDeltaY;
                if (!passable(current)) return false;
                continue;
            }

            // Crossing a grid corner requires both adjacent water cells;
            // otherwise a straight segment could cut between two blockers.
            var horizontal = (current.X + stepX, current.Y);
            var vertical = (current.X, current.Y + stepY);
            current = (current.X + stepX, current.Y + stepY);
            tMaxX += tDeltaX;
            tMaxY += tDeltaY;
            if (!passable(horizontal) || !passable(vertical) ||
                !passable(current))
                return false;
        }
        return true;
    }

    private static (int X, int Y) Cell(Vector2 point) =>
        ((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));

    private static Vector2 Center((int X, int Y) cell) =>
        new(cell.X + .5f, cell.Y + .5f);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}

public static class BoatTravelRules
{
    public const int MaximumPlayableSpawnRadius = 160;

    private static readonly (int X, int Y)[] Neighbours =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1)
    ];

    /// <summary>
    /// Resolves the deterministic land spawn used by both solo and dedicated
    /// hosts. A finite bound prevents a hostile seed/request from causing an
    /// unbounded terrain scan.
    /// </summary>
    public static Vector2 FindPlayableLandSpawn(
        long worldSeed,
        CancellationToken cancellationToken = default,
        int maximumRadius = MaximumPlayableSpawnRadius)
    {
        if (maximumRadius < 0 || maximumRadius > 512)
            throw new ArgumentOutOfRangeException(nameof(maximumRadius));
        for (var radius = 0; radius <= maximumRadius; radius++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                    continue;
                var material = ProceduralSurfaceTerrain.ClassifyAt(
                    worldSeed, x, y).Material;
                if (material is ProceduralSurfaceTerrain.Material.DeepWater or
                    ProceduralSurfaceTerrain.Material.ShallowWater or
                    ProceduralSurfaceTerrain.Material.RiverWater or
                    ProceduralSurfaceTerrain.Material.MangroveShallows)
                    continue;
                return new Vector2(x + .5f, y + .5f);
            }
        }
        throw new InvalidOperationException(
            $"No playable land was found within {maximumRadius} tiles of " +
            $"the world origin for seed {worldSeed}.");
    }

    public static Vector2 FindInitialPosition(
        IBoatNavigationQuery query,
        Vector2 origin,
        Func<Vector2, bool>? occupied = null,
        int maximumRadius = 96)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsFinite(origin) || maximumRadius <= 0) return origin;
        maximumRadius = Math.Min(maximumRadius, 512);
        var centerX = (int)MathF.Floor(origin.X);
        var centerY = (int)MathF.Floor(origin.Y);
        for (var radius = 1; radius <= maximumRadius; radius++)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Abs(x) != radius && Math.Abs(y) != radius) continue;
            var candidate = new Vector2(
                centerX + x + .5f,
                centerY + y + .5f);
            if (!query.IsInitialMooring(candidate) ||
                occupied?.Invoke(candidate) == true ||
                !HasLandingNeighbour(query, candidate))
                continue;
            return candidate;
        }
        return origin;
    }

    public static bool CanDisembark(
        IBoatNavigationQuery query,
        Vector2 boatPosition,
        Vector2 target,
        float maximumDistance = 2.4f)
    {
        ArgumentNullException.ThrowIfNull(query);
        return IsFinite(boatPosition) && IsFinite(target) &&
               float.IsFinite(maximumDistance) && maximumDistance >= 0 &&
               query.IsLanding(target) &&
               Vector2.DistanceSquared(boatPosition, target) <=
               maximumDistance * maximumDistance;
    }

    public static Vector2? FindDisembarkLanding(
        IBoatNavigationQuery query,
        Vector2 boatPosition,
        Vector2 requestedTarget,
        Func<Vector2, bool>? occupied = null,
        int searchRadius = 3,
        float maximumDistance = 2.4f)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsFinite(boatPosition) || !IsFinite(requestedTarget) ||
            searchRadius < 0 || !float.IsFinite(maximumDistance) ||
            maximumDistance < 0)
            return null;
        searchRadius = Math.Min(searchRadius, 32);
        var centerX = (int)MathF.Floor(boatPosition.X);
        var centerY = (int)MathF.Floor(boatPosition.Y);
        Vector2? best = null;
        var bestScore = float.MaxValue;
        for (var y = -searchRadius; y <= searchRadius; y++)
        for (var x = -searchRadius; x <= searchRadius; x++)
        {
            var candidate = new Vector2(
                centerX + x + .5f,
                centerY + y + .5f);
            if (occupied?.Invoke(candidate) == true ||
                !CanDisembark(
                    query, boatPosition, candidate, maximumDistance))
                continue;
            var score = Vector2.DistanceSquared(candidate, requestedTarget);
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        return best;
    }

    private static bool HasLandingNeighbour(
        IBoatNavigationQuery query,
        Vector2 point)
    {
        var tileX = (int)MathF.Floor(point.X);
        var tileY = (int)MathF.Floor(point.Y);
        foreach (var offset in Neighbours)
            if (query.IsLanding(new Vector2(
                    tileX + offset.X + .5f,
                    tileY + offset.Y + .5f)))
                return true;
        return false;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
