namespace IslandRpg.Protocol;

/// <summary>
/// A reliable message. Sequence is monotonic per connection; Tick is the sender's
/// simulation tick (or zero before a session has begun).
/// </summary>
public interface IProtocolMessage
{
    ProtocolMessageKind Kind { get; }
    ulong Sequence { get; }
    ulong Tick { get; }
}

public sealed record HandshakeRequestMessage(
    ulong Sequence,
    ulong Tick,
    ushort ProtocolVersion,
    string BuildVersion,
    string ContentVersion,
    Guid ClientId,
    Guid RequestedWorldId,
    string PlayerName,
    ulong ClientNonce,
    ushort ClientSnapshotPort,
    ClientCapabilities Capabilities,
    Guid ReconnectPlayerId,
    string ReconnectToken) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeRequest;
}

public sealed record HandshakeAcceptedMessage(
    ulong Sequence,
    ulong Tick,
    ushort ProtocolVersion,
    string BuildVersion,
    string ContentVersion,
    Guid SessionId,
    Guid PlayerId,
    ulong PlayerEntityId,
    Guid WorldId,
    long WorldSeed,
    float SpawnX,
    float SpawnY,
    int SpawnWorldLevel,
    ulong DatagramToken,
    ulong EchoClientNonce,
    ulong NextCommandSequence,
    string ReconnectToken,
    ushort ServerSnapshotPort,
    ushort ServerTickRate,
    ServerCapabilities Capabilities,
    bool IslandStart = false) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeAccepted;
}

public sealed record HandshakeRejectedMessage(
    ulong Sequence,
    ulong Tick,
    ushort ProtocolVersion,
    string BuildVersion,
    string ContentVersion,
    HandshakeRejectionCode Code,
    string Detail) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.HandshakeRejected;
}

public sealed record PlayerJoinedMessage(
    ulong Sequence,
    ulong Tick,
    Guid PlayerId,
    string PlayerName) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.PlayerJoined;
}

public sealed record PlayerLeftMessage(
    ulong Sequence,
    ulong Tick,
    Guid PlayerId,
    PlayerLeaveReason Reason,
    string Detail) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.PlayerLeft;
}

public sealed record WalkCommandMessage(
    ulong Sequence,
    ulong Tick,
    float DestinationX,
    float DestinationY,
    int WorldLevel) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.WalkCommand;
}

public sealed record StopCommandMessage(
    ulong Sequence,
    ulong Tick) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.StopCommand;
}

public sealed record ChatCommandMessage(
    ulong Sequence,
    ulong Tick,
    ChatChannel Channel,
    Guid TargetPlayerId,
    string Text) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ChatCommand;
}

/// <summary>A server-authored chat event after channel and recipient validation.</summary>
public sealed record ChatBroadcastMessage(
    ulong Sequence,
    ulong Tick,
    Guid SenderPlayerId,
    string SenderPlayerName,
    ChatChannel Channel,
    Guid TargetPlayerId,
    string Text) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.ChatBroadcast;
}

public sealed record CommandResultMessage(
    ulong Sequence,
    ulong Tick,
    ulong CommandSequence,
    bool Accepted,
    CommandRejectionCode RejectionCode,
    string Detail) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.CommandResult;
}

/// <summary>Fixed-size state for one network-visible entity.</summary>
public readonly record struct EntitySnapshot(
    ulong EntityId,
    NetworkEntityKind EntityKind,
    byte AnimationState,
    short WorldLevel,
    float X,
    float Y,
    float VelocityX,
    float VelocityY,
    NetworkEntityState State,
    uint Revision)
{
    public const int WireSize = 36;
}

/// <summary>
/// Snapshot metadata is identical whether the snapshot travels reliably as a
/// keyframe or as a UDP datagram. BaselineTick is zero for a keyframe.
/// </summary>
public readonly record struct SnapshotMetadata(
    ulong DatagramToken,
    ushort Sequence,
    ushort AcknowledgedSequence,
    uint AcknowledgementBits,
    ulong ServerTick,
    ulong BaselineTick,
    SnapshotFlags Flags);

public sealed record EntitySnapshotMessage(
    ulong Sequence,
    ulong Tick,
    SnapshotMetadata Metadata,
    IReadOnlyList<EntitySnapshot> Entities) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.EntitySnapshot;
}
