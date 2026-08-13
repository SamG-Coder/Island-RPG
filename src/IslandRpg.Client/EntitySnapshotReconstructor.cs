using IslandRpg.Protocol;

namespace IslandRpg.Client;

/// <summary>
/// Reconstructs complete render frames from bounded UDP interest-window deltas.
/// Keyframes are the authority for entity membership; deltas may update or add
/// entities, but cannot imply that an omitted entity despawned.
/// </summary>
internal sealed class EntitySnapshotReconstructor
{
    private readonly object _sync = new();
    private Dictionary<ulong, TrackedEntity> _entities = [];
    private bool _hasKeyframe;
    private ulong _latestKeyframeTick;
    private ulong _latestFrameTick;

    public void Clear()
    {
        lock (_sync)
        {
            _entities.Clear();
            _hasKeyframe = false;
            _latestKeyframeTick = 0;
            _latestFrameTick = 0;
        }
    }

    public bool TryReconstruct(
        EntitySnapshotMessage snapshot,
        out ReconstructedEntitySnapshot reconstructed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        reconstructed = default;

        var flags = snapshot.Metadata.Flags;
        var isKeyframe = flags == SnapshotFlags.Keyframe;
        var isDelta = flags == SnapshotFlags.Delta;
        if ((!isKeyframe && !isDelta) ||
            snapshot.Tick != snapshot.Metadata.ServerTick ||
            snapshot.Entities.Count > ProtocolLimits.MaxSnapshotEntities ||
            HasDuplicateEntityIds(snapshot.Entities))
        {
            return false;
        }

        lock (_sync)
        {
            return isKeyframe
                ? TryApplyKeyframe(snapshot, out reconstructed)
                : TryApplyDelta(snapshot, out reconstructed);
        }
    }

    private bool TryApplyKeyframe(
        EntitySnapshotMessage snapshot,
        out ReconstructedEntitySnapshot reconstructed)
    {
        reconstructed = default;
        var keyframeTick = snapshot.Metadata.ServerTick;
        if (_hasKeyframe && keyframeTick <= _latestKeyframeTick)
            return false;

        // Build the replacement transactionally. A keyframe at K removes an
        // absent entity last observed at or before K. An entity observed after
        // K survives because it may have spawned after that keyframe; the next
        // keyframe removes it if it is absent there too. Newer transforms are
        // likewise never rolled back by delayed reliable delivery.
        var replacement = new Dictionary<ulong, TrackedEntity>(
            Math.Max(snapshot.Entities.Count, _entities.Count));
        foreach (var entity in snapshot.Entities)
        {
            replacement[entity.EntityId] =
                _entities.TryGetValue(entity.EntityId, out var current) &&
                current.ServerTick > keyframeTick
                    ? current
                    : new TrackedEntity(entity, keyframeTick);
        }

        foreach (var pair in _entities)
        {
            if (pair.Value.ServerTick > keyframeTick)
                replacement.TryAdd(pair.Key, pair.Value);
        }

        if (replacement.Count > ProtocolLimits.MaxSnapshotEntities)
            return false;

        _entities = replacement;
        _hasKeyframe = true;
        _latestKeyframeTick = keyframeTick;

        var effectiveTick = Math.Max(keyframeTick, _latestFrameTick);
        var replacesLatest = effectiveTick == _latestFrameTick &&
            _latestFrameTick != 0;
        _latestFrameTick = effectiveTick;
        reconstructed = Complete(snapshot, effectiveTick, replacesLatest);
        return true;
    }

    private bool TryApplyDelta(
        EntitySnapshotMessage snapshot,
        out ReconstructedEntitySnapshot reconstructed)
    {
        reconstructed = default;
        var tick = snapshot.Metadata.ServerTick;
        if (!_hasKeyframe || tick <= _latestFrameTick)
            return false;

        var additions = 0;
        foreach (var entity in snapshot.Entities)
        {
            if (!_entities.ContainsKey(entity.EntityId)) additions++;
        }
        if (_entities.Count + additions > ProtocolLimits.MaxSnapshotEntities)
            return false;

        foreach (var entity in snapshot.Entities)
            _entities[entity.EntityId] = new TrackedEntity(entity, tick);
        _latestFrameTick = tick;
        reconstructed = Complete(snapshot, tick, replacesLatest: false);
        return true;
    }

    private ReconstructedEntitySnapshot Complete(
        EntitySnapshotMessage source,
        ulong effectiveTick,
        bool replacesLatest)
    {
        var entities = _entities.Values
            .Select(static tracked => tracked.Entity)
            .OrderBy(static entity => entity.EntityId)
            .ToArray();
        var metadata = source.Metadata with
        {
            ServerTick = effectiveTick,
            BaselineTick = _latestKeyframeTick,
            Flags = SnapshotFlags.Keyframe,
        };
        return new ReconstructedEntitySnapshot(
            new EntitySnapshotMessage(
                source.Sequence,
                effectiveTick,
                metadata,
                Array.AsReadOnly(entities)),
            replacesLatest);
    }

    private static bool HasDuplicateEntityIds(
        IReadOnlyList<EntitySnapshot> entities)
    {
        if (entities.Count < 2) return false;
        var ids = new HashSet<ulong>();
        foreach (var entity in entities)
        {
            if (!ids.Add(entity.EntityId)) return true;
        }
        return false;
    }

    private readonly record struct TrackedEntity(
        EntitySnapshot Entity,
        ulong ServerTick);
}

internal readonly record struct ReconstructedEntitySnapshot(
    EntitySnapshotMessage Snapshot,
    bool ReplacesLatestFrame);
