using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Rendering.Ui;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float NetworkResourceDispatchRange = 2.6f;
    private const double NetworkTreeStrikeCadenceSeconds = 1.05;
    private const double NetworkResourceCommitRetrySeconds = .2;
    private const double NetworkResourceCommitTimeoutSeconds = 2;

    internal static bool NetworkResourceWindupReady(
        double actionTime,
        double durationSeconds,
        double clock,
        double commitAt) =>
        actionTime >= durationSeconds ||
        (commitAt > 0 && clock >= commitAt);

    internal static bool ShouldRetryNetworkResourceReject(
        bool accepted,
        CommandRejectionCode code,
        string? detail)
    {
        if (accepted) return false;
        if (code is CommandRejectionCode.OutOfOrder or
            CommandRejectionCode.RateLimited or
            CommandRejectionCode.ServerBusy)
            return true;
        if (code != CommandRejectionCode.Impossible ||
            string.IsNullOrWhiteSpace(detail))
            return false;
        return detail.Contains("range", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("reach", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("revision", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NetworkTreeTarget(
        IslandTree Tree,
        SurfaceTreeVisual Visual,
        ResourceNodeId NodeId,
        WorldChunkKey Chunk,
        Vector2 Position);

    private readonly record struct NetworkTreeAction(
        ResourceActionKind Kind,
        NetworkTreeTarget Target,
        int ToolInventorySlot);

    private NetworkTreeAction? _pendingNetworkTreeAction;
    private NetworkTreeAction? _activeNetworkTreeAction;
    private Guid? _networkResourceCommandId;
    private ResourceNodeReference? _networkResourceCommandReference;
    private int _lastNetworkTreeStrike;
    private double _nextNetworkTreeStrikeAt;
    private bool _networkResourcePresentationOwned;
    private double _networkResourceCommitAt;
    private double _networkWorldActionCommitAt;
    private uint _networkResourceAwaitingActorRevision;
    private uint _networkResourceAwaitingInventoryRevision;
    private int _networkResourceExperienceGained;
    private int _networkResourcePreviousLevel;
    private int _networkResourceCurrentLevel;
    private int _networkResourceFarmingExperienceGained;
    private int _networkResourceFarmingPreviousLevel;
    private int _networkResourceFarmingCurrentLevel;
    private int _networkResourceMiningExperienceGained;
    private int _networkResourceMiningPreviousLevel;
    private int _networkResourceMiningCurrentLevel;
    private int _networkResourceAdventureExperienceGained;
    private int _networkResourceAdventurePreviousLevel;
    private int _networkResourceAdventureCurrentLevel;
    private readonly Dictionary<long, NetworkTreeTarget>
        _networkTreeTargets = [];
    private readonly HashSet<long> _networkTreeDescribeMisses = [];
    private readonly NetworkResourceHotPath _networkResourceHotPath = new();

    private void QueueNetworkTreeAction(
        IslandTree tree,
        ResourceActionKind action)
    {
        if (_player is null || _networkClient?.IsConnected != true ||
            !TryDescribeNetworkTree(tree, out var target))
            return;
        if (NetworkTreeIsDepleted(target))
        {
            ReportBlockedAction(
                "network-tree-depleted",
                "That tree has already been felled.");
            return;
        }

        var toolSlot = -1;
        if (action == ResourceActionKind.CutTree &&
            !TryFindNetworkAxeSlot(out toolSlot))
        {
            ReportBlockedAction(
                "network-chop-without-axe",
                PlayerInventory.HasAnyAxe(_activePlayer?.Inventory)
                    ? "Your axe is too blunt. You need small rocks to sharpen it."
                    : "You need an axe to chop down this tree.");
            return;
        }

        CancelNetworkResourceInteraction(stopPlayer: false);
        var pending = new NetworkTreeAction(action, target, toolSlot);
        _pendingNetworkTreeAction = pending;
        var range = TreeInteractionDistance(tree.GraphicName);
        if (WorldActionReach.InRange(
                NetworkActionPosition, target.Position, range))
        {
            BeginNetworkTreeAction(pending);
            return;
        }
        QueueNetworkWalkToAct(
            target.Position,
            range,
            action == ResourceActionKind.CutTree
                ? WorldActionType.CutTree
                : WorldActionType.GatherTreeSticks);
    }

    private void UpdateNetworkResourceInteraction()
    {
        if (_player is null) return;
        if (UpdateNetworkFishingInteraction()) return;
        if (UpdateNetworkVegetationInteraction()) return;
        if (UpdateNetworkMiningInteraction()) return;
        if (_pendingNetworkTreeAction is { } pending)
        {
            if (!NetworkTreeActionStillValid(pending))
            {
                CancelNetworkResourceInteraction();
                return;
            }
            if (WorldActionReach.InRange(
                    NetworkActionPosition,
                    pending.Target.Position,
                    TreeInteractionDistance(pending.Target.Tree.GraphicName)))
                BeginNetworkTreeAction(pending);
        }

        if (_activeNetworkTreeAction is not { } active) return;
        if (!NetworkTreeActionStillValid(active))
        {
            CancelNetworkResourceInteraction();
            return;
        }
        if (Vector2.DistanceSquared(
                _player.Position, active.Target.Position) >
            (NetworkResourceDispatchRange + .65f) *
            (NetworkResourceDispatchRange + .65f))
        {
            CancelNetworkResourceInteraction();
            _chatUi.AddMessage(
                "You move too far away to continue.",
                ChatMessageStyle.Warning);
            return;
        }

        if (active.Kind == ResourceActionKind.GatherTreeStick)
        {
            if (_player.Action != EntityAction.Gather)
                _player.GatherAt(active.Target.Position);
            RetryNetworkResourceCommitIfTimedOut();
            if (NetworkResourceWindupReady(
                    _player.ActionTime, GroundItemActionSeconds, _clock,
                    _networkResourceCommitAt) &&
                _networkResourceCommandId is null)
                DispatchNetworkResourceAction(active);
            return;
        }

        if (_player.Action != EntityAction.Work)
            _player.WorkAt(active.Target.Position);
        if (_networkResourceCommandId is not null ||
            IsAwaitingNetworkResourceGameplayState() ||
            _clock < _nextNetworkTreeStrikeAt ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Work), out var animation))
            return;

        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(
            (int)MathF.Round((framesPerAngle - 1) * .43f),
            0,
            framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastNetworkTreeStrike) return;
        _lastNetworkTreeStrike = strike;
        DispatchNetworkResourceAction(active);
    }

    private void BeginNetworkTreeAction(NetworkTreeAction action)
    {
        _pendingNetworkTreeAction = null;
        _activeNetworkTreeAction = action;
        _networkResourceCommandId = null;
        _lastNetworkTreeStrike = 0;
        _nextNetworkTreeStrikeAt = 0;
        _networkResourcePresentationOwned = true;
        _networkResourceCommitAt = action.Kind == ResourceActionKind.CutTree
            ? 0
            : _clock + GroundItemActionSeconds;
        SendNetworkPresentSkill(
            action.Kind == ResourceActionKind.CutTree
                ? EntityAction.Work
                : EntityAction.Gather);
        if (action.Kind == ResourceActionKind.CutTree)
        {
            _player!.WorkAt(action.Target.Position);
            _player.RestartActionTime();
            _chatUi.AddMessage(
                $"You begin cutting the " +
                $"{TreeDisplayName(action.Target.Visual.GraphicName)}.",
                ChatMessageStyle.Action);
        }
        else
        {
            _player!.GatherAt(action.Target.Position);
            _player.RestartActionTime();
        }
    }

    private void DispatchNetworkResourceAction(NetworkTreeAction action)
    {
        if (_networkClient?.IsConnected != true) return;
        var toolSlot = action.ToolInventorySlot;
        if (action.Kind == ResourceActionKind.CutTree)
        {
            if (!TryFindNetworkAxeSlot(out toolSlot))
            {
                ReportBlockedAction(
                    "network-chop-without-axe",
                    "You no longer have a usable axe.");
                CancelNetworkResourceInteraction();
                return;
            }
            action = action with { ToolInventorySlot = toolSlot };
            _activeNetworkTreeAction = action;
        }
        var reference = _networkClient.GetResourceReference(
            action.Target.Chunk, action.Target.NodeId);
        var commandId = Guid.NewGuid();
        _networkResourceCommandId = commandId;
        _networkResourceCommandReference = reference;
        ResetNetworkResourceExperienceObservation();
        if (action.Kind == ResourceActionKind.CutTree)
        {
            _nextNetworkTreeStrikeAt =
                _clock + NetworkTreeStrikeCadenceSeconds;
            PlaySoundCue("woodcutting-impact");
        }
        SendNetworkAction(new ResourceActionPayload(
            action.Kind,
            reference,
            toolSlot), commandId);
    }

    private bool TryDescribeNetworkTree(
        IslandTree tree,
        out NetworkTreeTarget target)
    {
        target = null!;
        if (!IsNetworkWorld || _activeWorldLevel != 0)
            return false;
        var tileKey = WorldHoverSelection.TileKey(tree.X, tree.Y);
        if (_networkTreeTargets.TryGetValue(tileKey, out target!))
            return target.Tree.GraphicName.Equals(
                       tree.GraphicName, StringComparison.OrdinalIgnoreCase) &&
                   target.Tree.FrameIndex == tree.FrameIndex;
        if (_networkTreeDescribeMisses.Contains(tileKey))
            return false;
        if (
            !SurfaceTreeCatalog.TryDescribeAt(
                _worldSeed, tree.X, tree.Y, out var visual) ||
            visual.FrameIndex != tree.FrameIndex ||
            !visual.GraphicName.Equals(
                tree.GraphicName, StringComparison.OrdinalIgnoreCase))
        {
            _networkTreeDescribeMisses.Add(tileKey);
            return false;
        }
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(tree.X + .5f, tree.Y + .5f),
            _activeWorldLevel);
        var nodeId = ProceduralResourceIdentity.ForTree(
            _worldSeed,
            _activeWorldLevel,
            tree.X,
            tree.Y,
            visual.Variant);
        target = new NetworkTreeTarget(
            tree,
            visual,
            nodeId,
            chunk,
            new Vector2(tree.X + .5f, tree.Y + .5f));
        _networkTreeTargets[tileKey] = target;
        _networkResourceHotPath.RememberTree(tileKey, nodeId, chunk);
        return true;
    }

    private bool TryGetNetworkTreeState(
        NetworkTreeTarget target,
        out ResourceNodeSparseState state)
    {
        state = null!;
        return _networkClient?.State.ResourceChunks.TryGetValue(
                   target.Chunk, out var chunk) == true &&
               chunk.Nodes.TryGetValue(target.NodeId, out state!);
    }

    private bool NetworkTreeIsDepleted(NetworkTreeTarget target) =>
        TryGetNetworkTreeState(target, out var state) &&
        (state.Depleted || state.Health <= 0);

    private bool IsNetworkTreeDepleted(IslandTree tree)
    {
        var tileKey = WorldHoverSelection.TileKey(tree.X, tree.Y);
        return _networkResourceHotPath.IsTreeDepleted(
            tileKey,
            _networkClient?.State.ResourceChunks);
    }

    private bool NetworkTreeBlocksWorld(IslandTree tree)
    {
        var tileKey = WorldHoverSelection.TileKey(tree.X, tree.Y);
        return _networkResourceHotPath.TreeBlocks(
            tileKey,
            _networkClient?.State.ResourceChunks);
    }

    private bool NetworkTreeActionStillValid(NetworkTreeAction action)
    {
        if (_networkClient?.IsConnected != true ||
            action.Target.Chunk.WorldLevel != _activeWorldLevel)
            return false;
        if (NetworkTreeIsDepleted(action.Target)) return false;
        if (action.Kind == ResourceActionKind.CutTree)
            return TryFindNetworkAxeSlot(out _);
        if (TryGetNetworkTreeState(action.Target, out var state) &&
            state.Remaining <= 0)
            return false;
        return true;
    }

    private bool TryFindNetworkAxeSlot(out int slot)
    {
        slot = -1;
        var items = _activePlayer?.Inventory;
        if (items is null) return false;

        bool Usable(int index)
        {
            if ((uint)index >= (uint)items.Length ||
                items[index] is not { } itemId ||
                !ItemCatalog.TryGet(itemId, out var item))
                return false;
            return item.HasTag(ItemTag.Tool) &&
                   item.HasTag(ItemTag.Axe) &&
                   item.WoodcuttingPower > 0;
        }

        if (Usable(_activeInventorySlot))
        {
            slot = _activeInventorySlot;
            return true;
        }

        var bestPower = 0;
        for (var index = 0;
             index < Math.Min(items.Length, PlayerInventory.Capacity);
             index++)
        {
            if (!Usable(index)) continue;
            var power = ItemCatalog.Get(items[index]!).WoodcuttingPower;
            if (power <= bestPower) continue;
            bestPower = power;
            slot = index;
        }
        if (slot >= 0) return true;

        // The authority owns automatic sharpening and inventory consumption.
        // A blunt stone axe is selectable only when the required rock exists.
        if (!items.Any(value => value == ItemIds.SmallRocks)) return false;
        slot = Array.FindIndex(items, value =>
            value == ItemIds.BluntStoneAxe);
        return slot >= 0;
    }

    private void HandleNetworkResourcesChanged(
        NetworkResourcesChangedEventArgs value)
    {
        if (!IsNetworkWorld) return;

        HandleNetworkVegetationChanged(value);
        HandleNetworkMiningChanged(value);
        if (_activeNetworkTreeAction is { } active &&
            value.Changes.Any(change =>
                change.NodeId == active.Target.NodeId &&
                change.State is { Depleted: true }))
        {
            _chatUi.AddMessage(
                $"The {TreeDisplayName(active.Target.Visual.GraphicName)} falls.",
                ChatMessageStyle.Action);
            CancelNetworkResourceInteraction();
        }
    }

    private void HandleNetworkResourceActionResult(
        ResourceActionResultMessage result)
    {
        if (_networkResourceCommandId != result.CommandId ||
            (_activeNetworkTreeAction is null &&
             _activeNetworkVegetationAction is null &&
             _activeNetworkMiningAction is null &&
             _activeNetworkFishingAction is null))
            return;
        _networkResourceCommandId = null;
        var expectedReference = _networkResourceCommandReference;
        _networkResourceCommandReference = null;
        if (!result.Accepted &&
            ShouldRetryNetworkResourceReject(
                result.Accepted, result.RejectionCode, result.Detail))
        {
            _networkVegetationActionDispatched = false;
            _networkResourceCommitAt = _clock + NetworkResourceCommitRetrySeconds;
            return;
        }
        var activeKind = _activeNetworkTreeAction?.Kind ??
                         _activeNetworkVegetationAction?.Kind ??
                         (_activeNetworkFishingAction is not null
                             ? ResourceActionKind.Fish
                             : ResourceActionKind.Mine);
        if (result.Action != activeKind ||
            expectedReference is null ||
            result.Resource != expectedReference.Value)
        {
            _chatUi.AddMessage(
                "The server returned a resource result for a different action.",
                ChatMessageStyle.Warning);
            CancelNetworkResourceInteraction();
            return;
        }
        if (!result.Accepted)
        {
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected the resource action " +
                      $"({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
            CancelNetworkResourceInteraction();
            return;
        }

        _networkResourceAwaitingActorRevision = result.ActorRevision;
        _networkResourceAwaitingInventoryRevision = result.InventoryRevision;
        if (result.Action == ResourceActionKind.CutTree &&
            _activeNetworkTreeAction is { } active)
        {
            ShowEntityImpact(
                TreeFeedbackKey(active.Target.NodeId.Value),
                result.Hit ? result.Damage : 0,
                result.Hit);
            _chatUi.AddMessage(
                result.Hit
                    ? $"You hit the " +
                      $"{TreeDisplayName(active.Target.Visual.GraphicName)} " +
                      $"for {result.Damage} damage."
                    : "You miss the tree.",
                result.Hit
                    ? ChatMessageStyle.Damage
                    : ChatMessageStyle.Miss);
            if (result.ToolWorn)
                _chatUi.AddMessage(
                    "Your stone axe becomes blunt.",
                    ChatMessageStyle.Warning);
        }
        else if (result.Action == ResourceActionKind.GatherTreeStick)
            _chatUi.AddMessage(
                "You gather a stick from beneath the tree.",
                ChatMessageStyle.Action);
        else if (_activeNetworkVegetationAction is { } vegetation)
        {
            _chatUi.AddMessage(
                vegetation.Kind == ResourceActionKind.GatherBerries
                    ? "You pick berries from the bush."
                    : "You gather usable plant fibres.",
                ChatMessageStyle.Action);
            if (!string.IsNullOrWhiteSpace(result.Detail))
                _chatUi.AddMessage(
                    result.Detail, ChatMessageStyle.Warning);
        }
        else if (result.Action == ResourceActionKind.Mine &&
                 _activeNetworkMiningAction is { } mining)
        {
            ShowEntityImpact(
                MiningFeedbackKey(mining.Target.StableKey),
                result.Hit ? result.Damage : 0,
                result.Hit);
            _chatUi.AddMessage(
                result.Hit
                    ? $"You hit the {mining.Target.Visual.DisplayName} " +
                      $"for {result.Damage} damage."
                    : $"You miss the {mining.Target.Visual.DisplayName}.",
                result.Hit
                    ? ChatMessageStyle.Damage
                    : ChatMessageStyle.Miss);
            if (!string.IsNullOrWhiteSpace(result.Detail))
                _chatUi.AddMessage(
                    result.Detail, ChatMessageStyle.Warning);
        }
        else if (result.Action == ResourceActionKind.Fish &&
                 _activeNetworkFishingAction is { } fishing)
        {
            var caught = result.FishingOutcome is { Caught: true };
            _entityFeedback.ShowLabel(
                FishFeedbackKey(fishing.Target.Fish.StableKey),
                caught ? "Caught" : "Miss",
                caught,
                _clock);
        }

        foreach (var reward in result.Rewards)
        {
            if (reward.Quantity <= 0 ||
                !ItemCatalog.TryGet(reward.ItemId, out var item))
                continue;
            _chatUi.AddMessage(
                reward.Quantity == 1
                    ? $"You receive {item.Name}."
                    : $"You receive {reward.Quantity} {item.Name}.",
                ChatMessageStyle.Experience);
            if (result.Action == ResourceActionKind.Mine)
                RecordQuestEvent(new(
                    QuestEventType.MineOre,
                    reward.ItemId));
        }
        if (_networkResourceExperienceGained > 0)
        {
            _chatUi.AddMessage(
                $"+{_networkResourceExperienceGained} Woodcutting XP.",
                ChatMessageStyle.Experience);
            if (_networkResourceCurrentLevel >
                _networkResourcePreviousLevel)
                _chatUi.AddMessage(
                    $"Your Woodcutting level is now " +
                    $"{_networkResourceCurrentLevel}.",
                    ChatMessageStyle.LevelUp);
        }
        if (_networkResourceFarmingExperienceGained > 0)
        {
            _chatUi.AddMessage(
                FarmingSkill.ExperienceMessage(
                    _networkResourceFarmingExperienceGained),
                ChatMessageStyle.Experience);
            if (_networkResourceFarmingCurrentLevel >
                _networkResourceFarmingPreviousLevel)
                _chatUi.AddMessage(
                    FarmingSkill.LevelUpMessage(
                        _networkResourceFarmingCurrentLevel),
                    ChatMessageStyle.LevelUp);
        }
        if (_networkResourceMiningExperienceGained > 0)
        {
            _chatUi.AddMessage(
                $"+{_networkResourceMiningExperienceGained} Mining XP.",
                ChatMessageStyle.Experience);
            if (_networkResourceMiningCurrentLevel >
                _networkResourceMiningPreviousLevel)
                _chatUi.AddMessage(
                    $"Your Mining level is now " +
                    $"{_networkResourceMiningCurrentLevel}.",
                    ChatMessageStyle.LevelUp);
        }
        if (_networkResourceAdventureExperienceGained > 0)
        {
            _chatUi.AddMessage(
                $"+{_networkResourceAdventureExperienceGained} Adventure XP.",
                ChatMessageStyle.Experience);
            if (_networkResourceAdventureCurrentLevel >
                _networkResourceAdventurePreviousLevel)
                _chatUi.AddMessage(
                    $"Your Adventure level is now " +
                    $"{_networkResourceAdventureCurrentLevel}.",
                    ChatMessageStyle.LevelUp);
        }
        ResetNetworkResourceExperienceObservation();
        if (result.Action is ResourceActionKind.GatherTreeStick or
            ResourceActionKind.GatherFibre or
            ResourceActionKind.GatherBerries)
            CancelNetworkResourceInteraction(
                preserveGameplayRevisionWait: true);
    }

    private void ObserveNetworkResourceGameplayState(
        NetworkPlayerGameplayState state,
        int previousWoodcuttingExperience,
        int previousFarmingExperience,
        int previousMiningExperience,
        int previousAdventureExperience)
    {
        if (_networkResourceCommandId is not null &&
            state.WoodcuttingExperience > previousWoodcuttingExperience)
        {
            _networkResourceExperienceGained =
                state.WoodcuttingExperience -
                previousWoodcuttingExperience;
            _networkResourcePreviousLevel =
                WoodcuttingSkill.LevelForExperience(
                    previousWoodcuttingExperience);
            _networkResourceCurrentLevel =
                WoodcuttingSkill.LevelForExperience(
                    state.WoodcuttingExperience);
        }
        if (_networkResourceCommandId is not null &&
            state.FarmingExperience > previousFarmingExperience)
        {
            _networkResourceFarmingExperienceGained =
                state.FarmingExperience - previousFarmingExperience;
            _networkResourceFarmingPreviousLevel =
                FarmingSkill.LevelForExperience(previousFarmingExperience);
            _networkResourceFarmingCurrentLevel =
                FarmingSkill.LevelForExperience(state.FarmingExperience);
        }
        if (_networkResourceCommandId is not null &&
            state.MiningExperience > previousMiningExperience)
        {
            _networkResourceMiningExperienceGained =
                state.MiningExperience - previousMiningExperience;
            _networkResourceMiningPreviousLevel =
                MiningSkill.LevelForExperience(previousMiningExperience);
            _networkResourceMiningCurrentLevel =
                MiningSkill.LevelForExperience(state.MiningExperience);
        }
        if (_networkResourceCommandId is not null &&
            state.AdventureExperience > previousAdventureExperience)
        {
            _networkResourceAdventureExperienceGained =
                state.AdventureExperience - previousAdventureExperience;
            _networkResourceAdventurePreviousLevel =
                AdventureService.LevelForExperience(
                    previousAdventureExperience);
            _networkResourceAdventureCurrentLevel =
                AdventureService.LevelForExperience(
                    state.AdventureExperience);
        }
        if (state.ActorRevision >= _networkResourceAwaitingActorRevision &&
            state.InventoryRevision >=
            _networkResourceAwaitingInventoryRevision)
        {
            _networkResourceAwaitingActorRevision = 0;
            _networkResourceAwaitingInventoryRevision = 0;
        }
    }

    private bool IsAwaitingNetworkResourceGameplayState()
    {
        if (_networkResourceAwaitingActorRevision == 0 &&
            _networkResourceAwaitingInventoryRevision == 0)
            return false;
        var gameplay = _networkClient?.State.Gameplay;
        if (gameplay is not null &&
            gameplay.ActorRevision >= _networkResourceAwaitingActorRevision &&
            gameplay.InventoryRevision >=
            _networkResourceAwaitingInventoryRevision)
        {
            _networkResourceAwaitingActorRevision = 0;
            _networkResourceAwaitingInventoryRevision = 0;
            return false;
        }
        return true;
    }

    private void CancelNetworkResourceInteraction(
        bool stopPlayer = true,
        bool preserveGameplayRevisionWait = false)
    {
        _pendingNetworkTreeAction = null;
        _activeNetworkTreeAction = null;
        _pendingNetworkVegetationAction = null;
        _activeNetworkVegetationAction = null;
        _pendingNetworkMiningAction = null;
        _activeNetworkMiningAction = null;
        _networkVegetationActionDispatched = false;
        _networkResourceCommandId = null;
        _networkResourceCommandReference = null;
        _networkResourceCommitAt = 0;
        _lastNetworkTreeStrike = 0;
        _nextNetworkTreeStrikeAt = 0;
        ResetNetworkResourceExperienceObservation();
        if (!preserveGameplayRevisionWait)
        {
            _networkResourceAwaitingActorRevision = 0;
            _networkResourceAwaitingInventoryRevision = 0;
        }
        _lastNetworkMiningStrike = 0;
        _nextNetworkMiningStrikeAt = 0;
        ClearNetworkFishingAction();
        if (stopPlayer && _networkResourcePresentationOwned)
        {
            if (_player?.Action is EntityAction.Work or EntityAction.Gather or
                EntityAction.Mine or EntityAction.Fish)
                _player.Stop();
            SendNetworkPresentSkill(EntityAction.Idle);
        }
        _networkResourcePresentationOwned = false;
    }

    private void ClearNetworkResourceProjection()
    {
        CancelNetworkResourceInteraction();
        _networkTreeTargets.Clear();
        _networkTreeDescribeMisses.Clear();
        _networkFishDescriptors.Clear();
        _networkResourceHotPath.Clear();
        ClearNetworkVegetationProjection();
        ClearNetworkMiningProjection();
    }

    private void ForgetNetworkResourceChunk(ChunkCoordinate coordinate)
    {
        if (!IsNetworkWorld) return;
        var forgotten = new WorldChunkKey(
            coordinate.X, coordinate.Y, coordinate.Level);
        _networkResourceHotPath.ForgetChunk(forgotten);
        foreach (var key in _networkFishDescriptors
                     .Where(pair => pair.Value.Chunk == forgotten)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _networkFishDescriptors.Remove(key);
        ForgetNetworkVegetationChunk(coordinate);
        ForgetNetworkMiningChunk(coordinate);
        foreach (var key in _networkTreeTargets
                     .Where(pair =>
                         pair.Value.Chunk.X == coordinate.X &&
                         pair.Value.Chunk.Y == coordinate.Y &&
                         pair.Value.Chunk.WorldLevel == coordinate.Level)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _networkTreeTargets.Remove(key);
    }

    private void RememberNetworkChunkResources(GpuWorldChunk gpu)
    {
        var level = gpu.Chunk.Coordinate.Level;
        foreach (var item in gpu.FishRenderItems)
        {
            var fish = item.Fish;
            _networkResourceHotPath.RememberFishFromWorld(
                _worldSeed,
                level,
                fish.X,
                fish.Y,
                (int)fish.Species,
                fish.StableKey);
        }
        if (level != 0) return;
        foreach (var tree in gpu.Chunk.Trees)
        {
            if (!SurfaceTreeCatalog.TryDescribeAt(
                    _worldSeed, tree.X, tree.Y, out var visual) ||
                visual.FrameIndex != tree.FrameIndex ||
                !visual.GraphicName.Equals(
                    tree.GraphicName, StringComparison.OrdinalIgnoreCase))
                continue;
            _networkResourceHotPath.RememberTreeFromWorld(
                _worldSeed,
                level,
                tree.X,
                tree.Y,
                visual.Variant);
        }
    }

    private void RetryNetworkResourceCommitIfTimedOut()
    {
        if (_networkResourceCommandId is null ||
            _networkResourceCommitAt <= 0 ||
            _clock < _networkResourceCommitAt +
            NetworkResourceCommitTimeoutSeconds)
            return;
        _networkResourceCommandId = null;
        _networkResourceCommandReference = null;
        _networkVegetationActionDispatched = false;
        _networkResourceCommitAt = _clock + NetworkResourceCommitRetrySeconds;
    }

    private void ResetNetworkResourceExperienceObservation()
    {
        _networkResourceExperienceGained = 0;
        _networkResourcePreviousLevel = 0;
        _networkResourceCurrentLevel = 0;
        _networkResourceFarmingExperienceGained = 0;
        _networkResourceFarmingPreviousLevel = 0;
        _networkResourceFarmingCurrentLevel = 0;
        _networkResourceMiningExperienceGained = 0;
        _networkResourceMiningPreviousLevel = 0;
        _networkResourceMiningCurrentLevel = 0;
        _networkResourceAdventureExperienceGained = 0;
        _networkResourceAdventurePreviousLevel = 0;
        _networkResourceAdventureCurrentLevel = 0;
    }

    private void RenderNetworkTreeHealthBars(Vector4 scene)
    {
        if (_activeNetworkTreeAction is not { } active)
            return;
        var tree = active.Target.Tree;
        if (!_treeAtlas.TryGetValue(
                WorldTreeCatalog.AtlasKey(tree), out var entry))
            return;
        var hasState = TryGetNetworkTreeState(active.Target, out var state);
        var health = hasState
            ? state.Health
            : active.Target.Visual.MaximumHealth;
        var terrain = SamplePlayerTerrain(tree.X + .5f, tree.Y + .5f);
        var world = new Vector2(
            (tree.X - tree.Y) * 48,
            (tree.X + tree.Y + 1) * 24 - terrain.Height * 20);
        DrawEntityFeedback(
            scene,
            SpriteBounds(entry.Frame, world),
            health / (float)Math.Max(1, active.Target.Visual.MaximumHealth),
            TreeFeedbackKey(active.Target.NodeId.Value),
            forceHealth: true);
    }
}
