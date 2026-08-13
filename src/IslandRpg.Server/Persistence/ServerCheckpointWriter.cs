using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace IslandRpg.Server.Persistence;

/// <summary>
/// Serializes checkpoints away from the 60 Hz authority thread. Capacity one
/// deliberately coalesces bursts to the newest immutable state; revisions in
/// <see cref="ServerCheckpointStore"/> prevent late writes from moving durable
/// state backwards.
/// </summary>
public sealed class ServerCheckpointWriter : IAsyncDisposable
{
    private readonly ServerCheckpointStore _store;
    private readonly Channel<ServerCheckpoint> _pending;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _sync = new();
    private readonly List<FlushWaiter> _waiters = [];
    private ExceptionDispatchInfo? _failure;
    private long _acceptedRevision;
    private long _durableRevision;
    private int _disposed;

    public ServerCheckpointWriter(ServerCheckpointStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pending = Channel.CreateBounded<ServerCheckpoint>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        _worker = Task.Run(WriteLoopAsync);
    }

    public long AcceptedRevision => Volatile.Read(ref _acceptedRevision);

    public long DurableRevision => Volatile.Read(ref _durableRevision);

    public bool TryQueue(ServerCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0, this);
            ThrowIfFaultedLocked();
            if (checkpoint.Revision <= _acceptedRevision) return false;

            // Revision admission and channel publication are one ordered
            // operation. Without this lock, a delayed producer could publish
            // revision N after N+1 and evict the newer checkpoint from the
            // capacity-one coalescing channel.
            if (!_pending.Writer.TryWrite(checkpoint))
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0, this);
                ThrowIfFaultedLocked();
                return false;
            }

            Volatile.Write(ref _acceptedRevision, checkpoint.Revision);
            return true;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposedOrFaulted();
        var target = Volatile.Read(ref _acceptedRevision);
        if (Volatile.Read(ref _durableRevision) >= target)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ThrowIfFaultedLocked();
            if (_durableRevision >= target) return Task.CompletedTask;
            _waiters.Add(new FlushWaiter(target, completion));
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pending.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }

        lock (_sync) ThrowIfFaultedLocked();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var checkpoint in _pending.Reader.ReadAllAsync(
                               _shutdown.Token).ConfigureAwait(false))
            {
                _store.Save(checkpoint);
                Volatile.Write(ref _durableRevision, checkpoint.Revision);
                CompleteWaiters(checkpoint.Revision);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                foreach (var waiter in _waiters)
                    waiter.Completion.TrySetException(exception);
                _waiters.Clear();
            }
        }
    }

    private void CompleteWaiters(long durableRevision)
    {
        lock (_sync)
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var waiter = _waiters[index];
                if (waiter.Revision > durableRevision) continue;
                _waiters.RemoveAt(index);
                waiter.Completion.TrySetResult();
            }
        }
    }

    private void ThrowIfDisposedOrFaulted()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        lock (_sync) ThrowIfFaultedLocked();
    }

    private void ThrowIfFaultedLocked() => _failure?.Throw();

    private sealed record FlushWaiter(
        long Revision,
        TaskCompletionSource Completion);
}
