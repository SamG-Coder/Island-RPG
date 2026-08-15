using IslandRpg.Fishing;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private sealed record NetworkFishingTarget(
        WorldFish Fish,
        FishSchoolDescriptor Descriptor,
        ResourceNodeId NodeId,
        WorldChunkKey Chunk,
        Vector2 Position);

    private readonly record struct NetworkFishingAction(
        NetworkFishingTarget Target,
        int ToolInventorySlot);

    private NetworkFishingAction? _pendingNetworkFishingAction;
    private NetworkFishingAction? _activeNetworkFishingAction;
    private int _lastNetworkFishingCatch;
    private double _nextNetworkFishingCatchAt;
    private readonly Dictionary<string, FishSchoolDescriptor>
        _networkFishDescriptors = new(StringComparer.Ordinal);

    private void QueueNetworkFishing(WorldFish fish)
    {
        if (_player is null || _networkClient?.IsConnected != true ||
            !TryDescribeNetworkFish(fish, out var target))
        {
            ReportBlockedAction(
                "network-fishing-unknown",
                "That fish school is not ready to catch.");
            return;
        }
        if (NetworkFishingIsDepleted(target))
        {
            ReportBlockedAction(
                "network-fishing-depleted",
                "The fish school has been exhausted.");
            return;
        }
        if (!TryFindNetworkFishingNetSlot(out var toolSlot, out var netPower))
        {
            ReportBlockedAction(
                "network-fishing-without-net",
                "You need a fishing net to catch fish.");
            return;
        }
        var level = FishingSkill.LevelForExperience(
            _activePlayer?.FishingExperience ?? 0);
        if (!FishingRules.CanCatch(target.Descriptor.Species, level, netPower))
        {
            var profile = FishingRules.Profile(target.Descriptor.Species);
            ReportBlockedAction(
                $"network-fishing-requirement-{target.Descriptor.Species}",
                level < profile.RequiredLevel
                    ? $"You need Fishing level {profile.RequiredLevel} " +
                      $"to catch {profile.DisplayName}."
                    : $"You need a stronger fishing net to catch " +
                      $"{profile.DisplayName}.");
            return;
        }
        if (_activePlayer is not null &&
            !ActivePlayerInventory().CanAdd(target.Descriptor.ItemId))
        {
            ReportBlockedAction(
                "network-fishing-inventory-full",
                "Your inventory is too full to hold another catch.");
            return;
        }

        CancelNetworkResourceInteraction(stopPlayer: false);
        var pending = new NetworkFishingAction(target, toolSlot);
        _pendingNetworkFishingAction = pending;
        var range = FishingNetReach();
        if (WorldActionReach.InRange(
                NetworkActionPosition, target.Position, range))
        {
            BeginNetworkFishingAction(pending);
            return;
        }
        if (LocalNetworkBoat() is { } boat)
        {
            SendNetworkBoatAction(
                BoatActionKind.Move, boat.State.BoatId,
                reference => new MoveBoatAction(
                    reference, target.Position.X, target.Position.Y));
            _moveMarker = new(target.Position, 0, Action: true);
            return;
        }
        QueueNetworkWalkToAct(
            target.Position,
            range,
            WorldActionType.Fish,
            fishKey: fish.StableKey);
    }

    private bool UpdateNetworkFishingInteraction()
    {
        if (_player is null) return false;
        if (_pendingNetworkFishingAction is { } pending)
        {
            if (!NetworkFishingActionStillValid(pending))
            {
                CancelNetworkResourceInteraction();
                return true;
            }
            if (WorldActionReach.InRange(
                    NetworkActionPosition,
                    pending.Target.Position,
                    FishingNetReach()))
                BeginNetworkFishingAction(pending);
        }

        if (_activeNetworkFishingAction is not { } active)
            return _pendingNetworkFishingAction is not null;
        if (!NetworkFishingActionStillValid(active))
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

        if (_player.Action != EntityAction.Fish)
            _player.FishAt(active.Target.Position);
        if (_networkResourceCommandId is not null ||
            IsAwaitingNetworkResourceGameplayState() ||
            _clock < _nextNetworkFishingCatchAt ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Fish), out var animation))
            return true;

        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(
            (int)MathF.Round((framesPerAngle - 1) * .43f),
            0,
            framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return true;
        var catchIndex = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (catchIndex <= _lastNetworkFishingCatch) return true;
        _lastNetworkFishingCatch = catchIndex;
        DispatchNetworkFishingAction(active);
        return true;
    }

    private void BeginNetworkFishingAction(NetworkFishingAction action)
    {
        _pendingNetworkFishingAction = null;
        _activeNetworkFishingAction = action;
        _networkResourceCommandId = null;
        _lastNetworkFishingCatch = 0;
        _nextNetworkFishingCatchAt = 0;
        _networkResourcePresentationOwned = true;
        _networkResourceCommitAt = 0;
        SendNetworkPresentSkill(EntityAction.Fish);
        if (LocalNetworkBoat() is { } boat)
        {
            _fishingBoatRiderOffset = Vector2.Zero;
            _fishingBoatRiderTargetOffset =
                FishingBoatRiderPosition(action.Target.Position) -
                boat.Position;
        }
        _player!.FishAt(action.Target.Position);
        _player.RestartActionTime();
        _chatUi.AddMessage(
            "You begin fishing.",
            Rendering.Ui.ChatMessageStyle.Action);
    }

    private void DispatchNetworkFishingAction(NetworkFishingAction action)
    {
        if (_networkClient?.IsConnected != true) return;
        var toolSlot = action.ToolInventorySlot;
        if (!TryFindNetworkFishingNetSlot(out toolSlot, out _))
        {
            ReportBlockedAction(
                "network-fishing-without-net",
                "You no longer have a usable fishing net.");
            CancelNetworkResourceInteraction();
            return;
        }
        action = action with { ToolInventorySlot = toolSlot };
        _activeNetworkFishingAction = action;
        var reference = _networkClient.GetResourceReference(
            action.Target.Chunk, action.Target.NodeId);
        var commandId = Guid.NewGuid();
        _networkResourceCommandId = commandId;
        _networkResourceCommandReference = reference;
        _nextNetworkFishingCatchAt =
            _clock + FishingAnimationCycleSeconds();
        ResetNetworkResourceExperienceObservation();
        SendNetworkAction(new ResourceActionPayload(
            ResourceActionKind.Fish, reference, toolSlot), commandId);
    }

    private bool TryDescribeNetworkFish(
        WorldFish fish,
        out NetworkFishingTarget target)
    {
        target = null!;
        if (!IsNetworkWorld) return false;
        if (!_networkFishDescriptors.TryGetValue(
                fish.StableKey, out var descriptor))
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
            if (descriptor is null) return false;
            _networkFishDescriptors[fish.StableKey] = descriptor;
            _networkResourceHotPath.RememberFish(
                fish.StableKey, descriptor.Id, descriptor.Chunk);
        }
        target = new(
            fish,
            descriptor,
            descriptor.Id,
            descriptor.Chunk,
            new Vector2(descriptor.Position.X, descriptor.Position.Y));
        return true;
    }

    private bool TryFindNetworkFishingNetSlot(out int slot, out int power)
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

    private bool NetworkFishingIsDepleted(NetworkFishingTarget target)
    {
        if (_networkClient?.State.ResourceChunks.TryGetValue(
                target.Chunk, out var chunk) != true ||
            chunk is null ||
            !chunk.Nodes.TryGetValue(target.NodeId, out var state))
            return false;
        return state.Depleted || state.Remaining <= 0;
    }

    private bool NetworkFishingActionStillValid(NetworkFishingAction action) =>
        !NetworkFishingIsDepleted(action.Target) &&
        TryFindNetworkFishingNetSlot(out _, out _);

    private void ClearNetworkFishingAction()
    {
        _pendingNetworkFishingAction = null;
        _activeNetworkFishingAction = null;
        _lastNetworkFishingCatch = 0;
        _nextNetworkFishingCatchAt = 0;
        CenterFishingBoatRider();
    }
}
