using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const double NetworkMiningStrikeCadenceSeconds = 1.05;

    private sealed record NetworkMiningTarget(
        WorldVegetation Node,
        string StableKey,
        UndergroundMiningVisual Visual,
        ResourceNodeId NodeId,
        WorldChunkKey Chunk,
        Vector2 Position);

    private readonly record struct NetworkMiningAction(
        NetworkMiningTarget Target,
        int ToolInventorySlot);

    private NetworkMiningAction? _pendingNetworkMiningAction;
    private NetworkMiningAction? _activeNetworkMiningAction;
    private int _lastNetworkMiningStrike;
    private double _nextNetworkMiningStrikeAt;
    private readonly Dictionary<string, NetworkMiningTarget>
        _networkMiningTargets = new(StringComparer.Ordinal);
    private readonly HashSet<WorldChunkKey> _networkMiningChunks = [];

    private void QueueNetworkMiningAction(string stableKey)
    {
        if (_player is null || _networkClient?.IsConnected != true ||
            !TryDescribeNetworkMining(stableKey, out var target))
            return;
        if (NetworkMiningIsDepleted(target))
        {
            ReportBlockedAction(
                "network-mining-depleted",
                "That mining node has already been depleted.");
            return;
        }
        if (!TryFindNetworkPickaxeSlot(out var toolSlot))
        {
            ReportBlockedAction(
                "network-mining-pickaxe",
                "You need a pickaxe to mine this.");
            return;
        }

        CancelNetworkResourceInteraction(stopPlayer: false);
        var pending = new NetworkMiningAction(target, toolSlot);
        _pendingNetworkMiningAction = pending;
        const float miningRange = WorldActionReach.Mining;
        if (WorldActionReach.InRange(
                NetworkActionPosition, target.Position, miningRange))
        {
            BeginNetworkMiningAction(pending);
            return;
        }
        QueueNetworkWalkToAct(
            target.Position,
            miningRange,
            WorldActionType.Mine,
            vegetationKey: stableKey);
    }

    private bool UpdateNetworkMiningInteraction()
    {
        if (_player is null) return false;
        if (_pendingNetworkMiningAction is { } pending)
        {
            if (!NetworkMiningActionStillValid(pending))
            {
                CancelNetworkResourceInteraction();
                return true;
            }
            if (WorldActionReach.InRange(
                    NetworkActionPosition,
                    pending.Target.Position,
                    WorldActionReach.Mining))
                BeginNetworkMiningAction(pending);
        }

        if (_activeNetworkMiningAction is not { } active)
            return _pendingNetworkMiningAction is not null;
        if (!NetworkMiningActionStillValid(active))
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
                "You move too far away to continue mining.",
                Rendering.Ui.ChatMessageStyle.Warning);
            return true;
        }

        if (_player.Action != EntityAction.Mine)
            _player.MineAt(active.Target.Position);
        if (_networkResourceCommandId is not null ||
            IsAwaitingNetworkResourceGameplayState() ||
            _clock < _nextNetworkMiningStrikeAt ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Mine), out var animation))
            return true;

        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(9, 0, framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return true;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastNetworkMiningStrike) return true;
        _lastNetworkMiningStrike = strike;
        DispatchNetworkMiningAction(active);
        return true;
    }

    private void BeginNetworkMiningAction(NetworkMiningAction action)
    {
        if (!TryFindNetworkPickaxeSlot(out var toolSlot))
        {
            CancelNetworkResourceInteraction();
            return;
        }
        action = action with { ToolInventorySlot = toolSlot };
        _pendingNetworkMiningAction = null;
        _activeNetworkMiningAction = action;
        _networkResourceCommandId = null;
        _lastNetworkMiningStrike = 0;
        _nextNetworkMiningStrikeAt = 0;
        _networkResourcePresentationOwned = true;
        _networkResourceCommitAt = 0;
        SendNetworkPresentSkill(EntityAction.Mine);
        _player!.MineAt(action.Target.Position);
        _player.RestartActionTime();
        _chatUi.AddMessage(
            $"You begin mining the {action.Target.Visual.DisplayName}.",
            Rendering.Ui.ChatMessageStyle.Action);
    }

    private void DispatchNetworkMiningAction(NetworkMiningAction action)
    {
        if (_networkClient?.IsConnected != true) return;
        if (!TryFindNetworkPickaxeSlot(out var toolSlot))
        {
            ReportBlockedAction(
                "network-mining-pickaxe",
                "You no longer have a usable pickaxe.");
            CancelNetworkResourceInteraction();
            return;
        }

        action = action with { ToolInventorySlot = toolSlot };
        _activeNetworkMiningAction = action;
        var reference = _networkClient.GetResourceReference(
            action.Target.Chunk, action.Target.NodeId);
        var commandId = Guid.NewGuid();
        _networkResourceCommandId = commandId;
        _networkResourceCommandReference = reference;
        _nextNetworkMiningStrikeAt =
            _clock + NetworkMiningStrikeCadenceSeconds;
        ResetNetworkResourceExperienceObservation();
        PlaySoundCue("mining-impact");
        SendNetworkAction(new ResourceActionPayload(
            ResourceActionKind.Mine,
            reference,
            toolSlot), commandId);
    }

    private bool TryDescribeNetworkMining(
        string stableKey,
        out NetworkMiningTarget target)
    {
        target = null!;
        if (!IsNetworkWorld ||
            _activeWorldLevel != (int)WorldLevel.Underground)
            return false;
        if (_networkMiningTargets.TryGetValue(stableKey, out target!))
            return true;
        var located = FindMiningNode(stableKey);
        if (located is not { } source) return false;
        var chunk = new WorldChunkKey(
            source.Gpu.Chunk.Coordinate.X,
            source.Gpu.Chunk.Coordinate.Y,
            source.Gpu.Chunk.Coordinate.Level);
        CacheNetworkMiningChunk(source.Gpu, chunk);
        return _networkMiningTargets.TryGetValue(stableKey, out target!);
    }

    private void CacheNetworkMiningChunk(
        GpuWorldChunk gpu,
        WorldChunkKey chunk)
    {
        if (!_networkMiningChunks.Add(chunk)) return;
        var features = UndergroundMiningCatalog.Generate(_worldSeed, chunk);
        var count = Math.Min(features.Count, gpu.Chunk.Vegetation.Length);
        for (var index = 0; index < count; index++)
        {
            var feature = features[index];
            var node = gpu.Chunk.Vegetation[index];
            if (!UndergroundMiningCatalog.TryGetVisual(
                    feature.GraphicName, out var visual) ||
                !feature.GraphicName.Equals(
                    node.GraphicName, StringComparison.OrdinalIgnoreCase) ||
                feature.FrameIndex != node.FrameIndex ||
                feature.Position.X != node.X ||
                feature.Position.Y != node.Y)
                continue;
            var nodeId = ProceduralResourceIdentity.ForMining(
                _worldSeed,
                chunk,
                feature.SourceTileX,
                feature.SourceTileY,
                feature.Ordinal,
                (int)visual.Variant);
            var stableKey = WorldMiningIdentity.StableKey(node, index);
            _networkMiningTargets[stableKey] = new(
                node,
                stableKey,
                visual,
                nodeId,
                chunk,
                new Vector2(feature.Position.X, feature.Position.Y));
        }
    }

    private bool TryGetNetworkMiningState(
        NetworkMiningTarget target,
        out ResourceNodeSparseState state)
    {
        state = null!;
        return _networkClient?.State.ResourceChunks.TryGetValue(
                   target.Chunk, out var chunk) == true &&
               chunk.Nodes.TryGetValue(target.NodeId, out state!);
    }

    private bool NetworkMiningIsDepleted(NetworkMiningTarget target) =>
        TryGetNetworkMiningState(target, out var state) &&
        (state.Depleted || state.Health <= 0);

    private bool IsNetworkMiningDepleted(string stableKey) =>
        _networkMiningTargets.TryGetValue(stableKey, out var target) &&
        NetworkMiningIsDepleted(target);

    private bool NetworkMiningBlocksWorld(string stableKey)
    {
        if (!_networkMiningTargets.TryGetValue(stableKey, out var target))
            return true;
        var state = TryGetNetworkMiningState(target, out var current)
            ? current
            : null;
        return NetworkResourceObstacleRules.BlocksWorld(
            ResourceNodeKind.MiningNode,
            regrowthGameSeconds: 0,
            state);
    }

    private bool NetworkMiningActionStillValid(NetworkMiningAction action) =>
        _networkClient?.IsConnected == true &&
        action.Target.Chunk.WorldLevel == _activeWorldLevel &&
        !NetworkMiningIsDepleted(action.Target) &&
        TryFindNetworkPickaxeSlot(out _);

    private bool TryFindNetworkPickaxeSlot(out int slot)
    {
        slot = -1;
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
                   definition.HasTag(ItemTag.Pickaxe) &&
                   definition.MiningPower > 0;
        }

        // Solo mining always chooses the strongest carried pickaxe. Resolve
        // its concrete slot here because the authority validates that exact
        // slot instead of accepting client-authored tool statistics.
        var bestPower = 0;
        for (var index = 0;
             index < Math.Min(items.Length, PlayerInventory.Capacity);
             index++)
        {
            if (!Usable(index, out var candidate) ||
                candidate.MiningPower <= bestPower)
                continue;
            slot = index;
            bestPower = candidate.MiningPower;
        }
        return slot >= 0;
    }

    private void HandleNetworkMiningChanged(
        IslandRpg.Client.NetworkResourcesChangedEventArgs value)
    {
        if (_activeNetworkMiningAction is not { } active) return;
        if (!value.Changes.Any(change =>
                change.NodeId == active.Target.NodeId &&
                change.State is { Depleted: true }))
            return;
        _chatUi.AddMessage(
            $"The {active.Target.Visual.DisplayName} is depleted.",
            Rendering.Ui.ChatMessageStyle.Action);
        CancelNetworkResourceInteraction();
    }

    private void ForgetNetworkMiningChunk(ChunkCoordinate coordinate)
    {
        var chunk = new WorldChunkKey(
            coordinate.X, coordinate.Y, coordinate.Level);
        if ((_pendingNetworkMiningAction is { } pending &&
             pending.Target.Chunk == chunk) ||
            (_activeNetworkMiningAction is { } active &&
             active.Target.Chunk == chunk))
            CancelNetworkResourceInteraction();
        _networkMiningChunks.Remove(chunk);
        foreach (var key in _networkMiningTargets
                     .Where(pair => pair.Value.Chunk == chunk)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _networkMiningTargets.Remove(key);
    }

    private void ClearNetworkMiningProjection()
    {
        _pendingNetworkMiningAction = null;
        _activeNetworkMiningAction = null;
        _lastNetworkMiningStrike = 0;
        _nextNetworkMiningStrikeAt = 0;
        _networkMiningTargets.Clear();
        _networkMiningChunks.Clear();
    }

    private void RenderNetworkMiningHealthBars(Vector4 scene)
    {
        if (_activeWorldLevel != (int)WorldLevel.Underground) return;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu)) continue;
            foreach (var cached in gpu.VegetationRenderItems)
            {
                if (cached.VegetationIndex < 0 ||
                    !MiningNodeCatalog.TryGet(
                        gpu.Chunk.Vegetation[cached.VegetationIndex], out _))
                    continue;
                if (!_networkMiningTargets.TryGetValue(
                        cached.StableKey, out var target) ||
                    NetworkMiningIsDepleted(target))
                    continue;
                var hasState = TryGetNetworkMiningState(target, out var state);
                var health = hasState
                    ? state.Health
                    : target.Visual.MaximumHealth;
                var active = _activeNetworkMiningAction?.Target.NodeId ==
                             target.NodeId;
                var feedbackKey = MiningFeedbackKey(target.StableKey);
                if (health >= target.Visual.MaximumHealth && !active &&
                    !_entityFeedback.HealthVisible(feedbackKey, _clock))
                    continue;
                if (!_treeAtlas.TryGetValue(cached.AtlasKey, out var entry))
                    continue;
                DrawEntityFeedback(
                    scene,
                    SpriteBounds(entry.Frame, cached.World),
                    health / (float)Math.Max(1, target.Visual.MaximumHealth),
                    feedbackKey,
                    forceHealth: active);
            }
        }
    }
}
