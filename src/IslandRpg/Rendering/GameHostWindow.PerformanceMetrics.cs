using IslandRpg.Rendering.Ui;
using FontStashSharp;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly PerformanceMetricsOverlay _performanceMetrics = new();
    private bool _performanceMetricsEnabled;

    private void RenderPerformanceMetrics()
    {
        if (!_performanceMetricsEnabled) return;
        var metrics = _performanceMetrics.Snapshot();
        const float left = 16;
        const float top = 16;
        const float width = 286;
        const float height = 142;
        var panel = new Vector4(left, top, width, height);
        DrawUiColor(panel, new(.018f, .021f, .019f, .93f));
        DrawPanelOutline(panel, 0, new(.38f, .34f, .22f, 1));
        DrawUiText(
            "PERFORMANCE",
            new(left + 12, top + 9),
            new(203, 188, 139, 255));
        DrawUiText(
            $"{metrics.FramesPerSecond,5:0} FPS",
            new(left + 12, top + 31),
            MetricColor(metrics.AverageFrameMilliseconds));
        DrawUiText(
            $"{metrics.CurrentFrameMilliseconds,5:0.0} ms",
            new(left + 112, top + 31),
            MetricColor(metrics.CurrentFrameMilliseconds));
        DrawUiText(
            $"avg {metrics.AverageFrameMilliseconds:0.0} ms",
            new(left + 202, top + 31),
            new(163, 157, 137, 255));

        var graph = new Vector4(left + 12, top + 60, width - 24, 68);
        DrawUiColor(graph, new(.008f, .010f, .009f, .96f));
        DrawFrameTimeGuide(graph, 16.67, new(.20f, .42f, .25f, .7f));
        DrawFrameTimeGuide(graph, 33.33, new(.52f, .38f, .14f, .75f));
        DrawFrameTimeSamples(graph, metrics.FrameMilliseconds);
        DrawPanelOutline(graph, 0, new(.19f, .20f, .16f, 1));
        DrawUiText(
            "16.7", new(graph.X + 3, GuideY(graph, 16.67) - 14),
            new(106, 151, 111, 220));
        DrawUiText(
            "33.3", new(graph.X + 3, GuideY(graph, 33.33) - 14),
            new(174, 139, 77, 220));
    }

    private void DrawFrameTimeSamples(
        Vector4 graph, IReadOnlyList<double> samples)
    {
        if (samples.Count < 2) return;
        var fast = new List<float>(samples.Count * 8);
        var slow = new List<float>(samples.Count * 8);
        var stalled = new List<float>(samples.Count * 8);
        var step = graph.Z /
                   (PerformanceMetricsOverlay.HistoryLength - 1);
        var start = PerformanceMetricsOverlay.HistoryLength -
                    samples.Count;
        for (var index = 1; index < samples.Count; index++)
        {
            var previousX = graph.X + (start + index - 1) * step;
            var currentX = graph.X + (start + index) * step;
            var previousY = GuideY(graph, samples[index - 1]);
            var currentY = GuideY(graph, samples[index]);
            var vertices = samples[index] <= 16.67
                ? fast
                : samples[index] <= 33.33
                    ? slow
                    : stalled;
            AddUiLine(
                vertices, previousX, previousY, currentX, currentY);
        }
        DrawUiLines(fast, new(.28f, .78f, .37f, 1));
        DrawUiLines(slow, new(.92f, .67f, .22f, 1));
        DrawUiLines(stalled, new(.92f, .28f, .22f, 1));
    }

    private void AddUiLine(
        List<float> vertices,
        float x1, float y1,
        float x2, float y2)
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

    private void DrawUiLines(List<float> vertices, Vector4 color)
    {
        if (vertices.Count == 0 || _uiSolidTexture == 0) return;
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "image"), 0);
        GL.Uniform1(
            GL.GetUniformLocation(_program, "opacity"),
            color.W * _uiOpacity);
        GL.Uniform1(GL.GetUniformLocation(_program, "outlineOnly"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "wading"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "spriteOutline"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "brightness"), 0f);
        GL.Uniform3(
            GL.GetUniformLocation(_program, "colorTint"),
            color.X, color.Y, color.Z);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "grayscaleAmount"), 0f);
        GL.Uniform2(GL.GetUniformLocation(_program, "texelSize"), 1f, 1f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _uiSolidTexture);
        GL.Uniform1(GL.GetUniformLocation(_program, "recolorPlayer"), 0);
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
        GL.DrawArrays(
            PrimitiveType.Lines, 0, vertices.Count / 4);
        GL.LineWidth(1);
        GL.Uniform1(GL.GetUniformLocation(_program, "tintAmount"), 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "opacity"), 1f);
    }

    private void DrawFrameTimeGuide(
        Vector4 graph, double milliseconds, Vector4 color) =>
        DrawUiColor(
            new(graph.X, GuideY(graph, milliseconds), graph.Z, 1),
            color);

    private static float GuideY(Vector4 graph, double milliseconds)
    {
        var ratio = Math.Clamp(
            milliseconds /
            PerformanceMetricsOverlay.GraphMaximumMilliseconds,
            0, 1);
        return graph.Y + graph.W -
               (float)(ratio * (graph.W - 1));
    }

    private static FSColor MetricColor(double milliseconds) =>
        milliseconds <= 16.67
            ? new(111, 213, 126, 255)
            : milliseconds <= 33.33
                ? new(237, 184, 74, 255)
                : new(235, 87, 72, 255);
}
