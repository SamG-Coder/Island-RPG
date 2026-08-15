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
    string ReconnectToken,
    byte Gender = 0,
    byte TeamColor = 0) : IProtocolMessage
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
    string PlayerName,
    ulong EntityId = 0,
    byte Gender = 0,
    byte TeamColor = 0) : IProtocolMessage
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

public sealed record PresentSkillCommandMessage(
    ulong Sequence,
    ulong Tick,
    byte Action,
    float DurationSeconds = 0.75f) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.PresentSkillCommand;
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

/// <summary>
/// Private social, trade, and guild lists for the owning player. Friends,
/// ignore, guild, follow, and the open trade (if any) are server-authored.
/// </summary>
public sealed record SocialStateMessage(
    ulong Sequence,
    ulong Tick,
    Guid PlayerId,
    IReadOnlyList<Guid> Friends,
    IReadOnlyList<Guid> Ignored,
    Guid GuildId,
    string GuildName,
    Guid FollowTargetPlayerId,
    Guid OpenTradeId,
    Guid TradePartnerPlayerId,
    bool TradeAccepted,
    bool TradeIncoming,
    IReadOnlyList<int> OwnOfferSlots,
    IReadOnlyList<int> PartnerOfferSlots,
    bool OwnConfirmed,
    bool PartnerConfirmed) : IProtocolMessage
{
    public ProtocolMessageKind Kind => ProtocolMessageKind.SocialState;

    public bool Equals(SocialStateMessage? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Sequence == other.Sequence &&
               Tick == other.Tick &&
               PlayerId == other.PlayerId &&
               SameGuids(Friends, other.Friends) &&
               SameGuids(Ignored, other.Ignored) &&
               GuildId == other.GuildId &&
               GuildName == other.GuildName &&
               FollowTargetPlayerId == other.FollowTargetPlayerId &&
               OpenTradeId == other.OpenTradeId &&
               TradePartnerPlayerId == other.TradePartnerPlayerId &&
               TradeAccepted == other.TradeAccepted &&
               TradeIncoming == other.TradeIncoming &&
               SameInts(OwnOfferSlots, other.OwnOfferSlots) &&
               SameInts(PartnerOfferSlots, other.PartnerOfferSlots) &&
               OwnConfirmed == other.OwnConfirmed &&
               PartnerConfirmed == other.PartnerConfirmed;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Sequence);
        hash.Add(Tick);
        hash.Add(PlayerId);
        AddGuids(ref hash, Friends);
        AddGuids(ref hash, Ignored);
        hash.Add(GuildId);
        hash.Add(GuildName);
        hash.Add(FollowTargetPlayerId);
        hash.Add(OpenTradeId);
        hash.Add(TradePartnerPlayerId);
        hash.Add(TradeAccepted);
        hash.Add(TradeIncoming);
        AddInts(ref hash, OwnOfferSlots);
        AddInts(ref hash, PartnerOfferSlots);
        hash.Add(OwnConfirmed);
        hash.Add(PartnerConfirmed);
        return hash.ToHashCode();
    }

    private static bool SameGuids(IReadOnlyList<Guid>? left, IReadOnlyList<Guid>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        for (var index = 0; index < leftCount; index++)
            if (left![index] != right![index])
                return false;
        return true;
    }

    private static bool SameInts(IReadOnlyList<int>? left, IReadOnlyList<int>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        for (var index = 0; index < leftCount; index++)
            if (left![index] != right![index])
                return false;
        return true;
    }

    private static void AddGuids(ref HashCode hash, IReadOnlyList<Guid>? values)
    {
        if (values is null) return;
        for (var index = 0; index < values.Count; index++)
            hash.Add(values[index]);
    }

    private static void AddInts(ref HashCode hash, IReadOnlyList<int>? values)
    {
        if (values is null) return;
        for (var index = 0; index < values.Count; index++)
            hash.Add(values[index]);
    }
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
