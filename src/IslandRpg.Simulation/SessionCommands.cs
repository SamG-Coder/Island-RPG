using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.Gameplay;

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

/// <summary>
/// Presentation-only stance so remotes start Work/Mine/Dig/Fish/Gather
/// when the local player begins the single-player clip, not after the
/// first mutating hit.
/// </summary>
public sealed record PresentSkillIntent(
    EntityAction Action,
    float DurationSeconds = (float)ActorSkillStance.OneShotSeconds) : ActorIntent;

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

public sealed record EmptyBucketIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int Slot) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record FillBucketIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int Slot,
    Vector2 Position,
    int WorldLevel) : GameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public enum SocialCommandKind : byte
{
    OfferTrade = 1,
    RespondTrade = 2,
    SetTradeOffer = 3,
    ConfirmTrade = 4,
    CancelTrade = 5,
    Follow = 6,
    StopFollow = 7,
    AddFriend = 8,
    RemoveFriend = 9,
    Ignore = 10,
    Unignore = 11,
    CreateGuild = 12,
    JoinGuild = 13,
    LeaveGuild = 14
}

/// <summary>
/// Server-owned social, trade, and follow commands. The render client only
/// names the other player; the session validates range, ignore, and inventory.
/// </summary>
public sealed record SocialIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    SocialCommandKind Kind,
    PlayerId TargetPlayerId = default,
    Guid TradeId = default,
    Guid GuildId = default,
    string Text = "",
    bool Accept = false,
    ImmutableArray<int> OfferSlots = default) : GameplayIntent(
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

public abstract record CombatGameplayIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision) : GameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record SetCombatTargetIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    EnemyReference Enemy) : CombatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record CancelCombatIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision) : CombatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record SetCombatStanceIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    MeleeCombatStance Stance) : CombatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

public sealed record RespawnIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision) : CombatGameplayIntent(
        CommandId, ExpectedInventoryRevision, ExpectedActorRevision);

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

public sealed record PlaceInventoryWorldObjectIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    string DefinitionId,
    int InventorySlot,
    Vector2 Position,
    int WorldLevel,
    int Rotation,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record PlantCropIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int SeedInventorySlot,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record HarvestCropIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Crop) : WorldGameplayIntent(
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

public sealed record CookStewIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Pot) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record StrikeTrainingDummyIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Dummy) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record PlantTreeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    int SeedInventorySlot,
    Vector2 Position,
    int WorldLevel,
    uint ExpectedChunkRevision) : WorldGameplayIntent(
        CommandId,
        ExpectedInventoryRevision,
        ExpectedActorRevision);

public sealed record StrikePlantedTreeIntent(
    Guid CommandId,
    uint ExpectedInventoryRevision,
    uint ExpectedActorRevision,
    WorldObjectHandle Tree,
    int ToolInventorySlot) : WorldGameplayIntent(
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

    public PlayerSocialSnapshot Social { get; init; }

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
    ConnectionAlreadyJoined,
    SessionFull,
    ExpiredPlayer
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

    public PlayerSocialSnapshot Social { get; init; }

    /// <summary>
    /// Previous live connection evicted by a valid reconnect takeover.
    /// Empty when the actor was already disconnected.
    /// </summary>
    public ClientConnectionId EvictedConnectionId { get; init; }
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

    /// <summary>
    /// Remaining players whose private social view changed because this
    /// actor left (open trade cancelled, follow cleared).
    /// </summary>
    public ImmutableArray<PlayerSocialPublication> Social { get; init; }
}

public readonly record struct PlayerSocialPublication(
    PlayerId PlayerId,
    PlayerSocialSnapshot Social);

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
    NotCrop,
    CropNotReady,
    NotPlantedTree,
    PlantLimitReached,
    TreeAlreadyFelled,
    BoatNotFound,
    StaleBoatRevision,
    AlreadyAboard,
    BoatOccupied,
    NotAboard,
    InvalidBoatDestination,
    BoatDestinationTooFar,
    BoatRouteUnreachable,
    InvalidBoatLanding,
    BoatPlanningLocked,
    CombatUnavailable,
    EnemyNotFound,
    EnemyDead,
    StaleEnemyRevision,
    InvalidCombatStance,
    RespawnLocked,
    ActorAlreadyAlive,
    Ignored,
    AlreadyFriends,
    NotFriends,
    AlreadyIgnored,
    NotIgnored,
    GuildNotFound,
    AlreadyInGuild,
    NotInGuild,
    GuildFull,
    InvalidGuildName,
    TradeNotFound,
    TradeNotReady,
    AlreadyTrading,
    NotFollowing
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

    public CombatTransactionResult? CombatTransaction { get; init; }

    /// <summary>
    /// Cross-feature boat state committed by a non-boat command. The server
    /// publishes this only after the requester's private command outcome.
    /// </summary>
    public BoatStateDelta? BoatDelta { get; init; }

    /// <summary>
    /// Private social views for every player whose lists or open trade
    /// changed. The server publishes each to that player's connection only.
    /// </summary>
    public ImmutableArray<PlayerSocialPublication> Social { get; init; }
}
