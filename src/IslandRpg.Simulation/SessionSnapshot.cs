using System.Collections.Immutable;
using System.Numerics;

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
    int AdventureExperience = 0);

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
    ImmutableArray<ChatMessageSnapshot> ChatHistory)
{
    public static SessionSnapshot Empty(SessionId sessionId) => new(
        sessionId,
        0,
        default,
        ImmutableArray<ActorSnapshot>.Empty,
        ImmutableArray<ChatMessageSnapshot>.Empty);
}

public readonly record struct SessionTickResult(
    int CommandsProcessed,
    SessionSnapshot? PublishedSnapshot)
{
    public bool SnapshotPublished => PublishedSnapshot is not null;
}
