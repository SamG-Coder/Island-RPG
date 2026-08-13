using System.Numerics;
using IslandRpg.Resources;

namespace IslandRpg.Simulation;

public readonly record struct JoinRequest(
    ClientConnectionId ConnectionId,
    string DisplayName,
    Vector2 SpawnPosition,
    IReadOnlyList<InitialInventoryItem>? InitialInventory = null,
    float InitialHunger = 100f,
    int SpawnWorldLevel = 0,
    bool ProvisionBoat = false);

/// <summary>
/// Server-authored join inventory, supplied only by the trusted host. Network
/// clients never choose these values.
/// </summary>
public readonly record struct InitialInventoryItem(
    string ItemId,
    int Quantity = 1);

public readonly record struct ReconnectRequest(
    ClientConnectionId ConnectionId,
    PlayerId PlayerId,
    ReconnectToken ReconnectToken);

public readonly record struct DisconnectRequest(
    ClientConnectionId ConnectionId,
    PlayerId PlayerId);

/// <summary>
/// Base type for every command payload accepted by the authoritative session.
/// </summary>
public abstract record SessionIntent;

/// <summary>
/// Compatibility base for the existing movement and chat payloads.
/// </summary>
public abstract record ActorIntent : SessionIntent;

public sealed record WalkIntent(
    Vector2 Destination,
    int WorldLevel = (int)IslandRpg.Navigation.NavigationWorldLevel.Overworld) :
    ActorIntent;

public sealed record StopIntent : ActorIntent
{
    public static StopIntent Instance { get; } = new();

    private StopIntent()
    {
    }
}

public sealed record ChatIntent(string Message) : ActorIntent;

/// <summary>
/// Base for revision-checked, idempotent gameplay commands.
/// </summary>
public abstract record GameplayIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision) : SessionIntent;

public sealed record SwapInventorySlotsIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int SourceSlot,
    int TargetSlot) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record CombineInventorySlotsIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int FirstSlot,
    int SecondSlot) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record CraftRecipeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    string RecipeId) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record ConsumeFoodIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int Slot) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

/// <summary>
/// Base for revision-checked world mutations. These transport-independent
/// commands are translated into atomic transactions by the session owner.
/// </summary>
public abstract record WorldGameplayIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public abstract record ResourceGameplayIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record GatherTreeStickIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public sealed record StrikeTreeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node,
    int ToolInventorySlot) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public sealed record GatherFibreIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public sealed record GatherBerriesIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node,
    int ToolInventorySlot) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public sealed record MineResourceIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node,
    int ToolInventorySlot) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public sealed record CatchFishIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    ResourceNodeReference Node,
    int FishingNetInventorySlot) : ResourceGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision,
        Node);

public abstract record BoatGameplayIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    BoatReference Boat) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record BoardBoatIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    BoatReference Boat) : BoatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision, Boat);

public sealed record MoveBoatIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    BoatReference Boat,
    Vector2 Target) : BoatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision, Boat);

public sealed record StopBoatIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    BoatReference Boat) : BoatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision, Boat);

public sealed record DisembarkBoatIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    BoatReference Boat,
    Vector2 RequestedLanding) : BoatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision, Boat);

public sealed record PickUpWorldObjectIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Object) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record DropInventoryItemIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int InventorySlot,
    int Quantity,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record OpenWorldContainerIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Container) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record TransferWorldContainerIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Container,
    WorldContainerTransferDirection Direction,
    int InventorySlot,
    int ContainerSlot,
    int Quantity) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record AddCampfireFuelIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Campfire,
    int InventorySlot) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record TakeCampfireFuelIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Campfire) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record LightCampfireIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Campfire) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record CookOnCampfireIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Campfire,
    int InventorySlot) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record PlaceConstructionIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    string DefinitionId,
    Vector2 Position,
    int WorldLevel,
    int Rotation,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record BuildConstructionIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Construction) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record DemolishWorldObjectIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Object) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record StartExcavationIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    Vector2 Position,
    int WorldLevel,
    int ShovelInventorySlot,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record WorkExcavationIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Excavation,
    int ShovelInventorySlot) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record RestoreExcavationIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Excavation) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record InstallCaveRopeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Shaft,
    int RopeInventorySlot) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record TakeCaveRopeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Entrance) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record FillExcavationIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Excavation,
    int MaterialInventorySlot) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record TraverseCaveIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Entrance) : WorldGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public readonly record struct ActorCommand(
    ClientConnectionId ConnectionId,
    PlayerId PlayerId,
    long Sequence,
    SessionIntent Intent);

public enum JoinStatus
{
    Accepted,
    QueueFull,
    InvalidRequest,
    ConnectionAlreadyJoined,
    SessionFull
}

public readonly record struct JoinResult(
    JoinStatus Status,
    PlayerIdentity Identity,
    ReconnectToken ReconnectToken,
    long NextCommandSequence,
    string? Error)
{
    public bool Accepted => Status == JoinStatus.Accepted;

    public PlayerGameplaySnapshot Gameplay { get; init; }

    public Vector2 Position { get; init; }

    public int WorldLevel { get; init; }

    /// <summary>
    /// Trusted server-provisioned island-start boat. It is created in the
    /// same owner-thread transaction as the actor so a failed provision
    /// cannot leave a durable player behind.
    /// </summary>
    public AuthoritativeBoatSnapshot? Boat { get; init; }
}

public enum ReconnectStatus
{
    Accepted,
    QueueFull,
    InvalidRequest,
    UnknownPlayer,
    InvalidToken,
    AlreadyConnected,
    ConnectionAlreadyJoined
}

public readonly record struct ReconnectResult(
    ReconnectStatus Status,
    PlayerIdentity Identity,
    long NextCommandSequence,
    string? Error)
{
    public bool Accepted => Status == ReconnectStatus.Accepted;

    public PlayerGameplaySnapshot Gameplay { get; init; }

    public Vector2 Position { get; init; }

    public int WorldLevel { get; init; }
}

public enum DisconnectStatus
{
    Accepted,
    QueueFull,
    UnknownPlayer,
    InvalidConnection,
    AlreadyDisconnected
}

public readonly record struct DisconnectResult(
    DisconnectStatus Status,
    string? Error)
{
    public bool Accepted => Status == DisconnectStatus.Accepted;

    /// <summary>
    /// Public semantic state produced while disconnecting the actor. The
    /// server broadcasts this only after it has completed requester-private
    /// disconnect handling, preserving reliable message ordering.
    /// </summary>
    public BoatStateDelta? BoatDelta { get; init; }
}

public enum IntentStatus
{
    Accepted,
    QueueFull,
    UnknownPlayer,
    InvalidConnection,
    Disconnected,
    InvalidSequence,
    StaleSequence,
    InvalidIntent,
    InvalidDestination,
    DestinationTooFar,
    WorldLevelMismatch,
    PathUnreachable,
    InvalidChat,
    InvalidCommandId,
    CommandIdConflict,
    StaleInventoryRevision,
    StaleActorRevision,
    InvalidInventorySlot,
    EmptyInventorySlot,
    NoMatchingRecipe,
    UnknownRecipe,
    CraftingLocked,
    MissingResources,
    MissingStation,
    InventoryFull,
    ItemNotConsumable,
    AlreadyFull,
    WorldCommandInvalid,
    ActorNotFound,
    DeadActor,
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
    ItemUnavailable,
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
    AlreadyCooking,
    NotCookable,
    CookingLocked,
    ResourceNotFound,
    WrongResourceKind,
    StaleNodeRevision,
    StaleResourceChunkRevision,
    MissingTool,
    ResourceCadenceLocked,
    ResourceDepleted,
    InvalidExcavation,
    MissingExcavationTool,
    ExcavationCadenceLocked,
    InvalidCaveLink,
    BoatNotFound,
    StaleBoatRevision,
    AlreadyAboard,
    BoatOccupied,
    NotAboard,
    InvalidBoatDestination,
    BoatDestinationTooFar,
    BoatRouteUnreachable,
    InvalidBoatLanding,
    BoatPlanningLocked
}

public readonly record struct CookingCompletionSnapshot(
    Guid CommandId,
    PlayerId PlayerId,
    string RawItemId,
    string ResultItemId,
    bool Burnt,
    bool Interrupted,
    uint ActorRevision,
    uint InventoryRevision)
{
    public PlayerGameplaySnapshot Gameplay { get; init; }

    public required WorldTransactionResult Transaction { get; init; }
}

public readonly record struct IntentResult(
    IntentStatus Status,
    long LastProcessedSequence,
    string? Error)
{
    public bool Accepted => Status == IntentStatus.Accepted;

    /// <summary>
    /// Present for revision-checked gameplay commands; empty for movement and
    /// chat commands.
    /// </summary>
    public Guid CommandId { get; init; }

    public uint InventoryRevision { get; init; }

    public uint ActorRevision { get; init; }

    /// <summary>
    /// True when this response was replayed from the player's bounded receipt
    /// history and no gameplay mutation was repeated.
    /// </summary>
    public bool Duplicate { get; init; }

    public PlayerGameplaySnapshot Gameplay { get; init; }

    /// <summary>
    /// Complete immutable receipt for a world command. Object and chunk deltas
    /// are safe to broadcast; gameplay and container state are requester-only.
    /// </summary>
    public WorldTransactionResult? WorldTransaction { get; init; }

    public ResourceTransactionResult? ResourceTransaction { get; init; }

    public BoatTransactionResult? BoatTransaction { get; init; }
}
