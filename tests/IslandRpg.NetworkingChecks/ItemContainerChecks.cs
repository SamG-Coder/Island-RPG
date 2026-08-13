using IslandRpg.Gameplay;

namespace IslandRpg.NetworkingChecks;

internal static class ItemContainerChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "container deposits commit exact stack quantities atomically",
            DepositCommitsExactQuantity);
        checks.Add(
            "failed container deposits preserve both inventories",
            FailedDepositPreservesBothSides);
        checks.Add(
            "container withdrawals commit exact stack quantities atomically",
            WithdrawCommitsExactQuantity);
        checks.Add(
            "failed container withdrawals preserve both inventories",
            FailedWithdrawPreservesBothSides);
    }

    private static void DepositCommitsExactQuantity()
    {
        var bag = new InventoryContainer(2);
        CheckAssert.True(
            bag.TryAdd(ItemIds.SlimeGel, 7, "player-one"),
            "the deposit source must be initialized");
        var chest = NewContainer("deposit", capacity: 1);

        CheckAssert.True(
            ItemContainerTransferService.TryDeposit(
                bag, 0, chest, quantity: 5),
            "a valid deposit must commit");
        CheckAssert.Equal(
            2,
            bag.Count(ItemIds.SlimeGel),
            "the source must lose exactly the requested quantity");
        CheckAssert.Equal(
            5,
            chest.Quantities[0],
            "the destination must gain exactly the requested quantity");
        CheckAssert.Equal(
            "player-one",
            chest.OwnerIds[0],
            "deposits must preserve stack ownership");
    }

    private static void FailedDepositPreservesBothSides()
    {
        var bag = new InventoryContainer(2);
        CheckAssert.True(
            bag.TryAdd(ItemIds.StoneAxe, 2, "player-two"),
            "the failed-deposit source must be initialized");
        var chest = NewContainer(
            "full deposit", capacity: 1, allowStacking: false);
        CheckAssert.True(
            chest.TryAdd(ItemIds.Logs, ownerId: "existing-owner"),
            "the failed-deposit destination must be initialized");
        var bagBefore = Snapshot(bag);
        var chestBefore = chest.Save();

        CheckAssert.False(
            ItemContainerTransferService.TryDeposit(
                bag, 0, chest, quantity: 1),
            "a full destination must reject the deposit");
        AssertUnchanged(
            bagBefore, Snapshot(bag),
            "a failed deposit must not mutate the source");
        AssertUnchanged(
            chestBefore, chest.Save(),
            "a failed deposit must not mutate the destination");

        var emptyChest = NewContainer(
            "invalid quantity", capacity: 2, allowStacking: false);
        var emptyChestBefore = emptyChest.Save();
        CheckAssert.False(
            ItemContainerTransferService.TryDeposit(
                bag, 0, emptyChest, quantity: 2),
            "a source slot cannot provide more than its stack quantity");
        AssertUnchanged(
            bagBefore, Snapshot(bag),
            "an invalid source quantity must preserve the source");
        AssertUnchanged(
            emptyChestBefore, emptyChest.Save(),
            "an invalid source quantity must preserve the destination");
    }

    private static void WithdrawCommitsExactQuantity()
    {
        var chest = NewContainer("withdraw", capacity: 1);
        CheckAssert.True(
            chest.TryAdd(ItemIds.SlimeGel, 6, "loot-owner"),
            "the withdrawal source must be initialized");
        var bag = new InventoryContainer(1);

        CheckAssert.True(
            ItemContainerTransferService.TryWithdraw(
                chest, 0, bag, quantity: 4),
            "a valid withdrawal must commit");
        CheckAssert.Equal(
            2,
            chest.Quantities[0],
            "the container must lose exactly the requested quantity");
        CheckAssert.Equal(
            4,
            bag.Count(ItemIds.SlimeGel),
            "the carried inventory must gain exactly the requested quantity");
        CheckAssert.Equal(
            "loot-owner",
            bag[0]?.OwnerId,
            "withdrawals must preserve stack ownership");
    }

    private static void FailedWithdrawPreservesBothSides()
    {
        var chest = NewContainer(
            "blocked withdraw", capacity: 1, allowStacking: false);
        CheckAssert.True(
            chest.TryAdd(ItemIds.StoneAxe, ownerId: "loot-owner"),
            "the failed-withdrawal source must be initialized");
        var bag = new InventoryContainer(1);
        CheckAssert.True(
            bag.TryAdd(ItemIds.Logs, ownerId: "player-owner"),
            "the failed-withdrawal destination must be initialized");
        var chestBefore = chest.Save();
        var bagBefore = Snapshot(bag);

        CheckAssert.False(
            ItemContainerTransferService.TryWithdraw(
                chest, 0, bag, quantity: 1),
            "a full carried inventory must reject the withdrawal");
        AssertUnchanged(
            chestBefore, chest.Save(),
            "a failed withdrawal must not mutate the source");
        AssertUnchanged(
            bagBefore, Snapshot(bag),
            "a failed withdrawal must not mutate the destination");
    }

    private static ItemContainerState NewContainer(
        string title,
        int capacity,
        bool allowStacking = true) =>
        new(new(
            Guid.NewGuid(),
            title,
            Columns: capacity,
            Rows: 1,
            AllowStacking: allowStacking));

    private static InventorySnapshot Snapshot(InventoryContainer inventory) =>
        new(
            inventory.ItemIds(),
            inventory.Quantities(),
            inventory.OwnerIds());

    private static void AssertUnchanged(
        InventorySnapshot expected,
        InventorySnapshot actual,
        string message)
    {
        CheckAssert.SequenceEqual(expected.Items, actual.Items, message);
        CheckAssert.SequenceEqual(
            expected.Quantities, actual.Quantities, message);
        CheckAssert.SequenceEqual(expected.OwnerIds, actual.OwnerIds, message);
    }

    private static void AssertUnchanged(
        ItemContainerSaveState expected,
        ItemContainerSaveState actual,
        string message)
    {
        CheckAssert.Equal(expected.Id, actual.Id, message);
        CheckAssert.SequenceEqual(expected.Items, actual.Items, message);
        CheckAssert.SequenceEqual(
            expected.Quantities, actual.Quantities, message);
        CheckAssert.SequenceEqual(
            expected.OwnerIds ?? [], actual.OwnerIds ?? [], message);
    }

    private sealed record InventorySnapshot(
        string?[] Items,
        int[] Quantities,
        string?[] OwnerIds);
}
