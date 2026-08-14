using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private sealed record NetworkVegetationTarget(
        WorldVegetation Vegetation,
        string StableKey,
        SurfaceVegetationVisual Visual,
        ResourceNodeId NodeId,
        WorldChunkKey Chunk,
        Vector2 Position);

    private readonly record struct NetworkVegetationAction(
        ResourceActionKind Kind,
        NetworkVegetationTarget Target,
        int ToolInventorySlot,
        ItemDefinition? Sickle);

    private NetworkVegetationAction? _pendingNetworkVegetationAction;
    private NetworkVegetationAction? _activeNetworkVegetationAction;
    private bool _networkVegetationActionDispatched;
    private readonly Dictionary<string, NetworkVegetationTarget>
        _networkVegetationTargets = new(StringComparer.Ordinal);
    private readonly HashSet<WorldChunkKey> _networkVegetationChunks = [];

    private void QueueNetworkVegetationAction(
        string stableKey,
        ResourceActionKind action)
    {
        if (_player is null || _networkClient?.IsConnected != true ||
            !TryDescribeNetworkVegetation(stableKey, out var target) ||
            action != ExpectedVegetationAction(target.Visual.ResourceKind))
            return;
        if (!NetworkVegetationIsReady(target))
        {
            ReportBlockedAction(
                "network-vegetation-recovering",
                target.Visual.ResourceKind == ResourceNodeKind.BerryBush
                    ? "This bush needs time to grow more berries."
                    : "This shrub needs time to grow more usable fibres.");
            return;
        }

        var toolSlot = -1;
        ItemDefinition? sickle = null;
        if (action == ResourceActionKind.GatherBerries)
            TryFindNetworkSickleSlot(out toolSlot, out sickle);

        CancelNetworkResourceInteraction(stopPlayer: false);
        var pending = new NetworkVegetationAction(
            action, target, toolSlot, sickle);
        _pendingNetworkVegetationAction = pending;
        if (Vector2.DistanceSquared(NetworkActionPosition, target.Position) <=
            NetworkResourceDispatchRange * NetworkResourceDispatchRange)
        {
            BeginNetworkVegetationAction(pending);
            return;
        }
        SendNetworkWalk(target.Position, preserveResourceAction: true);
    }

    private bool UpdateNetworkVegetationInteraction()
    {
        if (_player is null) return false;
        if (_pendingNetworkVegetationAction is { } pending)
        {
            if (!NetworkVegetationActionStillValid(pending))
            {
                CancelNetworkResourceInteraction();
                return true;
            }
            if (Vector2.DistanceSquared(
                    NetworkActionPosition, pending.Target.Position) <=
                NetworkResourceDispatchRange * NetworkResourceDispatchRange)
                BeginNetworkVegetationAction(pending);
        }

        if (_activeNetworkVegetationAction is not { } active)
            return _pendingNetworkVegetationAction is not null;
        if (!NetworkVegetationActionStillValid(active))
        {
            CancelNetworkResourceInteraction();
            return true;
        }
        if (Vector2.DistanceSquared(
                _player.Position, active.Target.Position) >
            (NetworkResourceDispatchRange + .65f) *
            (NetworkResourceDispatchRange + .65f))
        {
            CancelNetworkResourceInteraction();
            _chatUi.AddMessage(
                "You move too far away to continue.",
                Rendering.Ui.ChatMessageStyle.Warning);
            return true;
        }

        if (_player.Action != EntityAction.Gather)
            _player.GatherAt(active.Target.Position);
        var duration = active.Kind == ResourceActionKind.GatherBerries
            ? FarmingSkill.GatherSeconds(active.Sickle)
            : GroundItemActionSeconds;
        if (_player.ActionTime >= duration &&
            !_networkVegetationActionDispatched &&
            _networkResourceCommandId is null)
        {
            DispatchNetworkVegetationAction(active);
        }
        return true;
    }

    private void BeginNetworkVegetationAction(NetworkVegetationAction action)
    {
        if (action.Kind == ResourceActionKind.GatherBerries)
        {
            TryFindNetworkSickleSlot(out var toolSlot, out var sickle);
            action = action with
            {
                ToolInventorySlot = toolSlot,
                Sickle = sickle
            };
        }
        _pendingNetworkVegetationAction = null;
        _activeNetworkVegetationAction = action;
        _networkVegetationActionDispatched = false;
        _networkResourceCommandId = null;
        _networkResourcePresentationOwned = true;
        SendNetworkStop(preserveResourceAction: true);
        _player!.GatherAt(action.Target.Position);
    }

    private void DispatchNetworkVegetationAction(
        NetworkVegetationAction action)
    {
        if (_networkClient?.IsConnected != true) return;

        var toolSlot = -1;
        ItemDefinition? sickle = null;
        if (action.Kind == ResourceActionKind.GatherBerries)
        {
            TryFindNetworkSickleSlot(out toolSlot, out sickle);
            action = action with
            {
                ToolInventorySlot = toolSlot,
                Sickle = sickle
            };
            _activeNetworkVegetationAction = action;
        }

        var reference = _networkClient.GetResourceReference(
            action.Target.Chunk, action.Target.NodeId);
        var commandId = Guid.NewGuid();
        _networkResourceCommandId = commandId;
        _networkResourceCommandReference = reference;
        _networkVegetationActionDispatched = true;
        ResetNetworkResourceExperienceObservation();
        SendNetworkAction(new ResourceActionPayload(
            action.Kind,
            reference,
            action.Kind == ResourceActionKind.GatherFibre ? -1 : toolSlot),
            commandId);
    }

    private bool TryDescribeNetworkVegetation(
        string stableKey,
        out NetworkVegetationTarget target)
    {
        target = null!;
        if (!IsNetworkWorld || _activeWorldLevel != 0) return false;
        if (_networkVegetationTargets.TryGetValue(stableKey, out target!))
            return true;
        var located = FindVegetation(stableKey);
        if (located is not { } source) return false;
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(
                source.Vegetation.X, source.Vegetation.Y),
            _activeWorldLevel);
        CacheNetworkVegetationChunk(source.Gpu, chunk);
        return _networkVegetationTargets.TryGetValue(stableKey, out target!);
    }

    private void CacheNetworkVegetationChunk(
        GpuWorldChunk gpu,
        WorldChunkKey chunk)
    {
        if (!_networkVegetationChunks.Add(chunk)) return;
        var placements = SurfaceVegetationCatalog.DescribeChunk(
                _worldSeed, chunk)
            .Where(static value => value.Visual.ResourceKind.HasValue)
            .ToDictionary(
                static value => (value.Position.X, value.Position.Y));
        foreach (var cached in gpu.VegetationRenderItems)
        {
            if (cached.VegetationIndex < 0 ||
                !placements.TryGetValue((
                        gpu.Chunk.Vegetation[cached.VegetationIndex].X,
                        gpu.Chunk.Vegetation[cached.VegetationIndex].Y),
                    out var placement))
                continue;
            var vegetation = gpu.Chunk.Vegetation[cached.VegetationIndex];
            if (placement.Visual.GraphicName.Equals(
                    vegetation.GraphicName,
                    StringComparison.OrdinalIgnoreCase) is false ||
                placement.Visual.FrameIndex != vegetation.FrameIndex ||
                placement.Visual.ResourceKind is not { } resourceKind)
                continue;
            var nodeId = ProceduralResourceIdentity.ForVegetation(
                _worldSeed,
                chunk,
                resourceKind,
                placement.SourceTileX,
                placement.SourceTileY,
                placement.Ordinal,
                placement.Visual.Variant);
            _networkVegetationTargets[cached.StableKey] = new(
                vegetation,
                cached.StableKey,
                placement.Visual,
                nodeId,
                chunk,
                new Vector2(placement.Position.X, placement.Position.Y));
        }
    }

    private bool TryGetNetworkVegetationState(
        NetworkVegetationTarget target,
        out ResourceNodeSparseState state)
    {
        state = null!;
        return _networkClient?.State.ResourceChunks.TryGetValue(
                   target.Chunk, out var chunk) == true &&
               chunk.Nodes.TryGetValue(target.NodeId, out state!);
    }

    private bool NetworkVegetationIsReady(NetworkVegetationTarget target)
    {
        if (!TryGetNetworkVegetationState(target, out var state)) return true;
        return !state.Depleted && state.Remaining > 0;
    }

    private bool NetworkVegetationBlocksWorld(string stableKey)
    {
        if (!_networkVegetationTargets.TryGetValue(stableKey, out var target) ||
            target.Visual.ResourceKind is not { } kind)
            return true;
        var state = TryGetNetworkVegetationState(target, out var current)
            ? current
            : null;
        return NetworkResourceObstacleRules.BlocksWorld(
            kind,
            target.Visual.RegrowthGameSeconds,
            state);
    }

    private bool NetworkVegetationActionStillValid(
        NetworkVegetationAction action) =>
        _networkClient?.IsConnected == true &&
        action.Target.Chunk.WorldLevel == _activeWorldLevel &&
        NetworkVegetationIsReady(action.Target);

    private static ResourceActionKind? ExpectedVegetationAction(
        ResourceNodeKind? kind) => kind switch
    {
        ResourceNodeKind.FibreShrub => ResourceActionKind.GatherFibre,
        ResourceNodeKind.BerryBush => ResourceActionKind.GatherBerries,
        _ => null
    };

    private bool TryFindNetworkSickleSlot(
        out int slot,
        out ItemDefinition? sickle)
    {
        slot = -1;
        sickle = null;
        var items = _activePlayer?.Inventory;
        if (items is null) return false;

        bool Usable(int index, out ItemDefinition definition)
        {
            definition = null!;
            if ((uint)index >= (uint)items.Length ||
                items[index] is not { } itemId ||
                !ItemCatalog.TryGet(itemId, out definition))
                return false;
            return definition.HasTag(ItemTag.Tool) &&
                   definition.HasTag(ItemTag.Sickle) &&
                   definition.FarmingPower > 0;
        }

        if (Usable(_activeInventorySlot, out var selected))
        {
            slot = _activeInventorySlot;
            sickle = selected;
            return true;
        }

        var bestPower = 0;
        for (var index = 0;
             index < Math.Min(items.Length, PlayerInventory.Capacity);
             index++)
        {
            if (!Usable(index, out var candidate) ||
                candidate.FarmingPower <= bestPower)
                continue;
            slot = index;
            sickle = candidate;
            bestPower = candidate.FarmingPower;
        }
        return slot >= 0;
    }

    private void HandleNetworkVegetationChanged(
        NetworkResourcesChangedEventArgs value)
    {
        if (_activeNetworkVegetationAction is not { } active) return;
        if (value.Changes.Any(change =>
                change.NodeId == active.Target.NodeId &&
                change.State is { Depleted: true }))
            CancelNetworkResourceInteraction();
    }

    private void ForgetNetworkVegetationChunk(ChunkCoordinate coordinate)
    {
        var chunk = new WorldChunkKey(
            coordinate.X, coordinate.Y, coordinate.Level);
        if ((_pendingNetworkVegetationAction is { } pending &&
             pending.Target.Chunk == chunk) ||
            (_activeNetworkVegetationAction is { } active &&
             active.Target.Chunk == chunk))
        {
            CancelNetworkResourceInteraction();
        }
        _networkVegetationChunks.Remove(chunk);
        if (_networkVegetationTargets.Count == 0) return;
        foreach (var key in _networkVegetationTargets
                     .Where(pair =>
                         pair.Value.Chunk.X == coordinate.X &&
                         pair.Value.Chunk.Y == coordinate.Y &&
                         pair.Value.Chunk.WorldLevel == coordinate.Level)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _networkVegetationTargets.Remove(key);
    }

    private void ClearNetworkVegetationProjection()
    {
        _pendingNetworkVegetationAction = null;
        _activeNetworkVegetationAction = null;
        _networkVegetationActionDispatched = false;
        _networkVegetationTargets.Clear();
        _networkVegetationChunks.Clear();
    }
}
