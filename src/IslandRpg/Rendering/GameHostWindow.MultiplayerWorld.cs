using IslandRpg.Client;
using IslandRpg.Protocol;
using IslandRpg.World;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly record struct GroundObjectRenderSource(
        WorldGroundObject Object,
        float Opacity);

    private readonly Dictionary<Guid, WorldGroundObject>
        _networkWorldObjects = [];
    private readonly Dictionary<Guid, ChunkCoordinate>
        _networkWorldObjectChunks = [];
    private readonly Dictionary<ChunkCoordinate, HashSet<Guid>>
        _networkWorldObjectIdsByChunk = [];
    private readonly HashSet<Guid> _networkKnownWorldObjectIds = [];
    private readonly List<GroundObjectRenderSource>
        _visibleGroundObjectBuffer = [];

    private void ApplyNetworkWorldObjectChanges(
        IReadOnlyList<NetworkWorldObjectChange> changes)
    {
        // Network callbacks are marshalled through _networkEvents, so this
        // projection is owned exclusively by the window update/render thread.
        if (!IsNetworkWorld ||
            _networkClient?.State.Status != NetworkGameClientStatus.Connected)
            return;
        foreach (var change in changes)
        {
            _networkKnownWorldObjectIds.Add(change.ObjectId);
            if (change.Kind == WorldObjectDeltaKind.Remove)
            {
                RemoveNetworkWorldObject(change.ObjectId);
                continue;
            }

            if (change.State is not { } state) continue;
            UpsertNetworkWorldObject(state);
            ContinueNetworkConstruction(change);
        }
    }

    private void SynchronizeNetworkWorldObjects(
        IEnumerable<NetworkWorldObjectState> objects)
    {
        foreach (var state in objects)
        {
            _networkKnownWorldObjectIds.Add(state.ObjectId);
            UpsertNetworkWorldObject(state);
        }
    }

    private void UpsertNetworkWorldObject(NetworkWorldObjectState state)
    {
        var chunk = new ChunkCoordinate(
            state.ChunkX, state.ChunkY, state.WorldLevel);
        if (_networkWorldObjectChunks.TryGetValue(
                state.ObjectId, out var previousChunk) &&
            previousChunk != chunk &&
            _networkWorldObjectIdsByChunk.TryGetValue(
                previousChunk, out var previousIds))
        {
            previousIds.Remove(state.ObjectId);
            if (previousIds.Count == 0)
                _networkWorldObjectIdsByChunk.Remove(previousChunk);
        }

        _networkWorldObjects[state.ObjectId] =
            ProjectNetworkWorldObject(state);
        _networkWorldObjectChunks[state.ObjectId] = chunk;
        if (!_networkWorldObjectIdsByChunk.TryGetValue(chunk, out var ids))
        {
            ids = [];
            _networkWorldObjectIdsByChunk.Add(chunk, ids);
        }
        ids.Add(state.ObjectId);
    }

    private void RemoveNetworkWorldObject(Guid id)
    {
        if (_networkRepeatedConstructionId == id)
            StopNetworkRepeatedConstruction();
        _networkWorldObjects.Remove(id);
        if (!_networkWorldObjectChunks.Remove(id, out var chunk) ||
            !_networkWorldObjectIdsByChunk.TryGetValue(chunk, out var ids))
            return;
        ids.Remove(id);
        if (ids.Count == 0)
            _networkWorldObjectIdsByChunk.Remove(chunk);
    }

    private void ClearNetworkWorldObjects()
    {
        _networkWorldObjects.Clear();
        _networkWorldObjectChunks.Clear();
        _networkWorldObjectIdsByChunk.Clear();
        _networkKnownWorldObjectIds.Clear();
        _visibleGroundObjectBuffer.Clear();
    }

    private static WorldGroundObject ProjectNetworkWorldObject(
        NetworkWorldObjectState state) => new(
        state.ObjectId,
        state.DefinitionId,
        state.X,
        state.Y,
        FuelItemId: string.IsNullOrEmpty(state.FuelItemId)
            ? null
            : state.FuelItemId,
        LitUntilGameSeconds: state.LitUntilGameSeconds,
        Health: state.Health,
        MaxHealth: state.MaximumHealth,
        // Rotation is the established visual-frame input for walls, gates,
        // houses and other construction objects.
        VisualFrame: state.Rotation,
        // Public world state intentionally never projects private container
        // slots. Those remain isolated in NetworkGameClient.Containers.
        Container: null,
        GateState: state.GateState switch
        {
            WorldObjectGateState.None or WorldObjectGateState.Unlocked =>
                Gameplay.GateAccessState.Unlocked,
            WorldObjectGateState.Opened => Gameplay.GateAccessState.Opened,
            WorldObjectGateState.Locked => Gameplay.GateAccessState.Locked,
            _ => Gameplay.GateAccessState.Unlocked,
        });

    private List<GroundObjectRenderSource> CollectVisibleGroundObjects(
        List<GpuWorldChunk> visibleChunks)
    {
        _visibleGroundObjectBuffer.Clear();
        foreach (var gpu in visibleChunks)
        foreach (var value in gpu.Chunk.GroundObjects)
        {
            if (IsNetworkWorld &&
                _networkKnownWorldObjectIds.Contains(value.Id))
                continue;
            _visibleGroundObjectBuffer.Add(new(value, gpu.Opacity));
        }

        if (!IsNetworkWorld) return _visibleGroundObjectBuffer;
        foreach (var gpu in visibleChunks)
        {
            var chunk = gpu.Chunk.Coordinate;
            if (!_networkWorldObjectIdsByChunk.TryGetValue(
                    chunk, out var ids))
                continue;
            foreach (var id in ids)
                if (_networkWorldObjects.TryGetValue(id, out var value))
                    _visibleGroundObjectBuffer.Add(new(value, gpu.Opacity));
        }
        return _visibleGroundObjectBuffer;
    }
}
