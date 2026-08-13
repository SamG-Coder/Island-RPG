using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;

namespace IslandRpg.Simulation;

public readonly record struct ActorSnapshot(
    ActorId ActorId,
    PlayerId PlayerId,
    string DisplayName,
    Vector2 Position,
    Vector2 Velocity,
    Vector2? Destination,
    int WorldLevel,
    bool Connected,
    long LastProcessedCommandSequence,
    long? DisconnectedAtTick)
{
    /// <summary>
    /// Immutable authoritative gameplay state belonging to this actor's player.
    /// </summary>
    public PlayerGameplaySnapshot Gameplay { get; init; }

    public uint ActorRevision => Gameplay.ActorRevision;

    public uint InventoryRevision => Gameplay.Inventory.Revision;

    public BoatId? BoardedBoatId { get; init; }
}

public readonly record struct InventorySlotSnapshot(
    int Slot,
    string? ItemId,
    int Quantity);

public readonly record struct PlayerInventorySnapshot(
    uint Revision,
    ImmutableArray<InventorySlotSnapshot> Slots)
{
    public int Capacity => Slots.Length;
}

public readonly record struct PlayerGameplaySnapshot(
    uint ActorRevision,
    int Health,
    float Hunger,
    float WellFedSeconds,
    int CraftingExperience,
    int CookingExperience,
    PlayerInventorySnapshot Inventory,
    int WoodcuttingExperience = 0,
    int FarmingExperience = 0,
    int MiningExperience = 0,
    int AdventureExperience = 0,
    int DiggingExperience = 0,
    int FishingExperience = 0,
    int MaximumHealth = 100,
    int AttackExperience = 0,
    int StrengthExperience = 0,
    int DefenceExperience = 0,
    MeleeCombatStance CombatStance = MeleeCombatStance.Accurate,
    ActorLifeState LifeState = ActorLifeState.Alive,
    long RespawnAvailableTick = 0,
    SlimeVictimStatus CombatStatus = default,
    EnemyId? CombatTargetEnemyId = null,
    ulong CombatAttackSequence = 0,
    long NextCombatAttackTick = 0)
{
    public CombatStatusFlags StatusFlags(double now)
    {
        var flags = CombatStatusFlags.None;
        if (now < CombatStatus.SlowedUntil) flags |= CombatStatusFlags.Slowed;
        if (now < CombatStatus.RootedUntil) flags |= CombatStatusFlags.Rooted;
        if (now < CombatStatus.PoisonedUntil) flags |= CombatStatusFlags.Poisoned;
        return flags;
    }
}

public readonly record struct ChatMessageSnapshot(
    long MessageId,
    long Tick,
    PlayerId SenderPlayerId,
    ActorId SenderActorId,
    string SenderDisplayName,
    string Message);

/// <summary>
/// Deeply immutable state suitable for concurrent network serialization.
/// </summary>
public sealed record SessionSnapshot(
    SessionId SessionId,
    long Sequence,
    SimulationClockSnapshot Clock,
    ImmutableArray<ActorSnapshot> Actors,
    ImmutableArray<ChatMessageSnapshot> ChatHistory,
    ImmutableArray<AuthoritativeBoatSnapshot> Boats = default,
    ImmutableArray<AuthoritativeEnemySnapshot> Enemies = default,
    ImmutableArray<CombatEventSnapshot> CombatEvents = default)
{
    public static SessionSnapshot Empty(SessionId sessionId) => new(
        sessionId,
        0,
        default,
        ImmutableArray<ActorSnapshot>.Empty,
        ImmutableArray<ChatMessageSnapshot>.Empty,
        ImmutableArray<AuthoritativeBoatSnapshot>.Empty,
        ImmutableArray<AuthoritativeEnemySnapshot>.Empty,
        ImmutableArray<CombatEventSnapshot>.Empty);
}

public readonly record struct SessionTickResult(
    int CommandsProcessed,
    SessionSnapshot? PublishedSnapshot)
{
    public bool SnapshotPublished => PublishedSnapshot is not null;
}
