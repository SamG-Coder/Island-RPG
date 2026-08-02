namespace IslandRpg.Gameplay;

/// <summary>
/// Commits an accepted gift as one validated inventory operation. Callers
/// must not apply dialogue, memories, or relationship outcomes before this
/// succeeds.
/// </summary>
internal static class VillagerGiftTransferService
{
    public static bool TryTransfer(
        string?[]? giverInventory,
        int giverSlot,
        string expectedItemId,
        string?[]? receiverInventory,
        out string?[] updatedGiver,
        out string?[] updatedReceiver)
    {
        updatedGiver = PlayerInventory.Normalize(giverInventory);
        updatedReceiver = PlayerInventory.Normalize(receiverInventory);
        if ((uint)giverSlot >= PlayerInventory.Capacity ||
            !string.Equals(
                updatedGiver[giverSlot], expectedItemId,
                StringComparison.OrdinalIgnoreCase) ||
            !PlayerInventory.TryAdd(
                updatedReceiver, expectedItemId, out var receiver) ||
            !PlayerInventory.TryRemove(
                updatedGiver, giverSlot, out var giver))
            return false;
        updatedGiver = giver;
        updatedReceiver = receiver;
        return true;
    }
}
