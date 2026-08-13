using System.Collections.ObjectModel;
using IslandRpg.Protocol;

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
    ClientCapabilities Capabilities = ClientCapabilities.None);

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
    IReadOnlyList<InventorySlotState> InventorySlots);

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
    NetworkPlayerGameplayState? Gameplay)
{
    private static readonly IReadOnlyDictionary<Guid, NetworkPlayerPresence> EmptyPlayers =
        new ReadOnlyDictionary<Guid, NetworkPlayerPresence>(new Dictionary<Guid, NetworkPlayerPresence>());
    private static readonly IReadOnlyDictionary<ulong, EntitySnapshot> EmptyEntities =
        new ReadOnlyDictionary<ulong, EntitySnapshot>(new Dictionary<ulong, EntitySnapshot>());

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
        null);
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
