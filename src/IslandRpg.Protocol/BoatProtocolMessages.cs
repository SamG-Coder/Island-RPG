namespace IslandRpg.Protocol;

/// <summary>
/// Optimistic reference to one durable authoritative boat. Transform claims
/// never travel in the reference; the authority resolves current position.
/// </summary>
public readonly record struct BoatReference(Guid BoatId, uint ExpectedRevision)
{
    public bool IsWellFormed => BoatId != Guid.Empty;
}

public abstract record BoatActionPayload(
    BoatActionKind Action,
    BoatReference Boat) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.BoatAction;
}

public sealed record BoardBoatAction(BoatReference Boat) :
    BoatActionPayload(BoatActionKind.Board, Boat);

public sealed record MoveBoatAction(
    BoatReference Boat,
    float TargetX,
    float TargetY) : BoatActionPayload(BoatActionKind.Move, Boat);

public sealed record StopBoatAction(BoatReference Boat) :
    BoatActionPayload(BoatActionKind.Stop, Boat);

public sealed record DisembarkBoatAction(
    BoatReference Boat,
    float TargetX,
    float TargetY) : BoatActionPayload(BoatActionKind.Disembark, Boat);

/// <summary>
/// Reliable semantic state. High-rate transform interpolation travels in the
/// UDP entity stream under EntityId; this record owns identity and occupancy.
/// </summary>
public readonly record struct BoatState(
    Guid BoatId,
    ulong EntityId,
    uint Revision,
    Guid OwnerPlayerId,
    string GroupOwnerId,
    Guid OccupantPlayerId,
    ulong OccupantEntityId,
    float X,
    float Y,
    float FacingX,
    float FacingY,
    short WorldLevel,
    bool Moving);

public sealed record BoatBaselineMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<BoatState> Boats) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.BoatBaseline;
}

public readonly record struct BoatDelta(
    BoatDeltaKind Kind,
    BoatReference Reference,
    uint CurrentRevision,
    BoatState? State);

public sealed record BoatDeltaBatchMessage(
    ulong Sequence,
    ulong Tick,
    IReadOnlyList<BoatDelta> Deltas) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.BoatDeltaBatch;
}

/// <summary>
/// Requester-private command decision. Actor position is authoritative only
/// when Transitioned is true (board/disembark); observers receive BoatDelta.
/// </summary>
public sealed record BoatActionResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    BoatActionKind Action,
    BoatReference Boat,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail,
    uint ActorRevision,
    uint InventoryRevision,
    uint BoatRevision,
    bool Transitioned,
    float ActorX,
    float ActorY,
    short ActorWorldLevel) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.BoatActionResult;
}
