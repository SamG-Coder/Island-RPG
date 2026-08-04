using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class PlaceableObjectPlacementController
{
    public bool Active => ItemId is not null;
    public int InventorySlot { get; private set; } = -1;
    public string? ItemId { get; private set; }
    public bool ConsumesInventoryItem { get; private set; }

    public void Begin(int inventorySlot, string itemId)
    {
        InventorySlot = inventorySlot;
        ItemId = itemId;
        ConsumesInventoryItem = true;
    }

    public void BeginConstruction(string itemId)
    {
        InventorySlot = -1;
        ItemId = itemId;
        ConsumesInventoryItem = false;
    }

    public void Cancel()
    {
        InventorySlot = -1;
        ItemId = null;
        ConsumesInventoryItem = false;
    }

    public bool Matches(int inventorySlot, string itemId) =>
        Active &&
        InventorySlot == inventorySlot &&
        string.Equals(
            ItemId, itemId, StringComparison.OrdinalIgnoreCase);
}
