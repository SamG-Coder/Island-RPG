using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private enum ItemContainerContextSource
    {
        None,
        Container,
        Inventory
    }

    private static readonly string[] WithdrawContextItems =
    [
        "Withdraw 1",
        "Withdraw 5",
        "Withdraw 10",
        "Withdraw 25",
        "Withdraw 100",
        "Withdraw All",
        "Examine"
    ];
    private static readonly string[] DepositContextItems =
    [
        "Deposit 1",
        "Deposit 5",
        "Deposit 10",
        "Deposit 25",
        "Deposit 100",
        "Deposit All",
        "Examine"
    ];
    private static readonly int[] ContextQuantities =
        [1, 5, 10, 25, 100, int.MaxValue];
    private readonly ItemContainerWindowState _itemContainerWindow = new();
    private readonly ContextMenuControlState _itemContainerContext = new();
    private ItemContainerContextSource _itemContainerContextSource;
    private int _itemContainerContextSlot = -1;
    private Guid? _openWorldStorageId;

    private void OpenItemContainer(
        ItemContainerState container,
        Guid? worldStorageId = null)
    {
        CancelPlaceableObjectPlacement();
        _itemContainerWindow.Open(
            container,
            MouseState.IsButtonDown(
                OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left),
            MouseState.IsButtonDown(
                OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right));
        _openWorldStorageId = worldStorageId;
        _modalScreen.Open(ModalScreenKind.ItemContainer);
        _chatUi.BlurInput();
        _inventoryContext.Close();
        _itemContainerContext.Close();
        _gameUi.Close();
        UseDefaultGameCursor();
    }

    private void CloseItemContainer()
    {
        SaveOpenWorldStorage();
        _itemContainerWindow.Close();
        _openWorldStorageId = null;
        _itemContainerContext.Close();
        _itemContainerContextSource = ItemContainerContextSource.None;
        _itemContainerContextSlot = -1;
        if (_pauseMenu.IsPaused)
            _modalScreen.Open(ModalScreenKind.Pause);
        else
            _modalScreen.Close(ModalScreenKind.ItemContainer);
        ConsumeWorldPointerInput();
    }

    private void OpenDeveloperItemBank() =>
        OpenItemContainer(ItemContainerState.CreateAllItemsTest());

    private void UpdateItemContainerInput(
        Vector2 pointer, bool leftDown)
    {
        if (_itemContainerContext.Visible)
        {
            _itemContainerContext.UpdatePointer(pointer, leftDown);
            return;
        }
        var action = _itemContainerWindow.UpdatePointer(
            SceneClientBounds(), pointer, leftDown,
            MouseState.IsButtonDown(
                OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right));
        switch (action.Type)
        {
            case ItemContainerActionType.Close:
                CloseItemContainer();
                break;
            case ItemContainerActionType.TransferAll:
                TransferAllToOpenContainer();
                break;
            case ItemContainerActionType.DepositOne:
                DepositOneIntoOpenContainer(action.Slot);
                break;
            case ItemContainerActionType.WithdrawOne:
                WithdrawFromOpenContainer(action.Slot, 1);
                break;
            case ItemContainerActionType.OpenDepositMenu:
                OpenItemContainerContext(
                    ItemContainerContextSource.Inventory,
                    action.Slot,
                    pointer);
                break;
            case ItemContainerActionType.OpenWithdrawMenu:
                OpenItemContainerContext(
                    ItemContainerContextSource.Container,
                    action.Slot,
                    pointer);
                break;
        }
    }

    private void OpenItemContainerContext(
        ItemContainerContextSource source, int slot, Vector2 pointer)
    {
        var itemId = source switch
        {
            ItemContainerContextSource.Container =>
                _itemContainerWindow.Container?.Items
                    .ElementAtOrDefault(slot),
            ItemContainerContextSource.Inventory =>
                _activePlayer?.Inventory?.ElementAtOrDefault(slot),
            _ => null
        };
        if (itemId is null) return;
        _itemContainerContextSource = source;
        _itemContainerContextSlot = slot;
        _itemContainerContext.Open(
            pointer,
            source == ItemContainerContextSource.Container
                ? WithdrawContextItems
                : DepositContextItems,
            SceneClientBounds(),
            width: 148);
    }

    private void HandleItemContainerContextSelection(int option)
    {
        if ((uint)option >= (uint)WithdrawContextItems.Length)
            return;
        var source = _itemContainerContextSource;
        var slot = _itemContainerContextSlot;
        _itemContainerContextSource = ItemContainerContextSource.None;
        _itemContainerContextSlot = -1;
        var itemId = source switch
        {
            ItemContainerContextSource.Container =>
                _itemContainerWindow.Container?.Items
                    .ElementAtOrDefault(slot),
            ItemContainerContextSource.Inventory =>
                _activePlayer?.Inventory?.ElementAtOrDefault(slot),
            _ => null
        };
        if (itemId is null) return;
        if (option == WithdrawContextItems.Length - 1)
        {
            _chatUi.AddMessage(
                ItemCatalog.Get(itemId).Examine,
                ChatMessageStyle.Normal);
            return;
        }
        var quantity = ContextQuantities[option];
        if (source == ItemContainerContextSource.Container)
            WithdrawFromOpenContainer(slot, quantity);
        else if (source == ItemContainerContextSource.Inventory)
            DepositMatchingIntoOpenContainer(itemId, quantity);
    }

    private void TransferAllToOpenContainer()
    {
        if (_activePlayer is null ||
            _itemContainerWindow.Container is not { } container)
            return;
        var inventory = (string?[])(
            _activePlayer.Inventory ?? []).Clone();
        var before = PlayerInventory.Count(inventory);
        var moved = container.TransferAllFrom(inventory);
        if (moved == 0) return;
        SaveContainerInventory(inventory);
        _chatUi.AddMessage(
            moved == before
                ? $"Stored all {moved} bag items."
                : $"Stored {moved} items; the container is full.",
            moved == before
                ? ChatMessageStyle.Action
                : ChatMessageStyle.Warning);
    }

    private void DepositOneIntoOpenContainer(int slot)
    {
        if (_activePlayer is null ||
            _itemContainerWindow.Container is not { } container ||
            (uint)slot >= (uint)(_activePlayer.Inventory?.Length ?? 0) ||
            _activePlayer.Inventory![slot] is not { } itemId ||
            !container.TryAdd(itemId))
            return;
        var inventory = (string?[])(
            _activePlayer.Inventory ?? []).Clone();
        inventory[slot] = null;
        SaveContainerInventory(inventory);
    }

    private void DepositMatchingIntoOpenContainer(
        string itemId, int maximum)
    {
        if (_activePlayer is null ||
            _itemContainerWindow.Container is not { } container)
            return;
        var inventory = (string?[])(
            _activePlayer.Inventory ?? []).Clone();
        var moved = container.TransferMatchingFrom(
            inventory, itemId, maximum);
        if (moved > 0)
            SaveContainerInventory(inventory);
    }

    private void WithdrawFromOpenContainer(int slot, int maximum)
    {
        if (_activePlayer is null ||
            _itemContainerWindow.Container is not { } container)
            return;
        var inventory = (string?[])(
            _activePlayer.Inventory ?? []).Clone();
        var available = container.Quantities.ElementAtOrDefault(slot);
        var emptySlots = inventory.Count(item => item is null);
        var quantity = Math.Min(
            available,
            Math.Min(maximum, emptySlots));
        if (quantity <= 0 ||
            !container.TryTake(slot, quantity, out var itemId) ||
            itemId is null)
            return;
        for (var count = 0; count < quantity; count++)
        {
            var empty = Array.FindIndex(inventory, item => item is null);
            inventory[empty] = itemId;
        }
        SaveContainerInventory(inventory);
    }

    private void SaveContainerInventory(string?[] inventory)
    {
        _activeInventorySlot = -1;
        _activePlayer = _activePlayer! with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        SaveOpenWorldStorage();
    }

    private void SaveOpenWorldStorage()
    {
        if (_openWorldStorageId is not { } storageId ||
            _itemContainerWindow.Container is not { } container)
            return;
        var location = FindGroundObjectLocation(storageId);
        if (location is null ||
            !StorageContainerService.IsStorage(
                location.Value.Object.ItemId))
            return;
        location.Value.Chunk.GroundObjects[location.Value.Index] =
            StorageContainerService.Save(
                location.Value.Object, container);
        QueueChunkSave(location.Value.Chunk);
    }

    private void QueueWorldStorage(WorldGroundObject storage)
    {
        if (!StorageContainerService.IsStorage(storage.ItemId))
            return;
        _worldActions.QueuePath(
            new Vector2(storage.X, storage.Y),
            .9f,
            WorldActionType.OpenStorage,
            groundObjectId: storage.Id,
            clearTreeActions: true);
    }

    internal void OpenWorldStorage(Guid storageId)
    {
        var storage = FindGroundObject(storageId);
        if (storage is null ||
            !StorageContainerService.IsStorage(storage.ItemId))
            return;
        OpenItemContainer(
            StorageContainerService.Open(storage),
            storage.Id);
    }

    private void RenderItemContainerWindow()
    {
        if (_itemContainerWindow.Container is not { } container)
            return;
        var definition = container.Definition;
        var window = ItemContainerWindowState.WindowBounds(
            SceneClientBounds(), definition);
        DrawAoEPanelBorder(window);
        DrawCenteredUiText(
            definition.Title.ToUpperInvariant(),
            new(window.X, window.Y + 10, window.Z, 28),
            new(232, 217, 166, 255));
        DrawMenuButton(
            ItemContainerWindowState.CloseBounds(window), "X");

        var containerBounds = ItemContainerWindowState.ContainerBounds(
            window, definition);
        _itemContainerWindow.LayoutRows(window);
        DrawAoEPanelBorder(containerBounds);
        RenderInventoryPanel(
            new InventoryPanelState(
                containerBounds,
                container.Items,
                title: definition.Title,
                columns: definition.ColumnCount,
                rows: definition.RowCount,
                quantities: container.Quantities,
                firstVisibleRow:
                    _itemContainerWindow.Rows.FirstVisibleIndex,
                visibleRows:
                    _itemContainerWindow.Rows.VisibleRows),
            renderDragPreview: false);
        RenderListScrollbar(_itemContainerWindow.Rows);

        if (definition.ShowPlayerInventory)
        {
            var bag = ItemContainerWindowState.PlayerInventoryBounds(window);
            DrawAoEPanelBorder(bag);
            RenderInventoryPanel(
                new InventoryPanelState(
                    bag, _activePlayer?.Inventory ?? [],
                    title: "Bag"),
                renderDragPreview: false);
        }

        if (definition.ShowTransferAllButton)
            DrawMenuButton(
                ItemContainerWindowState.TransferAllBounds(window),
                "Deposit all");
        DrawUiText(
            definition.AllowStacking
                ? "Stacking enabled"
                : "Individual slots",
            new(window.X + 194, window.Y + window.W - 38),
            new(174, 164, 134, 255));
        RenderContextMenu(_itemContainerContext);
    }
}
