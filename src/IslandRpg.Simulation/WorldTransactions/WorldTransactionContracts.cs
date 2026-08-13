using System.Collections.Immutable;
using System.Numerics;

namespace IslandRpg.Simulation;

public readonly record struct WorldChunkKey(int X, int Y, int WorldLevel)
{
    public const int Size = 32;

    public static WorldChunkKey At(Vector2 position, int worldLevel) => new(
        FloorDiv((int)MathF.Floor(position.X), Size),
        FloorDiv((int)MathF.Floor(position.Y), Size),
        worldLevel);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

public readonly record struct WorldObjectHandle(
    Guid ObjectId,
    WorldChunkKey Chunk,
    uint ExpectedObjectRevision,
    uint ExpectedChunkRevision,
    uint ExpectedContainerRevision = 0);

public readonly record struct WorldTransactionContext(
    Guid CommandId,
    ActorId ActorId,
    uint ExpectedActorRevision,
    uint ExpectedInventoryRevision);

public enum WorldTransactionStatus
{
    Accepted,
    InvalidCommand,
    CommandIdConflict,
    ActorNotFound,
    DeadActor,
    StaleActorRevision,
    StaleInventoryRevision,
    ObjectNotFound,
    ObjectLocationMismatch,
    StaleObjectRevision,
    StaleChunkRevision,
    StaleContainerRevision,
    WrongWorldLevel,
    OutOfRange,
    AccessDenied,
    InvalidItem,
    InvalidQuantity,
    InvalidInventorySlot,
    ItemUnavailable,
    InventoryFull,
    NotPortable,
    NotContainer,
    ContainerFull,
    ContainerItemUnavailable,
    ContainerDepositDenied,
    NotCampfire,
    InvalidCampfireState,
    CampfireLightingRequirementsMissing,
    InvalidConstruction,
    MissingConstructionResources,
    ConstructionLocked,
    InvalidPlacement,
    NotConstructionSite,
    NoDemolitionRefund
}

public enum WorldObjectChangeKind
{
    Added,
    Updated,
    Removed
}

public readonly record struct WorldInventorySlotSnapshot(
    int Slot,
    string? ItemId,
    int Quantity,
    string? OwnerId);

public sealed record WorldTransactionActorInput(
    ActorId ActorId,
    Vector2 Position,
    int WorldLevel,
    PlayerGameplaySnapshot Gameplay,
    int FiremakingLevel = 1,
    float Energy = 100,
    string? GroupId = null);

public sealed record AuthoritativeWorldObjectSnapshot(
    Guid ObjectId,
    string DefinitionId,
    Vector2 Position,
    WorldChunkKey Chunk,
    uint ObjectRevision,
    uint ContainerRevision,
    int Rotation,
    int Health,
    int MaximumHealth,
    string? OwnerId,
    string? GroupOwnerId,
    bool HasContainer,
    string? FuelItemId,
    double LitUntilGameSeconds);

public readonly record struct WorldContainerSlotSnapshot(
    int Slot,
    string? ItemId,
    int Quantity,
    string? OwnerId);

public sealed record WorldContainerSnapshot(
    Guid ObjectId,
    uint ObjectRevision,
    uint ContainerRevision,
    string DefinitionId,
    bool AllowsDeposit,
    ImmutableArray<WorldContainerSlotSnapshot> Slots);

public sealed record WorldObjectTransactionDelta(
    WorldObjectChangeKind Kind,
    Guid ObjectId,
    WorldChunkKey Chunk,
    uint PreviousObjectRevision,
    uint CurrentObjectRevision,
    AuthoritativeWorldObjectSnapshot? Object);

public readonly record struct WorldChunkRevisionDelta(
    WorldChunkKey Chunk,
    uint PreviousRevision,
    uint CurrentRevision);

public sealed record WorldTransactionResult(
    Guid CommandId,
    WorldTransactionStatus Status,
    uint ActorRevision,
    uint InventoryRevision,
    ImmutableArray<WorldObjectTransactionDelta> ObjectDeltas,
    ImmutableArray<WorldChunkRevisionDelta> ChunkDeltas,
    PlayerGameplaySnapshot? Gameplay,
    WorldContainerSnapshot? Container,
    string Detail = "")
{
    public bool Accepted => Status == WorldTransactionStatus.Accepted;
}

public sealed record WorldObjectSeed(
    Guid ObjectId,
    string DefinitionId,
    Vector2 Position,
    int WorldLevel = 0,
    uint ObjectRevision = 1,
    uint ContainerRevision = 1,
    string? FuelItemId = null,
    double LitUntilGameSeconds = 0,
    int FiremakingLevel = 1,
    int Health = 0,
    int MaximumHealth = 0,
    string? OwnerId = null,
    string? GroupOwnerId = null,
    int Rotation = -1,
    IReadOnlyList<(string ItemId, int Quantity, string? OwnerId)>?
        ContainerItems = null);

public sealed record PickUpWorldObjectTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Object);

public sealed record DropInventoryItemTransaction(
    WorldTransactionContext Context,
    int InventorySlot,
    int Quantity,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision);

public sealed record OpenWorldContainerTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Container);

public enum WorldContainerTransferDirection
{
    Deposit,
    Withdraw
}

public sealed record TransferWorldContainerTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Container,
    WorldContainerTransferDirection Direction,
    int InventorySlot,
    int ContainerSlot,
    int Quantity);

public sealed record AddCampfireFuelTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Campfire,
    int InventorySlot,
    double GameSeconds);

public sealed record TakeCampfireFuelTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Campfire,
    double GameSeconds);

public sealed record LightCampfireTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Campfire,
    double GameSeconds);

public sealed record PlaceConstructionTransaction(
    WorldTransactionContext Context,
    string DefinitionId,
    Vector2 Position,
    int WorldLevel,
    int Rotation,
    uint ExpectedChunkRevision);

public sealed record BuildConstructionTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Construction);

public sealed record DemolishWorldObjectTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Object);
