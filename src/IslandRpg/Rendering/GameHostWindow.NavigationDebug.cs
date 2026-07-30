using IslandRpg.Gameplay;
using OpenTK.Graphics.OpenGL4;
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

        DrawNavigationLines(lines, new(.94f, .16f, .08f, .92f));
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
        AddNavigationLine(lines, north.X, north.Y, east.X, east.Y);
        AddNavigationLine(lines, east.X, east.Y, south.X, south.Y);
        AddNavigationLine(lines, south.X, south.Y, west.X, west.Y);
        AddNavigationLine(lines, west.X, west.Y, north.X, north.Y);
    }

    private void AddNavigationLine(
        List<float> vertices,
        float x1,
        float y1,
        float x2,
        float y2)
    {
        var width = Math.Max(1, ClientSize.X);
        var height = Math.Max(1, ClientSize.Y);
        vertices.Add((x1 - width * .5f) * 2 / width);
        vertices.Add(-(y1 - height * .5f) * 2 / height);
        vertices.Add(0);
        vertices.Add(0);
        vertices.Add((x2 - width * .5f) * 2 / width);
        vertices.Add(-(y2 - height * .5f) * 2 / height);
        vertices.Add(0);
        vertices.Add(0);
    }

    private void DrawNavigationLines(List<float> vertices, Vector4 color)
    {
        if (vertices.Count == 0 || _uiSolidTexture == 0) return;
        GL.UseProgram(_program);
        GL.Uniform1(_shaderUniforms.Get(_program, "image"), 0);
        GL.Uniform1(
            _shaderUniforms.Get(_program, "opacity"),
            color.W);
        GL.Uniform1(_shaderUniforms.Get(_program, "outlineOnly"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "wading"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "spriteOutline"), 0);
        GL.Uniform1(_shaderUniforms.Get(_program, "brightness"), 0f);
        GL.Uniform3(
            _shaderUniforms.Get(_program, "colorTint"),
            color.X, color.Y, color.Z);
        GL.Uniform1(_shaderUniforms.Get(_program, "tintAmount"), 1f);
        GL.Uniform1(_shaderUniforms.Get(_program, "grayscaleAmount"), 0f);
        GL.Uniform2(_shaderUniforms.Get(_program, "texelSize"), 1f, 1f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _uiSolidTexture);
        GL.Uniform1(_shaderUniforms.Get(_program, "recolorPlayer"), 0);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _streamVbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            vertices.Count * sizeof(float),
            vertices.ToArray(),
            BufferUsageHint.StreamDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(
            0, 2, VertexAttribPointerType.Float, false, 16, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(
            1, 2, VertexAttribPointerType.Float, false, 16, 8);
        GL.DisableVertexAttribArray(2);
        GL.VertexAttrib1(2, 1f);
        GL.DisableVertexAttribArray(3);
        GL.DisableVertexAttribArray(4);
        GL.LineWidth(2);
        GL.DrawArrays(PrimitiveType.Lines, 0, vertices.Count / 4);
        GL.LineWidth(1);
        GL.Uniform1(_shaderUniforms.Get(_program, "tintAmount"), 0f);
        GL.Uniform1(_shaderUniforms.Get(_program, "opacity"), 1f);
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
