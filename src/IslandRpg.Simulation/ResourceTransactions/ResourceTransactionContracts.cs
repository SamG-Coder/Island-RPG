using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.Fishing;

namespace IslandRpg.Simulation;

public enum ResourceTransactionStatus
{
    Accepted,
    InvalidCommand,
    ActorNotFound,
    DeadActor,
    StaleActorRevision,
    StaleInventoryRevision,
    ResourceNotFound,
    WrongResourceKind,
    StaleNodeRevision,
    StaleResourceChunkRevision,
    WrongWorldLevel,
    OutOfRange,
    InventoryFull,
    MissingTool,
    CadenceLocked,
    Depleted
}

public sealed record ResourceNodeTransactionDelta(
    ResourceNodeSparseState Previous,
    ResourceNodeSparseState Current);

public readonly record struct ResourceChunkRevisionDelta(
    WorldChunkKey Chunk,
    uint PreviousRevision,
    uint CurrentRevision);

public readonly record struct ResourceItemReward(
    string ItemId,
    int Quantity);

/// <summary>
/// Immutable result of one authoritative resource interaction. The actor
/// snapshot is requester-private; resource deltas are safe to replicate.
/// </summary>
public sealed record ResourceTransactionResult(
    Guid CommandId,
    ResourceTransactionStatus Status,
    uint ActorRevision,
    uint InventoryRevision,
    PlayerGameplaySnapshot? Gameplay,
    ResourceNodeTransactionDelta? NodeDelta,
    ResourceChunkRevisionDelta? ChunkDelta,
    ImmutableArray<ResourceItemReward> Rewards = default,
    bool Hit = false,
    int Damage = 0,
    bool ToolWorn = false,
    string Detail = "",
    FishingTransactionOutcome? FishingOutcome = null)
{
    public bool Accepted => Status == ResourceTransactionStatus.Accepted;

    public int RewardQuantity(string itemId) =>
        Rewards.IsDefaultOrEmpty
            ? 0
            : Rewards.Where(value => string.Equals(
                    value.ItemId, itemId,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(static value => value.Quantity);
}

public readonly record struct FishingTransactionOutcome(
    FishSpecies Species,
    bool Caught,
    float CatchChance);

public sealed record GatherTreeStickTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

public sealed record StrikeTreeTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    int ToolInventorySlot,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

public sealed record GatherFibreTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

public sealed record GatherBerriesTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    int ToolInventorySlot,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

public sealed record MineResourceTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    int ToolInventorySlot,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

public sealed record CatchFishTransaction(
    WorldTransactionContext Context,
    ResourceNodeReference Node,
    int FishingNetInventorySlot,
    float MaximumReach,
    double GameSeconds,
    double WorldGameSeconds = double.NaN);

// ReadyAtGameSeconds is retained for checkpoint/source compatibility. Resource
// action cadence has always been persisted in elapsed real seconds; renewable
// node ReadyAtGameSeconds values use accelerated world time instead.
public sealed record ResourceActorCadenceCheckpoint(
    ActorId ActorId,
    ResourceActionKind Action,
    double ReadyAtGameSeconds,
    ulong ActionOrdinal);

/// <summary>
/// Durable sparse overlay. Procedural defaults are regenerated from the world
/// seed; only mutated nodes, non-zero chunk revisions and action sequencing
/// needed for deterministic future rolls are stored.
/// </summary>
public sealed record AuthoritativeResourceTransactionsCheckpoint(
    ImmutableArray<ResourceChunkSparseState> Chunks,
    ImmutableArray<ResourceActorCadenceCheckpoint> ActorCadences)
{
    public static AuthoritativeResourceTransactionsCheckpoint Empty { get; } =
        new([], []);
}

public sealed record AuthoritativeResourceTransactionOptions
{
    public float InteractionRange { get; init; } = 3f;

    public ResourceActionCadence GatherTreeStickCadence { get; init; } =
        new(.75);

    public ResourceActionCadence StrikeTreeCadence { get; init; } =
        new(1.05);

    public ResourceActionCadence GatherFibreCadence { get; init; } =
        new(.75);

    public ResourceActionCadence GatherBerriesCadence { get; init; } =
        new(.75);

    public ResourceActionCadence MineCadence { get; init; } =
        new(1.05);

    public ResourceActionCadence FishCadence { get; init; } =
        new(2.8);

    internal AuthoritativeResourceTransactionOptions ValidatedCopy()
    {
        if (!float.IsFinite(InteractionRange) || InteractionRange <= 0)
            throw new ArgumentOutOfRangeException(nameof(InteractionRange));
        _ = GatherTreeStickCadence.NextReadyAt(0);
        _ = StrikeTreeCadence.NextReadyAt(0);
        _ = GatherFibreCadence.NextReadyAt(0);
        _ = GatherBerriesCadence.NextReadyAt(0);
        _ = MineCadence.NextReadyAt(0);
        _ = FishCadence.NextReadyAt(0);
        return this with { };
    }
}
