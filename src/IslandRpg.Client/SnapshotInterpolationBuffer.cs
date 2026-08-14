using System.Diagnostics;
using IslandRpg.Protocol;

namespace IslandRpg.Client;

public sealed record InterpolatedSnapshot(
    ulong OlderServerTick,
    ulong NewerServerTick,
    float Blend,
    IReadOnlyList<EntitySnapshot> Entities);

/// <summary>
/// Thread-safe receipt-time interpolation buffer. A 100 ms delay holds roughly
/// two frames at the intended 20 Hz snapshot rate, absorbing ordinary jitter.
/// </summary>
public sealed class SnapshotInterpolationBuffer
{
    private readonly object _sync = new();
    private readonly List<BufferedFrame> _frames;
    private readonly long _delayTimestampTicks;
    private readonly int _capacity;

    public SnapshotInterpolationBuffer(TimeSpan? renderDelay = null, int capacity = 8)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        var delay = renderDelay ?? TimeSpan.FromMilliseconds(100);
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(renderDelay));
        _delayTimestampTicks = (long)(delay.TotalSeconds * Stopwatch.Frequency);
        _capacity = capacity;
        _frames = new List<BufferedFrame>(capacity);
    }

    public int Count
    {
        get
        {
            lock (_sync) return _frames.Count;
        }
    }

    public void Clear()
    {
        lock (_sync) _frames.Clear();
    }

    public bool Add(EntitySnapshotMessage snapshot, long receivedTimestamp = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        receivedTimestamp = receivedTimestamp == 0 ? Stopwatch.GetTimestamp() : receivedTimestamp;
        var entities = snapshot.Entities.ToArray();
        lock (_sync)
        {
            if (_frames.Count > 0 && snapshot.Metadata.ServerTick <= _frames[^1].ServerTick)
                return false;
            _frames.Add(new BufferedFrame(receivedTimestamp, snapshot.Metadata.ServerTick, entities));
            if (_frames.Count > _capacity) _frames.RemoveRange(0, _frames.Count - _capacity);
            return true;
        }
    }

    /// <summary>
    /// Replaces the newest frame after a delayed keyframe reconciles entity
    /// membership without changing the already-observed effective server tick.
    /// </summary>
    internal bool ReplaceLatest(EntitySnapshotMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entities = snapshot.Entities.ToArray();
        lock (_sync)
        {
            if (_frames.Count == 0 ||
                snapshot.Metadata.ServerTick != _frames[^1].ServerTick)
            {
                return false;
            }

            // Preserve receipt time so reconciliation cannot introduce an
            // artificial interpolation stall for the same simulation frame.
            _frames[^1] = new BufferedFrame(
                _frames[^1].ReceivedTimestamp,
                snapshot.Metadata.ServerTick,
                entities);
            return true;
        }
    }

    public bool TrySample(out InterpolatedSnapshot? snapshot, long nowTimestamp = 0)
    {
        nowTimestamp = nowTimestamp == 0 ? Stopwatch.GetTimestamp() : nowTimestamp;
        BufferedFrame older;
        BufferedFrame newer;
        float blend;
        lock (_sync)
        {
            if (_frames.Count == 0)
            {
                snapshot = null;
                return false;
            }

            var target = nowTimestamp - _delayTimestampTicks;
            var newerIndex = _frames.FindIndex(frame => frame.ReceivedTimestamp >= target);
            if (newerIndex <= 0)
            {
                snapshot = Copy(newerIndex == 0 ? _frames[0] : _frames[^1]);
                return true;
            }

            older = _frames[newerIndex - 1];
            newer = _frames[newerIndex];
            var duration = Math.Max(1, newer.ReceivedTimestamp - older.ReceivedTimestamp);
            blend = Math.Clamp((float)(target - older.ReceivedTimestamp) / duration, 0, 1);
            if (newerIndex > 1) _frames.RemoveRange(0, newerIndex - 1);
        }

        // Interpolate outside the lock so the UDP receive thread can still
        // publish the next frame. Holding the lock across ToDictionary here
        // is the render-thread stall the live window hits after join.
        snapshot = blend <= 0 ? Copy(older) : Interpolate(older, newer, blend);
        return true;
    }

    private static InterpolatedSnapshot Copy(BufferedFrame frame) =>
        new(frame.ServerTick, frame.ServerTick, 0, frame.Entities);

    private static InterpolatedSnapshot Interpolate(BufferedFrame older, BufferedFrame newer, float blend)
    {
        var newerById = newer.Entities.ToDictionary(static entity => entity.EntityId);
        var result = new List<EntitySnapshot>(Math.Max(older.Entities.Length, newer.Entities.Length));
        foreach (var previous in older.Entities)
        {
            if (!newerById.Remove(previous.EntityId, out var current))
            {
                result.Add(previous);
                continue;
            }

            result.Add(current with
            {
                X = Lerp(previous.X, current.X, blend),
                Y = Lerp(previous.Y, current.Y, blend),
                VelocityX = Lerp(previous.VelocityX, current.VelocityX, blend),
                VelocityY = Lerp(previous.VelocityY, current.VelocityY, blend),
            });
        }

        if (blend >= 0.5f) result.AddRange(newerById.Values);
        return new InterpolatedSnapshot(
            older.ServerTick,
            newer.ServerTick,
            blend,
            result.AsReadOnly());
    }

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);

    private sealed record BufferedFrame(long ReceivedTimestamp, ulong ServerTick, EntitySnapshot[] Entities);
}
