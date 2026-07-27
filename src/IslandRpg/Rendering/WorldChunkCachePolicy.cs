using IslandRpg.World;

namespace IslandRpg.Rendering;

internal static class WorldChunkCachePolicy
{
    public static bool IsActiveLevel(
        ChunkCoordinate coordinate,
        int activeLevel) =>
        coordinate.Level == activeLevel;

    public static bool IsOutsideRetentionRadius(
        ChunkCoordinate coordinate,
        ChunkCoordinate center,
        int retentionRadius) =>
        Math.Abs(coordinate.X - center.X) > retentionRadius ||
        Math.Abs(coordinate.Y - center.Y) > retentionRadius;
}
