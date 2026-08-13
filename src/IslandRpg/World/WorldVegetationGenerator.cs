using IslandRpg.Resources;

namespace IslandRpg.World;

/// <summary>
/// Solo-world adapter over the headless canonical surface vegetation policy.
/// Keeping this layer free of generation decisions prevents network and solo
/// resource identity, placement, variants and visuals from drifting apart.
/// </summary>
internal static class WorldVegetationGenerator
{
    public const int MinimumCoastalFibreSourcesPerChunk =
        SurfaceVegetationCatalog.MinimumCoastalFibreSourcesPerChunk;

    public static readonly string[] RequiredGraphicNames =
        SurfaceVegetationCatalog.RequiredGraphicNames.ToArray();

    public static bool IsVegetationGraphic(string name) =>
        SurfaceVegetationCatalog.IsVegetationGraphic(name);

    public static WorldVegetation[] Generate(
        long seed,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyCollection<IslandTree> trees)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(trees);

        var canonicalTiles = tiles
            .Select(static tile => new SurfaceVegetationTile(
                tile.X,
                tile.Y,
                (ProceduralSurfaceTerrain.Material)tile.Biome,
                (ProceduralSurfaceTerrain.Region)tile.Region,
                tile.North,
                tile.East,
                tile.South,
                tile.West))
            .ToArray();
        var treeTiles = trees
            .Select(static tree => (tree.X, tree.Y))
            .ToArray();
        return SurfaceVegetationCatalog.Generate(
                seed, canonicalTiles, treeTiles)
            .Select(static value => new WorldVegetation(
                value.Position.X,
                value.Position.Y,
                value.Visual.GraphicName,
                value.Visual.FrameIndex,
                (WorldVegetationKind)value.Visual.Kind,
                value.Visual.CanBecomeInstance))
            .ToArray();
    }
}
