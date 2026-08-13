using IslandRpg.Gameplay;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private InventoryContainer ActivePlayerInventory() =>
        PlayerInventory.Load(
            _activePlayer?.Inventory,
            _activePlayer?.InventoryQuantities);

    private void SaveActivePlayerInventory(
        InventoryContainer inventory,
        bool saveImmediately = true)
    {
        if (IsNetworkWorld)
        {
            WarnNetworkMutationUnavailable();
            return;
        }
        if (_activePlayer is null) return;
        _activeInventorySlot = inventory[_activeInventorySlot] is null
            ? -1
            : _activeInventorySlot;
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            UpdatedUtc = DateTime.UtcNow
        };
        if (saveImmediately)
            _saves.SavePlayer(_activePlayer);
    }

    private void NormalizeActivePlayerInventory()
    {
        if (_activePlayer is null) return;
        var inventory = ActivePlayerInventory();
        var items = inventory.ItemIds();
        var quantities = inventory.Quantities();
        if (items.SequenceEqual(_activePlayer.Inventory ?? []) &&
            quantities.SequenceEqual(
                _activePlayer.InventoryQuantities ?? []))
            return;
        _activePlayer = _activePlayer with
        {
            Inventory = items,
            InventoryQuantities = quantities
        };
    }
}
