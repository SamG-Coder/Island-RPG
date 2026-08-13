using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class WorldPlacementGrid
{
    public const float CellSize =
        IslandRpg.Navigation.WorldPlacementGrid.CellSize;
    public const int CellsPerTerrainTile =
        IslandRpg.Navigation.WorldPlacementGrid.CellsPerTerrainTile;

    public static int Cell(float coordinate) =>
        IslandRpg.Navigation.WorldPlacementGrid.Cell(coordinate);

    public static float CellCenter(int cell) =>
        IslandRpg.Navigation.WorldPlacementGrid.CellCenter(cell);

    public static Vector2 CellCenter(int x, int y) =>
        new(CellCenter(x), CellCenter(y));

    public static float Snap(float coordinate) =>
        IslandRpg.Navigation.WorldPlacementGrid.Snap(coordinate);

    public static float SnapWithFootprint(float coordinate, float footprint)
    {
        return IslandRpg.Navigation.WorldPlacementGrid.SnapWithFootprint(
            coordinate, footprint);
    }
}
