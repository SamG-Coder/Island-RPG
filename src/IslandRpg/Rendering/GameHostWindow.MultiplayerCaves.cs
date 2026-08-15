using IslandRpg.Caves;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float NetworkCaveDispatchRange = 2.6f;
    private const double NetworkCaveStrikeCadenceSeconds = .9;

    private readonly record struct ExpectedNetworkCaveMutation(
        Guid CommandId,
        CaveActionKind Action,
        WorldObjectDeltaKind DeltaKind,
        Guid ObjectId,
        string DefinitionId,
        Vector2 Position,
        int ChunkX,
        int ChunkY,
        short WorldLevel,
        uint PreviousObjectRevision,
        uint CurrentObjectRevision,
        uint PreviousChunkRevision,
        uint CurrentChunkRevision)
    {
        public bool Matches(NetworkWorldObjectChange change)
        {
            if (change.Kind != DeltaKind ||
                (ObjectId != Guid.Empty && change.ObjectId != ObjectId) ||
                change.ChunkRevision != CurrentChunkRevision)
                return false;
            if (DeltaKind == WorldObjectDeltaKind.Remove)
                return NetworkPresentationApply.MatchesExpectedRemove(
                    change,
                    ObjectId,
                    PreviousObjectRevision,
                    CurrentChunkRevision);
            if (change.State is not { } state)
                return false;
            var definitionMatches = Action == CaveActionKind.WorkExcavation
                ? CaveExcavationRules.KindForItemId(state.DefinitionId) is
                    ExcavationKind.DigSite or ExcavationKind.ShallowHole or
                    ExcavationKind.OpenShaft
                : state.DefinitionId.Equals(
                    DefinitionId, StringComparison.Ordinal);
            return change.ObjectRevision == CurrentObjectRevision &&
                   state.ObjectRevision == CurrentObjectRevision &&
                   state.ChunkRevision == CurrentChunkRevision &&
                   state.ChunkX == ChunkX && state.ChunkY == ChunkY &&
                   state.WorldLevel == WorldLevel &&
                   definitionMatches &&
                   state.X == Position.X && state.Y == Position.Y;
        }
    }

    private sealed record ActiveNetworkCaveWork(
        Guid ObjectId,
        Vector2 Position,
        int ShovelInventorySlot);

    private Guid? _networkCaveCommandId;
    private CaveActionKind _networkCaveCommandAction;
    private PendingNetworkWorldAction? _networkCaveCommand;
    private ExpectedNetworkCaveMutation? _networkExpectedCaveMutation;
    private bool _networkCaveReceiptAccepted;
    private bool _networkCaveMutationObserved;
    private Guid _networkCaveObservedObjectId;
    private CaveActionResultMessage? _networkCaveAcceptedResult;
    private ActiveNetworkCaveWork? _activeNetworkCaveWork;
    private int _lastNetworkCaveStrike;
    private double _nextNetworkCaveStrikeAt;
    private bool _networkCavePresentationOwned;
    private int _networkCavePreviousDiggingExperience;

    private int FindNetworkInventorySlot(string itemId)
    {
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)_activeInventorySlot < (uint)inventory.Length &&
            inventory[_activeInventorySlot] == itemId)
            return _activeInventorySlot;
        return Array.FindIndex(inventory, value => value == itemId);
    }

    private int FindNetworkCaveFillSlot(WorldGroundObject excavation) =>
        FindNetworkInventorySlot(NetworkCaveFillItem(excavation));

    private string NetworkCaveFillItem(WorldGroundObject excavation)
    {
        var environment = new ProceduralCaveExcavationEnvironment(_worldSeed);
        return environment.TerrainAt(new(
            excavation.X, excavation.Y)).RewardItemId;
    }

    private int FindNetworkShovelSlot()
    {
        var inventory = _activePlayer?.Inventory ?? [];
        if ((uint)_activeInventorySlot < (uint)inventory.Length &&
            InventoryHasTagAt(_activeInventorySlot, ItemTag.Shovel))
            return _activeInventorySlot;
        for (var index = 0; index < inventory.Length; index++)
            if (InventoryHasTagAt(index, ItemTag.Shovel))
                return index;
        return -1;
    }

    private void QueueNetworkCaveStart(Vector2 target, int shovelSlot)
    {
        if (!IsNetworkWorld || _activeWorldLevel !=
                CaveExcavationRules.SurfaceWorldLevel ||
            !InventoryHasTagAt(shovelSlot, ItemTag.Shovel))
            return;
        target = new(
            MathF.Floor(target.X) + .5f,
            MathF.Floor(target.Y) + .5f);
        QueueNetworkPointAction(
            NetworkWorldActionKind.StartExcavation,
            target,
            shovelSlot);
    }

    private void QueueNetworkCaveWork(
        WorldGroundObject site, int shovelSlot)
    {
        if (!IsNetworkWorld || !CaveEntranceService.IsDigSite(site) ||
            !InventoryHasTagAt(shovelSlot, ItemTag.Shovel))
            return;
        var target = new Vector2(site.X, site.Y);
        if (!WorldActionReach.InRange(
                NetworkActionPosition, target, WorldActionReach.CaveDig))
        {
            QueueNetworkObjectAction(
                NetworkWorldActionKind.WorkExcavation, site, shovelSlot);
            return;
        }
        CancelNetworkCaveInteraction(stopPlayer: false);
        _activeNetworkCaveWork = new(
            site.Id, new(site.X, site.Y), shovelSlot);
        _networkCavePresentationOwned = true;
        _lastNetworkCaveStrike = 0;
        _nextNetworkCaveStrikeAt = 0;
        SendNetworkStop(preserveResourceAction: true);
        SendNetworkPresentSkill(EntityAction.Dig);
        _player?.DigAt(new(site.X, site.Y));
        _chatUi.AddMessage(
            "You continue excavating.", ChatMessageStyle.Action);
    }

    private void QueueNetworkCaveObjectAction(
        NetworkWorldActionKind action,
        WorldGroundObject value,
        int inventorySlot = -1)
    {
        if (inventorySlot < 0 && action ==
            NetworkWorldActionKind.WorkExcavation)
            inventorySlot = FindNetworkShovelSlot();
        QueueNetworkObjectAction(action, value, inventorySlot);
    }

    private void PrepareNetworkCaveCommand(
        Guid commandId,
        PendingNetworkWorldAction pending,
        IActionCommandPayload payload)
    {
        if (payload is not CaveActionPayload cave) return;
        _networkCaveCommandId = commandId;
        _networkCaveCommandAction = cave.Action;
        _networkCaveCommand = pending;
        _networkCaveReceiptAccepted = false;
        _networkCaveMutationObserved = false;
        _networkCaveObservedObjectId = Guid.Empty;
        _networkCaveAcceptedResult = null;
        _networkCavePreviousDiggingExperience =
            _activePlayer?.DiggingExperience ?? 0;
        _networkExpectedCaveMutation =
            CreateExpectedNetworkCaveMutation(commandId, cave, pending);
    }

    private ExpectedNetworkCaveMutation? CreateExpectedNetworkCaveMutation(
        Guid commandId,
        CaveActionPayload action,
        PendingNetworkWorldAction pending)
    {
        if (action is StartExcavationAction start)
        {
            var chunkX = FloorDiv(
                (int)MathF.Floor(start.X), WorldChunk.Size);
            var chunkY = FloorDiv(
                (int)MathF.Floor(start.Y), WorldChunk.Size);
            return new(
                commandId, action.Action, WorldObjectDeltaKind.Upsert,
                Guid.Empty, ItemIds.DigSite,
                new(start.X, start.Y), chunkX, chunkY, start.WorldLevel,
                0, 1, start.ExpectedChunkRevision,
                NextNetworkRevision(start.ExpectedChunkRevision));
        }
        var client = _networkClient;
        if (pending.ObjectId == Guid.Empty || client is null ||
            !client.State.WorldObjects.TryGetValue(
                pending.ObjectId, out var state) ||
            !TryCaveReference(action, out var reference))
            return null;
        var definition = action.Action switch
        {
            CaveActionKind.WorkExcavation => state.DefinitionId,
            CaveActionKind.InstallRope => ItemIds.CaveEntrance,
            CaveActionKind.TakeRope => ItemIds.CaveHole,
            _ => state.DefinitionId
        };
        var deltaKind = action.Action is
            CaveActionKind.RestoreExcavation or CaveActionKind.FillExcavation
                ? WorldObjectDeltaKind.Remove
                : WorldObjectDeltaKind.Upsert;
        return action.Action == CaveActionKind.Traverse
            ? null
            : new(
                commandId, action.Action, deltaKind, state.ObjectId,
                definition, new(state.X, state.Y), state.ChunkX,
                state.ChunkY, state.WorldLevel,
                reference.ExpectedObjectRevision,
                NextNetworkRevision(reference.ExpectedObjectRevision),
                reference.ExpectedChunkRevision,
                NextNetworkRevision(reference.ExpectedChunkRevision));
    }

    private static bool TryCaveReference(
        CaveActionPayload action, out WorldObjectReference reference)
    {
        reference = action switch
        {
            WorkExcavationAction value => value.Excavation,
            RestoreExcavationAction value => value.Excavation,
            InstallCaveRopeAction value => value.Shaft,
            TakeCaveRopeAction value => value.Entrance,
            FillExcavationAction value => value.Excavation,
            TraverseCaveAction value => value.Entrance,
            _ => default
        };
        return action is not StartExcavationAction;
    }

    private void UpdateNetworkCaveInteraction()
    {
        if (_player is null || _activeNetworkCaveWork is not { } active)
            return;
        if (_activeWorldLevel != CaveExcavationRules.SurfaceWorldLevel ||
            !_networkWorldObjects.TryGetValue(active.ObjectId, out var site) ||
            !CaveEntranceService.IsDigSite(site) ||
            Vector2.DistanceSquared(_player.Position, active.Position) >
            (NetworkCaveDispatchRange + .65f) *
            (NetworkCaveDispatchRange + .65f))
        {
            CancelNetworkCaveInteraction();
            return;
        }
        var shovelSlot = FindNetworkShovelSlot();
        if (shovelSlot < 0)
        {
            ReportBlockedAction(
                "network-dig-without-shovel",
                "You no longer have a usable shovel.");
            CancelNetworkCaveInteraction();
            return;
        }
        if (active.ShovelInventorySlot != shovelSlot)
        {
            active = active with { ShovelInventorySlot = shovelSlot };
            _activeNetworkCaveWork = active;
        }
        if (_player.Action != EntityAction.Dig)
            _player.DigAt(active.Position);
        if (_networkCaveCommandId is not null ||
            _clock < _nextNetworkCaveStrikeAt ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Dig), out var animation))
            return;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / 5);
        var cycleDuration = Math.Max(
            framesPerAngle * animation.SecondsPerFrame, .1f);
        var impactFrame = Math.Clamp(
            _player.Gender == EntityGender.Female ? 6 : 5,
            0, framesPerAngle - 1);
        var impactTime = impactFrame * animation.SecondsPerFrame;
        if (_player.ActionTime < impactTime) return;
        var strike = 1 + (int)(
            (_player.ActionTime - impactTime) / cycleDuration);
        if (strike <= _lastNetworkCaveStrike) return;
        _lastNetworkCaveStrike = strike;
        _nextNetworkCaveStrikeAt =
            _clock + NetworkCaveStrikeCadenceSeconds;
        QueueNetworkCaveObjectAction(
            NetworkWorldActionKind.WorkExcavation,
            site,
            shovelSlot);
        PlaySoundCue("digging-impact");
    }

    private void HandleNetworkCaveActionResult(CaveActionResultMessage result)
    {
        if (_networkCaveCommandId != result.CommandId ||
            result.Action != _networkCaveCommandAction)
            return;
        if (!result.Accepted)
        {
            if (result.Action == CaveActionKind.WorkExcavation &&
                result.RejectionCode is CommandRejectionCode.RateLimited or
                    CommandRejectionCode.OutOfOrder &&
                _activeNetworkCaveWork is not null)
            {
                CompleteNetworkCaveCommand();
                _nextNetworkCaveStrikeAt = _clock + .12;
                return;
            }
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected the cave action " +
                      $"({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
            CancelNetworkCaveInteraction();
            return;
        }
        _networkCaveReceiptAccepted = true;
        _networkCaveAcceptedResult = result;
        if (result.Transitioned)
        {
            CompleteNetworkCaveCommand();
            ApplyNetworkWorldLevelTransition(
                result.WorldLevel, new(result.X, result.Y));
            _chatUi.AddMessage(
                result.WorldLevel == CaveExcavationRules.UndergroundWorldLevel
                    ? "You climb down into the cave."
                    : "You climb back into the daylight.",
                ChatMessageStyle.Action);
            return;
        }
        if (_networkExpectedCaveMutation is null)
        {
            PresentAcceptedNetworkCaveAction(result);
            CompleteNetworkCaveCommand();
            return;
        }
        TryCompleteNetworkCaveMutation();
    }

    private void ObserveNetworkCaveWorldChange(
        NetworkWorldObjectChange change)
    {
        if (_networkExpectedCaveMutation is not { } expected ||
            !expected.Matches(change))
            return;
        _networkCaveObservedObjectId = change.ObjectId;
        _networkCaveMutationObserved = true;
        TryCompleteNetworkCaveMutation();
    }

    private void TryCompleteNetworkCaveMutation()
    {
        if (!_networkCaveReceiptAccepted || !_networkCaveMutationObserved ||
            _networkCaveCommand is not { } command ||
            _networkCaveAcceptedResult is not { } result)
            return;
        if (_networkCaveCommandAction == CaveActionKind.StartExcavation &&
            _networkWorldObjects.TryGetValue(
                _networkCaveObservedObjectId, out var started))
        {
            _chatUi.AddMessage(
                $"You begin excavating ({started.Health} health).",
                ChatMessageStyle.Action);
            CompleteNetworkCaveCommand();
            QueueNetworkCaveWork(started, command.InventorySlot);
            return;
        }
        PresentAcceptedNetworkCaveAction(result);
        CompleteNetworkCaveCommand();
    }

    private void PresentAcceptedNetworkCaveAction(CaveActionResultMessage result)
    {
        if (result.Action == CaveActionKind.WorkExcavation &&
            _activeNetworkCaveWork is { } active)
        {
            var damage = Math.Max(0, result.Damage);
            ShowEntityImpact(
                DigFeedbackKey(active.ObjectId), damage, damage > 0);
            if (damage > 0)
            {
                _chatUi.AddMessage(
                    $"You excavate for {damage} damage.",
                    ChatMessageStyle.Damage);
                PlaySoundCue("digging-impact");
            }
            var currentExperience = _activePlayer?.DiggingExperience ?? 0;
            var gained = Math.Max(
                0, currentExperience - _networkCavePreviousDiggingExperience);
            if (gained > 0)
            {
                _chatUi.AddMessage(
                    $"+{gained} Digging XP.",
                    ChatMessageStyle.Experience);
                var previousLevel = DiggingSkill.LevelForExperience(
                    _networkCavePreviousDiggingExperience);
                var currentLevel = DiggingSkill.LevelForExperience(
                    currentExperience);
                if (currentLevel > previousLevel)
                    _chatUi.AddMessage(
                        $"Your Digging level is now {currentLevel}.",
                        ChatMessageStyle.LevelUp);
            }
            if (result.Completed ||
                !_networkWorldObjects.TryGetValue(
                    active.ObjectId, out var value) ||
                !CaveEntranceService.IsDigSite(value))
            {
                _chatUi.AddMessage(
                    result.Detail == "cave_discovered"
                        ? "The completed excavation opens into a cave. A rope could secure the descent."
                        : "The hole has a solid bottom. Nothing lies below.",
                    ChatMessageStyle.Action);
                CancelNetworkCaveInteraction();
            }
        }
        else
        {
            var message = result.Action switch
            {
                CaveActionKind.RestoreExcavation =>
                    "You restore the unfinished ground.",
                CaveActionKind.InstallRope =>
                    "You secure the rope. The cave can now be entered.",
                CaveActionKind.TakeRope =>
                    "You recover the rope. The cave is no longer accessible.",
                CaveActionKind.FillExcavation =>
                    "You fill in the excavation.",
                _ => ""
            };
            if (message.Length > 0)
                _chatUi.AddMessage(message, ChatMessageStyle.Action);
        }
    }

    private void CompleteNetworkCaveCommand()
    {
        _networkCaveCommandId = null;
        _networkCaveCommand = null;
        _networkExpectedCaveMutation = null;
        _networkCaveReceiptAccepted = false;
        _networkCaveMutationObserved = false;
        _networkCaveObservedObjectId = Guid.Empty;
        _networkCaveAcceptedResult = null;
    }

    private void CancelNetworkCaveInteraction(bool stopPlayer = true)
    {
        CompleteNetworkCaveCommand();
        _activeNetworkCaveWork = null;
        _lastNetworkCaveStrike = 0;
        _nextNetworkCaveStrikeAt = 0;
        if (stopPlayer && _networkCavePresentationOwned)
        {
            if (_player?.Action == EntityAction.Dig)
                _player.Stop();
            SendNetworkPresentSkill(EntityAction.Idle);
        }
        _networkCavePresentationOwned = false;
    }

    private void RenderNetworkCaveHealthBar(Vector4 scene)
    {
        if (_activeNetworkCaveWork is not { } active ||
            !_networkWorldObjects.TryGetValue(active.ObjectId, out var site) ||
            site.MaxHealth <= 0)
            return;
        var anchor = SpriteAnchor(GroundObjectWorld(site));
        DrawEntityFeedback(
            scene,
            (anchor.X - 20, anchor.Y - 40, anchor.X + 20, anchor.Y),
            site.Health / (float)site.MaxHealth,
            DigFeedbackKey(site.Id),
            forceHealth: true);
    }

    private void ApplyNetworkWorldLevelTransition(
        int destinationLevel, Vector2 authoritativePosition)
    {
        if (_player is null) return;
        if (destinationLevel == _activeWorldLevel)
            return;
        // Never GetResult a pending generate here. That is a render-thread
        // lock on procedural chunk build and is what made join unplayable.
        CancelWorldLevelWork(clearMinimap: true);
        _networkClient?.SnapshotBuffer.Clear();
        foreach (var coordinate in _worldChunks.Keys.ToArray())
            UnloadWorldChunk(coordinate, save: false);
        _activeWorldLevel = destinationLevel;
        _caveEntranceLightWorld = destinationLevel ==
            CaveExcavationRules.UndergroundWorldLevel
                ? authoritativePosition
                : null;
        _player.TeleportTo(authoritativePosition);
        _playerEnemyTargetableAt = _clock +
            EnemySpawnerService.WorldTransitionGraceSeconds;
        _camera = Vector2.Zero;
        FollowPlayer();
    }
}
