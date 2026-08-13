namespace IslandRpg.Protocol;

/// <summary>Wire-level constants shared by every Island RPG client and server.</summary>
public static class ProtocolConstants
{
    public const uint ReliableMagic = 0x49525047; // IRPG
    public const uint SnapshotMagic = 0x49525544; // IRUD
    public const ushort CurrentVersion = 1;
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
    public const int RecipeIdBytes = 64;
    public const int PlayerInventorySlots = 28;
    public const int MaxInventoryQuantity = ushort.MaxValue;
    public const float MaxPlayerHunger = 100;
    public const int MaxSnapshotEntities = 1600;
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
