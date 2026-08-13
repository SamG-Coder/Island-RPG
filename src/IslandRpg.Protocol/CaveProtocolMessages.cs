namespace IslandRpg.Protocol;

/// <summary>Closed family of revision-checked cave commands.</summary>
public abstract record CaveActionPayload : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.CaveAction;

    public abstract CaveActionKind Action { get; }
}

public sealed record StartExcavationAction(
    float X,
    float Y,
    short WorldLevel,
    int ShovelInventorySlot,
    uint ExpectedChunkRevision) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.StartExcavation;
}

public sealed record WorkExcavationAction(
    WorldObjectReference Excavation,
    int ShovelInventorySlot) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.WorkExcavation;
}

public sealed record RestoreExcavationAction(
    WorldObjectReference Excavation) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.RestoreExcavation;
}

public sealed record InstallCaveRopeAction(
    WorldObjectReference Shaft,
    int RopeInventorySlot) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.InstallRope;
}

public sealed record TakeCaveRopeAction(
    WorldObjectReference Entrance) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.TakeRope;
}

public sealed record FillExcavationAction(
    WorldObjectReference Excavation,
    int MaterialInventorySlot) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.FillExcavation;
}

public sealed record TraverseCaveAction(
    WorldObjectReference Entrance) : CaveActionPayload
{
    public override CaveActionKind Action => CaveActionKind.Traverse;
}

/// <summary>
/// Reliable requester-only cave receipt. A transition is server authored and
/// supplies the exact destination; clients never choose a destination level.
/// </summary>
public sealed record CaveActionResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    CaveActionKind Action,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail,
    uint ActorRevision,
    uint InventoryRevision,
    bool Transitioned,
    float X,
    float Y,
    short WorldLevel,
    int Damage = 0,
    bool Completed = false) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.CaveActionResult;
}
