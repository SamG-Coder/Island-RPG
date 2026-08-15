using System.Collections.Immutable;
using System.Numerics;

namespace IslandRpg.Simulation;

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
    uint ExpectedInventoryRevision,
    string? PayloadFingerprint = null);

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
    NoDemolitionRefund,
    NotCookable,
    CookingLocked,
    AlreadyCooking,
    InvalidExcavation,
    MissingExcavationTool,
    ExcavationCadenceLocked,
    InvalidCaveLink,
    NotCrop,
    CropNotReady,
    NotPlantedTree,
    PlantLimitReached,
    TreeAlreadyFelled
}

public enum WorldObjectChangeKind
{
    Added,
    Updated,
    Removed
}

/// <summary>
/// Transport-neutral gate state. None distinguishes non-gate objects from an
/// unlocked gate without exposing the Core-only gameplay enum.
/// </summary>
public enum WorldGateAccessState : byte
{
    None,
    Unlocked,
    Opened,
    Locked
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
    double LitUntilGameSeconds,
    int FiremakingLevel = 1,
    WorldGateAccessState GateState = WorldGateAccessState.None,
    Guid? LinkedObjectId = null);

public readonly record struct WorldContainerSlotSnapshot(
    int Slot,
    string? ItemId,
    int Quantity,
    string? OwnerId);

public sealed record WorldContainerSnapshot(
    Guid ObjectId,
    WorldChunkKey Chunk,
    uint ChunkRevision,
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
    string Detail = "",
    WorldActorTransition? ActorTransition = null,
    CaveActionOutcome? CaveOutcome = null)
{
    public bool Accepted => Status == WorldTransactionStatus.Accepted;
}

public readonly record struct WorldActorTransition(
    Vector2 Position,
    int WorldLevel);

public readonly record struct CaveActionOutcome(
    int Damage,
    bool Completed);

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
    WorldGateAccessState GateState = WorldGateAccessState.None,
    IReadOnlyList<(string ItemId, int Quantity, string? OwnerId)>?
        ContainerItems = null,
    Guid? LinkedObjectId = null);

public readonly record struct AuthoritativeChunkRevisionSnapshot(
    WorldChunkKey Chunk,
    uint Revision);

public sealed record AuthoritativeWorldObjectCheckpoint(
    AuthoritativeWorldObjectSnapshot Object,
    WorldContainerSnapshot? Container);

/// <summary>
/// Complete committed world-object state. Command replay caches are transient;
/// stable objects, exact revisions and private container slots are durable.
/// </summary>
public sealed record AuthoritativeWorldTransactionsCheckpoint(
    ImmutableArray<AuthoritativeWorldObjectCheckpoint> Objects,
    ImmutableArray<AuthoritativeChunkRevisionSnapshot> ChunkRevisions,
    ImmutableArray<AuthoritativeExcavationCadenceCheckpoint>
        ExcavationCadences = default,
    ImmutableArray<Guid> PickedProceduralGroundObjects = default);

public readonly record struct AuthoritativeExcavationCadenceCheckpoint(
    ActorId ActorId,
    Guid ExcavationId,
    double NextAllowedGameSeconds);

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

public sealed record PlaceInventoryWorldObjectTransaction(
    WorldTransactionContext Context,
    string DefinitionId,
    int InventorySlot,
    Vector2 Position,
    int WorldLevel,
    int Rotation,
    uint ExpectedChunkRevision);

public sealed record PlantCropTransaction(
    WorldTransactionContext Context,
    Guid CropObjectId,
    int SeedInventorySlot,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision,
    double GameSeconds);

public sealed record HarvestCropTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Crop,
    double GameSeconds);

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

public sealed record BeginCampfireCookingTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Campfire,
    int InventorySlot,
    double GameSeconds);

public sealed record CookStewTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Pot,
    double GameSeconds);

public sealed record StrikeTrainingDummyTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Dummy,
    long WorldSeed,
    ulong AttackSequence);

public sealed record PlantTreeTransaction(
    WorldTransactionContext Context,
    Guid TreeObjectId,
    int SeedInventorySlot,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision,
    double GameSeconds,
    string PlanterDisplayName);

public sealed record StrikePlantedTreeTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Tree,
    int ToolInventorySlot,
    double GameSeconds,
    double WorldGameSeconds,
    long WorldSeed,
    ulong StrikeSequence);

public sealed record CompleteCampfireCookingTransaction(
    Guid OperationId,
    Guid CampfireId,
    WorldChunkKey CampfireChunk,
    Vector2 CampfirePosition,
    int PreferredInventorySlot,
    string RawItemId,
    string ResultItemId,
    int Experience,
    bool Burnt,
    Guid DropObjectId,
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

public sealed record StartExcavationTransaction(
    WorldTransactionContext Context,
    Vector2 Position,
    int WorldLevel,
    int ShovelInventorySlot,
    uint ExpectedChunkRevision,
    double GameSeconds);

public sealed record WorkExcavationTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Excavation,
    int ShovelInventorySlot,
    double GameSeconds);

public sealed record RestoreExcavationTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Excavation);

public sealed record InstallCaveRopeTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Shaft,
    int RopeInventorySlot);

public sealed record TakeCaveRopeTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Entrance);

public sealed record FillExcavationTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Excavation,
    int MaterialInventorySlot);

public sealed record TraverseCaveTransaction(
    WorldTransactionContext Context,
    WorldObjectHandle Entrance);
