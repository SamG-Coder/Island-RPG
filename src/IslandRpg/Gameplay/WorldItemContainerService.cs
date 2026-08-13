using IslandRpg.World;

namespace IslandRpg.Gameplay;

/// <summary>
/// Authoritative container boundary shared by permanent storage and transient
/// enemy loot bags. The definition is derived from the server-owned world
/// object; clients never choose capacity or access permissions.
/// </summary>
internal static class WorldItemContainerService
{
    public const int LootBagColumns = 4;
    public const int LootBagRows = 3;

    public static bool IsContainer(string itemId) =>
        StorageContainerService.IsStorage(itemId) ||
        IsLootBag(itemId);

    public static bool IsLootBag(string itemId) =>
        string.Equals(itemId, ItemIds.LootBag, StringComparison.Ordinal);

    public static ItemContainerState Open(WorldGroundObject value)
    {
        var definition = Definition(value.Id, value.ItemId);
        if (value.Container is not { } contents)
            return new ItemContainerState(definition);
        return new ItemContainerState(
            definition,
            new ItemContainerSaveState(
                value.Id,
                contents.Items,
                contents.Quantities,
                contents.OwnerIds));
    }

    /// <summary>
    /// Builds trusted initial contents while preserving the object's final
    /// access policy. This is used only by server-side world seeding; normal
    /// transfers still honor a loot bag's withdraw-only definition.
    /// </summary>
    public static ItemContainerState OpenForSeeding(WorldGroundObject value)
    {
        var definition = Definition(value.Id, value.ItemId) with
        {
            Access = ItemContainerAccess.DepositAndWithdraw
        };
        return new ItemContainerState(definition);
    }

    public static WorldGroundObject Save(
        WorldGroundObject value,
        ItemContainerState container)
    {
        if (!IsContainer(value.ItemId) ||
            value.Id != container.Definition.Id)
            throw new ArgumentException(
                "The container does not belong to this world object.",
                nameof(container));
        var saved = container.Save();
        return value with
        {
            Container = new WorldContainerContents(
                saved.Items,
                saved.Quantities,
                saved.OwnerIds)
        };
    }

    public static ItemContainerDefinition Definition(Guid id, string itemId) =>
        IsLootBag(itemId)
            ? new ItemContainerDefinition(
                id,
                "Loot",
                LootBagColumns,
                LootBagRows,
                ShowPlayerInventory: true,
                AllowStacking: true,
                ShowTransferAllButton: false,
                Access: ItemContainerAccess.WithdrawOnly)
            : StorageContainerService.Definition(id, itemId);
}
