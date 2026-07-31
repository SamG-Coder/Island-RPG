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

internal sealed record ItemContainerDefinition(
    Guid Id,
    string Title,
    int Columns,
    int Rows,
    bool ShowPlayerInventory = true,
    bool AllowStacking = true,
    bool ShowTransferAllButton = true)
{
    public int ColumnCount => Math.Max(1, Columns);
    public int RowCount => Math.Max(1, Rows);
    public int Capacity => ColumnCount * RowCount;
}

internal sealed record ItemContainerSaveState(
    Guid Id,
    string?[] Items,
    int[] Quantities,
    string?[]? OwnerIds = null);

internal sealed class ItemContainerState
{
    private readonly string?[] _items;
    private readonly int[] _quantities;
    private readonly string?[] _ownerIds;
    private readonly bool[] _spacers;

    public ItemContainerState(ItemContainerDefinition definition)
    {
        Definition = definition;
        _items = new string?[definition.Capacity];
        _quantities = new int[definition.Capacity];
        _ownerIds = new string?[definition.Capacity];
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
            _items[slot] = itemId;
            _quantities[slot] = definition.AllowStacking
                ? saved.Quantities[slot]
                : 1;
            if (saved.OwnerIds is { } owners &&
                slot < owners.Length)
                _ownerIds[slot] = owners[slot];
        }
    }

    public ItemContainerDefinition Definition { get; }
    public string?[] Items => _items;
    public int[] Quantities => _quantities;
    public string?[] OwnerIds => _ownerIds;
    public bool IsSpacer(int slot) =>
        (uint)slot < (uint)_spacers.Length && _spacers[slot];

    public ItemContainerSaveState Save() =>
        new(
            Definition.Id,
            (string?[])_items.Clone(),
            (int[])_quantities.Clone(),
            (string?[])_ownerIds.Clone());

    public bool TryAdd(
        string itemId,
        int quantity = 1,
        string? ownerId = null)
    {
        if (quantity <= 0) return true;
        if (Definition.AllowStacking)
        {
            var existing = -1;
            for (var slot = 0; slot < _items.Length; slot++)
                if (string.Equals(
                        _items[slot], itemId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        _ownerIds[slot], ownerId,
                        StringComparison.Ordinal))
                {
                    existing = slot;
                    break;
                }
            if (existing >= 0)
            {
                _quantities[existing] =
                    checked(_quantities[existing] + quantity);
                return true;
            }
        }

        if (Definition.AllowStacking)
        {
            var empty = FindEmptySlot();
            if (empty < 0) return false;
            _items[empty] = itemId;
            _quantities[empty] = quantity;
            _ownerIds[empty] = ownerId;
            return true;
        }

        var available = Enumerable.Range(0, _items.Length)
            .Count(index => _items[index] is null && !_spacers[index]);
        if (available < quantity) return false;
        for (var remaining = quantity; remaining > 0; remaining--)
        {
            var empty = FindEmptySlot();
            _items[empty] = itemId;
            _quantities[empty] = 1;
            _ownerIds[empty] = ownerId;
        }
        return true;
    }

    public bool TryTake(int slot, int quantity, out string? itemId)
    {
        itemId = null;
        if ((uint)slot >= (uint)_items.Length ||
            quantity <= 0 ||
            _items[slot] is not { } value ||
            _quantities[slot] < quantity)
            return false;
        itemId = value;
        _quantities[slot] -= quantity;
        if (_quantities[slot] == 0)
        {
            _items[slot] = null;
            _ownerIds[slot] = null;
        }
        return true;
    }

    public int TransferAllFrom(string?[] inventory)
    {
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

    public int TransferMatchingFrom(
        string?[] inventory, string itemId, int maximum)
    {
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

    public void AddSpacerRow()
    {
        var occupied = -1;
        for (var index = _items.Length - 1; index >= 0; index--)
            if (_items[index] is not null || _spacers[index])
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
        if (item.HasTag(ItemTag.CookedFood) ||
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
                container.TryAdd(item.Id, 100);
        }
        return container;
    }

    private int FindEmptySlot()
    {
        for (var index = 0; index < _items.Length; index++)
            if (_items[index] is null && !_spacers[index])
                return index;
        return -1;
    }
}
