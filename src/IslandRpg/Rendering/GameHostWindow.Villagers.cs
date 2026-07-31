using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using FontStashSharp;
using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly List<VillagerState> _villagers = [];
    private readonly List<VillagerWorldObject>
        _villagerWorldObjects = [];
    private readonly HashSet<Guid> _villagerReservedObjects = [];
    private readonly List<SocialActorObservation>
        _socialActorObservations = [];
    private double _villagersNextSaveAt;
    private bool _villagersDirty;
    private readonly Dictionary<string, VillagerSpeechBubble>
        _villagerSpeechBubbles = [];
    private readonly Queue<string> _queuedPlayerConversationTurns = [];
    private string? _conversationFloorSpeakerId;
    private double _conversationFloorUntil;
    private sealed record VillagerSpeechBubble(
        string Text, double ExpiresAt);

    private void LoadVillagers(Vector2 spawn)
    {
        _villagers.Clear();
        _villagerSpeechBubbles.Clear();
        _queuedPlayerConversationTurns.Clear();
        _conversationFloorSpeakerId = null;
        _conversationFloorUntil = 0;
        if (_activeWorld is null) return;
        if (!_activeWorld.AiNpcsEnabled ||
            _activeWorld.AiNpcCount <= 0)
        {
            _villagersNextSaveAt = double.PositiveInfinity;
            return;
        }
        var saved = _saves.LoadVillagers(_activeWorld.Id);
        if (saved.Count > 0)
            _villagers.AddRange(saved.Select(value =>
                VillagerSimulation.CatchUp(
                    value, _worldGameSeconds)));
        else
        {
            _villagers.AddRange(
                VillagerSimulation.CreateInitial(
                    _worldSeed,
                    spawn,
                    candidate => WorldLevelNavigation.IsWalkable(
                        _worldSeed,
                        (int)MathF.Floor(candidate.X),
                        (int)MathF.Floor(candidate.Y),
                        (int)WorldLevel.Overworld),
                    gameSeconds: _worldGameSeconds,
                    population: _activeWorld.AiNpcCount,
                    personas: _activeWorld.AiNpcPersonas));
            _villagersDirty = true;
        }
        _villagersNextSaveAt = _worldGameSeconds + 30;
    }

    private void UpdateVillagers(float elapsed)
    {
        if (_player is null || _activeWorld is null) return;
        UpdateConversationTurns();
        _villagerReservedObjects.Clear();
        foreach (var villager in _villagers)
            if (villager.GoalObjectId is { } goal)
                _villagerReservedObjects.Add(goal);
        for (var index = 0; index < _villagers.Count; index++)
        {
            var previous = _villagers[index];
            if (previous.Health <= 0)
            {
                if (previous.Action != EntityAction.Die)
                {
                    _villagers[index] = previous with
                    {
                        Action = EntityAction.Die,
                        ActionTime = 0,
                        TargetX = null,
                        TargetY = null,
                        FollowingActorId = null
                    };
                    _villagersDirty = true;
                }
                else
                    _villagers[index] = previous with
                    {
                        ActionTime = previous.ActionTime + elapsed
                    };
                continue;
            }
            if (previous.Activity == VillagerActivity.Conversing &&
                _worldGameSeconds >=
                previous.ActivityUntilGameSeconds)
            {
                previous = VillagerSimulation.CompleteConversation(
                    previous, _worldGameSeconds);
            }
            previous = VillagerSimulation.CompleteReflection(
                previous, _worldGameSeconds);
            if (_activePlayer is not null &&
                previous.FollowingActorId == _activePlayer.Id &&
                (previous.Activity != VillagerActivity.Blocked ||
                 _worldGameSeconds >=
                 previous.NextDecisionGameSeconds) &&
                previous.Activity != VillagerActivity.Conversing &&
                previous.Activity != VillagerActivity.Reflecting)
            {
                var followerPosition = new Vector2(
                    previous.PositionX, previous.PositionY);
                var distanceSquared = Vector2.DistanceSquared(
                    followerPosition, _player.Position);
                var shouldMove =
                    distanceSquared >
                    VillagerSimulation.FollowResumeDistance *
                    VillagerSimulation.FollowResumeDistance ||
                    previous.Action == EntityAction.Move &&
                    distanceSquared >
                    VillagerSimulation.FollowStopDistance *
                    VillagerSimulation.FollowStopDistance;
                if (shouldMove)
                {
                    var desiredFollowTarget =
                        VillagerSimulation.FollowTarget(
                            followerPosition,
                            _player.Position);
                    if (VillagerSimulation.NeedsFollowRetarget(
                            previous, desiredFollowTarget))
                    {
                        var followTarget =
                            WorldLevelNavigation.ReachableWalkableTarget(
                                _worldSeed,
                                followerPosition,
                                desiredFollowTarget,
                                previous.WorldLevel,
                                maximumRadius: 3);
                        if (Vector2.DistanceSquared(
                                followerPosition,
                                followTarget) <= .01f)
                            previous =
                                VillagerSimulation.BlockMovement(
                                    previous,
                                    _worldGameSeconds);
                        else
                            previous =
                                VillagerSimulation.RetargetFollowing(
                                    previous,
                                    followTarget,
                                    _worldGameSeconds);
                    }
                }
                else
                    previous = previous with
                    {
                        Activity = VillagerActivity.Following,
                        Action = EntityAction.Idle,
                        ActionTime =
                            previous.Action == EntityAction.Idle
                                ? previous.ActionTime
                                : 0,
                        TargetX = null,
                        TargetY = null
                    };
            }
            var currentTerrain = SamplePlayerTerrain(
                previous.PositionX, previous.PositionY);
            var targetTerrain = SamplePlayerTerrain(
                previous.TargetX ?? previous.PositionX,
                previous.TargetY ?? previous.PositionY);
            var wading = currentTerrain.Biome is
                Biome.ShallowWater or
                Biome.RiverWater or
                Biome.MangroveShallows;
            var villager = VillagerSimulation.AdvanceMovement(
                previous,
                elapsed,
                ActorMovementService.TerrainSpeedMultiplier(
                    wading,
                    currentTerrain.Height,
                    targetTerrain.Height),
                candidate => WorldLevelNavigation.IsWalkable(
                    _worldSeed,
                    (int)MathF.Floor(candidate.X),
                    (int)MathF.Floor(candidate.Y),
                    previous.WorldLevel),
                _worldGameSeconds);
            villager =
                VillagerCommitmentService.UpdateDeadlines(
                    villager, _worldGameSeconds);
            var movedPosition = new Vector2(
                villager.PositionX, villager.PositionY);
            for (var otherIndex = 0;
                 otherIndex < _villagers.Count;
                 otherIndex++)
            {
                if (otherIndex == index ||
                    _villagers[otherIndex].WorldLevel !=
                    villager.WorldLevel)
                    continue;
                var otherPosition = new Vector2(
                    _villagers[otherIndex].PositionX,
                    _villagers[otherIndex].PositionY);
                if (!VillagerSimulation.FootBoxesOverlap(
                        movedPosition, otherPosition))
                    continue;
                villager = VillagerSimulation.BlockMovement(
                    villager with
                    {
                        PositionX = previous.PositionX,
                        PositionY = previous.PositionY
                    },
                    _worldGameSeconds);
                break;
            }
            if (!ReferenceEquals(previous, villager))
            {
                _villagers[index] = villager;
                _villagersDirty = true;
            }
            if (villager.Activity is
                VillagerActivity.Conversing or
                VillagerActivity.Reflecting)
                continue;
            if (villager.WorldLevel != _activeWorldLevel ||
                _worldGameSeconds < villager.NextDecisionGameSeconds)
                continue;
            var position = new Vector2(
                villager.PositionX, villager.PositionY);
            var tier = VillagerSimulation.Tier(
                position, _player.Position);
            villager = VillagerSimulation.CatchUp(
                villager, _worldGameSeconds);
            if (TryExecuteVillagerSocialGoal(
                    index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                TryExecuteVillagerWorldAction(
                    index, villager, tier))
                continue;
            var decision = VillagerSimulation.Decide(
                villager, _player.Position, _worldGameSeconds);
            if (decision.MoveTarget is { } requestedTarget)
            {
                var safeTarget =
                    WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed,
                    position,
                    requestedTarget,
                    villager.WorldLevel,
                    maximumRadius: 2);
                decision = decision with
                {
                    MoveTarget = safeTarget
                };
                if (Vector2.DistanceSquared(
                        position, safeTarget) <= .01f &&
                    Vector2.DistanceSquared(
                        position, requestedTarget) > .01f)
                {
                    _villagers[index] =
                        VillagerSimulation.BlockMovement(
                            villager, _worldGameSeconds);
                    _villagersDirty = true;
                    continue;
                }
            }
            villager = VillagerSimulation.ApplyDecision(
                villager, decision, tier, _worldGameSeconds);
            _villagers[index] = villager;
            _villagersDirty = true;
            if (decision.Speech is { } speech &&
                tier == VillagerSimulationTier.Nearby)
                _chatUi.AddMessage(
                    $"{villager.Name}: {speech}",
                    ChatMessageStyle.Normal);
        }
        if (_villagersDirty &&
            _worldGameSeconds >= _villagersNextSaveAt)
            SaveVillagers();
    }

    private bool TryExecuteVillagerSocialGoal(
        int villagerIndex,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (_player is null || _activePlayer is null ||
            tier == VillagerSimulationTier.Distant)
            return false;
        if (ConversationFloorBusy)
            return false;
        _socialActorObservations.Clear();
        _socialActorObservations.Add(new(
            _activePlayer.Id,
            _activePlayer.Name,
            _player.Position,
            _activeWorldLevel,
            _activePlayer.Hunger,
            VillagerSimulation.CountFood(
                _activePlayer.Inventory ?? [])));
        foreach (var actor in _villagers)
            _socialActorObservations.Add(new(
                actor.Id,
                actor.Name,
                new(actor.PositionX, actor.PositionY),
                actor.WorldLevel,
                actor.Hunger,
                VillagerSimulation.CountFood(
                    actor.Inventory)));
        var goal = VillagerSimulation.SelectSocialGoal(
            villager,
            CollectionsMarshal.AsSpan(
                _socialActorObservations),
            _worldGameSeconds);
        if (goal.Intent == VillagerSocialIntent.None)
            return false;
        if (goal.Target is { } target)
        {
            var safeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed,
                new(villager.PositionX, villager.PositionY),
                target,
                    villager.WorldLevel,
                    maximumRadius: 2);
            if (Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    safeTarget) <= .01f)
            {
                _villagers[villagerIndex] =
                    VillagerSimulation.BlockMovement(
                        villager, _worldGameSeconds);
                _villagersDirty = true;
                return true;
            }
            _villagers[villagerIndex] =
                VillagerSimulation.ApplyDecision(
                    villager,
                    new(VillagerNeed.Social, safeTarget),
                    tier,
                    _worldGameSeconds);
            _villagersDirty = true;
            return true;
        }

        if (goal.Speech is { } speech &&
            goal.OtherActorId is { } conversationPartnerId)
        {
            var partner = _socialActorObservations
                .First(value =>
                    value.Id == conversationPartnerId);
            SpeakVillagerDialogue(
                villager,
                partner.Id,
                partner.Name,
                goal.Intent,
                speech);
            villager = _villagers[villagerIndex];
            villager = VillagerSimulation.RecordConversation(
                villager,
                partner.Id,
                partner.Name,
                goal.Intent,
                _worldGameSeconds);
            villager = villager with
            {
                Need = VillagerNeed.Idle
            };
            _villagers[villagerIndex] = villager;
            var otherVillagerIndex = _villagers.FindIndex(value =>
                value.Id == partner.Id);
            if (otherVillagerIndex >= 0)
            {
                var listener = _villagers[otherVillagerIndex];
                listener =
                    VillagerSimulation.RecordConversation(
                        listener,
                        villager.Id,
                        villager.Name,
                        goal.Intent,
                        _worldGameSeconds) with
                    {
                        Need = VillagerNeed.Idle
                    };
                _villagers[otherVillagerIndex] = listener;
                HoldVillagerConversation(
                    otherVillagerIndex,
                    new(villager.PositionX, villager.PositionY),
                    ConversationLineSeconds(speech));
            }
            _villagersDirty = true;
        }
        if (goal.OtherActorId is null ||
            goal.OtherActorId == _activePlayer.Id)
        {
            var updatedVillager = villager with
            {
                Need = VillagerNeed.Idle,
                Action = EntityAction.Idle,
                ActionTime = 0,
                NextDecisionGameSeconds =
                    _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds
            };
            _villagers[villagerIndex] = updatedVillager;
            _villagersDirty = true;
            return true;
        }

        var otherIndex = _villagers.FindIndex(value =>
            value.Id == goal.OtherActorId);
        if (otherIndex < 0) return true;
        if (goal.Intent is not
            (VillagerSocialIntent.RequestFood or
             VillagerSocialIntent.OfferFood))
            return true;
        var donorIndex =
            goal.Intent == VillagerSocialIntent.OfferFood
                ? villagerIndex
                : otherIndex;
        var receiverIndex =
            donorIndex == villagerIndex
                ? otherIndex
                : villagerIndex;
        var donor = _villagers[donorIndex];
        var receiver = _villagers[receiverIndex];
        if (VillagerSimulation.CountFood(donor.Inventory) <= 1)
            return true;
        var foodSlot = Array.FindIndex(
            donor.Inventory,
            item => item is not null &&
                    SurvivalService.TryFoodEffect(
                        item, out _));
        if (foodSlot < 0 ||
            !PlayerInventory.TryAdd(
                receiver.Inventory,
                donor.Inventory[foodSlot]!,
                out var receiverInventory) ||
            !PlayerInventory.TryRemove(
                donor.Inventory,
                foodSlot,
                out var donorInventory))
            return true;
        _villagers[donorIndex] = donor with
        {
            Inventory = donorInventory,
            Need = VillagerNeed.Idle,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                VillagerSimulation.SocialCooldownSeconds,
            LastSimulatedGameSeconds =
                _worldGameSeconds
        };
        _villagers[receiverIndex] = receiver with
        {
            Inventory = receiverInventory,
            Need = VillagerNeed.Food,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                VillagerSimulation.SocialCooldownSeconds,
            LastSimulatedGameSeconds =
                _worldGameSeconds
        };
        _villagersDirty = true;
        return true;
    }

    private bool TryExecuteVillagerWorldAction(
        int villagerIndex,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        _villagerWorldObjects.Clear();
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            foreach (var item in gpu.Chunk.GroundObjects)
            {
                if (_villagerReservedObjects.Contains(item.Id) &&
                    item.Id != villager.GoalObjectId)
                    continue;
                _villagerWorldObjects.Add(new(
                    item.Id,
                    item.ItemId,
                    new(item.X, item.Y),
                    item.OwnerId,
                    StorageContainerService.IsStorage(
                        item.ItemId)));
            }
        }
        var action = VillagerSimulation.SelectWorldAction(
            villager,
            CollectionsMarshal.AsSpan(_villagerWorldObjects));
        if (action.Kind == VillagerWorldActionKind.None)
            return false;
        if (action.ObjectId is { } reservedId)
            _villagerReservedObjects.Add(reservedId);
        if (action.Kind is
            VillagerWorldActionKind.ApproachItem or
            VillagerWorldActionKind.ApproachStorage)
        {
            var safeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed,
                new(villager.PositionX, villager.PositionY),
                action.Target ?? new(
                    villager.PositionX, villager.PositionY),
                villager.WorldLevel,
                maximumRadius: 2);
            if (Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    safeTarget) <= .01f)
            {
                _villagers[villagerIndex] =
                    VillagerSimulation.BlockMovement(
                        villager, _worldGameSeconds);
                _villagersDirty = true;
                return true;
            }
            var decision = new VillagerDecision(
                action.Kind ==
                VillagerWorldActionKind.ApproachItem
                    ? VillagerNeed.Food
                    : VillagerNeed.Safe,
                safeTarget);
            _villagers[villagerIndex] =
                VillagerSimulation.ApplyDecision(
                    villager,
                    decision,
                    tier,
                    _worldGameSeconds) with
                {
                    GoalObjectId = action.ObjectId
                };
            _villagersDirty = true;
            return true;
        }

        var targetGpu = _worldChunks.Values.FirstOrDefault(gpu =>
            IsActiveWorldChunk(gpu) &&
            gpu.Chunk.GroundObjects.Any(item =>
                item.Id == action.ObjectId));
        var target = targetGpu?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == action.ObjectId);
        if (targetGpu is null || target is null) return false;
        if (action.Kind == VillagerWorldActionKind.TakeItem)
        {
            if (target.OwnerId is { Length: > 0 } owner &&
                !string.Equals(
                    owner, villager.Id,
                    StringComparison.Ordinal) ||
                !PlayerInventory.TryAdd(
                    villager.Inventory,
                    target.ItemId,
                    out var inventory))
                return false;
            if (!targetGpu.Chunk.GroundObjects.Remove(target))
                return false;
            var updatedVillager = villager with
            {
                Inventory = inventory,
                Need = VillagerNeed.Explore,
                Action = EntityAction.Gather,
                ActionTime = 0,
                GoalObjectId = null,
                NextDecisionGameSeconds =
                    _worldGameSeconds +
                    Math.Max(
                        VillagerSimulation.DecisionInterval(tier),
                        VillagerSimulation.GatherPauseSeconds),
                LastSimulatedGameSeconds = _worldGameSeconds
            };
            _villagers[villagerIndex] =
                VillagerCommitmentService.RecordAcquiredItem(
                    updatedVillager, target.ItemId);
            QueueChunkSave(targetGpu.Chunk);
            _villagersDirty = true;
            return true;
        }

        if (action.Kind !=
                VillagerWorldActionKind.DepositItems ||
            !string.Equals(
                target.OwnerId, villager.Id,
                StringComparison.Ordinal))
            return false;
        var container = StorageContainerService.Open(target);
        var depositedInventory =
            (string?[])villager.Inventory.Clone();
        var moved = 0;
        for (var slot = 0;
             slot < depositedInventory.Length;
             slot++)
        {
            if (depositedInventory[slot] is not { } itemId ||
                !container.TryAdd(
                    itemId, ownerId: villager.Id))
                continue;
            depositedInventory[slot] = null;
            moved++;
        }
        if (moved == 0) return false;
        var savedStorage = StorageContainerService.Save(
            target, container);
        var targetIndex =
            targetGpu.Chunk.GroundObjects.IndexOf(target);
        targetGpu.Chunk.GroundObjects[targetIndex] = savedStorage;
        _villagers[villagerIndex] = villager with
        {
            Inventory = depositedInventory,
            Action = EntityAction.Work,
            ActionTime = 0,
            GoalObjectId = null,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                VillagerSimulation.DecisionInterval(tier),
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        QueueChunkSave(targetGpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private void SaveVillagers()
    {
        if (!_villagersDirty || _activeWorld is null) return;
        _saves.SaveVillagers(_activeWorld.Id, _villagers);
        _villagersDirty = false;
        _villagersNextSaveAt = _worldGameSeconds + 30;
    }

    private void ShowVillagerSpeech(
        int villagerIndex,
        string message,
        Vector2 listenerPosition)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count ||
            string.IsNullOrWhiteSpace(message))
            return;
        var villager = _villagers[villagerIndex];
        var seconds = ConversationLineSeconds(message);
        HoldVillagerConversation(
            villagerIndex, listenerPosition, seconds);
        villager = _villagers[villagerIndex];
        TakeConversationFloor(villager.Id, seconds);
        _villagerSpeechBubbles[villager.Id] =
            new(message, _clock + seconds);
        _chatUi.AddMessage(
            $"{villager.Name}: {message}",
            ChatMessageStyle.Npc);
    }

    private bool ConversationFloorBusy =>
        _clock < _conversationFloorUntil;

    private static double ConversationLineSeconds(string message) =>
        Math.Clamp(
            2.5 + message.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length * .22,
            4,
            8);

    private void TakeConversationFloor(
        string speakerId,
        double seconds)
    {
        _conversationFloorSpeakerId = speakerId;
        _conversationFloorUntil = double.IsPositiveInfinity(seconds)
            ? double.PositiveInfinity
            : _clock + seconds;
    }

    private bool TryQueuePlayerConversationTurn(string message)
    {
        if (_player is null || _activePlayer is null ||
            !_villagers.Any(villager =>
                villager.WorldLevel == _activeWorldLevel &&
                Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    _player.Position) < 10 * 10))
            return false;
        if (_queuedPlayerConversationTurns.Count < 8)
            _queuedPlayerConversationTurns.Enqueue(message);
        ShowOverheadSpeech(message);
        if (!ConversationFloorBusy)
            StartNextPlayerConversationTurn();
        return true;
    }

    private void UpdateConversationTurns()
    {
        if (ConversationFloorBusy) return;
        _conversationFloorSpeakerId = null;
        StartNextPlayerConversationTurn();
    }

    private void StartNextPlayerConversationTurn()
    {
        if (_activePlayer is null ||
            _queuedPlayerConversationTurns.Count == 0)
            return;
        var message = _queuedPlayerConversationTurns.Dequeue();
        TryHandleVillagerChat(message);
    }

    private void HoldVillagerConversation(
        int villagerIndex,
        Vector2 listenerPosition,
        double seconds,
        string? partnerId = null)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count)
            return;
        var villager = _villagers[villagerIndex];
        var position = new Vector2(
            villager.PositionX, villager.PositionY);
        var direction = listenerPosition - position;
        if (direction.LengthSquared > .0001f)
            direction = direction.Normalized();
        villager = VillagerSimulation.BeginConversation(
            villager with
            {
                FacingX = direction.X,
                FacingY = direction.Y
            },
            partnerId,
            _worldGameSeconds,
            seconds);
        _villagers[villagerIndex] = villager;
        _villagersDirty = true;
    }

    private void RenderVillagerOverheadSpeech(Vector4 scene)
    {
        if (_chatFont is null || _fontRenderer is null ||
            _villagerSpeechBubbles.Count == 0)
            return;
        foreach (var villager in _villagers)
        {
            if (!_villagerSpeechBubbles.TryGetValue(
                    villager.Id, out var bubble) ||
                _clock >= bubble.ExpiresAt ||
                villager.WorldLevel != _activeWorldLevel ||
                !_entityAnimations.TryGetValue(
                    (villager.Gender, villager.Action),
                    out var animation))
                continue;
            var directional = VillagerDirectionRig.Resolve(
                new(villager.FacingX, villager.FacingY),
                animation.Graphic.Sprite.Frames.Count,
                5,
                (int)(villager.ActionTime /
                      animation.SecondsPerFrame));
            var terrain = SamplePlayerTerrain(
                villager.PositionX, villager.PositionY);
            var projected = IsometricTerrainProjection.Project(
                villager.PositionX,
                villager.PositionY,
                terrain.Height);
            var sprite = SpriteBounds(
                animation.Graphic.Sprite.Frames[directional.Index],
                projected,
                directional.Mirror);
            DrawVillagerSpeechBubble(
                scene, sprite, bubble.Text);
        }
    }

    private void DrawVillagerSpeechBubble(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) sprite,
        string fullText)
    {
        const float horizontalPadding = 9;
        const float verticalPadding = 6;
        var font = _chatFont!;
        var renderer = _fontRenderer!;
        var scale = scene.Z / ReferenceWidth;
        var centerX = scene.X +
                      (sprite.Left + sprite.Right) * .5f * scale;
        const float maximumTextWidth = 260;
        var lines = new List<string>(3);
        var current = "";
        foreach (var word in fullText.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0
                ? word
                : current + " " + word;
            if (current.Length > 0 &&
                font.MeasureString(candidate).X >
                maximumTextWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
                current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) return;
        var lineHeight = MathF.Ceiling(
            font.MeasureString("Ag").Y);
        var textWidth = lines.Max(line =>
            font.MeasureString(line).X);
        var width = textWidth + horizontalPadding * 2;
        var height =
            lineHeight * lines.Count + verticalPadding * 2;
        var x = Math.Clamp(
            centerX - width * .5f,
            scene.X + 4,
            scene.X + scene.Z - width - 4);
        var y = Math.Max(
            scene.Y + 4,
            scene.Y + sprite.Top * scale - height - 12);
        var bounds = new Vector4(
            MathF.Round(x), MathF.Round(y),
            MathF.Ceiling(width), MathF.Ceiling(height));
        DrawRoundedUiColor(bounds, 6, new(.68f, .68f, .66f, .9f));
        DrawRoundedUiColor(
            new(bounds.X + 1, bounds.Y + 1,
                bounds.Z - 2, bounds.W - 2),
            5, new(.98f, .98f, .97f, .98f));
        var tailCenter = Math.Clamp(
            centerX,
            bounds.X + 10,
            bounds.X + bounds.Z - 10);
        DrawUiColor(
            new(
                MathF.Round(tailCenter - 3),
                bounds.Y + bounds.W - 1,
                6,
                6),
            new(.98f, .98f, .97f, .98f));
        _uiColorBatch.Flush();
        for (var index = 0; index < lines.Count; index++)
            font.DrawText(
                renderer,
                lines[index],
                new(
                    bounds.X + horizontalPadding,
                    bounds.Y + verticalPadding +
                    index * lineHeight),
                new FSColor(20, 20, 18, 255));
    }

    private bool TryHandleVillagerChat(string message)
    {
        if (_player is null) return false;
        var nearestIndex = -1;
        var nearestDistance = 10f * 10f;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var villager = _villagers[index];
            if (villager.WorldLevel != _activeWorldLevel) continue;
            var distance = Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY),
                _player.Position);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestIndex = index;
        }
        if (nearestIndex < 0) return false;
        var target = _villagers[nearestIndex];
        var text = message.Trim();
        var lower = text.ToLowerInvariant();
        if (_activePlayer is not null &&
            (lower.Contains("follow me") ||
             lower.Contains("come with me")))
        {
            target = target with
            {
                FollowingActorId = _activePlayer.Id,
                NextDecisionGameSeconds = _worldGameSeconds
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "All right, I'll stay with you.",
                _player.Position);
            return true;
        }
        if (lower.Contains("come here") ||
            lower.Contains("come back"))
        {
            target = target with
            {
                FollowingActorId = _activePlayer?.Id,
                NextDecisionGameSeconds = _worldGameSeconds
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "I'm coming.",
                _player.Position);
            return true;
        }
        if (lower.Contains("go away") ||
            lower.Contains("leave me alone") ||
            lower.Contains("get away from me"))
        {
            var hostile = lower.Contains("fuck") ||
                          lower.Contains("bitch") ||
                          lower.Contains("ugly") ||
                          lower.Contains("idiot") ||
                          lower.Contains("stupid");
            var dismissalReply = hostile
                ? "Fine. I'll leave, but don't speak to me like that."
                : "All right. I'll give you some space.";
            target = VillagerSimulation.ApplyDismissal(
                target,
                _activePlayer?.Id ?? "player",
                _activePlayer?.Name ?? "Survivor",
                text,
                dismissalReply,
                hostile ? -35 : -8,
                _worldGameSeconds);
            _villagers[nearestIndex] = target;
            _villagersDirty = true;
            ShowVillagerSpeech(
                nearestIndex,
                dismissalReply,
                _player.Position);
            return true;
        }
        if (lower is "wait" or "wait here" or "stay here" ||
            lower.Contains("stop following"))
        {
            target = target with
            {
                FollowingActorId = null,
                Action = EntityAction.Idle,
                ActionTime = 0,
                TargetX = null,
                TargetY = null
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "I'll wait here.",
                _player.Position);
            HoldVillagerConversation(
                nearestIndex, _player.Position, 10);
            return true;
        }
        if (TryBeginNpcAiSpeech(nearestIndex, message))
            return true;
        if (VillagerCommitmentService.TryParseGatherRequest(
                text, out var requestedItem,
                out var requestedQuantity))
        {
            var acceptance =
                VillagerCommitmentService.TryAccept(
                    target,
                    _activePlayer?.Id ?? "player",
                    VillagerPromiseKind.GatherItem,
                    requestedItem,
                    requestedQuantity,
                    _worldGameSeconds);
            if (acceptance.Accepted &&
                acceptance.Promise is { } promise)
            {
                target =
                    VillagerCommitmentService.AddPromise(
                        target, promise);
                _villagers[nearestIndex] = target;
                _villagersDirty = true;
            }
            ShowVillagerSpeech(
                nearestIndex,
                acceptance.Reply,
                _player.Position);
            return true;
        }
        var response = lower.Contains("name")
            ? $"My name is {target.Name}."
            : lower.Contains("how are") ||
              lower.Contains("hungry")
                ? target.Hunger < 35
                    ? "I'm hungry. I need to find food."
                    : "I'm doing well enough."
                : lower.Contains("sorry")
                    ? "I'll remember that you apologised."
                : lower.Contains("hello") ||
                      lower == "hi" ||
                      lower.StartsWith("hi ")
                        ? $"Hello. I'm {target.Name}."
                        : $"I heard you. Right now I'm focused on " +
                          $"{target.Need.ToString().ToLowerInvariant()}.";
        ShowVillagerSpeech(
            nearestIndex,
            response,
            _player.Position);
        return true;
    }

    private void NotifyVillagersOfTaking(
        WorldGroundObject item)
    {
        if (_player is null ||
            _activePlayer is null ||
            string.IsNullOrWhiteSpace(item.OwnerId) ||
            string.Equals(
                item.OwnerId, _activePlayer.Id,
                StringComparison.Ordinal))
            return;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var observer = _villagers[index];
            if (observer.WorldLevel != _activeWorldLevel ||
                !string.Equals(
                    observer.Id, item.OwnerId,
                    StringComparison.Ordinal) ||
                Vector2.DistanceSquared(
                    new(observer.PositionX, observer.PositionY),
                    _player.Position) > 12 * 12)
                continue;
            observer =
                VillagerSimulation.ObserveUnauthorizedTaking(
                    observer,
                    item.Id,
                    item.ItemId,
                    item.OwnerId,
                    _activePlayer.Id,
                    _worldGameSeconds,
                    confidence: 1,
                    itemValue: ItemValue(item.ItemId),
                    out var reaction);
            _villagers[index] = observer;
            _villagersDirty = true;
            _chatUi.AddMessage(
                $"{observer.Name}: " +
                VillagerSimulation.ReactionSpeech(
                    observer.Name,
                    ItemCatalog.Get(item.ItemId).Name,
                    reaction),
                ChatMessageStyle.Warning);
        }
    }

    internal void GiveItemToVillager(
        string villagerId,
        int inventorySlot,
        string itemId)
    {
        if (_player is null || _activePlayer is null ||
            !InventoryContainsAt(inventorySlot, itemId))
            return;
        var villagerIndex = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (villagerIndex < 0) return;
        var villager = _villagers[villagerIndex];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        if (Vector2.Distance(_player.Position, target) >
            VillagerSimulation.InteractionRange + .3f)
        {
            _worldActions.QueueVillagerGift(
                villager, inventorySlot, itemId);
            return;
        }
        if (!TryGetDropTerrain(
                (int)MathF.Floor(target.X),
                (int)MathF.Floor(target.Y),
                out var gpu,
                out var reason))
        {
            ReportBlockedAction("villager-gift-blocked", reason);
            return;
        }
        if (!PlayerInventory.TryRemove(
                _activePlayer.Inventory,
                inventorySlot,
                out var inventory))
            return;

        var itemInstanceId = Guid.NewGuid();
        gpu.Chunk.GroundObjects.Add(new(
            itemInstanceId,
            itemId,
            target.X,
            target.Y,
            OwnerId: villager.Id));
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == inventorySlot)
            _activeInventorySlot = -1;
        var itemName = ItemCatalog.Get(itemId).Name;
        villager = VillagerSimulation.RecordGift(
            villager,
            _activePlayer.Id,
            _activePlayer.Name,
            itemInstanceId,
            itemId,
            _worldGameSeconds);
        var giftSpeech =
            $"{villager.Name}, this {itemName} is for you.";
        villager = VillagerSimulation.RecordDialogueTurn(
            villager,
            _activePlayer.Id,
            _activePlayer.Name,
            giftSpeech,
            _worldGameSeconds);
        villager = VillagerSimulation.RecordDialogueTurn(
            villager,
            villager.Id,
            villager.Name,
            $"Thank you, {_activePlayer.Name}.",
            _worldGameSeconds + 1);
        _villagers[villagerIndex] = villager;
        _villagersDirty = true;
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(gpu.Chunk);

        var message =
            $"{_activePlayer.Name}: {giftSpeech}";
        _chatUi.AddMessage(message, ChatMessageStyle.Player);
        ShowOverheadSpeech(
            $"{villager.Name}, this {itemName} is for you.");
        ShowVillagerSpeech(
            villagerIndex,
            $"Thank you, {_activePlayer.Name}.",
            _player.Position);
        _player.Stop();
    }

    private static int ItemValue(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.HasTag(ItemTag.MetalToolSprite)) return 30;
        if (item.HasTag(ItemTag.Tool)) return 15;
        if (item.HasTag(ItemTag.PlaceableObject)) return 20;
        if (item.HasTag(ItemTag.CookedFood)) return 5;
        return 2;
    }

    private void DrawVillager(VillagerState villager)
    {
        const int storedVillagerAngles = 5;
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, villager.Action),
                out var animation))
            return;
        var graphic = animation.Graphic;
        var rawFrame = (int)(
            villager.ActionTime / animation.SecondsPerFrame);
        var directional = VillagerDirectionRig.Resolve(
            new Vector2(
                villager.FacingX,
                villager.FacingY),
            graphic.Sprite.Frames.Count,
            storedVillagerAngles,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            villager.PositionX, villager.PositionY);
        var world = IsometricTerrainProjection.Project(
            villager.PositionX,
            villager.PositionY,
            terrain.Height);
        DrawSprite(
            graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index],
            world,
            mirror: directional.Mirror,
            wading: terrain.Biome is
                Biome.ShallowWater or
                Biome.RiverWater or
                Biome.MangroveShallows,
            teamColor: villager.TeamColor);
    }

    private bool TryGetVillagerUnderMouse(
        Vector2 mouse,
        out VillagerState villager)
    {
        for (var index = _villagers.Count - 1; index >= 0; index--)
        {
            var candidate = _villagers[index];
            if (candidate.WorldLevel != _activeWorldLevel ||
                candidate.Health <= 0 ||
                !_entityAnimations.TryGetValue(
                    (candidate.Gender, candidate.Action),
                    out var animation))
                continue;
            var frameIndex = (int)(
                candidate.ActionTime / animation.SecondsPerFrame);
            var directional = VillagerDirectionRig.Resolve(
                new(candidate.FacingX, candidate.FacingY),
                animation.Graphic.Sprite.Frames.Count,
                5,
                frameIndex);
            var terrain = SamplePlayerTerrain(
                candidate.PositionX, candidate.PositionY);
            var projected = IsometricTerrainProjection.Project(
                candidate.PositionX,
                candidate.PositionY,
                terrain.Height);
            var bounds = SpriteBounds(
                animation.Graphic.Sprite.Frames[directional.Index],
                projected,
                directional.Mirror);
            if (mouse.X < bounds.Left || mouse.X > bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y > bounds.Bottom)
                continue;
            villager = candidate;
            return true;
        }
        villager = null!;
        return false;
    }
}
