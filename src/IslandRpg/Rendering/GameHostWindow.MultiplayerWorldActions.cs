using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const float NetworkInteractionDispatchRange = 2.6f;

    private enum NetworkWorldActionKind
    {
        PickUp,
        Drop,
        OpenContainer,
        AddCampfireFuel,
        TakeCampfireFuel,
        LightCampfire,
        PlaceConstruction,
        BuildConstruction,
        Demolish
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
            if (TryGetNetworkGroundObjectUnderMouse(
                    SceneMousePosition(), out var contextObject, out _))
                OpenNetworkGroundObjectContext(contextObject, target);
            else
            {
                _pendingNetworkWorldAction = null;
                StopNetworkRepeatedConstruction();
                SendNetworkWalk(target);
            }
        }

        if (!placingObject && leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position) &&
            TryGetNetworkGroundObjectUnderMouse(
                SceneMousePosition(), out var groundObject, out _))
        {
            if (ConstructionService.IsConstructionSite(groundObject))
                QueueNetworkObjectAction(
                    NetworkWorldActionKind.BuildConstruction, groundObject);
            else if (IsNetworkContainer(groundObject.Id))
                QueueNetworkOpenContainer(groundObject);
            else if (!PlaceableObjectCatalog.IsPlaceable(
                         groundObject.ItemId))
                QueueNetworkObjectAction(
                    NetworkWorldActionKind.PickUp, groundObject);
        }

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
        }
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
            else
                QueueNetworkObjectAction(
                    action, value,
                    action == NetworkWorldActionKind.AddCampfireFuel
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
                ItemCatalog.Get(value.ItemId).Examine,
                ChatMessageStyle.Normal);
        return true;
    }

    private void QueueNetworkOpenContainer(WorldGroundObject value)
    {
        _networkRequestedContainerId = value.Id;
        QueueNetworkObjectAction(
            NetworkWorldActionKind.OpenContainer, value);
    }

    private void QueueNetworkObjectAction(
        NetworkWorldActionKind kind,
        WorldGroundObject value,
        int inventorySlot = -1)
    {
        if (kind == NetworkWorldActionKind.BuildConstruction)
            _networkRepeatedConstructionId = value.Id;
        QueueNetworkWorldAction(new(
            kind,
            new Vector2(value.X, value.Y),
            value.Id,
            inventorySlot));
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
        _pendingNetworkWorldAction = action;
        if (Vector2.DistanceSquared(_player.Position, action.Target) <=
            NetworkInteractionDispatchRange *
            NetworkInteractionDispatchRange)
        {
            DispatchPendingNetworkWorldAction();
            return;
        }
        SendNetworkWalk(action.Target);
    }

    private void UpdatePendingNetworkWorldAction()
    {
        if (_player is null || _pendingNetworkWorldAction is not { } pending)
            return;
        if (pending.ObjectId != Guid.Empty &&
            !_networkClient!.State.WorldObjects.ContainsKey(pending.ObjectId))
        {
            _pendingNetworkWorldAction = null;
            _chatUi.AddMessage(
                "That object is no longer there.",
                ChatMessageStyle.Warning);
            return;
        }
        if (Vector2.DistanceSquared(_player.Position, pending.Target) <=
            NetworkInteractionDispatchRange *
            NetworkInteractionDispatchRange)
            DispatchPendingNetworkWorldAction();
    }

    private void DispatchPendingNetworkWorldAction()
    {
        if (_pendingNetworkWorldAction is not { } pending) return;
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
        var commandId = Guid.NewGuid();
        if (pending.Kind == NetworkWorldActionKind.BuildConstruction)
        {
            _networkBuildCommandId = commandId;
            _networkExpectedBuildMutation =
                ExpectedConstructionMutation(commandId, payload, pending);
            _networkBuildAwaitingDelta = false;
        }
        else if (pending.Kind == NetworkWorldActionKind.PlaceConstruction)
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
            NetworkWorldActionKind.PlaceConstruction)
        {
            var chunkRevision = NetworkChunkRevision(
                action.Target, _activeWorldLevel);
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
            NetworkWorldActionKind.BuildConstruction =>
                new BuildConstructionAction(reference),
            NetworkWorldActionKind.Demolish =>
                new DemolishWorldObjectAction(reference),
            _ => null
        };
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
        foreach (var candidate in _networkWorldObjects.Values)
        {
            if (!_networkWorldObjectChunks.TryGetValue(
                    candidate.Id, out var coordinate) ||
                coordinate.Level != _activeWorldLevel ||
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
                Right: Math.Max(bounds.Right, centerX + minimumHitSize * .5f),
                Bottom: Math.Max(bounds.Bottom, centerY + minimumHitSize * .5f));
            if (mouse.X < hit.Left || mouse.X >= hit.Right ||
                mouse.Y < hit.Top || mouse.Y >= hit.Bottom ||
                !WorldHoverSelection.Prefer(world.Y, ref selectedDepth))
                continue;
            groundObject = candidate;
            _worldChunks.TryGetValue(coordinate, out chunk!);
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

    private void HandleNetworkWorldActionResult(ActionResultMessage result)
    {
        if (_networkBuildCommandId == result.CommandId)
        {
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
        if (_networkContainerTransferCommandId != result.CommandId) return;
        if (result.Accepted)
        {
            _networkContainerTransferAwaitingState = true;
            return;
        }
        _networkContainerTransferCommandId = null;
        _networkContainerTransferAwaitingState = false;
        _networkContainerTransfers.Clear();
    }

    private void ResetNetworkContainerInteraction()
    {
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
