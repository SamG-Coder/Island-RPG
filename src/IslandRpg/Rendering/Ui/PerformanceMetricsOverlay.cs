namespace IslandRpg.Rendering.Ui;

internal sealed record PerformanceMetricsSnapshot(
    double FramesPerSecond,
    double CurrentFrameMilliseconds,
    double AverageFrameMilliseconds,
    IReadOnlyList<double> FrameMilliseconds);

internal sealed class PerformanceMetricsOverlay
{
    public const int HistoryLength = 120;
    public const double GraphMaximumMilliseconds = 50;
    private readonly double[] _frameMilliseconds =
        new double[HistoryLength];
    private int _nextSample;
    private int _sampleCount;

    public void RecordFrame(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            return;
        _frameMilliseconds[_nextSample] =
            Math.Min(elapsedSeconds * 1000, 1000);
        _nextSample = (_nextSample + 1) % HistoryLength;
        _sampleCount = Math.Min(_sampleCount + 1, HistoryLength);
    }

    public PerformanceMetricsSnapshot Snapshot()
    {
        if (_sampleCount == 0)
            return new(0, 0, 0, []);

        var samples = new double[_sampleCount];
        var first = (_nextSample - _sampleCount + HistoryLength) %
                    HistoryLength;
        var total = 0d;
        for (var index = 0; index < _sampleCount; index++)
        {
            var value = _frameMilliseconds[
                (first + index) % HistoryLength];
            samples[index] = value;
            total += value;
        }
        var average = total / samples.Length;
        return new(
            average > 0 ? 1000 / average : 0,
            samples[^1],
            average,
            samples);
    }
}
