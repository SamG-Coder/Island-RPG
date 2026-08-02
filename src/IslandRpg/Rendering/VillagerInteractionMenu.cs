using IslandRpg.Gameplay;

namespace IslandRpg.Rendering;

internal enum VillagerInteractionKind : byte
{
    WalkHere,
    Attack,
    Give,
    Examine
}

internal sealed record VillagerInteractionOption(
    VillagerInteractionKind Kind,
    string Label,
    int InventorySlot = -1,
    string? ItemId = null);

internal static class VillagerInteractionMenu
{
    public static IReadOnlyList<VillagerInteractionOption> Build(
        string?[]? inventory,
        int activeSlot)
    {
        var options = new List<VillagerInteractionOption>(4)
        {
            new(VillagerInteractionKind.WalkHere, "Walk here"),
            new(VillagerInteractionKind.Attack, "Attack")
        };
        var giftSlot = SelectedGiftSlot(inventory, activeSlot);
        if (giftSlot >= 0 && inventory![giftSlot] is { } itemId)
        {
            var selected = giftSlot == activeSlot;
            options.Add(new(
                VillagerInteractionKind.Give,
                selected
                    ? $"Give {ItemCatalog.Get(itemId).Name}"
                    : "Give food",
                giftSlot,
                itemId));
        }
        options.Add(new(VillagerInteractionKind.Examine, "Examine"));
        return options;
    }

    private static int SelectedGiftSlot(
        string?[]? inventory,
        int activeSlot)
    {
        if (inventory is null) return -1;
        if ((uint)activeSlot < (uint)inventory.Length &&
            inventory[activeSlot] is not null)
            return activeSlot;
        var length = Math.Min(inventory.Length, PlayerInventory.Capacity);
        for (var slot = 0; slot < length; slot++)
            if (inventory[slot] is { } itemId &&
                SurvivalService.TryFoodEffect(itemId, out _))
                return slot;
        return -1;
    }
}
