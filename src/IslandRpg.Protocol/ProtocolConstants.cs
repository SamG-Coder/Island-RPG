namespace IslandRpg.Protocol;

/// <summary>Wire-level constants shared by every Island RPG client and server.</summary>
public static class ProtocolConstants
{
    public const uint ReliableMagic = 0x49525047; // IRPG
    public const uint SnapshotMagic = 0x49525544; // IRUD
    public const ushort CurrentVersion = 10;
    public const int ReliableHeaderSize = 28;
    public const int TcpLengthPrefixSize = sizeof(uint);
    public const int MaxReliableFrameBytes = 64 * 1024;
    public const int MaxUdpDatagramBytes = 1200;
    public const int UdpSnapshotHeaderSize = 44;
}

/// <summary>Bounds are measured in encoded UTF-8 bytes, not UTF-16 characters.</summary>
public static class ProtocolLimits
{
    public const int BuildVersionBytes = 64;
    public const int ContentVersionBytes = 64;
    public const int PlayerNameBytes = 48;
    public const int ChatTextBytes = 512;
    public const int DetailBytes = 256;
    public const int LeaveReasonBytes = 128;
    public const int ReconnectTokenBytes = 128;
    public const int ItemIdBytes = 64;
    public const int DefinitionIdBytes = 64;
    public const int RecipeIdBytes = 64;
    public const int PlayerInventorySlots = 28;
    public const int MaxInventoryQuantity = ushort.MaxValue;
    public const int MaxContainerSlots = 128;
    public const int MaxContainerTransferQuantity = ushort.MaxValue;
    public const int MaxWorldObjectsPerBatch = 512;
    public const int MaxWorldChunkRevisionsPerBatch = 512;
    public const int MaxResourceNodesPerBatch = 512;
    public const int MaxResourceRewardsPerAction = 8;
    public const int MaxBoatsPerBatch =
        IslandRpg.Gameplay.NetworkPopulationLimits.MaximumBoats;
    public const int MaxEnemiesPerBatch =
        IslandRpg.Gameplay.NetworkPopulationLimits.MaximumEnemies;
    public const int MaxCombatEventsPerBatch = 256;
    public const int GroupOwnerIdBytes = 64;
    public const int MinConstructionRotation = 0;
    public const int MaxConstructionRotation = 3;
    public const float MaxPlayerHunger = 100;
    public const int MaxSnapshotEntities =
        IslandRpg.Gameplay.NetworkPopulationLimits.MaximumSnapshotEntities;
}

public enum ProtocolMessageKind : byte
{
    HandshakeRequest = 1,
    HandshakeAccepted = 2,
    HandshakeRejected = 3,
    PlayerJoined = 4,
    PlayerLeft = 5,
    WalkCommand = 16,
    StopCommand = 17,
    ChatCommand = 18,
    ChatBroadcast = 19,
    CommandResult = 24,
    EntitySnapshot = 32,
    ActionCommand = 33,
    ActionResult = 34,
    PlayerState = 35,
    WorldObjectState = 36,
    WorldObjectDeltaBatch = 37,
    ContainerState = 38,
    WorldChunkRevisionBatch = 39,
    CookingResult = 40,
    ResourceChunkBaseline = 41,
    ResourceNodeDeltaBatch = 42,
    ResourceActionResult = 43,
    CaveActionResult = 44,
    BoatBaseline = 45,
    BoatDeltaBatch = 46,
    BoatActionResult = 47,
    EnemyBaseline = 48,
    EnemyDeltaBatch = 49,
    CombatEventBatch = 50,
    CombatActionResult = 51,
}

[Flags]
public enum ClientCapabilities : uint
{
    None = 0,
    UdpSnapshots = 1 << 0,
    SnapshotAcknowledgements = 1 << 1,
    DeltaSnapshots = 1 << 2,
}

[Flags]
public enum ServerCapabilities : uint
{
    None = 0,
    UdpSnapshots = 1 << 0,
    DeltaSnapshots = 1 << 1,
}

/// <summary>Public visual/access state for a gate world object.</summary>
public enum WorldObjectGateState : byte
{
    None,
    Unlocked,
    Opened,
    Locked,
}

public enum HandshakeRejectionCode : byte
{
    Unknown = 0,
    ProtocolMismatch = 1,
    BuildMismatch = 2,
    ContentMismatch = 3,
    ServerFull = 4,
    InvalidName = 5,
    DuplicateClient = 6,
    ServerStopping = 7,
}

public enum PlayerLeaveReason : byte
{
    Disconnected = 0,
    Quit = 1,
    TimedOut = 2,
    Kicked = 3,
    ServerStopped = 4,
}

public enum ChatChannel : byte
{
    Local = 0,
    Group = 1,
    Global = 2,
    Whisper = 3,
}

public enum CommandRejectionCode : byte
{
    None = 0,
    Invalid = 1,
    OutOfOrder = 2,
    RateLimited = 3,
    NotAuthorized = 4,
    Impossible = 5,
    ServerBusy = 6,
}

public enum ActionCommandKind : byte
{
    InventorySwap = 1,
    CombineItems = 2,
    CraftRecipe = 3,
    ConsumeItem = 4,
    PickUpWorldObject = 5,
    DropInventoryItem = 6,
    OpenContainer = 7,
    ContainerTransfer = 8,
    AddCampfireFuel = 9,
    TakeCampfireFuel = 10,
    LightCampfire = 11,
    PlaceConstruction = 12,
    BuildConstruction = 13,
    DemolishWorldObject = 14,
    CookOnCampfire = 15,
    ResourceAction = 16,
    CaveAction = 17,
    BoatAction = 18,
    CombatAction = 19,
}

public enum BoatActionKind : byte
{
    Board = 1,
    Move = 2,
    Stop = 3,
    Disembark = 4,
}

public enum BoatDeltaKind : byte
{
    Upsert = 1,
    Remove = 2,
}

public enum CaveActionKind : byte
{
    StartExcavation = 1,
    WorkExcavation = 2,
    RestoreExcavation = 3,
    InstallRope = 4,
    TakeRope = 5,
    FillExcavation = 6,
    Traverse = 7,
}

public enum ResourceNodeDeltaKind : byte
{
    Upsert = 1,
    Remove = 2,
}

public enum ContainerTransferDirection : byte
{
    Deposit = 1,
    Withdraw = 2,
}

public enum ContainerAccessMode : byte
{
    DepositAndWithdraw = 1,
    WithdrawOnly = 2,
}

public enum WorldObjectDeltaKind : byte
{
    Upsert = 1,
    Remove = 2,
}

[Flags]
public enum PlayerStateFlags : byte
{
    None = 0,
    Baseline = 1 << 0,
    Actor = 1 << 1,
    Inventory = 1 << 2,
}

[Flags]
public enum SnapshotFlags : byte
{
    None = 0,
    Keyframe = 1 << 0,
    Delta = 1 << 1,
}

public enum NetworkEntityKind : byte
{
    Unknown = 0,
    Player = 1,
    Villager = 2,
    Enemy = 3,
    GroundObject = 4,
    Projectile = 5,
    Boat = 6,
}

[Flags]
public enum NetworkEntityState : uint
{
    None = 0,
    Moving = 1 << 0,
    Dead = 1 << 1,
    InCombat = 1 << 2,
    Interacting = 1 << 3,
    Hidden = 1 << 4,
}

public enum CombatActionKind : byte
{
    SetTarget = 1,
    Cancel = 2,
    SetStance = 3,
    Respawn = 4,
}

public enum CombatStance : byte
{
    Balanced = 0,
    Aggressive = 1,
    Defensive = 2,
}

public enum CombatLifeState : byte
{
    Alive = 0,
    Dead = 1,
}

[Flags]
public enum CombatStatusFlags : uint
{
    None = 0,
    Slowed = 1 << 0,
    Rooted = 1 << 1,
    Poisoned = 1 << 2,
    Hidden = 1 << 3,
    Burrowed = 1 << 4,
}

public enum CombatEnemyArchetype : byte
{
    WaterSlime = 1,
    GrassSlime = 2,
    SandSlime = 3,
    CaveSlime = 4,
}

public enum CombatEnemySize : byte
{
    Small = 1,
    Medium = 2,
    Large = 3,
}

public enum CombatEnemyBehavior : byte
{
    Idle = 1,
    Chasing = 2,
    Attacking = 3,
    Burrowed = 4,
    Dead = 5,
}

public enum EnemyDeltaKind : byte
{
    Upsert = 1,
    Remove = 2,
}

public enum CombatEventKind : byte
{
    AttackStarted = 1,
    Damage = 2,
    StatusApplied = 3,
    StatusExpired = 4,
    Death = 5,
    Split = 6,
    LootDropped = 7,
    Respawn = 8,
}

public enum CombatStatusEffect : byte
{
    None = 0,
    Slow = 1,
    Root = 2,
    Poison = 3,
    Hide = 4,
    Burrow = 5,
}
