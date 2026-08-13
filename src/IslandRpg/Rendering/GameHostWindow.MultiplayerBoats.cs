using IslandRpg.Client;
using IslandRpg.Fishing;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Rendering.Ui;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Presentation-only adapter for server-owned boats and fishing. No route,
/// occupancy, catch roll, inventory, resource or persistence state is authored
/// here: clicks become exact optimistic actions and authoritative state drives
/// every visible transition.
/// </summary>
internal sealed partial class GameHostWindow
{
    private const float NetworkBoatBoardingRange = 2.15f;
    private const float NetworkBoatFishingReach = 2.85f;

    private sealed class NetworkBoatPresentation(BoatState state)
    {
        public BoatState State { get; set; } = state;
        public Vector2 Position { get; set; } = new(state.X, state.Y);
        public Vector2 Facing { get; set; } = NormalizeFacing(
            new(state.FacingX, state.FacingY));
        public bool Moving { get; set; } = state.Moving;
        public bool HasTransform { get; set; }
        public double AnimationTime { get; set; }
    }

    private readonly record struct NetworkFishingTarget(
        WorldFish Fish,
        FishSchoolDescriptor Descriptor,
        int FishingNetSlot);

    private readonly Dictionary<Guid, NetworkBoatPresentation>
        _networkBoats = [];
    private readonly Dictionary<ulong, Guid> _networkBoatIdsByEntity = [];
    private Guid? _networkBoatCommandId;
    private BoatActionKind? _networkBoatCommandKind;
    private Guid? _networkBoatCommandBoatId;
    private uint _networkBoatCommandRevision;
    private Guid? _networkPendingBoardBoatId;
    private double _networkPendingBoardQueuedAt;
    private bool _networkPendingBoardMovementObserved;
    private float _networkPendingBoardDistance;
    private Vector2? _networkPendingDisembarkTarget;
    private bool _networkDisembarkMoveAccepted;
    private NetworkFishingTarget? _networkPendingFishing;
    private NetworkFishingTarget? _networkActiveFishing;
    private Guid? _networkFishingCommandId;
    private ResourceNodeReference? _networkFishingReference;
    private int _networkFishingCompletedCycles;
    private uint _networkFishingAwaitingActorRevision;
    private uint _networkFishingAwaitingInventoryRevision;
    private uint _networkFishingAwaitingNodeRevision;
    private uint _networkFishingAwaitingChunkRevision;
    private bool _networkFishingReceiptObserved;
    private bool _networkFishingDeltaObserved;
    private bool _networkFishingDeltaArrivedEarly;
    private uint _networkFishingEarlyNodeRevision;
    private uint _networkFishingEarlyChunkRevision;
    private int _networkFishingExperienceBefore;

    private void InitializeNetworkBoats()
    {
        ClearNetworkBoatPresentation();
        if (_networkClient is null) return;
        foreach (var boat in _networkClient.State.Boats.Values)
            UpsertNetworkBoat(boat);
        RefreshLocalNetworkBoatState();
    }

    private bool IsNetworkActorAboard(ulong entityId) =>
        IsNetworkWorld && _networkBoats.Values.Any(value =>
            value.State.OccupantEntityId == entityId);

    private void ClearNetworkBoatPresentation()
    {
        _networkBoats.Clear();
        _networkBoatIdsByEntity.Clear();
        _networkBoatCommandId = null;
        _networkBoatCommandKind = null;
        _networkBoatCommandBoatId = null;
        _networkBoatCommandRevision = 0;
        _networkPendingBoardBoatId = null;
        _networkPendingBoardQueuedAt = 0;
        _networkPendingBoardMovementObserved = false;
        _networkPendingBoardDistance = 0;
        _networkPendingDisembarkTarget = null;
        _networkDisembarkMoveAccepted = false;
        CancelNetworkFishingPresentation();
        _fishingBoat = null;
        _fishingBoatBoarded = false;
        _fishingBoatDisembarkTargeting = false;
        _fishingBoatRiderOffset = Vector2.Zero;
        _fishingBoatRiderTargetOffset = Vector2.Zero;
    }

    private void HandleNetworkBoatsChanged(NetworkBoatsChangedEventArgs value)
    {
        if (!IsNetworkWorld) return;
        if (value.IsBaseline)
        {
            var current = value.Changes
                .Where(change => change.State is not null)
                .Select(change => change.BoatId)
                .ToHashSet();
            foreach (var id in _networkBoats.Keys
                         .Where(id => !current.Contains(id)).ToArray())
                RemoveNetworkBoat(id);
        }
        foreach (var change in value.Changes)
        {
            if (change.State is { } state)
                UpsertNetworkBoat(state);
            else
                RemoveNetworkBoat(change.BoatId);
        }
        RefreshLocalNetworkBoatState();
    }

    private void UpsertNetworkBoat(BoatState state)
    {
        if (!_networkBoats.TryGetValue(state.BoatId, out var presentation))
        {
            presentation = new(state);
            _networkBoats.Add(state.BoatId, presentation);
        }
        else
        {
            if (presentation.State.EntityId != state.EntityId)
                _networkBoatIdsByEntity.Remove(presentation.State.EntityId);
            presentation.State = state;
            // A reliable stopped state is the authoritative arrival point.
            // Snap to it even after UDP interpolation so follow-up actions
            // (notably deferred disembarking) use the exact server position.
            if (!presentation.HasTransform || !state.Moving)
            {
                presentation.Position = new(state.X, state.Y);
                if (!state.Moving)
                    presentation.HasTransform = false;
            }
            presentation.Facing = NormalizeFacing(
                new(state.FacingX, state.FacingY));
            presentation.Moving = state.Moving;
        }
        _networkBoatIdsByEntity[state.EntityId] = state.BoatId;
    }

    private void RemoveNetworkBoat(Guid id)
    {
        if (!_networkBoats.Remove(id, out var presentation)) return;
        _networkBoatIdsByEntity.Remove(presentation.State.EntityId);
        if (_networkBoatCommandBoatId == id)
        {
            _networkBoatCommandId = null;
            _networkBoatCommandKind = null;
            _networkBoatCommandBoatId = null;
        }
        if (_networkPendingBoardBoatId == id)
        {
            _networkPendingBoardBoatId = null;
            _networkPendingBoardQueuedAt = 0;
            _networkPendingBoardMovementObserved = false;
            _networkPendingBoardDistance = 0;
        }
    }

    private void RefreshLocalNetworkBoatState()
    {
        var playerId = _networkClient?.State.PlayerId ?? Guid.Empty;
        var localBoat = _networkBoats.Values.FirstOrDefault(value =>
            value.State.OccupantPlayerId == playerId);
        _fishingBoatBoarded = localBoat is not null;
        if (!_fishingBoatBoarded)
        {
            _fishingBoat = null;
            _fishingBoatRiderOffset = Vector2.Zero;
            _fishingBoatRiderTargetOffset = Vector2.Zero;
            if (_networkActiveFishing is not null)
                CancelNetworkFishingPresentation();
            return;
        }

        _fishingBoat ??= new WorldEntity(localBoat!.Position)
        {
            MoveSpeed = 3.4f
        };
        _fishingBoat.SyncPosition(localBoat!.Position);
        _fishingBoat.Face(localBoat.Facing);
        if (localBoat.Moving)
            _fishingBoat.MoveTo(localBoat.Position + localBoat.Facing);
        else
            _fishingBoat.Stop();
        _player?.SyncPosition(localBoat.Position + _fishingBoatRiderOffset);
    }

    private NetworkBoatPresentation? LocalNetworkBoat()
    {
        var playerId = _networkClient?.State.PlayerId ?? Guid.Empty;
        return _networkBoats.Values.FirstOrDefault(value =>
            value.State.OccupantPlayerId == playerId);
    }

    private void HandleNetworkBoatActionResult(BoatActionResultMessage result)
    {
        if (_networkBoatCommandId != result.CommandId ||
            _networkBoatCommandKind != result.Action ||
            _networkBoatCommandBoatId != result.Boat.BoatId ||
            _networkBoatCommandRevision != result.Boat.ExpectedRevision)
            return;

        _networkBoatCommandId = null;
        _networkBoatCommandKind = null;
        _networkBoatCommandBoatId = null;
        _networkBoatCommandRevision = 0;
        if (!result.Accepted)
        {
            if (result.Action == BoatActionKind.Move &&
                _networkPendingFishing is not null)
                CancelNetworkFishingPresentation();
            _networkPendingDisembarkTarget = null;
            _networkDisembarkMoveAccepted = false;
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected the boat action " +
                      $"({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
            return;
        }

        switch (result.Action)
        {
            case BoatActionKind.Board when result.Transitioned:
                _chatUi.AddMessage(
                    "You board the fishing boat.",
                    ChatMessageStyle.Action);
                break;
            case BoatActionKind.Move:
                _networkDisembarkMoveAccepted =
                    _networkPendingDisembarkTarget is not null;
                break;
            case BoatActionKind.Stop:
                _moveMarker = null;
                break;
            case BoatActionKind.Disembark when result.Transitioned:
                _networkPendingDisembarkTarget = null;
                _networkDisembarkMoveAccepted = false;
                _fishingBoatDisembarkTargeting = false;
                CancelNetworkFishingPresentation();
                _chatUi.AddMessage(
                    "You step ashore.", ChatMessageStyle.Action);
                break;
        }
    }

    private bool UpdateNetworkBoatInput(bool leftDown, bool rightDown)
    {
        if (!IsNetworkWorld || _activeWorldLevel != 0)
            return false;
        var leftPressed = leftDown && !_gameLeftWasDown;
        var rightPressed = rightDown && !_gameRightWasDown;
        if ((!leftPressed && !rightPressed) ||
            IsPointerOverGameUi(MouseState.Position))
            return false;

        var pointer = SceneMousePosition();
        var target = ScreenToTerrain(pointer);
        if (!_fishingBoatBoarded)
        {
            if (!TryGetNetworkBoatUnderMouse(pointer, out var boat))
                return false;
            _gameLeftWasDown = leftDown;
            _gameRightWasDown = rightDown;
            if (!leftPressed && !rightPressed) return true;
            if (_player is null ||
                Vector2.DistanceSquared(_player.Position, boat.Position) >
                NetworkBoatBoardingRange * NetworkBoatBoardingRange)
            {
                QueueNetworkBoatBoarding(boat);
                return true;
            }
            SendNetworkBoatAction(
                BoatActionKind.Board, boat.State.BoatId,
                reference => new BoardBoatAction(reference));
            return true;
        }

        if (!_fishingBoatDisembarkTargeting &&
            TryGetFishUnderMouse(pointer, out _))
            return false;
        _gameLeftWasDown = leftDown;
        _gameRightWasDown = rightDown;
        if (_fishingBoatDisembarkTargeting && leftPressed)
        {
            ChooseNetworkDisembarkTarget(target);
            return true;
        }
        if (!rightPressed) return false;
        var localBoat = LocalNetworkBoat();
        if (localBoat is null) return true;
        if (!FishingBoatTravel.IsNavigable(InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(target.X),
                (int)MathF.Floor(target.Y))))
        {
            ReportBlockedAction(
                "network-boat-use-disembark",
                "Use the disembark action, then choose a shore.");
            return true;
        }
        PrepareNetworkBoatInteraction();
        SendNetworkBoatAction(
            BoatActionKind.Move, localBoat.State.BoatId,
            reference => new MoveBoatAction(reference, target.X, target.Y));
        _moveMarker = new(target, 0);
        return true;
    }

    private void QueueNetworkBoatBoarding(NetworkBoatPresentation boat)
    {
        if (_player is null) return;
        SendNetworkWalk(
            boat.Position,
            preserveFishingAction: true,
            preserveBoatBoarding: true);
        _networkPendingBoardBoatId = boat.State.BoatId;
        _networkPendingBoardQueuedAt = _clock;
        _networkPendingBoardMovementObserved = false;
        _networkPendingBoardDistance =
            Vector2.Distance(_player.Position, boat.Position);
        _moveMarker = new(boat.Position, 0, Action: true);
    }

    private bool TryGetNetworkBoatUnderMouse(
        Vector2 pointer,
        out NetworkBoatPresentation boat)
    {
        boat = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var candidate in _networkBoats.Values)
        {
            if (candidate.State.WorldLevel != _activeWorldLevel ||
                GetNetworkBoatVisual(candidate) is not { } visual)
                continue;
            var bounds = SpriteBounds(
                visual.Frame, visual.World, visual.Mirror);
            if (!SpriteHitTesting.Contains(
                    visual.Frame, bounds, pointer,
                    SpritePixelScale(),
                    SpriteHitTesting.SizeAwareTolerance(visual.Frame)) ||
                !WorldHoverSelection.Prefer(
                    visual.World.Y, ref selectedDepth))
                continue;
            boat = candidate;
        }
        return boat is not null;
    }

    private void ChooseNetworkDisembarkTarget(Vector2 target)
    {
        var localBoat = LocalNetworkBoat();
        if (localBoat is null) return;
        if (FishingBoatTravel.IsNavigable(InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(target.X),
                (int)MathF.Floor(target.Y))))
        {
            ReportBlockedAction(
                "network-boat-disembark-water",
                "Choose dry land along the shore.");
            return;
        }

        PrepareNetworkBoatInteraction();
        _networkPendingDisembarkTarget = target;
        _networkDisembarkMoveAccepted = false;
        _fishingBoatDisembarkTargeting = false;
        if (FishingBoatTravel.FindDisembarkLanding(
                _worldSeed, localBoat.Position, target) is not null)
        {
            SendNetworkDisembark(localBoat, target);
            return;
        }
        SendNetworkBoatAction(
            BoatActionKind.Move, localBoat.State.BoatId,
            reference => new MoveBoatAction(reference, target.X, target.Y));
        _moveMarker = new(target, 0, Action: true);
    }

    private void SendNetworkDisembark(
        NetworkBoatPresentation boat,
        Vector2 target) =>
        SendNetworkBoatAction(
            BoatActionKind.Disembark, boat.State.BoatId,
            reference => new DisembarkBoatAction(
                reference, target.X, target.Y));

    private void SendNetworkBoatStop()
    {
        var localBoat = LocalNetworkBoat();
        if (localBoat is null) return;
        _networkPendingDisembarkTarget = null;
        _networkDisembarkMoveAccepted = false;
        PrepareNetworkBoatInteraction();
        SendNetworkBoatAction(
            BoatActionKind.Stop, localBoat.State.BoatId,
            reference => new StopBoatAction(reference));
    }

    private void SendNetworkBoatAction(
        BoatActionKind kind,
        Guid boatId,
        Func<BoatReference, BoatActionPayload> create)
    {
        if (_networkClient?.IsConnected != true ||
            _networkBoatCommandId is not null ||
            !_networkClient.TryGetBoatReference(boatId, out var reference))
            return;
        var commandId = Guid.NewGuid();
        _networkBoatCommandId = commandId;
        _networkBoatCommandKind = kind;
        _networkBoatCommandBoatId = boatId;
        _networkBoatCommandRevision = reference.ExpectedRevision;
        SendNetworkAction(create(reference), commandId);
    }

    private void PrepareNetworkBoatInteraction()
    {
        ReleaseNetworkCookingPresentation();
        CancelNetworkResourceInteraction();
        CancelNetworkFishingPresentation();
    }

    private void UpdateNetworkBoatFishingPresentation(float elapsed)
    {
        foreach (var boat in _networkBoats.Values)
            if (boat.Moving)
                boat.AnimationTime += elapsed;
        RefreshLocalNetworkBoatState();

        if (_networkPendingBoardBoatId is not null &&
            _player?.Action == EntityAction.Move)
            _networkPendingBoardMovementObserved = true;

        if (_networkPendingBoardBoatId is { } boardId &&
            _networkBoatCommandId is null &&
            _player is { Action: not EntityAction.Move } &&
            _networkBoats.TryGetValue(boardId, out var boardingBoat))
        {
            if (Vector2.DistanceSquared(
                    _player.Position, boardingBoat.Position) <=
                NetworkBoatBoardingRange * NetworkBoatBoardingRange)
            {
                _networkPendingBoardBoatId = null;
                _networkPendingBoardQueuedAt = 0;
                _networkPendingBoardMovementObserved = false;
                _networkPendingBoardDistance = 0;
                SendNetworkBoatAction(
                    BoatActionKind.Board, boardingBoat.State.BoatId,
                    reference => new BoardBoatAction(reference));
            }
            else if (_networkPendingBoardMovementObserved ||
                    _clock - _networkPendingBoardQueuedAt >=
                    NetworkBoardingStartupTimeoutSeconds())
            {
                _networkPendingBoardBoatId = null;
                _networkPendingBoardQueuedAt = 0;
                _networkPendingBoardMovementObserved = false;
                _networkPendingBoardDistance = 0;
                _moveMarker = null;
                ReportBlockedAction(
                    "network-boat-board-unreachable",
                    "You cannot reach that fishing boat from here.");
            }
        }

        var localBoat = LocalNetworkBoat();
        if (_networkPendingDisembarkTarget is { } shore &&
            _networkDisembarkMoveAccepted &&
            _networkBoatCommandId is null &&
            localBoat is { Moving: false } &&
            FishingBoatTravel.FindDisembarkLanding(
                _worldSeed, localBoat.Position, shore) is not null)
        {
            _networkDisembarkMoveAccepted = false;
            SendNetworkDisembark(localBoat, shore);
        }

        if (_networkPendingFishing is { } pending &&
            _player is not null &&
            _player.Action != EntityAction.Move)
        {
            var origin = localBoat?.Position ?? _player.Position;
            var reach = localBoat is null
                ? FishingNetReach()
                : NetworkBoatFishingReach;
            if (Vector2.DistanceSquared(
                    origin,
                    new(pending.Descriptor.Position.X,
                        pending.Descriptor.Position.Y)) <= reach * reach)
                BeginNetworkFishing(pending);
        }

        if (_networkActiveFishing is not { } active || _player is null)
            return;
        if (NetworkFishIsDepleted(active.Descriptor))
        {
            CancelNetworkFishingPresentation();
            return;
        }
        if (_player.Action != EntityAction.Fish)
            _player.FishAt(new(
                active.Descriptor.Position.X,
                active.Descriptor.Position.Y));
        if (localBoat is not null)
        {
            // Occupied actors are deliberately suppressed from UDP actor
            // snapshots. Advance only their presentation animation here;
            // catches still occur exclusively through server Fish actions.
            _player.AdvanceAction(elapsed);
            var displacement =
                _fishingBoatRiderTargetOffset - _fishingBoatRiderOffset;
            var distance = displacement.Length;
            if (distance > .0001f)
                _fishingBoatRiderOffset += displacement / distance *
                    Math.Min(distance, FishingBoatRiderMoveSpeed * elapsed);
            _player.SyncPosition(
                localBoat.Position + _fishingBoatRiderOffset);
        }
        var completed = (int)(_player.ActionTime /
                              FishingAnimationCycleSeconds());
        if (completed <= _networkFishingCompletedCycles ||
            _networkFishingCommandId is not null ||
            IsAwaitingNetworkFishingState())
            return;
        _networkFishingCompletedCycles = completed;
        DispatchNetworkFishing(active);
    }

    private double NetworkBoardingStartupTimeoutSeconds() =>
        Math.Clamp(
            3 + _networkPendingBoardDistance /
                IslandRpg.Navigation.ActorMovementService.BaseMoveSpeed,
            6,
            30);

    private void QueueNetworkFishing(WorldFish fish)
    {
        if (!TryDescribeNetworkFish(fish, out var descriptor) ||
            NetworkFishIsDepleted(descriptor))
            return;
        if (!TryFindNetworkFishingNetSlot(out var netSlot, out var netPower))
        {
            ReportBlockedAction(
                "network-fishing-without-net",
                "You need a fishing net to catch fish.");
            return;
        }
        var level = FishingSkill.LevelForExperience(
            _activePlayer?.FishingExperience ?? 0);
        if (!FishingRules.CanCatch(descriptor.Species, level, netPower))
        {
            var profile = FishingRules.Profile(descriptor.Species);
            ReportBlockedAction(
                $"network-fishing-requirement-{descriptor.Species}",
                level < profile.RequiredLevel
                    ? $"You need Fishing level {profile.RequiredLevel} " +
                      $"to catch {profile.DisplayName}."
                    : $"You need a stronger fishing net to catch " +
                      $"{profile.DisplayName}.");
            return;
        }

        ReleaseNetworkCookingPresentation();
        CancelNetworkResourceInteraction();
        CancelNetworkFishingPresentation();
        var action = new NetworkFishingTarget(fish, descriptor, netSlot);
        var target = new Vector2(
            descriptor.Position.X, descriptor.Position.Y);
        var localBoat = LocalNetworkBoat();
        var origin = localBoat?.Position ?? _player?.Position ?? target;
        var reach = localBoat is null
            ? FishingNetReach()
            : NetworkBoatFishingReach;
        if (Vector2.DistanceSquared(origin, target) <= reach * reach)
        {
            BeginNetworkFishing(action);
            return;
        }
        _networkPendingFishing = action;
        if (localBoat is not null)
        {
            SendNetworkBoatAction(
                BoatActionKind.Move, localBoat.State.BoatId,
                reference => new MoveBoatAction(
                    reference, target.X, target.Y));
            _moveMarker = new(target, 0, Action: true);
        }
        else
            SendNetworkWalk(target, preserveFishingAction: true);
    }

    private void BeginNetworkFishing(NetworkFishingTarget action)
    {
        _networkPendingFishing = null;
        _networkActiveFishing = action;
        _networkFishingCompletedCycles = 0;
        _moveMarker = null;
        SendNetworkStopForFishing();
        if (LocalNetworkBoat() is { } boat)
        {
            _fishingBoatRiderOffset = Vector2.Zero;
            _fishingBoatRiderTargetOffset =
                FishingBoatRiderPosition(new(
                    action.Descriptor.Position.X,
                    action.Descriptor.Position.Y)) - boat.Position;
        }
        _player?.FishAt(new(
            action.Descriptor.Position.X,
            action.Descriptor.Position.Y));
    }

    private void SendNetworkStopForFishing()
    {
        var localBoat = LocalNetworkBoat();
        if (localBoat is null)
            SendNetworkStop(preserveFishingAction: true);
        else if (_networkBoatCommandId is null)
            SendNetworkBoatAction(
                BoatActionKind.Stop, localBoat.State.BoatId,
                reference => new StopBoatAction(reference));
    }

    private void DispatchNetworkFishing(NetworkFishingTarget action)
    {
        if (_networkClient?.IsConnected != true ||
            !TryFindNetworkFishingNetSlot(out var slot, out _))
        {
            CancelNetworkFishingPresentation();
            return;
        }
        var reference = _networkClient.GetResourceReference(
            action.Descriptor.Chunk, action.Descriptor.Id);
        var commandId = Guid.NewGuid();
        _networkFishingCommandId = commandId;
        _networkFishingReference = reference;
        _networkFishingReceiptObserved = false;
        _networkFishingDeltaObserved = false;
        _networkFishingDeltaArrivedEarly = false;
        _networkFishingEarlyNodeRevision = 0;
        _networkFishingEarlyChunkRevision = 0;
        _networkFishingAwaitingActorRevision = 0;
        _networkFishingAwaitingInventoryRevision = 0;
        _networkFishingAwaitingNodeRevision = reference.ExpectedNodeRevision;
        _networkFishingAwaitingChunkRevision =
            reference.ExpectedResourceChunkRevision;
        _networkFishingExperienceBefore =
            _activePlayer?.FishingExperience ?? 0;
        _networkActiveFishing = action with { FishingNetSlot = slot };
        SendNetworkAction(new ResourceActionPayload(
            ResourceActionKind.Fish, reference, slot), commandId);
    }

    private bool TryFindNetworkFishingNetSlot(
        out int slot,
        out int power)
    {
        slot = -1;
        power = 0;
        var items = _activePlayer?.Inventory;
        if (items is null) return false;
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index] is not { } id ||
                !ItemCatalog.TryGet(id, out var item) ||
                !item.HasTag(ItemTag.FishingNet) ||
                item.FishingPower <= power)
                continue;
            slot = index;
            power = item.FishingPower;
        }
        return slot >= 0;
    }

    private bool TryDescribeNetworkFish(
        WorldFish fish,
        out FishSchoolDescriptor descriptor)
    {
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(fish.X, fish.Y),
            _activeWorldLevel);
        descriptor = new ProceduralFishSchoolSource()
            .DescribeSchools(_worldSeed, chunk)
            .FirstOrDefault(value =>
                value.StableKey.Equals(
                    fish.StableKey, StringComparison.Ordinal) &&
                value.Species == (FishSpecies)fish.Species)!;
        return descriptor is not null;
    }

    private bool NetworkFishIsDepleted(FishSchoolDescriptor? descriptor)
    {
        if (descriptor is null) return true;
        if (_networkClient?.State.ResourceChunks.TryGetValue(
                descriptor.Chunk, out var chunk) != true ||
            chunk is null ||
            !chunk.Nodes.TryGetValue(descriptor.Id, out var state))
            return false;
        return state.Depleted || state.Remaining <= 0;
    }

    private bool IsAwaitingNetworkFishingState()
    {
        if (!_networkFishingReceiptObserved) return false;
        var gameplay = _networkClient?.State.Gameplay;
        var gameplayReady = gameplay is not null &&
            gameplay.ActorRevision >= _networkFishingAwaitingActorRevision &&
            gameplay.InventoryRevision >=
                _networkFishingAwaitingInventoryRevision;
        if (!gameplayReady || !_networkFishingDeltaObserved) return true;
        CompleteNetworkFishingObservation();
        return false;
    }

    private void CompleteNetworkFishingObservation()
    {
        _networkFishingReference = null;
        _networkFishingReceiptObserved = false;
        _networkFishingDeltaObserved = false;
        _networkFishingDeltaArrivedEarly = false;
        _networkFishingEarlyNodeRevision = 0;
        _networkFishingEarlyChunkRevision = 0;
        _networkFishingAwaitingActorRevision = 0;
        _networkFishingAwaitingInventoryRevision = 0;
        _networkFishingAwaitingNodeRevision = 0;
        _networkFishingAwaitingChunkRevision = 0;
        if (_activePlayer is not null &&
            _activePlayer.FishingExperience >
            _networkFishingExperienceBefore)
        {
            var gained = _activePlayer.FishingExperience -
                         _networkFishingExperienceBefore;
            _chatUi.AddMessage(
                FishingSkill.ExperienceMessage(gained),
                ChatMessageStyle.Experience);
            var previousLevel = FishingSkill.LevelForExperience(
                _networkFishingExperienceBefore);
            var currentLevel = FishingSkill.LevelForExperience(
                _activePlayer.FishingExperience);
            if (currentLevel > previousLevel)
                _chatUi.AddMessage(
                    FishingSkill.LevelUpMessage(currentLevel),
                    ChatMessageStyle.LevelUp);
        }
    }

    private void ObserveNetworkFishingResourceChanges(
        NetworkResourcesChangedEventArgs value)
    {
        if (_networkFishingReference is not { } expected ||
            value.Chunk != expected.Chunk)
            return;
        foreach (var change in value.Changes)
        {
            if (change.NodeId != expected.Id)
                continue;
            if (_networkFishingReceiptObserved &&
                change.NodeRevision == _networkFishingAwaitingNodeRevision &&
                change.ResourceChunkRevision ==
                    _networkFishingAwaitingChunkRevision)
                _networkFishingDeltaObserved = true;
            else if (!_networkFishingReceiptObserved)
            {
                _networkFishingDeltaArrivedEarly = true;
                _networkFishingEarlyNodeRevision = change.NodeRevision;
                _networkFishingEarlyChunkRevision =
                    change.ResourceChunkRevision;
            }
            break;
        }
        IsAwaitingNetworkFishingState();
    }

    private bool TryHandleNetworkFishingResult(
        ResourceActionResultMessage result)
    {
        if (_networkFishingCommandId != result.CommandId)
            return false;
        _networkFishingCommandId = null;
        var expected = _networkFishingReference;
        if (result.Action != ResourceActionKind.Fish ||
            result.FishingOutcome is not { } outcome ||
            expected is null || result.Resource != expected.Value)
        {
            _chatUi.AddMessage(
                "The server returned mismatched fishing state.",
                ChatMessageStyle.Warning);
            CancelNetworkFishingPresentation();
            return true;
        }
        if (!result.Accepted)
        {
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected fishing ({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
            CancelNetworkFishingPresentation();
            return true;
        }

        var active = _networkActiveFishing;
        if (active is null ||
            outcome.Species != active.Value.Descriptor.Species)
        {
            _chatUi.AddMessage(
                "The server returned a different fish species.",
                ChatMessageStyle.Warning);
            CancelNetworkFishingPresentation();
            return true;
        }
        _entityFeedback.ShowLabel(
            FishFeedbackKey(active.Value.Fish.StableKey),
            outcome.Caught ? "Caught" : "Miss",
            outcome.Caught,
            _clock);
        _networkFishingAwaitingActorRevision = result.ActorRevision;
        _networkFishingAwaitingInventoryRevision = result.InventoryRevision;
        _networkFishingAwaitingNodeRevision = checked(
            expected.Value.ExpectedNodeRevision + 1);
        _networkFishingAwaitingChunkRevision = checked(
            expected.Value.ExpectedResourceChunkRevision + 1);
        _networkFishingReceiptObserved = true;
        if (_networkFishingDeltaArrivedEarly &&
            _networkFishingEarlyNodeRevision ==
                _networkFishingAwaitingNodeRevision &&
            _networkFishingEarlyChunkRevision ==
                _networkFishingAwaitingChunkRevision)
            _networkFishingDeltaObserved = true;
        if (!outcome.Caught)
        {
            // A miss advances cadence but cannot produce a resource delta.
            _networkFishingDeltaObserved = true;
            _networkFishingAwaitingNodeRevision =
                expected.Value.ExpectedNodeRevision;
            _networkFishingAwaitingChunkRevision =
                expected.Value.ExpectedResourceChunkRevision;
        }
        else
        {
            foreach (var reward in result.Rewards)
            {
                if (reward.Quantity <= 0 ||
                    !ItemCatalog.TryGet(reward.ItemId, out var item))
                    continue;
                _chatUi.AddMessage(
                    FishingSkill.InventoryMessage(item.Name),
                    ChatMessageStyle.Experience);
            }
        }
        IsAwaitingNetworkFishingState();
        return true;
    }

    private void CancelNetworkFishingPresentation()
    {
        _networkPendingFishing = null;
        _networkActiveFishing = null;
        _networkFishingCommandId = null;
        _networkFishingReference = null;
        _networkFishingCompletedCycles = 0;
        _networkFishingReceiptObserved = false;
        _networkFishingDeltaObserved = false;
        _networkFishingDeltaArrivedEarly = false;
        _networkFishingEarlyNodeRevision = 0;
        _networkFishingEarlyChunkRevision = 0;
        _activeFishKey = null;
        if (_player?.Action == EntityAction.Fish)
            _player.Stop();
        CenterFishingBoatRider();
    }

    private void ApplyNetworkBoatSnapshot(
        EntitySnapshot snapshot,
        float elapsed)
    {
        if (!_networkBoatIdsByEntity.TryGetValue(
                snapshot.EntityId, out var id) ||
            !_networkBoats.TryGetValue(id, out var boat))
            return;
        var previous = boat.Position;
        boat.Position = new(snapshot.X, snapshot.Y);
        var velocity = new Vector2(snapshot.VelocityX, snapshot.VelocityY);
        if (velocity.LengthSquared > .0001f)
            boat.Facing = velocity.Normalized();
        else
        {
            var displacement = boat.Position - previous;
            if (displacement.LengthSquared > .0001f)
                boat.Facing = displacement.Normalized();
        }
        boat.Moving = snapshot.State.HasFlag(NetworkEntityState.Moving) ||
                      velocity.LengthSquared > .0001f;
        boat.HasTransform = true;
        if (boat.Moving) boat.AnimationTime += elapsed;
    }

    private void PruneNetworkBoatTransforms(HashSet<ulong> seen)
    {
        foreach (var boat in _networkBoats.Values)
        {
            if (seen.Contains(boat.State.EntityId)) continue;
            boat.HasTransform = false;
            boat.Position = new(boat.State.X, boat.State.Y);
            boat.Facing = NormalizeFacing(new(
                boat.State.FacingX, boat.State.FacingY));
            boat.Moving = boat.State.Moving;
        }
    }

    private static Vector2 NormalizeFacing(Vector2 facing) =>
        facing.LengthSquared > .0001f
            ? facing.Normalized()
            : Vector2.UnitX;

    private FishingBoatVisual? GetNetworkBoatVisual(
        NetworkBoatPresentation boat)
    {
        if (_fishingRaftFrames.Length != FishingRaftDirectionCount ||
            boat.State.WorldLevel != _activeWorldLevel)
            return null;
        var directional = VillagerDirectionRig.Resolve(
            boat.Facing,
            FishingRaftDirectionCount,
            FishingRaftDirectionCount,
            0);
        var terrain = SamplePlayerTerrain(
            boat.Position.X, boat.Position.Y);
        var world = IsometricTerrainProjection.Project(
            boat.Position.X, boat.Position.Y, terrain.Height);
        return new(
            _fishingRaftFrames[directional.Index],
            _fishingRaftTextures[directional.Index],
            world,
            directional.Mirror);
    }

    private void DrawNetworkBoats()
    {
        if (!IsNetworkWorld) return;
        var playerId = _networkClient?.State.PlayerId ?? Guid.Empty;
        foreach (var boat in _networkBoats.Values
                     .Where(value =>
                         value.State.WorldLevel == _activeWorldLevel)
                     .OrderBy(value => value.Position.X + value.Position.Y))
        {
            var localOccupant =
                boat.State.OccupantPlayerId == playerId;
            var gender = localOccupant
                ? _activePlayer?.Gender ?? EntityGender.Male
                : EntityGender.Male;
            var boarded = boat.State.OccupantPlayerId != Guid.Empty;
            if (localOccupant &&
                _networkActiveFishing is not null &&
                _fishingBoatFishingComposites.TryGetValue(
                    gender, out var fishingComposite) &&
                _entityAnimations.TryGetValue(
                    (gender, EntityAction.Fish), out var fishingAnimation))
            {
                DrawNetworkBoatComposite(
                    boat,
                    fishingComposite,
                    _player?.Facing ?? boat.Facing,
                    (int)((_player?.ActionTime ?? 0) /
                          fishingAnimation.SecondsPerFrame),
                    _activePlayer?.TeamColor ?? 0);
                continue;
            }
            if (!_fishingBoatComposites.TryGetValue(
                    (gender, boarded), out var composite))
            {
                if (GetNetworkBoatVisual(boat) is { } simple)
                    DrawSprite(
                        simple.Frame, simple.Texture, simple.World,
                        mirror: simple.Mirror);
                continue;
            }
            DrawNetworkBoatComposite(
                boat,
                composite,
                boat.Facing,
                boat.Moving
                    ? (int)(boat.AnimationTime /
                            (_fishingBoatAnimation?.SecondsPerFrame ?? .1f))
                    : 0,
                localOccupant
                    ? _activePlayer?.TeamColor ?? 0
                    : 1 + (int)(boat.State.EntityId % 7));
        }
    }

    private void DrawNetworkBoatComposite(
        NetworkBoatPresentation boat,
        FishingBoatComposite composite,
        Vector2 facing,
        int rawFrame,
        int teamColor)
    {
        var directional = VillagerDirectionRig.Resolve(
            facing,
            composite.Frames.Length,
            FishingRaftDirectionCount,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            boat.Position.X, boat.Position.Y);
        var world = IsometricTerrainProjection.Project(
            boat.Position.X, boat.Position.Y, terrain.Height);
        DrawSprite(
            composite.Frames[directional.Index],
            composite.Textures[directional.Index],
            world,
            mirror: directional.Mirror,
            teamColor: teamColor);
    }
}
