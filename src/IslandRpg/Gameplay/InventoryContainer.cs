namespace IslandRpg.Gameplay;

internal sealed record InventoryStack(
    string ItemId,
    int Quantity,
    string? OwnerId = null);

/// <summary>
/// Shared slot-and-stack storage used by carried inventories and world
/// containers. Item definitions decide whether identical items may share a
/// slot; the container only supplies capacity and optional ownership.
/// </summary>
internal sealed class InventoryContainer
{
    private readonly InventoryStack?[] _slots;

    public InventoryContainer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _slots = new InventoryStack?[capacity];
    }

    public int Capacity => _slots.Length;
    public int UsedSlots => _slots.Count(value => value is not null);
    public int ItemCount => _slots.Sum(value => value?.Quantity ?? 0);
    public InventoryStack? this[int slot] =>
        (uint)slot < (uint)_slots.Length ? _slots[slot] : null;

    public bool TryAdd(
        string itemId,
        int quantity = 1,
        string? ownerId = null,
        bool allowStacking = true,
        Predicate<int>? slotAvailable = null)
    {
        if (quantity <= 0) return true;
        if (!ItemCatalog.TryGet(itemId, out var definition)) return false;
        var stackable = allowStacking && definition.CanStack;
        if (stackable)
        {
            var existing = Array.FindIndex(_slots, value =>
                value is not null &&
                value.ItemId.Equals(
                    itemId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    value.OwnerId, ownerId, StringComparison.Ordinal));
            if (existing >= 0)
            {
                var stack = _slots[existing]!;
                _slots[existing] = stack with
                {
                    Quantity = checked(stack.Quantity + quantity)
                };
                return true;
            }
        }

        var requiredSlots = stackable ? 1 : quantity;
        var empty = EmptySlots(slotAvailable).Take(requiredSlots).ToArray();
        if (empty.Length != requiredSlots) return false;
        if (stackable)
        {
            _slots[empty[0]] = new(itemId, quantity, ownerId);
            return true;
        }
        foreach (var slot in empty)
            _slots[slot] = new(itemId, 1, ownerId);
        return true;
    }

    /// <summary>
    /// Adds as much of a gathered quantity as the available slots can hold.
    /// Unlike an atomic transfer, a world harvest should not discard every
    /// obtainable item merely because the complete rolled yield will not fit.
    /// </summary>
    public int AddUpTo(
        string itemId,
        int quantity,
        string? ownerId = null,
        bool allowStacking = true,
        Predicate<int>? slotAvailable = null)
    {
        if (quantity <= 0 ||
            !ItemCatalog.TryGet(itemId, out var definition))
            return 0;
        if (allowStacking && definition.CanStack)
            return TryAdd(
                itemId, quantity, ownerId,
                allowStacking: true, slotAvailable: slotAvailable)
                ? quantity
                : 0;

        var available = Math.Min(
            quantity, EmptySlots(slotAvailable).Count());
        return available > 0 && TryAdd(
            itemId, available, ownerId,
            allowStacking: false, slotAvailable: slotAvailable)
            ? available
            : 0;
    }

    /// <summary>
    /// Reserves ordinary slots for an internal, short-lived crafting product.
    /// These values may exist only inside an atomic crafting transaction and
    /// must be consumed before the successful inventory is committed.
    /// </summary>
    internal bool TryAddTransient(string itemId, int quantity = 1)
    {
        if (quantity <= 0) return true;
        if (ItemCatalog.TryGet(itemId, out _)) return false;
        var empty = EmptySlots(null).Take(quantity).ToArray();
        if (empty.Length != quantity) return false;
        foreach (var slot in empty)
            _slots[slot] = new(itemId, 1);
        return true;
    }

    public bool TryAddAtPreferredSlot(
        string itemId, int preferredSlot, int quantity = 1)
    {
        if ((uint)preferredSlot < (uint)Capacity &&
            _slots[preferredSlot] is null &&
            TrySetSlot(preferredSlot, itemId, quantity))
            return true;
        return TryAdd(itemId, quantity);
    }

    public bool TryTake(
        int slot,
        int quantity,
        out InventoryStack taken)
    {
        taken = null!;
        if ((uint)slot >= (uint)_slots.Length || quantity <= 0 ||
            _slots[slot] is not { } value || value.Quantity < quantity)
            return false;
        taken = value with { Quantity = quantity };
        var remaining = value.Quantity - quantity;
        _slots[slot] = remaining == 0
            ? null
            : value with { Quantity = remaining };
        return true;
    }

    public bool TrySetSlot(
        int slot,
        string itemId,
        int quantity = 1,
        string? ownerId = null,
        bool allowStacking = true)
    {
        if ((uint)slot >= (uint)_slots.Length || _slots[slot] is not null ||
            quantity <= 0 || !ItemCatalog.TryGet(itemId, out var definition) ||
            quantity > 1 && !(allowStacking && definition.CanStack))
            return false;
        _slots[slot] = new(itemId, quantity, ownerId);
        return true;
    }

    public bool TryReplace(int slot, string itemId, int quantity = 1)
    {
        if ((uint)slot >= (uint)_slots.Length || quantity <= 0 ||
            _slots[slot] is not { } existing ||
            !ItemCatalog.TryGet(itemId, out var definition) ||
            quantity > 1 && !definition.CanStack)
            return false;
        _slots[slot] = new(itemId, quantity, existing.OwnerId);
        return true;
    }

    public bool TrySwap(int source, int target)
    {
        if (source == target || (uint)source >= (uint)_slots.Length ||
            (uint)target >= (uint)_slots.Length || _slots[source] is null)
            return false;
        (_slots[source], _slots[target]) = (_slots[target], _slots[source]);
        return true;
    }

    public int Count(string itemId) => _slots.Sum(value =>
        value is not null && value.ItemId.Equals(
            itemId, StringComparison.OrdinalIgnoreCase)
            ? value.Quantity
            : 0);

    public int Count(Predicate<string> accepts) => _slots.Sum(value =>
        value is not null && accepts(value.ItemId) ? value.Quantity : 0);

    public bool TryTake(Predicate<string> accepts, int quantity)
    {
        if (quantity <= 0) return true;
        if (Count(accepts) < quantity) return false;
        var remaining = quantity;
        for (var slot = 0; slot < Capacity && remaining > 0; slot++)
        {
            if (_slots[slot] is not { } value ||
                !accepts(value.ItemId))
                continue;
            var take = Math.Min(value.Quantity, remaining);
            TryTake(slot, take, out _);
            remaining -= take;
        }
        return true;
    }

    public InventoryContainer Clone()
    {
        var clone = new InventoryContainer(Capacity);
        for (var slot = 0; slot < Capacity; slot++)
            if (_slots[slot] is { } value)
                clone._slots[slot] = value;
        return clone;
    }

    /// <summary>
    /// Commits a previously validated candidate with the same capacity.
    /// </summary>
    internal void CopyFrom(InventoryContainer candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Capacity != Capacity)
            throw new ArgumentException(
                "Candidate inventory capacity must match the target.",
                nameof(candidate));
        Array.Copy(candidate._slots, _slots, Capacity);
    }

    public bool CanAdd(
        string itemId,
        int quantity = 1,
        string? ownerId = null,
        bool allowStacking = true,
        Predicate<int>? slotAvailable = null)
    {
        if (quantity <= 0) return true;
        if (!ItemCatalog.TryGet(itemId, out var definition)) return false;
        if (allowStacking && definition.CanStack &&
            _slots.Any(value => value is not null &&
                value.ItemId.Equals(
                    itemId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    value.OwnerId, ownerId, StringComparison.Ordinal)))
            return true;
        var required = allowStacking && definition.CanStack ? 1 : quantity;
        return EmptySlots(slotAvailable).Take(required).Count() == required;
    }

    public string?[] ItemIds() =>
        _slots.Select(value => value?.ItemId).ToArray();

    public int[] Quantities() =>
        _slots.Select(value => value?.Quantity ?? 0).ToArray();

    public string?[] OwnerIds() =>
        _slots.Select(value => value?.OwnerId).ToArray();

    private IEnumerable<int> EmptySlots(Predicate<int>? available)
    {
        for (var slot = 0; slot < _slots.Length; slot++)
            if (_slots[slot] is null && (available?.Invoke(slot) ?? true))
                yield return slot;
    }
}
