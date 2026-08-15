using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private double _nextCampfireExpiryCheckAt;
    private Guid? _activeCampfireFuelPickupId;
    private readonly List<(Vector2 Position, int WorldLevel)>
        _litCampfireRecoverySources = [];
    private double _litCampfireRecoveryCacheAt = double.NaN;
    private string? _litCampfireRecoveryWorldId;

    private bool IsHumanNearLitCampfire(
        Vector2 position, int worldLevel) => IsNearLitCampfire(
            position,
            worldLevel,
            EntityHealthRegenerationService.LitCampfireRange);

    private bool IsNearLitCampfire(
        Vector2 position, int worldLevel, float range)
    {
        RefreshLitCampfireRecoverySources();
        var rangeSquared = Math.Max(0, range) * Math.Max(0, range);
        foreach (var source in _litCampfireRecoverySources)
            if (source.WorldLevel == worldLevel &&
                Vector2.DistanceSquared(position, source.Position) <=
                rangeSquared)
                return true;
        return false;
    }

    private void RefreshLitCampfireRecoverySources()
    {
        if (_litCampfireRecoveryCacheAt == _worldGameSeconds &&
            _litCampfireRecoveryWorldId == _activeWorld?.Id)
            return;
        _litCampfireRecoveryCacheAt = _worldGameSeconds;
        _litCampfireRecoveryWorldId = _activeWorld?.Id;
        _litCampfireRecoverySources.Clear();
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            foreach (var candidate in gpu.Chunk.GroundObjects)
            {
                if (CampfireService.State(
                        candidate, _worldGameSeconds) != CampfireState.Lit)
                    continue;
                _litCampfireRecoverySources.Add((
                    new(candidate.X, candidate.Y),
                    gpu.Chunk.Coordinate.Level));
            }
        }
    }

    private bool TryAddCampfireFuel(
        Guid campfireId, int inventorySlot, string fuelItemId)
    {
        if (IsNetworkWorld)
        {
            if (!_networkWorldObjects.TryGetValue(
                    campfireId, out var networkCampfire))
                return false;
            QueueNetworkObjectAction(
                NetworkWorldActionKind.AddCampfireFuel,
                networkCampfire,
                inventorySlot);
            return true;
        }
        if (_activePlayer is null ||
            !InventoryContainsAt(inventorySlot, fuelItemId))
            return false;
        var location = FindGroundObjectLocation(campfireId);
        if (location is null)
            return false;
        var inventory = ActivePlayerInventory();
        if (!CampfireService.CanAddFuel(
                location.Value.Object, fuelItemId, _worldGameSeconds) ||
            !inventory.TryTake(inventorySlot, 1, out _))
            return false;

        location.Value.Chunk.GroundObjects[location.Value.Index] =
            CampfireService.AddFuel(
                location.Value.Object, fuelItemId, _worldGameSeconds);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == inventorySlot)
            _activeInventorySlot = -1;
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Value.Chunk);
        _chatUi.AddMessage(
            $"You add the {ItemCatalog.Get(fuelItemId).Name} " +
            "to the campfire.",
            ChatMessageStyle.Action);
        return true;
    }

    private void QueueCampfireLight(WorldGroundObject campfire)
    {
        if (IsNetworkWorld)
        {
            QueueNetworkObjectAction(
                NetworkWorldActionKind.LightCampfire, campfire);
            return;
        }
        if (_activePlayer is null) return;
        if (!CampfireService.CanLight(
                campfire, _activePlayer.Inventory ?? [], _worldGameSeconds))
        {
            ReportBlockedAction(
                "campfire-light-requirements",
                "You need small rocks and a knife to light the campfire.");
            return;
        }
        _worldActions.QueuePath(
            new Vector2(campfire.X, campfire.Y),
            .72f,
            WorldActionType.LightCampfire,
            groundObjectId: campfire.Id,
            clearTreeActions: true);
    }

    private void QueueCampfireFuelPickup(WorldGroundObject campfire)
    {
        if (IsNetworkWorld)
        {
            _worldActions.QueuePath(
                new Vector2(campfire.X, campfire.Y),
                WorldActionReach.Campfire,
                WorldActionType.TakeCampfireFuel,
                groundObjectId: campfire.Id,
                clearTreeActions: true);
            SendNetworkWalk(
                WorldActionReach.StandOff(
                    NetworkActionPosition,
                    new Vector2(campfire.X, campfire.Y),
                    WorldActionReach.Campfire));
            return;
        }
        if (_activePlayer is null) return;
        if (campfire.FuelItemId is not { } fuelItemId ||
            !ActivePlayerInventory().CanAdd(fuelItemId))
        {
            ReportBlockedAction(
                "campfire-take-fuel-full",
                "Your inventory is too full to take the log.");
            return;
        }
        _worldActions.QueuePath(
            new Vector2(campfire.X, campfire.Y),
            .72f,
            WorldActionType.TakeCampfireFuel,
            groundObjectId: campfire.Id,
            clearTreeActions: true);
    }

    internal void BeginCampfireFuelPickup(
        Guid campfireId, Vector2 target)
    {
        if (_player is null || _activePlayer is null) return;
        var location = FindGroundObjectLocation(campfireId);
        if (location is null ||
            !CampfireService.CanRemoveFuel(
                location.Value.Object, _worldGameSeconds))
            return;
        if (location.Value.Object.FuelItemId is not { } fuelItemId ||
            !ActivePlayerInventory().CanAdd(fuelItemId))
        {
            ReportBlockedAction(
                "campfire-take-fuel-full",
                "Your inventory is too full to take the log.");
            return;
        }
        _activeGroundPickupId = null;
        _activeCampfireFuelPickupId = campfireId;
        if (IsNetworkWorld)
        {
            SendNetworkPresentSkill(EntityAction.Gather);
            _networkWorldActionCommitAt = _clock + GroundItemActionSeconds;
        }
        _player.GatherAt(target);
        if (IsNetworkWorld)
            _player.RestartActionTime();
    }

    internal void UpdateCampfireFuelPickup()
    {
        if (_player is null ||
            _activeCampfireFuelPickupId is not { } campfireId)
            return;
        if (_player.Action != EntityAction.Gather)
        {
            _activeCampfireFuelPickupId = null;
            return;
        }
        if (!NetworkResourceWindupReady(
                _player.ActionTime, GroundItemActionSeconds, _clock,
                _networkWorldActionCommitAt))
            return;
        _activeCampfireFuelPickupId = null;
        TryTakeCampfireFuel(campfireId);
        _player.Stop();
    }

    private void TryTakeCampfireFuel(Guid campfireId)
    {
        if (IsNetworkWorld)
        {
            if (_networkWorldObjects.TryGetValue(
                    campfireId, out var networkCampfire))
                QueueNetworkObjectAction(
                    NetworkWorldActionKind.TakeCampfireFuel,
                    networkCampfire);
            return;
        }
        if (_activePlayer is null) return;
        var location = FindGroundObjectLocation(campfireId);
        if (location is null) return;
        var campfire = location.Value.Object;
        var inventory = ActivePlayerInventory();
        if (!CampfireService.CanRemoveFuel(campfire, _worldGameSeconds) ||
            campfire.FuelItemId is not { } fuelItemId ||
            !inventory.TryAdd(fuelItemId))
        {
            ReportBlockedAction(
                "campfire-take-fuel-full",
                "Your inventory is too full to take the log.");
            return;
        }
        location.Value.Chunk.GroundObjects[location.Value.Index] =
            CampfireService.RemoveFuel(campfire, _worldGameSeconds);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Value.Chunk);
        _chatUi.AddMessage(
            $"You take the {ItemCatalog.Get(fuelItemId).Name} " +
            "from the campfire.",
            ChatMessageStyle.Action);
    }

    internal void TryLightCampfire(Guid campfireId)
    {
        if (IsNetworkWorld)
        {
            if (_networkWorldObjects.TryGetValue(
                    campfireId, out var networkCampfire))
                QueueNetworkObjectAction(
                    NetworkWorldActionKind.LightCampfire,
                    networkCampfire);
            return;
        }
        if (_activePlayer is null) return;
        var location = FindGroundObjectLocation(campfireId);
        if (location is null) return;
        var failure = CampfireService.LightFailure(
            location.Value.Object,
            _activePlayer.Inventory ?? [],
            _worldGameSeconds);
        if (failure != CampfireLightFailure.None)
        {
            ReportBlockedAction(
                CampfireService.LightFailureCode(failure),
                CampfireService.LightFailureMessage(failure));
            return;
        }
        var level = FiremakingSkill.LevelForExperience(
            _activePlayer.FiremakingExperience);
        location.Value.Chunk.GroundObjects[location.Value.Index] =
            EntityInteractionService.LightCampfire(
                location.Value.Object,
                _activePlayer.Inventory,
                _worldGameSeconds,
                level);
        var award = FiremakingSkill.AwardExperience(
            _activePlayer.FiremakingExperience);
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            FiremakingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(location.Value.Chunk);
        _chatUi.AddMessage(
            "You strike the small rocks against the knife and light the campfire.",
            ChatMessageStyle.Action);
        RecordQuestEvent(new(QuestEventType.LightCampfire));
        if (award.Gained > 0)
            _chatUi.AddMessage(
                FiremakingSkill.ExperienceMessage(award.Gained),
                ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                FiremakingSkill.LevelUpMessage(award.Level),
                ChatMessageStyle.LevelUp);
    }

    private void ExamineCampfire(WorldGroundObject campfire)
    {
        var message = CampfireService.State(
            campfire, _worldGameSeconds) switch
        {
            CampfireState.Lit =>
                $"A burning campfire fueled with " +
                $"{ItemCatalog.Get(campfire.FuelItemId!).Name}. " +
                $"It has about {Math.Max(0,
                    campfire.LitUntilGameSeconds - _worldGameSeconds) /
                    3600:0.0} hours remaining.",
            CampfireState.Fueled =>
                $"An unlit campfire containing " +
                $"{ItemCatalog.Get(campfire.FuelItemId!).Name}.",
            _ => ItemCatalog.Get(ItemIds.Campfire).Examine
        };
        _chatUi.AddMessage(message, ChatMessageStyle.Normal);
    }

    private void UpdateExpiredCampfires()
    {
        if (_clock < _nextCampfireExpiryCheckAt) return;
        _nextCampfireExpiryCheckAt = _clock + 1;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            var changed = false;
            for (var index = 0;
                 index < gpu.Chunk.GroundObjects.Count;
                 index++)
            {
                var current = gpu.Chunk.GroundObjects[index];
                if (!CampfireService.IsCampfire(current) ||
                    current.LitUntilGameSeconds <= 0 ||
                    current.LitUntilGameSeconds > _worldGameSeconds)
                    continue;
                if (CharcoalService.IsReady(
                        current, _worldGameSeconds))
                {
                    var origin = new Vector2(current.X, current.Y);
                    var dropChunk = gpu.Chunk;
                    var dropPosition = origin + new Vector2(.8f, .35f);
                    if (TryFindGroundObjectDrop(
                            origin,
                            out var dropGpu,
                            out var clearPosition,
                            out _))
                    {
                        dropChunk = dropGpu.Chunk;
                        dropPosition = clearPosition;
                    }
                    dropChunk.GroundObjects.Add(new(
                        Guid.NewGuid(),
                        ItemIds.Charcoal,
                        dropPosition.X,
                        dropPosition.Y));
                    if (!ReferenceEquals(dropChunk, gpu.Chunk))
                        QueueChunkSave(dropChunk);
                    _chatUi.AddMessage(
                        "The spent log fuel has burned down into charcoal.",
                        ChatMessageStyle.Action);
                }
                gpu.Chunk.GroundObjects[index] =
                    CampfireService.Expire(
                        current, _worldGameSeconds);
                changed = true;
            }
            if (changed) QueueChunkSave(gpu.Chunk);
        }
    }

    private (
        WorldChunk Chunk,
        int Index,
        WorldGroundObject Object)? FindGroundObjectLocation(Guid id)
    {
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveWorldChunk(gpu)) continue;
            var index = gpu.Chunk.GroundObjects.FindIndex(
                item => item.Id == id);
            if (index >= 0)
                return (
                    gpu.Chunk,
                    index,
                    gpu.Chunk.GroundObjects[index]);
        }
        return null;
    }
}
