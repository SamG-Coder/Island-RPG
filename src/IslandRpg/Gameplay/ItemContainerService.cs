namespace IslandRpg.Gameplay;

internal enum ItemContainerCategory
{
    Tools,
    Resources,
    Food,
    Seeds,
    Furniture,
    Other
}

internal enum ItemContainerAccess
{
    DepositAndWithdraw,
    WithdrawOnly
}

internal sealed record ItemContainerDefinition(
    Guid Id,
    string Title,
    int Columns,
    int Rows,
    bool ShowPlayerInventory = true,
    bool AllowStacking = true,
    bool ShowTransferAllButton = true,
    ItemContainerAccess Access = ItemContainerAccess.DepositAndWithdraw)
{
    public int ColumnCount => Math.Max(1, Columns);
    public int RowCount => Math.Max(1, Rows);
    public int Capacity => ColumnCount * RowCount;
    public bool AllowsDeposit => Access == ItemContainerAccess.DepositAndWithdraw;
}

internal sealed record ItemContainerSaveState(
    Guid Id,
    string?[] Items,
    int[] Quantities,
    string?[]? OwnerIds = null);

internal sealed class ItemContainerState
{
    private readonly InventoryContainer _inventory;
    private readonly bool[] _spacers;

    public ItemContainerState(ItemContainerDefinition definition)
    {
        Definition = definition;
        _inventory = new(definition.Capacity);
        _spacers = new bool[definition.Capacity];
    }

    public ItemContainerState(
        ItemContainerDefinition definition,
        ItemContainerSaveState saved) : this(definition)
    {
        if (saved.Id != definition.Id)
            throw new ArgumentException(
                "The saved container ID does not match its definition.",
                nameof(saved));
        var length = Math.Min(
            definition.Capacity,
            Math.Min(saved.Items.Length, saved.Quantities.Length));
        for (var slot = 0; slot < length; slot++)
        {
            if (saved.Items[slot] is not { } itemId ||
                saved.Quantities[slot] <= 0 ||
                !ItemCatalog.TryGet(itemId, out _))
                continue;
            var ownerId = saved.OwnerIds is { } owners &&
                          slot < owners.Length
                ? owners[slot]
                : null;
            _inventory.TrySetSlot(
                slot,
                itemId,
                definition.AllowStacking ? saved.Quantities[slot] : 1,
                ownerId,
                definition.AllowStacking);
        }
    }

    public ItemContainerDefinition Definition { get; }
    public string?[] Items => _inventory.ItemIds();
    public int[] Quantities => _inventory.Quantities();
    public string?[] OwnerIds => _inventory.OwnerIds();
    public bool IsSpacer(int slot) =>
        (uint)slot < (uint)_spacers.Length && _spacers[slot];
    public bool IsEmpty => _inventory.UsedSlots == 0;

    public ItemContainerSaveState Save() =>
        new(
            Definition.Id,
            _inventory.ItemIds(),
            _inventory.Quantities(),
            _inventory.OwnerIds());

    public bool TryAdd(
        string itemId,
        int quantity = 1,
        string? ownerId = null)
    {
        if (!Definition.AllowsDeposit) return false;
        return _inventory.TryAdd(
            itemId, quantity, ownerId,
            Definition.AllowStacking, SlotAvailable);
    }

    public bool TryTake(int slot, int quantity, out string? itemId)
    {
        if (!_inventory.TryTake(slot, quantity, out var taken))
        {
            itemId = null;
            return false;
        }
        itemId = taken.ItemId;
        return true;
    }

    public int TransferAllFrom(string?[] inventory)
    {
        if (!Definition.AllowsDeposit) return 0;
        var moved = 0;
        for (var slot = 0; slot < inventory.Length; slot++)
        {
            if (inventory[slot] is not { } itemId)
                continue;
            if (!TryAdd(itemId))
                break;
            inventory[slot] = null;
            moved++;
        }
        return moved;
    }

    public int TransferAllFrom(InventoryContainer inventory)
    {
        if (!Definition.AllowsDeposit) return 0;
        var moved = 0;
        for (var slot = 0; slot < inventory.Capacity; slot++)
        {
            if (inventory[slot] is not { } stack) continue;
            var quantity = stack.Quantity;
            if (!TryAdd(stack.ItemId, quantity, stack.OwnerId))
                continue;
            if (!inventory.TryTake(slot, quantity, out _))
                throw new InvalidOperationException(
                    "Container transfer changed after validation.");
            moved += quantity;
        }
        return moved;
    }

    public int TransferMatchingFrom(
        string?[] inventory, string itemId, int maximum)
    {
        if (!Definition.AllowsDeposit) return 0;
        if (maximum <= 0) return 0;
        var moved = 0;
        for (var slot = 0;
             slot < inventory.Length && moved < maximum;
             slot++)
        {
            if (!string.Equals(
                    inventory[slot], itemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryAdd(itemId))
                break;
            inventory[slot] = null;
            moved++;
        }
        return moved;
    }

    public int TransferMatchingFrom(
        InventoryContainer inventory, string itemId, int maximum)
    {
        if (!Definition.AllowsDeposit || maximum <= 0) return 0;
        var moved = 0;
        for (var slot = 0;
             slot < inventory.Capacity && moved < maximum;
             slot++)
        {
            if (inventory[slot] is not { } stack ||
                !stack.ItemId.Equals(itemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var quantity = Math.Min(stack.Quantity, maximum - moved);
            if (!TryAdd(itemId, quantity, stack.OwnerId)) break;
            inventory.TryTake(slot, quantity, out _);
            moved += quantity;
        }
        return moved;
    }

    public void AddSpacerRow()
    {
        var occupied = -1;
        for (var index = Definition.Capacity - 1; index >= 0; index--)
            if (_inventory[index] is not null || _spacers[index])
            {
                occupied = index;
                break;
            }
        var start = occupied + 1;
        var columns = Definition.ColumnCount;
        var alignedStart =
            (int)Math.Ceiling(start / (double)columns) * columns;
        var end = Math.Min(
            _spacers.Length, alignedStart + columns);
        for (var slot = start; slot < end; slot++)
            _spacers[slot] = true;
    }

    public static ItemContainerCategory Category(ItemDefinition item)
    {
        if (item.HasTag(ItemTag.Tool))
            return ItemContainerCategory.Tools;
        if (SurvivalService.TryFoodEffect(item.Id, out _) ||
            item.HasTag(ItemTag.CookedFood) ||
            item.HasTag(ItemTag.BurntFood) ||
            item.HasTag(ItemTag.Fish) ||
            item.HasTag(ItemTag.Berry))
            return ItemContainerCategory.Food;
        if (item.HasTag(ItemTag.Seed))
            return ItemContainerCategory.Seeds;
        if (item.HasTag(ItemTag.PlaceableObject))
            return ItemContainerCategory.Furniture;
        if (item.HasTag(ItemTag.NaturalMaterial) ||
            item.HasTag(ItemTag.WoodcuttingMaterial) ||
            item.HasTag(ItemTag.MiningMaterial) ||
            item.HasTag(ItemTag.Mineral))
            return ItemContainerCategory.Resources;
        return ItemContainerCategory.Other;
    }

    public static ItemContainerState CreateAllItemsTest()
    {
        var grouped = ItemCatalog.All
            .OrderBy(Category)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(Category)
            .ToArray();
        const int columns = 14;
        var requiredSlots = 0;
        for (var index = 0; index < grouped.Length; index++)
        {
            if (index > 0)
            {
                requiredSlots =
                    (int)Math.Ceiling(requiredSlots / (double)columns) *
                    columns;
                requiredSlots += columns;
            }
            requiredSlots += grouped[index].Count();
        }
        var definition = new ItemContainerDefinition(
            new Guid("89a6a389-9cce-4ed5-bd6b-e0062aa2a595"),
            "Developer Item Bank",
            columns,
            Math.Max(
                1,
                (int)Math.Ceiling(requiredSlots / (double)columns)),
            ShowPlayerInventory: true,
            AllowStacking: true,
            ShowTransferAllButton: true);
        var container = new ItemContainerState(definition);
        for (var groupIndex = 0; groupIndex < grouped.Length; groupIndex++)
        {
            if (groupIndex > 0)
                container.AddSpacerRow();
            foreach (var item in grouped[groupIndex])
                container.TryAdd(item.Id, item.CanStack ? 100 : 1);
        }
        return container;
    }

    private bool SlotAvailable(int slot) => !_spacers[slot];
}
