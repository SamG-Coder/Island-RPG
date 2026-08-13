namespace IslandRpg.Protocol;

public readonly record struct CombatEnemyReference(Guid EnemyId, uint ExpectedRevision)
{
    public bool IsWellFormed => EnemyId != Guid.Empty;
}

public abstract record CombatActionPayload(CombatActionKind Action) : IActionCommandPayload
{
    public ActionCommandKind Kind => ActionCommandKind.CombatAction;
}

public sealed record SetCombatTargetAction(CombatEnemyReference Enemy) :
    CombatActionPayload(CombatActionKind.SetTarget);
public sealed record CancelCombatAction() : CombatActionPayload(CombatActionKind.Cancel);
public sealed record SetCombatStanceAction(CombatStance Stance) :
    CombatActionPayload(CombatActionKind.SetStance);
public sealed record RespawnAction() : CombatActionPayload(CombatActionKind.Respawn);

/// <summary>Reliable semantic enemy state; motion remains in UDP snapshots.</summary>
public readonly record struct EnemyState(
    Guid EnemyId,
    ulong EntityId,
    uint Revision,
    CombatEnemyArchetype Archetype,
    CombatEnemySize Size,
    CombatEnemyBehavior Behavior,
    CombatStatusFlags StatusFlags,
    float X,
    float Y,
    short WorldLevel,
    int Health,
    int MaximumHealth,
    ulong TargetEntityId,
    Guid ParentEnemyId,
    uint SpawnOrdinal);

public sealed record EnemyBaselineMessage(
    ulong Sequence, ulong Tick, IReadOnlyList<EnemyState> Enemies) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.EnemyBaseline;
}

public readonly record struct EnemyDelta(
    EnemyDeltaKind Kind,
    CombatEnemyReference Reference,
    uint CurrentRevision,
    EnemyState? State);

public sealed record EnemyDeltaBatchMessage(
    ulong Sequence, ulong Tick, IReadOnlyList<EnemyDelta> Deltas) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.EnemyDeltaBatch;
}

/// <summary>Session-global ordinal prevents replayed audiovisual effects.</summary>
public readonly record struct CombatEvent(
    ulong EventOrdinal,
    CombatEventKind Kind,
    ulong SourceEntityId,
    ulong TargetEntityId,
    int Amount,
    CombatStatusEffect StatusEffect,
    float X,
    float Y,
    short WorldLevel,
    ulong RelatedEntityId);

public sealed record CombatEventBatchMessage(
    ulong Sequence, ulong Tick, IReadOnlyList<CombatEvent> Events) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.CombatEventBatch;
}

public sealed record CombatActionResultMessage(
    ulong Sequence,
    ulong Tick,
    Guid CommandId,
    CombatActionKind Action,
    CombatEnemyReference Enemy,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail,
    uint ActorRevision,
    uint InventoryRevision,
    uint EnemyRevision) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.CombatActionResult;
}
