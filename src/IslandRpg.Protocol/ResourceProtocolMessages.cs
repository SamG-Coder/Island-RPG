using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Protocol;

/// <summary>
/// One client-authored interaction with a stable procedural resource. The
/// optional tool slot is -1 when no inventory tool participates.
/// </summary>
public sealed record ResourceActionPayload(
    ResourceActionKind Action,
    ResourceNodeReference Resource,
    int ToolInventorySlot = -1) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.ResourceAction;
}

/// <summary>Retained node revision for a node with no sparse visible state.</summary>
public readonly record struct ResourceNodeRevisionState(
    ResourceNodeId Id,
    uint Revision);

/// <summary>
/// Complete sparse overlay and tombstone high-water for one procedural chunk.
/// A zero chunk revision is valid for a never-mutated chunk.
/// </summary>
public sealed record ResourceChunkBaselineMessage(
    ulong Sequence,
    ulong Tick,
    WorldChunkKey Chunk,
    uint ResourceChunkRevision,
    IReadOnlyList<ResourceNodeSparseState> Nodes,
    IReadOnlyList<ResourceNodeRevisionState> Tombstones) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ResourceChunkBaseline;
}

/// <summary>
/// One optimistic resource mutation. Current revisions describe the state
/// after the operation and must advance the exact reference supplied.
/// </summary>
public readonly record struct ResourceNodeDelta(
    ResourceNodeDeltaKind Kind,
    ResourceNodeReference Reference,
    uint CurrentNodeRevision,
    uint CurrentResourceChunkRevision,
    ResourceNodeSparseState? State);

/// <summary>
/// Bounded authoritative resource changes. Every change in a chunk is applied
/// as one atomic revision transition by clients.
/// </summary>
public sealed record ResourceNodeDeltaBatchMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<ResourceNodeDelta> Deltas) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ResourceNodeDeltaBatch;
}

public readonly record struct ResourceItemRewardState(
    string ItemId,
    int Quantity);

/// <summary>
/// Requester-private outcome for a resource action. Public node state travels
/// separately so observers never receive inventory or tool-wear details.
/// </summary>
public sealed record ResourceActionResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail,
    uint ActorRevision,
    uint InventoryRevision,
    ResourceActionKind Action,
    ResourceNodeReference Resource,
    IReadOnlyList<ResourceItemRewardState> Rewards,
    bool Hit,
    int Damage,
    bool ToolWorn) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ResourceActionResult;
}
