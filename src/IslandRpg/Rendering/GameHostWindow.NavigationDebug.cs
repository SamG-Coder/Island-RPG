using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void RenderNavigationBlocks(Vector4 scene)
    {
        if (!_navigationBlocksToggle.IsChecked ||
            !_settingsMenu.DeveloperModeEnabled)
            return;

        var lines = new List<float>();
        foreach (var obstacle in ActiveNavigationObstacles())
        {
            var minimumX = WorldPlacementGrid.Cell(
                obstacle.Center.X - obstacle.Width * .5f - .18f);
            var maximumX = WorldPlacementGrid.Cell(
                obstacle.Center.X + obstacle.Width * .5f + .18f);
            var minimumY = WorldPlacementGrid.Cell(
                obstacle.Center.Y - obstacle.Depth * .5f - .18f);
            var maximumY = WorldPlacementGrid.Cell(
                obstacle.Center.Y + obstacle.Depth * .5f + .18f);

            for (var cellY = minimumY; cellY <= maximumY; cellY++)
            for (var cellX = minimumX; cellX <= maximumX; cellX++)
            {
                var center = WorldPlacementGrid.CellCenter(
                    cellX, cellY);
                if (!obstacle.Contains(center))
                    continue;
                AddNavigationCellOutline(
                    lines, scene, cellX, cellY);
            }
        }

        DrawUiLines(lines, new(.94f, .16f, .08f, .92f));
    }

    private void AddNavigationCellOutline(
        List<float> lines,
        Vector4 scene,
        int cellX,
        int cellY)
    {
        var size = WorldPlacementGrid.CellSize;
        var x = cellX * size;
        var y = cellY * size;
        var center = WorldPlacementGrid.CellCenter(cellX, cellY);
        var elevation = SamplePlayerTerrain(center.X, center.Y).Height;
        var north = NavigationPoint(scene, x, y, elevation);
        var east = NavigationPoint(scene, x + size, y, elevation);
        var south = NavigationPoint(
            scene, x + size, y + size, elevation);
        var west = NavigationPoint(scene, x, y + size, elevation);
        AddUiLine(lines, north.X, north.Y, east.X, east.Y);
        AddUiLine(lines, east.X, east.Y, south.X, south.Y);
        AddUiLine(lines, south.X, south.Y, west.X, west.Y);
        AddUiLine(lines, west.X, west.Y, north.X, north.Y);
    }

    private Vector2 NavigationPoint(
        Vector4 scene,
        float x,
        float y,
        float elevation)
    {
        var reference = SpriteAnchor(
            IsometricTerrainProjection.Project(x, y, elevation));
        var scale = scene.Z / ReferenceWidth;
        return new(
            scene.X + reference.X * scale,
            scene.Y + reference.Y * scale);
    }
}
