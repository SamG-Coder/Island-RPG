using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float NetworkInteractionDispatchRange = 2.6f;
    private const float NetworkGroundPickupRange = WorldActionReach.GroundPickup;

    private enum NetworkWorldActionKind
    {
        PickUp,
        Drop,
        OpenContainer,
        AddCampfireFuel,
        TakeCampfireFuel,
        LightCampfire,
        CookOnCampfire,
        UseCraftingStation,
        PlaceInventoryWorldObject,
        PlaceConstruction,
        BuildConstruction,
        Demolish,
        StartExcavation,
        WorkExcavation,
        RestoreExcavation,
        InstallCaveRope,
        TakeCaveRope,
        FillExcavation,
        TraverseCave,
        HarvestCrop,
        CookStew,
        CutPlantedTree
    }

    private readonly record struct PendingNetworkWorldAction(
        NetworkWorldActionKind Kind,
        Vector2 Target,
        Guid ObjectId = default,
        int InventorySlot = -1,
        int Quantity = 1,
        string? DefinitionId = null,
        int Rotation = 0);

    private readonly record struct PendingNetworkContainerTransfer(
        ContainerTransferDirection Direction,
        int InventorySlot,
        int ContainerSlot,
        int Quantity);

    /// <summary>
    /// Exact authoritative mutation expected after one accepted construction
    /// command. Public world deltas do not carry their originating command ID,
    /// so command acceptance is paired with the immutable revisions and state
    /// authored into its request. This prevents another player's update from
    /// releasing a local construction queue.
    /// </summary>
    private readonly record struct ExpectedNetworkConstructionMutation(
        Guid CommandId,
        Guid ObjectId,
        string DefinitionId,
        Vector2 Position,
        int Rotation,
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
            if (change.Kind != WorldObjectDeltaKind.Upsert ||
                change.State is not { } state ||
                (ObjectId != Guid.Empty && change.ObjectId != ObjectId))
                return false;
            return change.ObjectRevision == CurrentObjectRevision &&
                   change.ChunkRevision == CurrentChunkRevision &&
                   state.ObjectRevision == CurrentObjectRevision &&
                   state.ChunkRevision == CurrentChunkRevision &&
                   state.ChunkX == ChunkX &&
                   state.ChunkY == ChunkY &&
                   state.WorldLevel == WorldLevel &&
                   state.DefinitionId.Equals(
                       DefinitionId, StringComparison.Ordinal) &&
                   state.X == Position.X &&
                   state.Y == Position.Y &&
                   state.Rotation == Rotation;
        }
    }

    private PendingNetworkWorldAction? _pendingNetworkWorldAction;
    private readonly List<NetworkWorldActionKind>
        _networkGroundObjectContextActions = [];
    private Guid? _networkRequestedContainerId;
    private readonly Queue<PendingNetworkContainerTransfer>
        _networkContainerTransfers = [];
    private Guid? _networkContainerTransferCommandId;
    private bool _networkContainerTransferAwaitingState;
    private Guid? _networkRepeatedConstructionId;
    private Guid? _networkBuildCommandId;
    private ExpectedNetworkConstructionMutation?
        _networkExpectedBuildMutation;
    private bool _networkBuildAwaitingDelta;
    private readonly Queue<PendingNetworkWorldAction>
        _networkConstructionPlacements = [];
    private Guid? _networkPlacementCommandId;
    private Guid? _networkCookingCommandId;
    private string? _networkCookingRawItemId;
    private bool _networkCookingPresentationOwned;
    private ExpectedNetworkConstructionMutation?
        _networkExpectedPlacementMutation;
    private bool _networkPlacementAwaitingDelta;

    private void UpdateNetworkWorldInteractionInput(
        bool leftDown, bool rightDown)
    {
        var placingObject = UpdatePlaceableObjectPlacementInput(
            leftDown, rightDown);
        if (!placingObject && rightDown && !_gameRightWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
        {
            var target = ScreenToTerrain(SceneMousePosition());
            if (TryTargetBucketFill(target))
            {
            }
            else if (!TryOpenWorldRightClickContext(
                    SceneMousePosition(), target))
            {
                _pendingNetworkWorldAction = null;
                StopNetworkRepeatedConstruction();
                SendNetworkWalk(target);
            }
        }

        if (!placingObject && leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryGetEnemyUnderMouse(
                SceneMousePosition(), out var combatEnemy))
            SendNetworkCombatTarget(combatEnemy.Id);
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryTargetBucketFill(ScreenToTerrain(SceneMousePosition())))
        {
        }
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryTargetCaveDig(ScreenToTerrain(SceneMousePosition())))
        {
        }
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryGetGroundObjectUnderMouse(
                SceneMousePosition(), out var groundObject, out _))
        {
            if (ConstructionService.IsConstructionSite(groundObject))
                QueueNetworkObjectAction(
                    NetworkWorldActionKind.BuildConstruction, groundObject);
            else if (IsNetworkContainer(groundObject.Id))
                QueueNetworkOpenContainer(groundObject);
            else if (groundObject.ItemId == ItemIds.TrainingDummy)
                QueueTrainingDummyAttack(groundObject);
            else if (CaveEntranceService.IsEntrance(groundObject))
                QueueCaveEntry(groundObject);
            else if (CaveEntranceService.IsDigSite(groundObject))
                QueueContinueCaveDig(groundObject);
            else if (groundObject.ItemId == ItemIds.CookingPot)
                QueuePotCooking(groundObject);
            else if (CraftingStationService.IsStation(
                         groundObject.ItemId))
                QueueNetworkCraftingStation(groundObject);
            else if (!PlaceableObjectCatalog.IsPlaceable(
                         groundObject.ItemId) &&
                     !CaveEntranceService.IsExcavation(groundObject))
                QueueGroundObjectPickup(groundObject);
        }
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
                 !IsPointerOverGameUi(MouseState.Position) &&
                 TryGetFishUnderMouse(
                     SceneMousePosition(), out var fishingTarget))
            QueueNetworkFishing(fishingTarget);
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
                 !IsPointerOverGameUi(MouseState.Position) &&
                 TryGetGatherableVegetationUnderMouse(
                     SceneMousePosition(),
                     out var vegetation,
                     out var vegetationKey))
        {
            if (vegetation.Kind == WorldVegetationKind.BerryBush)
                QueueNetworkVegetationAction(
                    vegetationKey, ResourceActionKind.GatherBerries);
            else
                QueueNetworkVegetationAction(
                    vegetationKey, ResourceActionKind.GatherFibre);
        }
        else if (!placingObject && leftDown && !_gameLeftWasDown &&
                 !IsPointerOverGameUi(MouseState.Position) &&
                 TryGetTreeUnderMouse(
                     SceneMousePosition(), out var actionTree))
            QueueNetworkTreeAction(
                actionTree, ResourceActionKind.CutTree);

        _gameRightWasDown = rightDown;
        _gameLeftWasDown = leftDown;
    }

    private void OpenNetworkGroundObjectContext(
        WorldGroundObject value, Vector2 walkTarget)
    {
        _groundObjectContextTarget = value;
        _groundObjectContextWalkTarget = walkTarget;
        _networkGroundObjectContextActions.Clear();
        var labels = new List<string>();

        void Add(string label, NetworkWorldActionKind action)
        {
            labels.Add(label);
            _networkGroundObjectContextActions.Add(action);
        }

        if (ConstructionService.IsConstructionSite(value))
        {
            Add("Build", NetworkWorldActionKind.BuildConstruction);
            Add("Demolish", NetworkWorldActionKind.Demolish);
        }
        else if (IsNetworkContainer(value.Id))
            Add("Open", NetworkWorldActionKind.OpenContainer);
        else if (CampfireService.IsCampfire(value))
        {
            var state = CampfireService.State(
                value, _worldGameSeconds);
            if (state == CampfireState.Empty &&
                _activeInventorySlot >= 0 &&
                _activePlayer?.Inventory?.ElementAtOrDefault(
                    _activeInventorySlot) is { } fuelItemId &&
                CampfireService.CanAddFuel(
                    value, fuelItemId, _worldGameSeconds))
                Add("Add fuel", NetworkWorldActionKind.AddCampfireFuel);
            if (state == CampfireState.Fueled)
            {
                Add("Light", NetworkWorldActionKind.LightCampfire);
                Add("Take log", NetworkWorldActionKind.TakeCampfireFuel);
            }
            if (state == CampfireState.Lit &&
                TrySelectedRawCookingItem(out _, out _))
                Add("Cook", NetworkWorldActionKind.CookOnCampfire);
        }
        else if (CaveEntranceService.IsEntrance(value))
        {
            Add(_activeWorldLevel == (int)WorldLevel.Overworld
                    ? "Climb down"
                    : "Climb up",
                NetworkWorldActionKind.TraverseCave);
            if (_activeWorldLevel == (int)WorldLevel.Overworld)
                Add("Take rope", NetworkWorldActionKind.TakeCaveRope);
        }
        else if (CaveEntranceService.IsDigSite(value))
        {
            Add("Continue digging", NetworkWorldActionKind.WorkExcavation);
            Add("Restore ground", NetworkWorldActionKind.RestoreExcavation);
        }
        else if (CaveEntranceService.IsHole(value))
        {
            if (FindNetworkInventorySlot(ItemIds.Rope) is >= 0)
                Add("Install rope", NetworkWorldActionKind.InstallCaveRope);
            if (FindNetworkCaveFillSlot(value) is >= 0)
                Add("Fill hole", NetworkWorldActionKind.FillExcavation);
        }
        else if (CaveEntranceService.IsShallowHole(value) &&
                 FindNetworkCaveFillSlot(value) is >= 0)
            Add("Fill hole", NetworkWorldActionKind.FillExcavation);
        else if (PlantedTreeService.IsPlantedTree(value))
        {
            if (PlantedTreeService.IsLiving(value))
                Add("Chop tree", NetworkWorldActionKind.CutPlantedTree);
        }
        else if (CropService.IsCrop(value))
            Add("Harvest", NetworkWorldActionKind.HarvestCrop);
        else if (value.ItemId == ItemIds.CookingPot)
            Add("Cook stew", NetworkWorldActionKind.CookStew);
        else if (CraftingStationService.IsStation(value.ItemId))
            Add(
                CraftingStationService.ActionLabel(value.ItemId),
                NetworkWorldActionKind.UseCraftingStation);
        else if (!PlaceableObjectCatalog.IsPlaceable(value.ItemId))
            Add("Pick up", NetworkWorldActionKind.PickUp);

        labels.Add("Walk Here");
        labels.Add("Examine");
        _inventoryContext.Close();
        _treeContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _miningContext.Close();
        _groundObjectContext.Open(
            MouseState.Position, labels, SceneClientBounds(),
            labels.Count > 4 ? 178 : 142);
    }

    private bool TryHandleNetworkGroundObjectContextSelection(
        WorldGroundObject value, int option)
    {
        if (!IsNetworkWorld) return false;
        if ((uint)option < (uint)_networkGroundObjectContextActions.Count)
        {
            var action = _networkGroundObjectContextActions[option];
            if (action == NetworkWorldActionKind.OpenContainer)
                QueueNetworkOpenContainer(value);
            else if (action == NetworkWorldActionKind.UseCraftingStation)
                QueueNetworkCraftingStation(value);
            else if (action == NetworkWorldActionKind.CookStew)
                QueuePotCooking(value);
            else if (action == NetworkWorldActionKind.WorkExcavation)
                QueueContinueCaveDig(value);
            else if (action == NetworkWorldActionKind.InstallCaveRope)
            {
                var ropeSlot = FindNetworkInventorySlot(ItemIds.Rope);
                if (ropeSlot >= 0)
                    QueueGroundObjectDrop(new(
                        ropeSlot, ItemIds.Rope,
                        new(value.X, value.Y), true, value.Id));
            }
            else if (action == NetworkWorldActionKind.FillExcavation)
            {
                var fillSlot = FindNetworkCaveFillSlot(value);
                if (fillSlot >= 0 &&
                    _activePlayer?.Inventory?.ElementAtOrDefault(fillSlot)
                        is { } fillItem)
                    QueueGroundObjectDrop(new(
                        fillSlot, fillItem,
                        new(value.X, value.Y), true, value.Id));
            }
            else if (action == NetworkWorldActionKind.CutPlantedTree)
                QueuePlantedTreeChop(value);
            else if (action == NetworkWorldActionKind.PickUp ||
                     action == NetworkWorldActionKind.HarvestCrop)
                QueueGroundObjectPickup(value);
            else if (action == NetworkWorldActionKind.TakeCampfireFuel)
                QueueCampfireFuelPickup(value);
            else if (action == NetworkWorldActionKind.CookOnCampfire &&
                     TrySelectedRawCookingItem(out var cookSlot, out var cookItem))
                QueueCampfireCooking(value, cookSlot, cookItem);
            else if (action == NetworkWorldActionKind.AddCampfireFuel &&
                     _activeInventorySlot >= 0 &&
                     _activePlayer?.Inventory?.ElementAtOrDefault(
                         _activeInventorySlot) is { } fuelItemId)
            {
                _worldActions.QueuePath(
                    new Vector2(value.X, value.Y),
                    WorldActionReach.Campfire,
                    WorldActionType.DropGroundObject,
                    inventorySlot: _activeInventorySlot,
                    itemId: fuelItemId,
                    groundObjectId: value.Id,
                    clearTreeActions: true);
                SendNetworkWalkCommand(
                    WorldActionReach.StandOff(
                        NetworkActionPosition,
                        new Vector2(value.X, value.Y),
                        WorldActionReach.Campfire));
            }
            else
                QueueNetworkObjectAction(
                    action, value,
                    action is NetworkWorldActionKind.AddCampfireFuel or
                        NetworkWorldActionKind.CookOnCampfire
                        ? _activeInventorySlot
                        : -1);
            return true;
        }

        var trailing = option - _networkGroundObjectContextActions.Count;
        if (trailing == 0)
        {
            _pendingNetworkWorldAction = null;
            SendNetworkWalk(_groundObjectContextWalkTarget);
        }
        else if (trailing == 1)
            _chatUi.AddMessage(
                PlantedTreeService.IsPlantedTree(value)
                    ? PlantedTreeService.Examine(value)
                    : ItemCatalog.Get(value.ItemId).Examine,
                ChatMessageStyle.Normal);
        return true;
    }

    private void QueueNetworkOpenContainer(WorldGroundObject value)
    {
        _networkRequestedContainerId = value.Id;
        QueueNetworkObjectAction(
            NetworkWorldActionKind.OpenContainer, value);
    }

    private void QueueNetworkCraftingStation(WorldGroundObject value)
    {
        if (!CraftingStationService.IsStation(value.ItemId)) return;
        QueueNetworkObjectAction(
            NetworkWorldActionKind.UseCraftingStation, value);
    }

    private void QueueNetworkObjectAction(
        NetworkWorldActionKind kind,
        WorldGroundObject value,
        int inventorySlot = -1)
    {
        if (kind == NetworkWorldActionKind.BuildConstruction)
            _networkRepeatedConstructionId = value.Id;
        var target = new Vector2(value.X, value.Y);
        if (kind == NetworkWorldActionKind.BuildConstruction &&
            _player is not null)
            target = PlaceableObjectCatalog.ClosestInteractionPoint(
                value.ItemId, target, _player.Position,
                rotation: value.VisualFrame);
        QueueNetworkWorldAction(new(
            kind,
            target,
            value.Id,
            inventorySlot,
            DefinitionId: value.ItemId));
    }

    private void QueueNetworkPointAction(
        NetworkWorldActionKind kind,
        Vector2 target,
        int inventorySlot = -1,
        int quantity = 1,
        string? definitionId = null,
        int rotation = 0) =>
        QueueNetworkWorldAction(new(
            kind, target, InventorySlot: inventorySlot,
            Quantity: quantity, DefinitionId: definitionId,
            Rotation: rotation));

    private void QueueNetworkWorldAction(PendingNetworkWorldAction action)
    {
        if (_player is null) return;
        ReleaseNetworkCookingPresentation();
        _pendingNetworkWorldAction = action;
        if (action.Kind == NetworkWorldActionKind.Demolish)
        {
            DispatchPendingNetworkWorldAction();
            return;
        }
        if (NetworkWorldActionReadyToCommit(action))
        {
            DispatchPendingNetworkWorldAction();
            return;
        }
        var range = NetworkApproachRange(action);
        var type = action.Kind == NetworkWorldActionKind.BuildConstruction &&
                   action.ObjectId != Guid.Empty
            ? WorldActionType.BuildConstruction
            : WorldActionType.NetworkWorldAction;
        QueueNetworkWalkToAct(
            action.Target,
            range,
            type,
            groundObjectId: action.ObjectId == Guid.Empty
                ? null
                : action.ObjectId,
            itemId: action.DefinitionId);
    }

    internal void TryDispatchPendingNetworkWorldAction()
    {
        if (_pendingNetworkWorldAction is { } pending &&
            NetworkWorldActionReadyToCommit(pending))
            DispatchPendingNetworkWorldAction();
    }

    internal static bool NetworkWorldActionReadyToCommit(
        Vector2 client,
        Vector2 authority,
        Vector2 target,
        float clientStandOff,
        float serverRange = AuthoritativeWorldTransactions.InteractionRange)
    {
        if (!WorldActionReach.InRange(client, target, clientStandOff))
            return false;
        var delta = authority - target;
        return delta.LengthSquared <= serverRange * serverRange;
    }

    private bool NetworkWorldActionReadyToCommit(
        PendingNetworkWorldAction action) =>
        NetworkWorldActionReadyToCommit(
            NetworkActionPosition,
            _networkAuthoritativePosition,
            action.Target,
            NetworkApproachRange(action));

    private void UpdatePendingNetworkWorldAction()
    {
        if (_player is null || _pendingNetworkWorldAction is not { } pending)
            return;
        if (pending.ObjectId != Guid.Empty &&
            NetworkWorldActionTargetGone(pending))
        {
            _pendingNetworkWorldAction = null;
            _chatUi.AddMessage(
                "That object is no longer there.",
                ChatMessageStyle.Warning);
            return;
        }
        if (NetworkWorldActionReadyToCommit(pending))
            DispatchPendingNetworkWorldAction();
    }

    private bool NetworkWorldActionTargetGone(PendingNetworkWorldAction pending)
    {
        if (pending.Kind == NetworkWorldActionKind.PickUp)
            return FindGroundObject(pending.ObjectId) is null &&
                   !_networkClient!.State.WorldObjects.ContainsKey(
                       pending.ObjectId);
        return !_networkClient!.State.WorldObjects.ContainsKey(pending.ObjectId);
    }

    private float NetworkApproachRange(PendingNetworkWorldAction action) =>
        action.Kind switch
        {
            NetworkWorldActionKind.PickUp or
                NetworkWorldActionKind.Drop or
                NetworkWorldActionKind.HarvestCrop =>
                WorldActionReach.GroundPickup,
            NetworkWorldActionKind.AddCampfireFuel or
                NetworkWorldActionKind.TakeCampfireFuel or
                NetworkWorldActionKind.LightCampfire or
                NetworkWorldActionKind.CookOnCampfire =>
                WorldActionReach.Campfire,
            NetworkWorldActionKind.CookStew =>
                WorldActionReach.CookStew,
            NetworkWorldActionKind.OpenContainer =>
                WorldActionReach.Container,
            NetworkWorldActionKind.UseCraftingStation =>
                WorldActionReach.CraftingStation,
            NetworkWorldActionKind.BuildConstruction =>
                WorldActionReach.Construction,
            NetworkWorldActionKind.PlaceInventoryWorldObject or
                NetworkWorldActionKind.PlaceConstruction =>
                WorldActionReach.Placeable(action.DefinitionId),
            NetworkWorldActionKind.InstallCaveRope or
                NetworkWorldActionKind.FillExcavation =>
                WorldActionReach.GroundPickup,
            NetworkWorldActionKind.StartExcavation or
                NetworkWorldActionKind.WorkExcavation or
                NetworkWorldActionKind.RestoreExcavation or
                NetworkWorldActionKind.TakeCaveRope =>
                WorldActionReach.CaveDig,
            NetworkWorldActionKind.TraverseCave =>
                WorldActionReach.CaveEnter,
            _ => NetworkInteractionDispatchRange
        };

    private static Vector2 NetworkStandOff(
        Vector2 from, Vector2 to, float range) =>
        WorldActionReach.StandOff(from, to, range);

    private void DispatchPendingNetworkWorldAction()
    {
        if (_pendingNetworkWorldAction is not { } pending) return;
        if (pending.Kind == NetworkWorldActionKind.WorkExcavation)
        {
            _pendingNetworkWorldAction = null;
            if (_networkWorldObjects.TryGetValue(
                    pending.ObjectId, out var excavation))
                QueueNetworkCaveWork(excavation, pending.InventorySlot);
            return;
        }
        if (pending.Kind == NetworkWorldActionKind.UseCraftingStation)
        {
            _pendingNetworkWorldAction = null;
            if (!_networkWorldObjects.TryGetValue(
                    pending.ObjectId, out var station) ||
                !CraftingStationService.IsStation(station.ItemId) ||
                !_networkWorldObjectChunks.TryGetValue(
                    pending.ObjectId, out var stationChunk) ||
                stationChunk.Level != _activeWorldLevel)
            {
                _chatUi.AddMessage(
                    "That crafting station is no longer available.",
                    ChatMessageStyle.Warning);
                return;
            }
            SendNetworkStop();
            OpenCraftingWindow(station.ItemId);
            return;
        }
        var payload = CreateNetworkWorldActionPayload(pending);
        if (payload is null)
        {
            _pendingNetworkWorldAction = null;
            _chatUi.AddMessage(
                "The authoritative state changed before that action could begin.",
                ChatMessageStyle.Warning);
            return;
        }
        _pendingNetworkWorldAction = null;
        _dispatchedNetworkWorldAction = pending;
        if (pending.Kind == NetworkWorldActionKind.StartExcavation)
        {
            SendNetworkPresentSkill(EntityAction.Dig);
            _player?.DigAt(pending.Target);
        }
        else if (pending.Kind == NetworkWorldActionKind.BuildConstruction)
        {
            SendNetworkPresentSkill(EntityAction.Build);
            _player?.BuildAt(pending.Target);
        }
        var commandId = Guid.NewGuid();
        if (pending.Kind == NetworkWorldActionKind.CookOnCampfire)
        {
            _networkCookingCommandId = commandId;
            var inventory = _activePlayer?.Inventory ?? [];
            _networkCookingRawItemId = inventory.ElementAtOrDefault(
                pending.InventorySlot);
            _pendingNetworkCookingTarget = pending.Target;
        }
        PrepareNetworkCaveCommand(commandId, pending, payload);
        if (pending.Kind == NetworkWorldActionKind.BuildConstruction)
        {
            _networkBuildCommandId = commandId;
            _networkExpectedBuildMutation =
                ExpectedConstructionMutation(commandId, payload, pending);
            _networkBuildAwaitingDelta = false;
        }
        else if (pending.Kind is
                 NetworkWorldActionKind.PlaceConstruction or
                 NetworkWorldActionKind.PlaceInventoryWorldObject)
        {
            _networkPlacementCommandId = commandId;
            _networkExpectedPlacementMutation =
                ExpectedConstructionMutation(commandId, payload, pending);
            _networkPlacementAwaitingDelta = false;
        }
        SendNetworkAction(payload, commandId);
    }

    private ExpectedNetworkConstructionMutation?
        ExpectedConstructionMutation(
            Guid commandId,
            IActionCommandPayload payload,
            PendingNetworkWorldAction pending)
    {
        if (payload is PlaceConstructionAction place)
        {
            var chunkX = FloorDiv(
                (int)MathF.Floor(place.X), WorldChunk.Size);
            var chunkY = FloorDiv(
                (int)MathF.Floor(place.Y), WorldChunk.Size);
            return new(
                commandId,
                Guid.Empty,
                place.DefinitionId,
                new(place.X, place.Y),
                place.Rotation,
                chunkX,
                chunkY,
                place.WorldLevel,
                0,
                1,
                place.ExpectedChunkRevision,
                NextNetworkRevision(place.ExpectedChunkRevision));
        }

        if (payload is PlaceInventoryWorldObjectAction furniture)
        {
            var chunkX = FloorDiv(
                (int)MathF.Floor(furniture.X), WorldChunk.Size);
            var chunkY = FloorDiv(
                (int)MathF.Floor(furniture.Y), WorldChunk.Size);
            return new(
                commandId,
                Guid.Empty,
                furniture.DefinitionId,
                new(furniture.X, furniture.Y),
                furniture.Rotation,
                chunkX,
                chunkY,
                furniture.WorldLevel,
                0,
                1,
                furniture.ExpectedChunkRevision,
                NextNetworkRevision(
                    furniture.ExpectedChunkRevision));
        }

        if (payload is not BuildConstructionAction build)
            return null;
        var client = _networkClient;
        if (client is null || !client.State.WorldObjects.TryGetValue(
                build.Construction.ObjectId, out var state))
            return null;
        return new(
            commandId,
            state.ObjectId,
            state.DefinitionId,
            new(state.X, state.Y),
            state.Rotation,
            state.ChunkX,
            state.ChunkY,
            state.WorldLevel,
            build.Construction.ExpectedObjectRevision,
            NextNetworkRevision(
                build.Construction.ExpectedObjectRevision),
            build.Construction.ExpectedChunkRevision,
            NextNetworkRevision(
                build.Construction.ExpectedChunkRevision));
    }

    private static uint NextNetworkRevision(uint revision) =>
        revision == uint.MaxValue ? uint.MaxValue : revision + 1;

    private IActionCommandPayload? CreateNetworkWorldActionPayload(
        PendingNetworkWorldAction action)
    {
        if (action.Kind is NetworkWorldActionKind.Drop or
            NetworkWorldActionKind.PlaceInventoryWorldObject or
            NetworkWorldActionKind.PlaceConstruction or
            NetworkWorldActionKind.StartExcavation)
        {
            var chunkRevision = NetworkChunkRevision(
                action.Target, _activeWorldLevel);
            if (action.Kind == NetworkWorldActionKind.StartExcavation)
                return new StartExcavationAction(
                    action.Target.X,
                    action.Target.Y,
                    checked((short)_activeWorldLevel),
                    action.InventorySlot,
                    chunkRevision);
            if (action.Kind ==
                NetworkWorldActionKind.PlaceInventoryWorldObject)
            {
                return new PlaceInventoryWorldObjectAction(
                    action.DefinitionId!,
                    action.InventorySlot,
                    action.Target.X,
                    action.Target.Y,
                    checked((short)_activeWorldLevel),
                    action.Rotation,
                    chunkRevision);
            }
            return action.Kind == NetworkWorldActionKind.Drop
                ? new DropInventoryItemAction(
                    action.InventorySlot,
                    action.Quantity,
                    action.Target.X,
                    action.Target.Y,
                    checked((short)_activeWorldLevel),
                    chunkRevision)
                : new PlaceConstructionAction(
                    action.DefinitionId!,
                    action.InventorySlot >= 0
                        ? action.InventorySlot
                        : 0,
                    action.Target.X,
                    action.Target.Y,
                    checked((short)_activeWorldLevel),
                    action.Rotation,
                    chunkRevision);
        }

        if (!TryNetworkWorldObjectReference(
                action.ObjectId, out var reference))
            return null;
        return action.Kind switch
        {
            NetworkWorldActionKind.PickUp =>
                new PickUpWorldObjectAction(reference),
            NetworkWorldActionKind.OpenContainer =>
                new OpenContainerAction(reference),
            NetworkWorldActionKind.AddCampfireFuel =>
                new AddCampfireFuelAction(reference, action.InventorySlot),
            NetworkWorldActionKind.TakeCampfireFuel =>
                new TakeCampfireFuelAction(reference),
            NetworkWorldActionKind.LightCampfire =>
                new LightCampfireAction(reference),
            NetworkWorldActionKind.CookOnCampfire =>
                new CookOnCampfireAction(reference, action.InventorySlot),
            NetworkWorldActionKind.CookStew =>
                new CookStewAction(reference),
            NetworkWorldActionKind.BuildConstruction =>
                new BuildConstructionAction(reference),
            NetworkWorldActionKind.Demolish =>
                new DemolishWorldObjectAction(reference),
            NetworkWorldActionKind.WorkExcavation =>
                new WorkExcavationAction(reference, action.InventorySlot),
            NetworkWorldActionKind.RestoreExcavation =>
                new RestoreExcavationAction(reference),
            NetworkWorldActionKind.InstallCaveRope =>
                new InstallCaveRopeAction(reference, action.InventorySlot),
            NetworkWorldActionKind.TakeCaveRope =>
                new TakeCaveRopeAction(reference),
            NetworkWorldActionKind.FillExcavation =>
                new FillExcavationAction(reference, action.InventorySlot),
            NetworkWorldActionKind.TraverseCave =>
                new TraverseCaveAction(reference),
            NetworkWorldActionKind.HarvestCrop =>
                new HarvestCropAction(reference),
            _ => null
        };
    }

    private void SendNetworkGroundPickup(WorldGroundObject value)
    {
        if (!TryNetworkWorldObjectReference(value.Id, out var reference) &&
            !TryProceduralGroundLootReference(value, out reference))
        {
            _chatUi.AddMessage(
                "The authoritative state changed before that action could begin.",
                ChatMessageStyle.Warning);
            return;
        }
        SendNetworkAction(new PickUpWorldObjectAction(reference), Guid.NewGuid());
    }

    private void SendNetworkHarvest(WorldGroundObject value)
    {
        if (!TryNetworkWorldObjectReference(value.Id, out var reference))
        {
            _chatUi.AddMessage(
                "The authoritative state changed before that action could begin.",
                ChatMessageStyle.Warning);
            return;
        }
        SendNetworkAction(new HarvestCropAction(reference), Guid.NewGuid());
    }

    private void SendNetworkGroundDrop(int inventorySlot, Vector2 target)
    {
        SendNetworkAction(
            new DropInventoryItemAction(
                inventorySlot,
                1,
                target.X,
                target.Y,
                checked((short)_activeWorldLevel),
                NetworkChunkRevision(target, _activeWorldLevel)),
            Guid.NewGuid());
    }

    private bool TryProceduralGroundLootReference(
        WorldGroundObject value,
        out WorldObjectReference reference)
    {
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(value.X, value.Y),
            _activeWorldLevel);
        if (!GeneratedPortableGroundLoot.TryResolve(
                _worldSeed, chunk, value.Id, out _))
        {
            reference = default;
            return false;
        }
        reference = new(
            value.Id,
            chunk.X,
            chunk.Y,
            checked((short)chunk.WorldLevel),
            GeneratedPortableGroundLoot.VirginCommandRevision,
            NetworkChunkRevision(new(value.X, value.Y), _activeWorldLevel));
        return true;
    }

    private bool TryNetworkWorldObjectReference(
        Guid objectId, out WorldObjectReference reference)
    {
        var client = _networkClient;
        if (client is null || !client.State.WorldObjects.TryGetValue(
                objectId, out var value))
        {
            reference = default;
            return false;
        }
        var chunk = new NetworkWorldChunk(
            value.ChunkX, value.ChunkY, value.WorldLevel);
        var chunkRevision = client.State.WorldChunkRevisions
            .TryGetValue(chunk, out var currentChunkRevision)
                ? currentChunkRevision
                : value.ChunkRevision;
        reference = new(
            value.ObjectId,
            value.ChunkX,
            value.ChunkY,
            value.WorldLevel,
            value.ObjectRevision,
            chunkRevision);
        return true;
    }

    private uint NetworkChunkRevision(Vector2 position, int worldLevel)
    {
        if (_networkClient is null) return 0;
        var chunk = new NetworkWorldChunk(
            FloorDiv((int)MathF.Floor(position.X), WorldChunk.Size),
            FloorDiv((int)MathF.Floor(position.Y), WorldChunk.Size),
            checked((short)worldLevel));
        return _networkClient.State.WorldChunkRevisions.TryGetValue(
            chunk, out var revision) ? revision : 0;
    }

    private bool IsNetworkContainer(Guid objectId) =>
        _networkClient?.State.WorldObjects.TryGetValue(
            objectId, out var value) == true && value.HasContainer;

    private bool TryGetNetworkGroundObjectUnderMouse(
        Vector2 mouse,
        out WorldGroundObject groundObject,
        out GpuWorldChunk chunk)
    {
        groundObject = null!;
        chunk = null!;
        var selectedDepth = float.NegativeInfinity;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsChunkVisible(gpu) ||
                !_networkWorldObjectIdsByChunk.TryGetValue(
                    gpu.Chunk.Coordinate, out var ids))
                continue;
            foreach (var objectId in ids)
            {
                if (!_networkWorldObjects.TryGetValue(
                        objectId, out var candidate) ||
                    !TryGroundObjectVisual(
                        candidate, out var frame, out _, out _, out _))
                    continue;
                var world = GroundObjectWorld(candidate);
                var bounds = SpriteBounds(frame, world);
                const float minimumHitSize = 24;
                var centerX = (bounds.Left + bounds.Right) * .5f;
                var centerY = (bounds.Top + bounds.Bottom) * .5f;
                var hit = (
                    Left: Math.Min(bounds.Left, centerX - minimumHitSize * .5f),
                    Top: Math.Min(bounds.Top, centerY - minimumHitSize * .5f),
                    Right: Math.Max(
                        bounds.Right, centerX + minimumHitSize * .5f),
                    Bottom: Math.Max(
                        bounds.Bottom, centerY + minimumHitSize * .5f));
                if (mouse.X < hit.Left || mouse.X >= hit.Right ||
                    mouse.Y < hit.Top || mouse.Y >= hit.Bottom ||
                    !WorldHoverSelection.Prefer(world.Y, ref selectedDepth))
                    continue;
                groundObject = candidate;
                chunk = gpu;
            }
        }
        return groundObject is not null;
    }

    private void ApplyNetworkContainerState(NetworkContainerState state)
    {
        if (!IsNetworkWorld ||
            (_networkRequestedContainerId != state.ObjectId &&
             _openWorldStorageId != state.ObjectId) ||
            !_networkWorldObjects.TryGetValue(
                state.ObjectId, out var worldObject) ||
            !WorldItemContainerService.IsContainer(worldObject.ItemId))
            return;

        var template = WorldItemContainerService.Open(worldObject);
        var definition = template.Definition with
        {
            Access = state.Access == ContainerAccessMode.WithdrawOnly
                ? ItemContainerAccess.WithdrawOnly
                : ItemContainerAccess.DepositAndWithdraw
        };
        var items = new string?[definition.Capacity];
        var quantities = new int[definition.Capacity];
        foreach (var slot in state.Slots)
        {
            if ((uint)slot.Slot >= (uint)items.Length || slot.IsEmpty)
                continue;
            items[slot.Slot] = slot.ItemId;
            quantities[slot.Slot] = slot.Quantity;
        }
        var projected = new ItemContainerState(
            definition,
            new ItemContainerSaveState(
                definition.Id, items, quantities));
        if (_openWorldStorageId == state.ObjectId &&
            _itemContainerWindow.Container is { } open &&
            open.Definition == projected.Definition)
            open.CopyFrom(projected);
        else
            OpenItemContainer(projected, state.ObjectId);
        _networkRequestedContainerId = state.ObjectId;

        if (_networkContainerTransferCommandId is not null)
        {
            if (!_networkContainerTransferAwaitingState) return;
            _networkContainerTransferCommandId = null;
            _networkContainerTransferAwaitingState = false;
            SendNextNetworkContainerTransfer();
        }
    }

    private void StartNetworkContainerTransfers(
        IEnumerable<PendingNetworkContainerTransfer> transfers)
    {
        _networkContainerTransfers.Clear();
        foreach (var transfer in transfers)
            if (transfer.Quantity > 0)
                _networkContainerTransfers.Enqueue(transfer);
        _networkContainerTransferCommandId = null;
        _networkContainerTransferAwaitingState = false;
        SendNextNetworkContainerTransfer();
    }

    private void SendNextNetworkContainerTransfer()
    {
        if (_networkContainerTransferCommandId is not null ||
            _openWorldStorageId is not { } containerId ||
            !_networkContainerTransfers.TryPeek(out var transfer) ||
            _networkClient?.State.Containers.TryGetValue(
                containerId, out var container) != true)
            return;
        _networkContainerTransfers.Dequeue();
        var commandId = Guid.NewGuid();
        _networkContainerTransferCommandId = commandId;
        _networkContainerTransferAwaitingState = false;
        SendNetworkAction(new ContainerTransferAction(
            container!.Reference,
            container!.ContainerRevision,
            transfer.Direction,
            Math.Max(0, transfer.InventorySlot),
            Math.Max(0, transfer.ContainerSlot),
            transfer.Quantity), commandId);
    }

    private PendingNetworkWorldAction? _dispatchedNetworkWorldAction;

    internal static string DescribeNetworkActionRejection(
        ActionResultMessage result)
    {
        if (!string.IsNullOrWhiteSpace(result.Detail) &&
            result.Detail.Contains(' '))
            return result.Detail;
        return result.Detail switch
        {
            "OutOfRange" =>
                "You need to stand closer before that will complete.",
            "MissingConstructionResources" =>
                "You need the materials and a hammer to build that.",
            "ConstructionLocked" =>
                "Your Crafting level is too low to build that.",
            "InvalidPlacement" =>
                "That foundation cannot be placed there.",
            "InvalidConstruction" =>
                "That is not a valid construction.",
            "StaleChunkRevision" or "StaleObjectRevision" or
                "StaleActorRevision" or "StaleInventoryRevision" =>
                "The world changed before that action completed.",
            "NotConstructionSite" =>
                "That is no longer a construction site.",
            "AccessDenied" =>
                "You cannot change someone else's construction.",
            "InventoryFull" =>
                "Your inventory is too full.",
            _ => string.IsNullOrWhiteSpace(result.Detail)
                ? $"Server rejected the action ({result.RejectionCode})."
                : result.Detail
        };
    }

    internal static bool ShouldRetryNetworkWorldActionReject(
        bool accepted, string? detail) =>
        !accepted && detail is "OutOfRange" or "StaleChunkRevision" or
            "StaleObjectRevision" or "StaleActorRevision" or
            "StaleInventoryRevision";

    private bool HandleNetworkWorldActionResult(ActionResultMessage result)
    {
        if (result.Accepted &&
            result.Detail.StartsWith("dummy_", StringComparison.Ordinal))
        {
            PresentNetworkDummyStrike(result.Detail);
            return false;
        }
        if (_networkCookingCommandId == result.CommandId)
        {
            if (result.Accepted &&
                _networkCookingRawItemId is { } rawItemId)
            {
                _chatUi.AddMessage(
                    $"You place the {ItemCatalog.Get(rawItemId).Name} " +
                    "over the fire.",
                    ChatMessageStyle.Action);
            }
            else
                ClearNetworkCookingPresentation();
            _networkCookingCommandId = null;
        }
        var retry = ShouldRetryNetworkWorldActionReject(
            result.Accepted, result.Detail);
        if (_networkBuildCommandId == result.CommandId)
        {
            if (retry && _dispatchedNetworkWorldAction is { } build)
            {
                _networkBuildCommandId = null;
                _networkExpectedBuildMutation = null;
                _networkBuildAwaitingDelta = false;
                _pendingNetworkWorldAction = build;
                return true;
            }
            if (!result.Accepted ||
                _networkExpectedBuildMutation is not { } expected ||
                expected.CommandId != result.CommandId)
            {
                StopNetworkRepeatedConstruction();
            }
            else
                _networkBuildAwaitingDelta = true;
        }
        if (_networkPlacementCommandId == result.CommandId)
        {
            if (retry && _dispatchedNetworkWorldAction is { } placed)
            {
                ClearNetworkPlacementExpectation();
                _pendingNetworkWorldAction = placed;
                return true;
            }
            if (!result.Accepted ||
                _networkExpectedPlacementMutation is not { } expected ||
                expected.CommandId != result.CommandId)
            {
                _networkConstructionPlacements.Clear();
                ClearNetworkPlacementExpectation();
            }
            else
                _networkPlacementAwaitingDelta = true;
        }
        if (_networkContainerTransferCommandId != result.CommandId)
            return false;
        if (result.Accepted)
        {
            _networkContainerTransferAwaitingState = true;
            return false;
        }
        _networkContainerTransferCommandId = null;
        _networkContainerTransferAwaitingState = false;
        _networkContainerTransfers.Clear();
        return false;
    }

    private Vector2? _pendingNetworkCookingTarget;

    private void ClearNetworkCookingPresentation()
    {
        _networkCookingCommandId = null;
        _networkCookingRawItemId = null;
        _pendingNetworkCookingTarget = null;
        ReleaseNetworkCookingPresentation();
    }

    private void ReleaseNetworkCookingPresentation()
    {
        if (_networkCookingPresentationOwned &&
            _player?.Action == EntityAction.Gather)
            _player.Stop();
        _networkCookingPresentationOwned = false;
    }

    private void PresentNetworkDummyStrike(string detail)
    {
        var targetId = _combatTargetId;
        if (detail == "dummy_miss")
        {
            if (targetId is { } missId)
                ShowEntityImpact(GroundFeedbackKey(missId), 0, false);
            _chatUi.AddMessage("You miss.", ChatMessageStyle.Action);
            return;
        }
        var separator = detail.LastIndexOf(':');
        var damage = 0;
        if (separator > 0)
            int.TryParse(detail[(separator + 1)..], out damage);
        if (targetId is { } hitId)
            ShowEntityImpact(GroundFeedbackKey(hitId), damage, true);
        _chatUi.AddMessage(
            $"You hit for {damage}.", ChatMessageStyle.Action);
        if (detail.StartsWith("dummy_reset:", StringComparison.Ordinal))
            _chatUi.AddMessage(
                "The training dummy is knocked down and reset.",
                ChatMessageStyle.Action);
    }

    private void ResetNetworkContainerInteraction()
    {
        ClearNetworkCookingPresentation();
        _networkRequestedContainerId = null;
        _networkContainerTransfers.Clear();
        _networkContainerTransferCommandId = null;
        _networkContainerTransferAwaitingState = false;
        StopNetworkRepeatedConstruction();
        _networkConstructionPlacements.Clear();
        ClearNetworkPlacementExpectation();
    }

    private void ContinueNetworkConstruction(
        NetworkWorldObjectChange change)
    {
        ContinueNetworkConstructionPlacement(change);
        ContinueNetworkRepeatedConstruction(change);
    }

    private void ContinueNetworkRepeatedConstruction(
        NetworkWorldObjectChange change)
    {
        if (!_networkBuildAwaitingDelta ||
            _networkExpectedBuildMutation is not { } expected ||
            !expected.Matches(change) ||
            _networkRepeatedConstructionId != change.ObjectId ||
            !_networkWorldObjects.TryGetValue(
                change.ObjectId, out var value))
            return;
        _networkBuildCommandId = null;
        _networkExpectedBuildMutation = null;
        _networkBuildAwaitingDelta = false;
        if (!ConstructionService.IsConstructionSite(value))
        {
            StopNetworkRepeatedConstruction();
            return;
        }
        QueueNetworkObjectAction(
            NetworkWorldActionKind.BuildConstruction, value);
    }

    private void StartNetworkConstructionPlacements(
        IEnumerable<PendingNetworkWorldAction> placements)
    {
        _networkConstructionPlacements.Clear();
        foreach (var placement in placements)
            _networkConstructionPlacements.Enqueue(placement);
        ClearNetworkPlacementExpectation();
        SendNextNetworkConstructionPlacement();
    }

    private void ContinueNetworkConstructionPlacement(
        NetworkWorldObjectChange change)
    {
        if (!_networkPlacementAwaitingDelta ||
            _networkExpectedPlacementMutation is not { } expected ||
            !expected.Matches(change))
            return;
        ClearNetworkPlacementExpectation();
        SendNextNetworkConstructionPlacement();
    }

    private void SendNextNetworkConstructionPlacement()
    {
        if (_networkPlacementCommandId is not null ||
            !_networkConstructionPlacements.TryDequeue(out var placement))
            return;
        QueueNetworkWorldAction(placement);
    }

    private void StopNetworkRepeatedConstruction()
    {
        if (_networkRepeatedConstructionId is not null &&
            _player?.Action == EntityAction.Build)
        {
            _player.Stop();
            SendNetworkPresentSkill(EntityAction.Idle);
        }
        _networkRepeatedConstructionId = null;
        _networkBuildCommandId = null;
        _networkExpectedBuildMutation = null;
        _networkBuildAwaitingDelta = false;
    }

    private void ClearNetworkPlacementExpectation()
    {
        _networkPlacementCommandId = null;
        _networkExpectedPlacementMutation = null;
        _networkPlacementAwaitingDelta = false;
    }
}
