using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.World;

/// <summary>
/// Solo-world adapter over the headless canonical cave feature catalog.
/// </summary>
internal static class CaveFeaturePlacement
{
    public const int MaximumNodes =
        UndergroundMiningCatalog.MaximumNodesPerChunk;

    public static WorldVegetation[] Generate(
        long seed,
        ChunkCoordinate coordinate,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable)
    {
        // Tiles/renderable are retained for call-site compatibility. Their
        // values are produced from the same cave field the Core catalog now
        // samples, so resource authority and solo presentation cannot drift.
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(renderable);
        var water = tiles.Select(tile => tile.Biome is
                Biome.ShallowWater or Biome.RiverWater)
            .ToArray();
        return UndergroundMiningCatalog.Generate(
                seed,
                new WorldChunkKey(
                    coordinate.X, coordinate.Y, coordinate.Level),
                renderable,
                water)
            .Select(value => new WorldVegetation(
                value.Position.X,
                value.Position.Y,
                value.GraphicName,
                value.FrameIndex,
                WorldVegetationKind.Shrub,
                false))
            .ToArray();
    }
}
