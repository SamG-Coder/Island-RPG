using System.Numerics;

namespace IslandRpg.Gameplay;

/// <summary>
/// Cross-layer population invariant. One authoritative enemy baseline must
/// always fit in one bounded reliable protocol message and one checkpoint.
/// </summary>
public static class CombatPopulationLimits
{
    public const int MaximumEnemies = 512;
}

/// <summary>Stable enemy archetypes shared by simulation and presentation.</summary>
public enum EnemyKind : byte
{
    WaterSlime,
    GrassSlime,
    SandSlime,
    CaveSlime
}

/// <summary>Authoritative high-level enemy activity.</summary>
public enum EnemyBehavior : byte
{
    Idle,
    Roam,
    Chase,
    Return,
    Attack,
    Dead
}

public enum SlimeStatusKind : byte
{
    None,
    Slow,
    Root,
    Poison
}

public readonly record struct SlimeAttackAbility(
    SlimeStatusKind Status,
    double DurationSeconds,
    float MovementMultiplier = 1,
    int PoisonDamage = 0,
    double PoisonIntervalSeconds = 1);

/// <summary>
/// Absolute simulation-clock deadlines for effects applied by slimes. Absolute
/// times make this value straightforward to checkpoint and reconnect safely.
/// </summary>
public readonly record struct SlimeVictimStatus(
    double SlowedUntil = 0,
    double RootedUntil = 0,
    double PoisonedUntil = 0,
    double NextPoisonTickAt = 0,
    int PoisonDamage = 0)
{
    public float MovementMultiplier(double now) =>
        now < RootedUntil ? 0 : now < SlowedUntil ? .58f : 1;
}

public readonly record struct SlimeStatusAdvance(
    SlimeVictimStatus Status,
    int PoisonDamage,
    int PoisonTicks = 0,
    bool SlowExpired = false,
    bool RootExpired = false,
    bool PoisonExpired = false);

/// <summary>Immutable input used to derive smaller slimes after a death.</summary>
public readonly record struct SlimeSplitSource(
    Guid EnemyId,
    Guid SpawnerId,
    EnemyKind Kind,
    Vector2 SpawnPosition,
    Vector2 Position,
    int WorldLevel,
    int PowerLevel,
    int MaximumHealth,
    float SizeScale,
    int SplitGeneration);

/// <summary>
/// Complete deterministic child data. The authority can persist this without
/// asking the renderer-owned legacy enemy model to manufacture identifiers.
/// </summary>
public readonly record struct SlimeSplitChild(
    Guid EnemyId,
    Guid SpawnerId,
    EnemyKind Kind,
    Vector2 SpawnPosition,
    Vector2 Position,
    int WorldLevel,
    int PowerLevel,
    int Health,
    float SizeScale,
    int SplitGeneration);

public readonly record struct SlimeLootSource(
    long WorldSeed,
    Guid EnemyId,
    EnemyKind Kind,
    int PowerLevel);

public readonly record struct SlimeLootDrop(
    string ItemId,
    int Quantity);
