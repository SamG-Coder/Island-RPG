namespace IslandRpg.Protocol;

/// <summary>
/// A closed set of client-authored action payloads. The kind is encoded before
/// the payload so untrusted input can be decoded without type guessing.
/// </summary>
public interface IActionCommandPayload
{
    ActionCommandKind Kind { get; }
}

public sealed record InventorySwapAction(
    int SourceSlot,
    int TargetSlot) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.InventorySwap;
}

public sealed record CombineItemsAction(
    int SourceSlot,
    int TargetSlot) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.CombineItems;
}

public sealed record CraftRecipeAction(
    string RecipeId) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.CraftRecipe;
}

public sealed record ConsumeItemAction(
    int Slot) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.ConsumeItem;
}

/// <summary>
/// A client action against exact authoritative revisions. Sequence remains the
/// per-connection command sequence carried by every reliable frame.
/// </summary>
public sealed record ActionCommandMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    uint ActorRevision,
    uint InventoryRevision,
    IActionCommandPayload Payload) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ActionCommand;
}

/// <summary>
/// Correlates an authoritative decision with the command and the revisions
/// visible immediately after that decision.
/// </summary>
public sealed record ActionResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail,
    uint ActorRevision,
    uint InventoryRevision) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ActionResult;
}

/// <summary>
/// One indexed inventory change. An empty slot has an empty item id and zero
/// quantity; an occupied slot has a nonempty item id and positive quantity.
/// </summary>
public readonly record struct InventorySlotState(
    int Slot,
    string ItemId,
    int Quantity)
{
    public bool IsEmpty => ItemId.Length == 0;
}

/// <summary>
/// Authoritative player state. A baseline contains Actor and Inventory and all
/// 28 slots. A delta identifies its baselines and contains only changed slots.
/// Fields outside the sections named by Flags are ignored by the receiver.
/// </summary>
public sealed record PlayerStateMessage(
    ulong Sequence,
    ulong Tick,
    Guid PlayerId,
    ulong PlayerEntityId,
    PlayerStateFlags Flags,
    uint BaselineActorRevision,
    uint BaselineInventoryRevision,
    uint ActorRevision,
    uint InventoryRevision,
    int Health,
    float Hunger,
    float WellFedSeconds,
    int CraftingExperience,
    int CookingExperience,
    IReadOnlyList<InventorySlotState> InventorySlots) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.PlayerState;
}
