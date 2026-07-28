using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class InventoryPanelState(
    Vector4 bounds,
    string?[] inventory,
    int activeSlot = -1,
    int draggingSlot = -1,
    bool allowDragOutsideToGame = false,
    string title = "Bag",
    int columns = GameUiControlState.InventoryColumns,
    int? rows = null,
    IReadOnlyList<int>? quantities = null,
    bool showCount = true,
    float gridTop = GameUiControlState.InventoryGridTop,
    int firstVisibleRow = 0,
    int? visibleRows = null)
{
    public Vector4 Bounds { get; } = bounds;
    public string?[] Inventory { get; } = inventory;
    public int ActiveSlot { get; } = activeSlot;
    public int DraggingSlot { get; } = draggingSlot;
    public bool AllowDragOutsideToGame { get; } =
        allowDragOutsideToGame;
    public string Title { get; } = title;
    public int Columns { get; } = Math.Max(1, columns);
    public int Rows { get; } = Math.Max(
        1, rows ?? (int)Math.Ceiling(
            inventory.Length / (double)Math.Max(1, columns)));
    public int Capacity => Columns * Rows;
    public IReadOnlyList<int>? Quantities { get; } = quantities;
    public bool ShowCount { get; } = showCount;
    public float GridTop { get; } = gridTop;
    public int FirstVisibleRow => Math.Clamp(
        firstVisibleRow, 0, Math.Max(0, Rows - 1));
    public int VisibleRows => Math.Clamp(
        visibleRows ?? Rows, 1, Rows);
    public int FirstVisibleSlot => FirstVisibleRow * Columns;
    public int VisibleSlotCount => Math.Min(
        Capacity - FirstVisibleSlot,
        VisibleRows * Columns);
    public IEnumerable<int> VisibleSlots =>
        Enumerable.Range(FirstVisibleSlot, VisibleSlotCount);

    public int QuantityAt(int slot) =>
        Quantities is not null && (uint)slot < (uint)Quantities.Count
            ? Math.Max(0, Quantities[slot])
            : Inventory.ElementAtOrDefault(slot) is null ? 0 : 1;

    public Vector4 SlotBounds(int slot) =>
        new(
            Bounds.X + MathF.Round(
                (Bounds.Z -
                 (GameUiControlState.InventorySlotSize *
                  Columns +
                  GameUiControlState.InventoryColumnGap *
                  (Columns - 1))) / 2) +
            slot % Columns *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryColumnGap),
            Bounds.Y + GridTop +
            (slot / Columns - FirstVisibleRow) *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryRowGap),
            GameUiControlState.InventorySlotSize,
            GameUiControlState.InventorySlotSize);
}
