using IslandRpg.Protocol;

namespace IslandRpg.Client;

/// <summary>
/// Budgeted ingest of a join-sized public baseline. The dedicated server can
/// publish hundreds of world objects in one generation; the presentation
/// thread must copy them in slices so a single update cannot reproduce the
/// 1000 ms post-"Connected" hitch. This type never waits on TCP or the
/// dedicated server.
/// </summary>
public sealed class NetworkPresentationApply
{
    public const int MaximumWorldObjectsPerSlice = 64;
    public const int MaximumEnemiesPerSlice = 32;
    public const int MaximumEventsPerUpdate = 16;

    private object? _worldGeneration;
    private NetworkWorldObjectState[] _worldPending = [];
    private int _worldCursor;
    private readonly Dictionary<Guid, NetworkWorldObjectState> _worldPresented = [];

    public IReadOnlyDictionary<Guid, NetworkWorldObjectState> PresentedWorldObjects =>
        _worldPresented;

    public bool HasPendingWorldObjects =>
        _worldGeneration is not null && _worldCursor < _worldPending.Length;

    /// <summary>
    /// Copies at most <see cref="MaximumWorldObjectsPerSlice"/> changed
    /// objects from the current client generation into the presented set.
    /// A live generation change does not restart a mid-pass; the in-flight
    /// copy finishes first. Removals are emitted only after the last slice
    /// so a mid-join view never drops objects that have not been visited yet.
    /// </summary>
    public NetworkPresentationSlice<NetworkWorldObjectState> ApplyWorldObjects(
        IReadOnlyDictionary<Guid, NetworkWorldObjectState> incoming,
        IReadOnlyDictionary<NetworkWorldChunk, uint>? chunkRevisions = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (!ReferenceEquals(_worldGeneration, incoming))
        {
            // The live client replaces WorldObjects on every reliable delta.
            // Restarting ToArray() mid-pass never finishes and recopies the
            // whole world every frame.
            if (_worldGeneration is null ||
                _worldCursor >= _worldPending.Length)
            {
                _worldGeneration = incoming;
                _worldPending = incoming.Count == 0
                    ? []
                    : incoming.Values.ToArray();
                _worldCursor = 0;
            }
        }

        var upserts = new List<NetworkWorldObjectState>(
            MaximumWorldObjectsPerSlice);
        for (; _worldCursor < _worldPending.Length &&
               upserts.Count < MaximumWorldObjectsPerSlice;
             _worldCursor++)
        {
            var state = _worldPending[_worldCursor];
            if (_worldPresented.TryGetValue(state.ObjectId, out var existing) &&
                existing.ObjectRevision == state.ObjectRevision &&
                existing.ChunkRevision == state.ChunkRevision)
            {
                _worldPresented[state.ObjectId] = state;
                continue;
            }

            _worldPresented[state.ObjectId] = state;
            upserts.Add(state);
        }

        List<NetworkPresentationRemoval>? removals = null;
        var complete = _worldCursor >= _worldPending.Length;
        if (complete)
        {
            foreach (var (id, previous) in _worldPresented)
            {
                if (incoming.ContainsKey(id)) continue;
                var chunk = new NetworkWorldChunk(
                    previous.ChunkX, previous.ChunkY, previous.WorldLevel);
                var currentChunk = previous.ChunkRevision;
                if (chunkRevisions is not null &&
                    chunkRevisions.TryGetValue(chunk, out var revision) &&
                    revision > 0)
                    currentChunk = revision;
                removals ??= [];
                removals.Add(new(
                    id,
                    previous.ObjectRevision,
                    currentChunk));
            }
            if (removals is not null)
            {
                foreach (var removal in removals)
                    _worldPresented.Remove(removal.ObjectId);
            }
        }

        return new(
            upserts,
            removals ?? (IReadOnlyList<NetworkPresentationRemoval>)
                Array.Empty<NetworkPresentationRemoval>(),
            complete,
            upserts.Count);
    }

    /// <summary>
    /// Turns one ingest slice into the same change records
    /// <c>NetworkGameClient.ApplyWorldObjectChanges</c> publishes: upserts
    /// carry the live object, removals carry (currentChunk, knownRevision).
    /// </summary>
    public static IReadOnlyList<NetworkWorldObjectChange> ToChanges(
        NetworkPresentationSlice<NetworkWorldObjectState> slice)
    {
        var changes = new List<NetworkWorldObjectChange>(
            slice.Upserts.Count + slice.Removals.Count);
        foreach (var state in slice.Upserts)
            changes.Add(new(
                WorldObjectDeltaKind.Upsert,
                state.ObjectId,
                state.ChunkRevision,
                state.ObjectRevision,
                state));
        foreach (var removal in slice.Removals)
            changes.Add(new(
                WorldObjectDeltaKind.Remove,
                removal.ObjectId,
                removal.CurrentChunkRevision,
                removal.PreviousObjectRevision,
                null));
        return changes;
    }

    /// <summary>
    /// Same Remove match the cave fill/restore observer uses.
    /// </summary>
    public static bool MatchesExpectedRemove(
        NetworkWorldObjectChange change,
        Guid objectId,
        uint previousObjectRevision,
        uint currentChunkRevision) =>
        change.Kind == WorldObjectDeltaKind.Remove &&
        (objectId == Guid.Empty || change.ObjectId == objectId) &&
        change.ChunkRevision == currentChunkRevision &&
        change.State is null &&
        change.ObjectRevision == previousObjectRevision;

    public void Reset()
    {
        _worldGeneration = null;
        _worldPending = [];
        _worldCursor = 0;
        _worldPresented.Clear();
    }
}

public readonly record struct NetworkPresentationRemoval(
    Guid ObjectId,
    uint PreviousObjectRevision,
    uint CurrentChunkRevision);

public readonly record struct NetworkPresentationSlice<T>(
    IReadOnlyList<T> Upserts,
    IReadOnlyList<NetworkPresentationRemoval> Removals,
    bool Complete,
    int Applied);