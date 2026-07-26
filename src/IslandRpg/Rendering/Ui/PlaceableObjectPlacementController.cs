using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class PlaceableObjectPlacementController
{
    public bool Active => InventorySlot >= 0 && ItemId is not null;
    public int InventorySlot { get; private set; } = -1;
    public string? ItemId { get; private set; }

    public void Begin(int inventorySlot, string itemId)
    {
        InventorySlot = inventorySlot;
        ItemId = itemId;
    }

    public void Cancel()
    {
        InventorySlot = -1;
        ItemId = null;
    }

    public bool Matches(int inventorySlot, string itemId) =>
        Active &&
        InventorySlot == inventorySlot &&
        string.Equals(
            ItemId, itemId, StringComparison.OrdinalIgnoreCase);
}
