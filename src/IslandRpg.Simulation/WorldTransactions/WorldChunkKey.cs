using System.Numerics;

namespace IslandRpg.Simulation;

/// <summary>
/// Canonical identity of a streamed world chunk. The source remains beside
/// the world-transaction contracts, while IslandRpg.Core owns the compiled
/// type so every headless gameplay layer can use the same identity without a
/// Core-to-Simulation project-reference cycle.
/// </summary>
public readonly record struct WorldChunkKey(int X, int Y, int WorldLevel)
{
    public const int Size = 32;

    public static WorldChunkKey At(Vector2 position, int worldLevel) => new(
        FloorDiv((int)MathF.Floor(position.X), Size),
        FloorDiv((int)MathF.Floor(position.Y), Size),
        worldLevel);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
