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
/// Stable identity and optimistic-concurrency token for a persisted world
/// object. Chunk and level make lookups explicit without trusting object ID
/// alone.
/// </summary>
public readonly record struct WorldObjectReference(
    Guid ObjectId,
    int ChunkX,
    int ChunkY,
    short WorldLevel,
    uint ExpectedObjectRevision,
    uint ExpectedChunkRevision);

public sealed record PickUpWorldObjectAction(
    WorldObjectReference Object) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.PickUpWorldObject;
}

public sealed record DropInventoryItemAction(
    int InventorySlot,
    int Quantity,
    float X,
    float Y,
    short WorldLevel,
    uint ExpectedChunkRevision) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.DropInventoryItem;
}

public sealed record OpenContainerAction(
    WorldObjectReference Object) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.OpenContainer;
}

public sealed record ContainerTransferAction(
    WorldObjectReference Container,
    uint ExpectedContainerRevision,
    ContainerTransferDirection Direction,
    int InventorySlot,
    int ContainerSlot,
    int Quantity) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.ContainerTransfer;
}

public sealed record AddCampfireFuelAction(
    WorldObjectReference Campfire,
    int InventorySlot) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.AddCampfireFuel;
}

public sealed record TakeCampfireFuelAction(
    WorldObjectReference Campfire) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.TakeCampfireFuel;
}

public sealed record LightCampfireAction(
    WorldObjectReference Campfire) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.LightCampfire;
}

public sealed record CookOnCampfireAction(
    WorldObjectReference Campfire,
    int InventorySlot) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.CookOnCampfire;
}

public sealed record PlaceConstructionAction(
    string DefinitionId,
    int InventorySlot,
    float X,
    float Y,
    short WorldLevel,
    int Rotation,
    uint ExpectedChunkRevision) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.PlaceConstruction;
}

public sealed record BuildConstructionAction(
    WorldObjectReference Construction) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.BuildConstruction;
}

public sealed record DemolishWorldObjectAction(
    WorldObjectReference Object) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.DemolishWorldObject;
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
/// Private authoritative completion of a timed campfire cooking operation.
/// The accepting ActionResult begins presentation; this message ends it.
/// </summary>
public sealed record CookingResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    string RawItemId,
    string ResultItemId,
    bool Burnt,
    bool Interrupted,
    uint ActorRevision,
    uint InventoryRevision) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.CookingResult;
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
    IReadOnlyList<InventorySlotState> InventorySlots,
    int WoodcuttingExperience = 0) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.PlayerState;
}

/// <summary>
/// Public state for one world object. Container contents are intentionally
/// absent; only a private ContainerStateMessage carries slots.
/// </summary>
public readonly record struct WorldObjectState(
    Guid ObjectId,
    int ChunkX,
    int ChunkY,
    short WorldLevel,
    uint ChunkRevision,
    uint ObjectRevision,
    string DefinitionId,
    float X,
    float Y,
    int Rotation,
    int Health,
    int MaximumHealth,
    bool HasContainer,
    string FuelItemId,
    double LitUntilGameSeconds,
    WorldObjectGateState GateState);

public sealed record WorldObjectStateMessage(
    ulong Sequence,
    ulong Tick,
    WorldObjectState Object) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.WorldObjectState;
}

public readonly record struct WorldObjectDelta(
    WorldObjectDeltaKind Kind,
    WorldObjectReference Reference,
    uint CurrentChunkRevision,
    WorldObjectState? State);

public sealed record WorldObjectDeltaBatchMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<WorldObjectDelta> Deltas) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.WorldObjectDeltaBatch;
}

public readonly record struct ContainerSlotState(
    int Slot,
    string ItemId,
    int Quantity)
{
    public bool IsEmpty => ItemId.Length == 0;
}

/// <summary>
/// Private authoritative container state sent only to the player with an open
/// container. A baseline carries every slot; a delta names changed slots.
/// </summary>
public sealed record ContainerStateMessage(
    ulong Sequence,
    ulong Tick,
    WorldObjectReference Container,
    uint BaselineContainerRevision,
    uint ContainerRevision,
    string DefinitionId,
    ContainerAccessMode Access,
    int SlotCount,
    bool IsBaseline,
    IReadOnlyList<ContainerSlotState> Slots) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ContainerState;
}
