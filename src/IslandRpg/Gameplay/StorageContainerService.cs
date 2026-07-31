using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class StorageContainerService
{
    public static bool IsStorage(string itemId) =>
        itemId is ItemIds.StorageChest or ItemIds.StorageBarrel;

    public static ItemContainerState Open(WorldGroundObject storage)
    {
        var definition = Definition(storage.Id, storage.ItemId);
        if (storage.Container is not { } contents)
            return new(definition);
        return new(
            definition,
            new(
                storage.Id,
                contents.Items,
                contents.Quantities,
                contents.OwnerIds));
    }

    public static WorldGroundObject Save(
        WorldGroundObject storage,
        ItemContainerState container)
    {
        if (!IsStorage(storage.ItemId) ||
            storage.Id != container.Definition.Id)
            throw new ArgumentException(
                "The container does not belong to this storage object.",
                nameof(container));
        var saved = container.Save();
        return storage with
        {
            Container = new(
                saved.Items,
                saved.Quantities,
                saved.OwnerIds)
        };
    }

    public static ItemContainerDefinition Definition(
        Guid id,
        string itemId) =>
        itemId switch
        {
            ItemIds.StorageChest => new(
                id,
                "Wooden Chest",
                Columns: 8,
                Rows: 6,
                ShowPlayerInventory: true,
                AllowStacking: true,
                ShowTransferAllButton: true),
            ItemIds.StorageBarrel => new(
                id,
                "Storage Barrel",
                Columns: 5,
                Rows: 8,
                ShowPlayerInventory: true,
                AllowStacking: true,
                ShowTransferAllButton: true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(itemId), itemId,
                "This item is not persistent storage.")
        };
}
