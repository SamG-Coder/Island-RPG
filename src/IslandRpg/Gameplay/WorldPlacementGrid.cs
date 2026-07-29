using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class WorldPlacementGrid
{
    public const float CellSize = .25f;
    public const int CellsPerTerrainTile = 4;

    public static int Cell(float coordinate) =>
        (int)MathF.Floor(coordinate / CellSize);

    public static float CellCenter(int cell) =>
        (cell + .5f) * CellSize;

    public static Vector2 CellCenter(int x, int y) =>
        new(CellCenter(x), CellCenter(y));

    public static float Snap(float coordinate) =>
        MathF.Round(coordinate / CellSize) * CellSize;

    public static float SnapWithFootprint(float coordinate, float footprint)
    {
        var half = footprint * .5f;
        return MathF.Round((coordinate - half) / CellSize) * CellSize + half;
    }
}
