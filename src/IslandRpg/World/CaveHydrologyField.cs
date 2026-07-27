using OpenTK.Mathematics;

namespace IslandRpg.World;

/// <summary>
/// Infinite, coordinate-stable karst conduit field. Macro cells act as
/// recharge sites and drain toward lower neighbouring hydraulic potential.
/// Their accumulated joins widen passages and create junction chambers.
/// </summary>
internal static class CaveHydrologyField
{
    internal const float Boundary = 0f;
    private const int CellSize = 38;

    public static float Density(long seed, float worldX, float worldY) =>
        Density(seed, worldX, worldY, null);

    internal static float Density(
        long seed,
        float worldX,
        float worldY,
        SamplingContext? context)
    {
        var cellX = FloorDiv((int)MathF.Floor(worldX), CellSize);
        var cellY = FloorDiv((int)MathF.Floor(worldY), CellSize);
        var point = new Vector2(worldX, worldY);
        var density = float.MinValue;
        for (var y = cellY - 2; y <= cellY + 2; y++)
        for (var x = cellX - 2; x <= cellX + 2; x++)
        {
            var topology = context?.Topology(x, y) ??
                           BuildTopology(seed, x, y, null);
            var start = topology.Start;
            var end = topology.End;
            var joins = topology.Incoming;
            var radius = 2.6f + Math.Min(joins, 4) * .58f;
            var distance = DistanceToSegment(point, start, end);
            density = Math.Max(density, radius - distance);

            // Flow convergence dissolves broader, asymmetric chambers.
            if (joins >= 3)
            {
                var chamberRadius = 4.8f + joins * .62f;
                var chamberDistance = (point - end).Length;
                density = Math.Max(
                    density, chamberRadius - chamberDistance);
            }

            // Some fracture-dominated regions develop angular cross-links.
            if (topology.HasCrossLink)
            {
                density = Math.Max(
                    density,
                    2.15f -
                    DistanceToSegment(point, start, topology.CrossEnd));
            }
        }

        // Fine dissolution roughness is evaluated in world coordinates, so
        // contours agree exactly on both sides of a chunk boundary.
        var roughness =
            ValueNoise(seed ^ 0x5E71A91D, worldX / 7.5f, worldY / 7.5f) *
            .9f - .45f;
        return density + roughness;
    }

    public static float Strength(long seed, float worldX, float worldY) =>
        SmoothStep(-1.1f, 1.3f, Density(seed, worldX, worldY));

    internal static float Strength(float density) =>
        SmoothStep(-1.1f, 1.3f, density);

    internal sealed class SamplingContext(long seed)
    {
        private readonly Dictionary<(int X, int Y), CellTopology> topology = [];
        private readonly Dictionary<(int X, int Y), (int X, int Y)> destinations = [];

        internal float Density(float worldX, float worldY) =>
            CaveHydrologyField.Density(seed, worldX, worldY, this);

        internal CellTopology Topology(int x, int y)
        {
            if (topology.TryGetValue((x, y), out var value)) return value;
            value = BuildTopology(seed, x, y, destinations);
            topology[(x, y)] = value;
            return value;
        }
    }

    private static CellTopology BuildTopology(
        long seed,
        int x,
        int y,
        Dictionary<(int X, int Y), (int X, int Y)>? destinations)
    {
        var destination = Destination(seed, x, y, destinations);
        var crossCell = ((x + y) & 1) == 0
            ? (X: x + 1, Y: y)
            : (X: x, Y: y + 1);
        return new(
            Node(seed, x, y),
            Node(seed, destination.X, destination.Y),
            IncomingCount(seed, destination.X, destination.Y, destinations),
            Pattern(seed, x, y) == CavePattern.AngularMaze &&
            UnitHash(seed, x, y, 73) > .57f,
            Node(seed, crossCell.X, crossCell.Y));
    }

    private static (int X, int Y) Destination(
        long seed,
        int x,
        int y,
        Dictionary<(int X, int Y), (int X, int Y)>? cache = null)
    {
        if (cache is not null &&
            cache.TryGetValue((x, y), out var cached))
            return cached;
        var current = Potential(seed, x, y);
        var best = (X: x, Y: y);
        var bestPotential = current;
        foreach (var offset in Neighbours)
        {
            var candidate = (X: x + offset.X, Y: y + offset.Y);
            var potential = Potential(
                seed, candidate.X, candidate.Y);
            if (potential >= bestPotential) continue;
            best = candidate;
            bestPotential = potential;
        }
        if (best != (x, y))
        {
            if (cache is not null) cache[(x, y)] = best;
            return best;
        }

        // Local minima become sumps, but still receive a short outlet so an
        // infinite world cannot produce isolated single-room caves.
        var direction = (int)(UnitHash(seed, x, y, 29) * Neighbours.Length) %
                        Neighbours.Length;
        var result = (
            x + Neighbours[direction].X,
            y + Neighbours[direction].Y);
        if (cache is not null) cache[(x, y)] = result;
        return result;
    }

    private static int IncomingCount(
        long seed,
        int x,
        int y,
        Dictionary<(int X, int Y), (int X, int Y)>? cache = null)
    {
        var count = 0;
        foreach (var offset in Neighbours)
        {
            var sourceX = x + offset.X;
            var sourceY = y + offset.Y;
            if (Destination(seed, sourceX, sourceY, cache) == (x, y))
                count++;
        }
        return count;
    }

    private static Vector2 Node(long seed, int x, int y)
    {
        var jitterX = UnitHash(seed, x, y, 11) * .54f - .27f;
        var jitterY = UnitHash(seed, x, y, 17) * .54f - .27f;
        return new(
            (x + .5f + jitterX) * CellSize,
            (y + .5f + jitterY) * CellSize);
    }

    private static float Potential(long seed, int x, int y)
    {
        var basinX = FloorDiv(x, 7);
        var basinY = FloorDiv(y, 7);
        var local = UnitHash(seed, x, y, 41) * .48f;
        var basin = UnitHash(seed, basinX, basinY, 43) * .52f;
        return local + basin;
    }

    private static CavePattern Pattern(long seed, int x, int y) =>
        UnitHash(seed, FloorDiv(x, 9), FloorDiv(y, 9), 61) switch
        {
            < .48f => CavePattern.VadoseBranchwork,
            < .78f => CavePattern.WaterTableLoops,
            _ => CavePattern.AngularMaze
        };

    private static float DistanceToSegment(
        Vector2 point, Vector2 start, Vector2 end)
    {
        var delta = end - start;
        var lengthSquared = Math.Max(delta.LengthSquared, .0001f);
        var amount = Math.Clamp(
            Vector2.Dot(point - start, delta) / lengthSquared,
            0f, 1f);
        return (point - Vector2.Lerp(start, end, amount)).Length;
    }

    private static float ValueNoise(long seed, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = Fade(x - x0);
        var ty = Fade(y - y0);
        return Lerp(
            Lerp(
                UnitHash(seed, x0, y0, 89),
                UnitHash(seed, x0 + 1, y0, 89), tx),
            Lerp(
                UnitHash(seed, x0, y0 + 1, 89),
                UnitHash(seed, x0 + 1, y0 + 1, 89), tx),
            ty);
    }

    private static float UnitHash(
        long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = seed ^ salt * 0x9E3779B9L;
            value ^= (long)x *
                     unchecked((long)0x632BE59BD9B4E019UL);
            value ^= (long)y *
                     unchecked((long)0x9E3779B185EBCA87UL);
            value ^= value >> 27;
            value *= unchecked((long)0x3C79AC492BA7B653UL);
            value ^= value >> 33;
            return (value & 0xFFFFFF) / 16777215f;
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0
            ? quotient - 1
            : quotient;
    }

    private static float Fade(float value) =>
        value * value * (3f - 2f * value);

    private static float Lerp(float a, float b, float amount) =>
        a + (b - a) * amount;

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        var normalized = Math.Clamp(
            (value - minimum) / (maximum - minimum), 0f, 1f);
        return normalized * normalized * (3f - 2f * normalized);
    }

    private static readonly (int X, int Y)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),            (1, 0),
        (-1, 1),  (0, 1),  (1, 1)
    ];

    private enum CavePattern
    {
        VadoseBranchwork,
        WaterTableLoops,
        AngularMaze
    }

    internal readonly record struct CellTopology(
        Vector2 Start,
        Vector2 End,
        int Incoming,
        bool HasCrossLink,
        Vector2 CrossEnd);
}
