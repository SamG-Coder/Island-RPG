using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class InventoryPanelState(
    Vector4 bounds,
    string?[] inventory,
    int activeSlot = -1,
    int draggingSlot = -1,
    bool allowDragOutsideToGame = false)
{
    public Vector4 Bounds { get; } = bounds;
    public string?[] Inventory { get; } = inventory;
    public int ActiveSlot { get; } = activeSlot;
    public int DraggingSlot { get; } = draggingSlot;
    public bool AllowDragOutsideToGame { get; } =
        allowDragOutsideToGame;

    public Vector4 SlotBounds(int slot) =>
        new(
            Bounds.X + MathF.Round(
                (Bounds.Z -
                 (GameUiControlState.InventorySlotSize *
                  GameUiControlState.InventoryColumns +
                  GameUiControlState.InventoryColumnGap *
                  (GameUiControlState.InventoryColumns - 1))) / 2) +
            slot % GameUiControlState.InventoryColumns *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryColumnGap),
            Bounds.Y + GameUiControlState.InventoryGridTop +
            slot / GameUiControlState.InventoryColumns *
            (GameUiControlState.InventorySlotSize +
             GameUiControlState.InventoryRowGap),
            GameUiControlState.InventorySlotSize,
            GameUiControlState.InventorySlotSize);
}
