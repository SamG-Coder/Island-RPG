using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal enum InventoryInteractionType
{
    None,
    Activate,
    Swap,
    DropOutsideToGame,
    OpenContextMenu,
    ClearSelection
}

internal readonly record struct InventoryInteraction(
    InventoryInteractionType Type,
    int SourceSlot = -1,
    int TargetSlot = -1);

internal sealed class InventoryInteractionController
{
    private bool _leftWasDown;
    private bool _rightWasDown;
    private int _pressedSlot = -1;
    private Vector2 _pressPosition;
    private bool _allowDragOutsideToGame;

    public int DraggingSlot { get; private set; } = -1;
    public bool AllowsCurrentDragOutsideToGame =>
        DraggingSlot >= 0 && _allowDragOutsideToGame;

    public InventoryInteraction Update(
        InventoryPanelState panel,
        Vector2 pointer,
        bool leftDown,
        bool rightDown,
        bool interactionBlocked = false)
    {
        _allowDragOutsideToGame = panel.AllowDragOutsideToGame;
        if (interactionBlocked)
        {
            Cancel();
            _leftWasDown = leftDown;
            _rightWasDown = rightDown;
            return default;
        }

        if (rightDown && !_rightWasDown)
        {
            _rightWasDown = true;
            var contextSlot = SlotAt(panel, pointer, includeEmpty: false);
            if (contextSlot >= 0)
                return new(
                    InventoryInteractionType.OpenContextMenu,
                    contextSlot);
        }
        _rightWasDown = rightDown;

        if (leftDown && !_leftWasDown)
        {
            _pressedSlot = SlotAt(panel, pointer, includeEmpty: false);
            _pressPosition = pointer;
        }
        else if (leftDown &&
                 _pressedSlot >= 0 &&
                 DraggingSlot < 0 &&
                 (pointer - _pressPosition).LengthSquared >= 16)
            DraggingSlot = _pressedSlot;
        else if (!leftDown && _leftWasDown)
        {
            _leftWasDown = false;
            if (DraggingSlot >= 0)
            {
                var source = DraggingSlot;
                var target = SlotAt(panel, pointer, includeEmpty: true);
                Cancel();
                if (target >= 0)
                    return new(
                        InventoryInteractionType.Swap, source, target);
                if (panel.AllowDragOutsideToGame)
                    return new(
                        InventoryInteractionType.DropOutsideToGame,
                        source);
                return default;
            }

            var pressed = _pressedSlot;
            _pressedSlot = -1;
            if (pressed >= 0 &&
                SlotAt(panel, pointer, includeEmpty: false) == pressed)
                return new(
                    InventoryInteractionType.Activate, pressed);
            if (pressed < 0 && panel.Bounds.Contains(pointer))
                return new(InventoryInteractionType.ClearSelection);
        }

        _leftWasDown = leftDown;
        return default;
    }

    public void Cancel()
    {
        _pressedSlot = -1;
        DraggingSlot = -1;
        _allowDragOutsideToGame = false;
    }

    private static int SlotAt(
        InventoryPanelState panel,
        Vector2 pointer,
        bool includeEmpty)
    {
        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
        {
            if (!includeEmpty &&
                (slot >= panel.Inventory.Length ||
                 panel.Inventory[slot] is null))
                continue;
            if (panel.SlotBounds(slot).Contains(pointer))
                return slot;
        }
        return -1;
    }
}
