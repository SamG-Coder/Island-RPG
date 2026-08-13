using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;

namespace IslandRpg.Simulation;

public readonly record struct EnemyId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("N");
}

public readonly record struct EnemyReference(
    EnemyId EnemyId,
    uint ExpectedRevision)
{
    public bool IsWellFormed => !EnemyId.IsEmpty && ExpectedRevision > 0;
}

public enum ActorLifeState : byte
{
    Alive = 0,
    Dead = 1
}

[Flags]
public enum CombatStatusFlags : byte
{
    None = 0,
    Slowed = 1 << 0,
    Rooted = 1 << 1,
    Poisoned = 1 << 2,
    Hidden = 1 << 3,
    Burrowed = 1 << 4
}

public sealed record AuthoritativeEnemySnapshot(
    EnemyId EnemyId,
    ulong NetworkEntityId,
    uint Revision,
    EnemyKind Kind,
    EnemyBehavior Behavior,
    Vector2 SpawnPosition,
    Vector2 Position,
    Vector2 Velocity,
    int WorldLevel,
    int PowerLevel,
    int Health,
    int MaximumHealth,
    float SizeScale,
    CombatStatusFlags StatusFlags,
    ActorId? TargetActorId,
    ulong TargetNetworkEntityId,
    EnemyId? ParentEnemyId,
    uint SpawnOrdinal,
    ulong AttackSequence,
    long NextAttackTick,
    int SplitGeneration,
    long DeathRemovalTick,
    long ReactionReadyTick,
    long BurrowEmergeTick)
{
    public bool Alive => Health > 0 && Behavior != EnemyBehavior.Dead;
}

public enum EnemyChangeKind : byte
{
    Added = 1,
    Updated = 2,
    Removed = 3
}

public sealed record EnemyStateDelta(
    EnemyChangeKind Kind,
    AuthoritativeEnemySnapshot? Previous,
    AuthoritativeEnemySnapshot? Current);

public enum CombatEventKind : byte
{
    PlayerAttacked = 1,
    EnemyAttacked = 2,
    StatusApplied = 3,
    ActorDied = 4,
    ActorRespawned = 5,
    EnemyDied = 6,
    EnemySplit = 7,
    LootRolled = 8,
    TargetCancelled = 9,
    StatusExpired = 10
}

public sealed record CombatLootSnapshot(string ItemId, int Quantity);

public sealed record CombatEventSnapshot(
    ulong EventOrdinal,
    long Tick,
    CombatEventKind Kind,
    ActorId? ActorId,
    EnemyId? EnemyId,
    int Damage = 0,
    bool Hit = false,
    SlimeStatusKind Status = SlimeStatusKind.None,
    ImmutableArray<CombatLootSnapshot> Loot = default,
    ImmutableArray<EnemyId> SpawnedEnemyIds = default);

public sealed record AuthoritativeEnemySeed(
    EnemyId EnemyId,
    EnemyKind Kind,
    Vector2 Position,
    int WorldLevel = 0,
    int PowerLevel = 1,
    int Health = 0,
    int MaximumHealth = 0,
    Vector2? SpawnPosition = null,
    uint Revision = 1,
    EnemyId? ParentEnemyId = null,
    uint SpawnOrdinal = 0,
    int SplitGeneration = 0);

public sealed record CombatActorInput(
    ActorId ActorId,
    ulong NetworkEntityId,
    Vector2 Position,
    int WorldLevel,
    bool Connected,
    PlayerGameplaySnapshot Gameplay);

public sealed record CombatActorMutation(
    ActorId ActorId,
    PlayerGameplaySnapshot Gameplay,
    Vector2? Position = null,
    int? WorldLevel = null,
    bool ClearMovement = false);

public sealed record CombatLootDropRequest(
    Guid ObjectId,
    EnemyId SourceEnemyId,
    ActorId? OwnerActorId,
    Vector2 Position,
    int WorldLevel,
    ImmutableArray<CombatLootSnapshot> Items);

public sealed record CombatAdvanceResult(
    ImmutableArray<EnemyStateDelta> EnemyDeltas,
    ImmutableArray<CombatEventSnapshot> Events,
    ImmutableArray<CombatActorMutation> ActorMutations,
    ImmutableArray<CombatLootDropRequest> LootDrops)
{
    public static CombatAdvanceResult Empty { get; } = new([], [], [], []);
}

public enum CombatTransactionStatus : byte
{
    Accepted = 0,
    InvalidCommand,
    ActorNotFound,
    DeadActor,
    ActorAlive,
    StaleActorRevision,
    StaleInventoryRevision,
    EnemyNotFound,
    EnemyDead,
    StaleEnemyRevision,
    WrongWorldLevel,
    InvalidStance,
    RespawnLocked
}

public sealed record CombatTransactionResult(
    Guid CommandId,
    CombatTransactionStatus Status,
    PlayerGameplaySnapshot Gameplay,
    EnemyStateDelta? EnemyDelta = null,
    CombatEventSnapshot? Event = null,
    string Detail = "")
{
    public bool Accepted => Status == CombatTransactionStatus.Accepted;
}

public sealed record AuthoritativeEnemyCheckpoint(
    EnemyId EnemyId,
    uint Revision,
    EnemyKind Kind,
    EnemyBehavior Behavior,
    Vector2 SpawnPosition,
    Vector2 Position,
    Vector2 Velocity,
    int WorldLevel,
    int PowerLevel,
    int Health,
    int MaximumHealth,
    float SizeScale,
    SlimeVictimStatus Status,
    ActorId? TargetActorId,
    EnemyId? ParentEnemyId,
    uint SpawnOrdinal,
    ulong AttackSequence,
    long NextAttackTick,
    int SplitGeneration,
    long DeathRemovalTick,
    long ReactionReadyTick,
    long BurrowEmergeTick);

public sealed record AuthoritativeCombatCheckpoint(
    long WorldSeed,
    ulong NextEventOrdinal,
    uint NextSpawnOrdinal,
    ImmutableArray<AuthoritativeEnemyCheckpoint> Enemies)
{
    public static AuthoritativeCombatCheckpoint Empty(long worldSeed) =>
        new(worldSeed, 1, 1, []);
}

public sealed record AuthoritativeCombatOptions
{
    public int MaximumEnemies { get; init; } =
        CombatPopulationLimits.MaximumEnemies;

    public float AggroRange { get; init; } = 7f;

    public float LeashRange { get; init; } = 12f;

    public float PlayerAttackRange { get; init; } = .82f;

    public float EnemyAttackRange { get; init; } = SlimeCombatRules.AttackRange;

    public int PlayerAttackIntervalTicks { get; init; } = 144;

    public int EnemyAttackIntervalTicks { get; init; } = 120;

    public int RespawnDelayTicks { get; init; } = 300;

    public int DeathRetentionTicks { get; init; } = 90;

    public float PlayerChaseSpeed { get; init; } =
        ActorMovementService.BaseMoveSpeed;

    public Vector2 RespawnPosition { get; init; } = Vector2.Zero;

    internal AuthoritativeCombatOptions ValidatedCopy()
    {
        if (MaximumEnemies is <= 0 or > CombatPopulationLimits.MaximumEnemies ||
            !float.IsFinite(AggroRange) || AggroRange <= 0 ||
            !float.IsFinite(LeashRange) || LeashRange < AggroRange ||
            !float.IsFinite(PlayerAttackRange) || PlayerAttackRange <= 0 ||
            !float.IsFinite(EnemyAttackRange) || EnemyAttackRange <= 0 ||
            PlayerAttackIntervalTicks <= 0 || EnemyAttackIntervalTicks <= 0 ||
            RespawnDelayTicks < 0 || DeathRetentionTicks <= 0 ||
            !float.IsFinite(PlayerChaseSpeed) || PlayerChaseSpeed <= 0 ||
            !float.IsFinite(RespawnPosition.X) ||
            !float.IsFinite(RespawnPosition.Y))
            throw new ArgumentOutOfRangeException(nameof(MaximumEnemies));
        return this with { };
    }
}
