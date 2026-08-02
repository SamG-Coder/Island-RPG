using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal enum ItemContainerActionType
{
    None,
    Close,
    TransferAll,
    DepositOne,
    WithdrawOne,
    OpenDepositMenu,
    OpenWithdrawMenu
}

internal readonly record struct ItemContainerAction(
    ItemContainerActionType Type,
    int Slot = -1);

internal sealed class ItemContainerWindowState
{
    private bool _leftWasDown;
    private bool _rightWasDown;
    private string[] _rowIds = [];

    public bool Visible { get; private set; }
    public ItemContainerState? Container { get; private set; }
    public ListControlState Rows { get; } = new();

    public void Open(
        ItemContainerState container,
        bool leftDown = false,
        bool rightDown = false)
    {
        Container = container;
        _rowIds = Enumerable.Range(
                0, container.Definition.RowCount)
            .Select(index => index.ToString())
            .ToArray();
        Rows.ScrollToIndex(0);
        // Consume the press that opened the window. A container must never
        // treat its opener's click as a deposit or withdrawal.
        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
        Visible = true;
    }

    public void Close() => Visible = false;

    public ItemContainerAction UpdatePointer(
        Vector4 viewport, Vector2 pointer, bool leftDown, bool rightDown)
    {
        var clicked = leftDown && !_leftWasDown;
        var rightClicked = rightDown && !_rightWasDown;
        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
        if (!Visible || Container is null)
            return default;
        var window = WindowBounds(viewport, Container.Definition);
        LayoutRows(window);
        Rows.UpdatePointer(pointer, leftDown);
        if (!clicked && !rightClicked)
            return default;
        if (Rows.ScrollTrack.HitTest(pointer))
            return default;
        if (CloseBounds(window).Contains(pointer))
            return new(ItemContainerActionType.Close);
        if (Container.Definition.AllowsDeposit &&
            Container.Definition.ShowTransferAllButton &&
            TransferAllBounds(window).Contains(pointer))
            return new(ItemContainerActionType.TransferAll);

        var containerPanel = ContainerBounds(window, Container.Definition);
        var containerInventory = new InventoryPanelState(
            containerPanel,
            Container.Items,
            title: Container.Definition.Title,
            columns: Container.Definition.ColumnCount,
            rows: Container.Definition.RowCount,
            quantities: Container.Quantities,
            firstVisibleRow: Rows.FirstVisibleIndex,
            visibleRows: Rows.VisibleRows);
        if (containerPanel.Contains(pointer))
        foreach (var slot in containerInventory.VisibleSlots)
            if (containerInventory.SlotBounds(slot).Contains(pointer))
                return new(
                    rightClicked
                        ? ItemContainerActionType.OpenWithdrawMenu
                        : ItemContainerActionType.WithdrawOne,
                    slot);

        if (Container.Definition.ShowPlayerInventory &&
            Container.Definition.AllowsDeposit)
        {
            var playerPanel = PlayerInventoryBounds(window);
            var inventoryPanel = new InventoryPanelState(
                playerPanel, [],
                title: "Bag");
            if (!playerPanel.Contains(pointer))
                return default;
            for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
                if (inventoryPanel.SlotBounds(slot).Contains(pointer))
                    return new(
                        rightClicked
                            ? ItemContainerActionType.OpenDepositMenu
                            : ItemContainerActionType.DepositOne,
                        slot);
        }
        return default;
    }

    public void LayoutRows(Vector4 window)
    {
        if (Container is null) return;
        var panel = ContainerBounds(window, Container.Definition);
        Rows.Layout(
            new(
                panel.X + 8,
                panel.Y + GameUiControlState.InventoryGridTop,
                panel.Z - 16,
                panel.W - GameUiControlState.InventoryGridTop - 8),
            _rowIds,
            rowHeight: GameUiControlState.InventorySlotSize +
                       GameUiControlState.InventoryRowGap,
            rowGap: 0,
            deleteWidth: 0,
            actionGap: 0);
    }

    public bool Scroll(
        Vector4 viewport, Vector2 pointer, float wheelDelta)
    {
        if (!Visible || Container is null) return false;
        var window = WindowBounds(viewport, Container.Definition);
        LayoutRows(window);
        return Rows.Scroll(pointer, wheelDelta);
    }

    public static Vector4 WindowBounds(
        Vector4 viewport, ItemContainerDefinition definition)
    {
        var gridWidth =
            definition.ColumnCount * GameUiControlState.InventorySlotSize +
            (definition.ColumnCount - 1) *
            GameUiControlState.InventoryColumnGap;
        var gridHeight =
            definition.RowCount * GameUiControlState.InventorySlotSize +
            (definition.RowCount - 1) *
            GameUiControlState.InventoryRowGap;
        var containerWidth = Math.Max(240, gridWidth + 34);
        var inventoryWidth = definition.ShowPlayerInventory ? 190 : 0;
        var width = Math.Min(
            viewport.Z - 30,
            containerWidth + inventoryWidth + 42);
        var height = Math.Min(
            viewport.W - 30,
            Math.Max(380, Math.Min(560, gridHeight + 160)));
        return new(
            viewport.X + (viewport.Z - width) * .5f,
            viewport.Y + (viewport.W - height) * .5f,
            width,
            height);
    }

    public static Vector4 CloseBounds(Vector4 window) =>
        new(window.X + window.Z - 38, window.Y + 10, 26, 24);

    public static Vector4 TransferAllBounds(Vector4 window) =>
        new(window.X + 16, window.Y + window.W - 48, 166, 32);

    public static Vector4 ContainerBounds(
        Vector4 window, ItemContainerDefinition definition)
    {
        var inventoryWidth = definition.ShowPlayerInventory ? 190 : 0;
        return new(
            window.X + 14,
            window.Y + 42,
            window.Z - inventoryWidth - 28,
            window.W - 98);
    }

    public static Vector4 PlayerInventoryBounds(Vector4 window) =>
        new(
            window.X + window.Z - 186,
            window.Y + 42,
            172,
            GameUiControlState.PanelHeight);
}
