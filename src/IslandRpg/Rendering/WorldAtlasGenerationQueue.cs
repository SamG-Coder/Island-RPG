using System.Diagnostics;
using IslandRpg.World;

namespace IslandRpg.Rendering;

internal readonly record struct WorldAtlasGenerationResult(
    WorldAtlasTileSnapshot Snapshot,
    double ElapsedMilliseconds);

internal sealed class WorldAtlasGenerationQueue : IDisposable
{
    private sealed record Work(
        CancellationTokenSource Cancellation,
        Task<WorldAtlasGenerationResult> Task,
        long StartedTimestamp);

    private const int MaximumConcurrency = 2;
    private readonly Dictionary<WorldAtlasTileKey, Work> _active = [];
    private IReadOnlyList<WorldAtlasTileKey> _desired = [];
    private HashSet<WorldAtlasTileKey> _desiredSet = [];
    private long _seed;
    private bool _disposed;

    public int ActiveCount => _active.Count;
    public double OldestActiveMilliseconds
    {
        get
        {
            if (_active.Count == 0) return 0;
            var oldest = _active.Values.Min(work =>
                work.StartedTimestamp);
            return Stopwatch.GetElapsedTime(oldest).TotalMilliseconds;
        }
    }
    public static int ConcurrencyLimit => MaximumConcurrency;
    public int DesiredCount => _desired.Count;
    public int CancelledCount { get; private set; }
    public int DiscardedCount { get; private set; }

    public void SetRequest(
        long seed,
        IReadOnlyList<WorldAtlasTileKey> desired,
        Func<WorldAtlasTileKey, bool> isCached)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _seed = seed;
        _desired = desired;
        _desiredSet = desired.ToHashSet();

        foreach (var key in _active.Keys
                     .Where(key => !_desiredSet.Contains(key))
                     .ToArray())
            Cancel(key);

        Pump(isCached);
    }

    public IReadOnlyList<WorldAtlasGenerationResult> DrainCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completed = new List<WorldAtlasGenerationResult>();
        foreach (var pair in _active
                     .Where(pair => pair.Value.Task.IsCompleted)
                     .ToArray())
        {
            _active.Remove(pair.Key);
            pair.Value.Cancellation.Dispose();
            if (pair.Value.Task.IsCanceled)
                continue;
            if (pair.Value.Task.IsFaulted)
                throw pair.Value.Task.Exception?.GetBaseException() ??
                      new InvalidOperationException(
                          "World atlas generation failed.");
            var result = pair.Value.Task.Result;
            if (!_desiredSet.Contains(pair.Key))
            {
                DiscardedCount++;
                continue;
            }
            completed.Add(result);
        }
        return completed;
    }

    public void CancelAll(Action? afterCompletion = null)
    {
        var tasks = _active.Values
            .Select(work => work.Task)
            .ToArray();
        foreach (var key in _active.Keys.ToArray())
            Cancel(key);
        _desired = [];
        _desiredSet.Clear();
        if (afterCompletion is null) return;
        if (tasks.Length == 0)
        {
            afterCompletion();
            return;
        }
        _ = Task.WhenAll(tasks).ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                afterCompletion();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Pump(Func<WorldAtlasTileKey, bool> isCached)
    {
        foreach (var key in _desired)
        {
            if (_active.Count >= MaximumConcurrency) break;
            if (isCached(key) || _active.ContainsKey(key)) continue;
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var seed = _seed;
            var task = Task.Run(
                () =>
                {
                    var timer = Stopwatch.StartNew();
                    using var hydrology =
                        MacroHydrology.BeginAtlasSampling();
                    var snapshot =
                        WorldAtlasGenerator.GenerateIsometricTile(
                            seed, key, token);
                    return new WorldAtlasGenerationResult(
                        snapshot, timer.Elapsed.TotalMilliseconds);
                },
                token);
            _active.Add(
                key,
                new(
                    cancellation,
                    task,
                    Stopwatch.GetTimestamp()));
        }
    }

    private void Cancel(WorldAtlasTileKey key)
    {
        if (!_active.Remove(key, out var work)) return;
        CancelledCount++;
        work.Cancellation.Cancel();
        Observe(work.Task);
        work.Cancellation.Dispose();
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed) return;
        CancelAll();
        _disposed = true;
    }
}
