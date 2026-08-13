using System.Collections.ObjectModel;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Client;

public enum NetworkGameClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted,
}

public sealed record ClientHandshakeOptions(
    string BuildVersion,
    string ContentVersion,
    Guid ClientId,
    string PlayerName,
    Guid RequestedWorldId = default,
    Guid ReconnectPlayerId = default,
    string ReconnectToken = "",
    ushort ClientSnapshotPort = 0,
    ClientCapabilities Capabilities =
        ClientCapabilities.UdpSnapshots |
        ClientCapabilities.DeltaSnapshots);

public sealed record NetworkPlayerPresence(Guid PlayerId, string PlayerName);

/// <summary>
/// The latest private, server-authored gameplay state for the controlled
/// player. Inventory indexes remain stable so the existing game UI can render
/// them without inventing client-side mutations.
/// </summary>
public sealed record NetworkPlayerGameplayState(
    uint ActorRevision,
    uint InventoryRevision,
    int Health,
    float Hunger,
    float WellFedSeconds,
    int CraftingExperience,
    int CookingExperience,
    IReadOnlyList<InventorySlotState> InventorySlots,
    int WoodcuttingExperience = 0,
    int FarmingExperience = 0,
    int MiningExperience = 0,
    int AdventureExperience = 0,
    int DiggingExperience = 0);

/// <summary>
/// Immutable public projection of a server-authored world object. Container
/// contents are deliberately excluded and are available only through the
/// private <see cref="NetworkContainerState"/> projection.
/// </summary>
public sealed record NetworkWorldObjectState(
    Guid ObjectId,
    int ChunkX,
    int ChunkY,
    short WorldLevel,
    uint ChunkRevision,
    uint ObjectRevision,
    string DefinitionId,
    float X,
    float Y,
    int Rotation,
    int Health,
    int MaximumHealth,
    bool HasContainer,
    string FuelItemId,
    double LitUntilGameSeconds,
    WorldObjectGateState GateState,
    Guid LinkedObjectId = default);

/// <summary>
/// Immutable private state for one container currently known to this client.
/// Slot indexes remain stable and the list is replaced atomically per update.
/// </summary>
public sealed record NetworkContainerState(
    WorldObjectReference Reference,
    uint ContainerRevision,
    string DefinitionId,
    ContainerAccessMode Access,
    IReadOnlyList<ContainerSlotState> Slots)
{
    public Guid ObjectId => Reference.ObjectId;
}

/// <summary>
/// Immutable sparse resource overlay and retained revision high-water for one
/// procedural chunk. A missing ID has its deterministic default revision zero.
/// </summary>
public sealed record NetworkResourceChunkState(
    WorldChunkKey Chunk,
    uint ResourceChunkRevision,
    IReadOnlyDictionary<ResourceNodeId, ResourceNodeSparseState> Nodes,
    IReadOnlyDictionary<ResourceNodeId, uint> NodeRevisionHighWater);

public sealed record NetworkResourceChange(
    ResourceNodeDeltaKind Kind,
    ResourceNodeId NodeId,
    WorldChunkKey Chunk,
    uint NodeRevision,
    uint ResourceChunkRevision,
    ResourceNodeSparseState? State);

public readonly record struct NetworkWorldChunk(
    int ChunkX,
    int ChunkY,
    short WorldLevel);

public sealed record NetworkWorldObjectChange(
    WorldObjectDeltaKind Kind,
    Guid ObjectId,
    uint ChunkRevision,
    uint ObjectRevision,
    NetworkWorldObjectState? State);

public sealed record NetworkGameClientState(
    NetworkGameClientStatus Status,
    Guid SessionId,
    Guid PlayerId,
    ulong PlayerEntityId,
    Guid WorldId,
    long WorldSeed,
    float SpawnX,
    float SpawnY,
    int SpawnWorldLevel,
    ushort ServerTickRate,
    ulong ServerTick,
    string ReconnectToken,
    string? LastError,
    IReadOnlyDictionary<Guid, NetworkPlayerPresence> Players,
    IReadOnlyDictionary<ulong, EntitySnapshot> Entities,
    NetworkPlayerGameplayState? Gameplay,
    IReadOnlyDictionary<Guid, NetworkWorldObjectState> WorldObjects,
    IReadOnlyDictionary<NetworkWorldChunk, uint> WorldChunkRevisions,
    IReadOnlyDictionary<Guid, NetworkContainerState> Containers,
    IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState> ResourceChunks)
{
    private static readonly IReadOnlyDictionary<Guid, NetworkPlayerPresence> EmptyPlayers =
        new ReadOnlyDictionary<Guid, NetworkPlayerPresence>(new Dictionary<Guid, NetworkPlayerPresence>());
    private static readonly IReadOnlyDictionary<ulong, EntitySnapshot> EmptyEntities =
        new ReadOnlyDictionary<ulong, EntitySnapshot>(new Dictionary<ulong, EntitySnapshot>());
    private static readonly IReadOnlyDictionary<Guid, NetworkWorldObjectState> EmptyWorldObjects =
        new ReadOnlyDictionary<Guid, NetworkWorldObjectState>(new Dictionary<Guid, NetworkWorldObjectState>());
    private static readonly IReadOnlyDictionary<NetworkWorldChunk, uint> EmptyWorldChunkRevisions =
        new ReadOnlyDictionary<NetworkWorldChunk, uint>(new Dictionary<NetworkWorldChunk, uint>());
    private static readonly IReadOnlyDictionary<Guid, NetworkContainerState> EmptyContainers =
        new ReadOnlyDictionary<Guid, NetworkContainerState>(new Dictionary<Guid, NetworkContainerState>());
    private static readonly IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState> EmptyResourceChunks =
        new ReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>(new Dictionary<WorldChunkKey, NetworkResourceChunkState>());

    public static NetworkGameClientState Disconnected { get; } = new(
        NetworkGameClientStatus.Disconnected,
        Guid.Empty,
        Guid.Empty,
        0,
        Guid.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        string.Empty,
        null,
        EmptyPlayers,
        EmptyEntities,
        null,
        EmptyWorldObjects,
        EmptyWorldChunkRevisions,
        EmptyContainers,
        EmptyResourceChunks);
}

public sealed record NetworkChatEvent(
    ulong ServerTick,
    Guid SenderPlayerId,
    string SenderPlayerName,
    ChatChannel Channel,
    Guid TargetPlayerId,
    string Text);

public sealed class HandshakeRejectedException : Exception
{
    public HandshakeRejectedException(HandshakeRejectedMessage rejection)
        : base($"Server rejected the connection ({rejection.Code}): {rejection.Detail}")
    {
        Rejection = rejection;
    }

    public HandshakeRejectedMessage Rejection { get; }
}

public sealed class NetworkClientStateChangedEventArgs(NetworkGameClientState state) : EventArgs
{
    public NetworkGameClientState State { get; } = state;
}

public sealed class NetworkCommandResultEventArgs(CommandResultMessage result) : EventArgs
{
    public CommandResultMessage Result { get; } = result;
}

public sealed class NetworkCookingResultEventArgs(
    CookingResultMessage result) : EventArgs
{
    public CookingResultMessage Result { get; } = result;
}

public sealed class NetworkResourceActionResultEventArgs(
    ResourceActionResultMessage result) : EventArgs
{
    public ResourceActionResultMessage Result { get; } = result;
}

public sealed class NetworkCaveActionResultEventArgs(
    CaveActionResultMessage result) : EventArgs
{
    public CaveActionResultMessage Result { get; } = result;
}

public sealed class NetworkPlayerEventArgs(NetworkPlayerPresence player) : EventArgs
{
    public NetworkPlayerPresence Player { get; } = player;
}

public sealed class NetworkPlayerLeftEventArgs(PlayerLeftMessage message) : EventArgs
{
    public PlayerLeftMessage Message { get; } = message;
}

public sealed class NetworkChatEventArgs(NetworkChatEvent message) : EventArgs
{
    public NetworkChatEvent Message { get; } = message;
}

public sealed class NetworkSnapshotEventArgs(EntitySnapshotMessage snapshot) : EventArgs
{
    public EntitySnapshotMessage Snapshot { get; } = snapshot;
}

public sealed class NetworkPlayerStateEventArgs(
    NetworkPlayerGameplayState state) : EventArgs
{
    public NetworkPlayerGameplayState State { get; } = state;
}

public sealed class NetworkActionResultEventArgs(
    ActionResultMessage result) : EventArgs
{
    public ActionResultMessage Result { get; } = result;
}

public sealed class NetworkWorldObjectsChangedEventArgs(
    IReadOnlyList<NetworkWorldObjectChange> changes) : EventArgs
{
    public IReadOnlyList<NetworkWorldObjectChange> Changes { get; } = changes;
}

public sealed class NetworkContainerStateEventArgs(
    NetworkContainerState state) : EventArgs
{
    public NetworkContainerState State { get; } = state;
}

public sealed class NetworkResourcesChangedEventArgs(
    WorldChunkKey chunk,
    bool isBaseline,
    IReadOnlyList<NetworkResourceChange> changes) : EventArgs
{
    public WorldChunkKey Chunk { get; } = chunk;
    public bool IsBaseline { get; } = isBaseline;
    public IReadOnlyList<NetworkResourceChange> Changes { get; } = changes;
}
