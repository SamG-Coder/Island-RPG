using IslandRpg.Rendering.Ui;
using FontStashSharp;
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
            var color = samples[index] <= 16.67
                ? new Vector4(.28f, .78f, .37f, 1)
                : samples[index] <= 33.33
                    ? new Vector4(.92f, .67f, .22f, 1)
                    : new Vector4(.92f, .28f, .22f, 1);
            _uiColorBatch.AddLine(
                previousX,
                previousY,
                currentX,
                currentY,
                2,
                color,
                _uiOpacity);
        }
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
