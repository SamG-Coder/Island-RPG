using System.Collections.Immutable;
using System.Numerics;

namespace IslandRpg.Simulation;

public readonly record struct PlayerSocialSnapshot(
    ImmutableArray<PlayerId> Friends,
    ImmutableArray<PlayerId> Ignored,
    Guid? GuildId,
    string GuildName,
    PlayerId? FollowTarget,
    Guid? OpenTradeId,
    PlayerId? TradePartner,
    bool TradeAccepted = false,
    bool TradeIncoming = false,
    ImmutableArray<int> OwnOfferSlots = default,
    ImmutableArray<int> PartnerOfferSlots = default,
    bool OwnConfirmed = false,
    bool PartnerConfirmed = false)
{
    public static PlayerSocialSnapshot Empty { get; } = new(
        ImmutableArray<PlayerId>.Empty,
        ImmutableArray<PlayerId>.Empty,
        null,
        "",
        null,
        null,
        null);
}

public readonly record struct GuildSnapshot(
    Guid GuildId,
    string Name,
    PlayerId Leader,
    ImmutableArray<PlayerId> Members);

public readonly record struct AuthoritativeGuildCheckpoint(
    Guid GuildId,
    string Name,
    PlayerId Leader,
    ImmutableArray<PlayerId> Members);

/// <summary>
/// Owner-thread social, trade, and follow directory. Lists stay per-actor so
/// a tick never walks the world-object set to update them.
/// </summary>
internal sealed class AuthoritativeSocialDirectory
{
    public const int MaximumListEntries = 64;
    public const int MaximumGuildMembers = 64;
    public const int MaximumGuildNameLength = 24;
    public const float TradeRange = 3f;
    public const float FollowDistance = 1.6f;
    public const float FollowRetargetDistance = 0.6f;

    public static Vector2 StandNear(Vector2 follower, Vector2 leader)
    {
        var away = follower - leader;
        if (away.LengthSquared() <= .0001f)
            away = Vector2.UnitX;
        else
            away = Vector2.Normalize(away);
        return leader + away * FollowDistance;
    }

    private readonly Dictionary<PlayerId, HashSet<PlayerId>> _friends = [];
    private readonly Dictionary<PlayerId, HashSet<PlayerId>> _ignored = [];
    private readonly Dictionary<PlayerId, Guid> _guildByPlayer = [];
    private readonly Dictionary<Guid, MutableGuild> _guilds = [];
    private readonly Dictionary<Guid, MutableTrade> _trades = [];
    private readonly Dictionary<PlayerId, Guid> _tradeByPlayer = [];
    private readonly Dictionary<PlayerId, PlayerId> _follow = [];
    private ImmutableDictionary<PlayerId, ImmutableHashSet<PlayerId>>
        _publishedIgnored =
            ImmutableDictionary<PlayerId, ImmutableHashSet<PlayerId>>.Empty;

    public IReadOnlyDictionary<PlayerId, PlayerId> Followers => _follow;

    public void ForgetPlayer(PlayerId playerId)
    {
        StopFollow(playerId);
        foreach (var follower in _follow
                     .Where(pair => pair.Value == playerId)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _follow.Remove(follower);
        if (_tradeByPlayer.TryGetValue(playerId, out var tradeId))
            CancelTrade(tradeId);
        if (_guildByPlayer.TryGetValue(playerId, out var guildId) &&
            _guilds.TryGetValue(guildId, out var guild))
        {
            guild.Members.Remove(playerId);
            _guildByPlayer.Remove(playerId);
            if (guild.Members.Count == 0)
                _guilds.Remove(guildId);
            else if (guild.Leader == playerId)
                guild.Leader = guild.Members.OrderBy(static value => value.Value)
                    .First();
        }
        _friends.Remove(playerId);
        _ignored.Remove(playerId);
        PublishOwnerIgnored(playerId);
    }

    public bool IsIgnored(PlayerId owner, PlayerId other) =>
        other.Value != Guid.Empty &&
        _ignored.TryGetValue(owner, out var list) &&
        list.Contains(other);

    /// <summary>
    /// Lock-free ignore lookup for connection-thread chat fan-out. Reads the
    /// last published snapshot so it never enters the session owner.
    /// </summary>
    public bool IsIgnoredPublished(PlayerId owner, PlayerId other)
    {
        var snapshot = Volatile.Read(ref _publishedIgnored);
        return other.Value != Guid.Empty &&
               snapshot.TryGetValue(owner, out var list) &&
               list.Contains(other);
    }

    private void PublishOwnerIgnored(PlayerId owner)
    {
        var current = _publishedIgnored;
        var next = _ignored.TryGetValue(owner, out var list) && list.Count > 0
            ? current.SetItem(owner, list.ToImmutableHashSet())
            : current.Remove(owner);
        Volatile.Write(ref _publishedIgnored, next);
    }

    public PlayerId? FollowTarget(PlayerId follower) =>
        _follow.TryGetValue(follower, out var target) ? target : null;

    public void StopFollow(PlayerId follower) => _follow.Remove(follower);

    public bool TryFollow(PlayerId follower, PlayerId target)
    {
        if (follower == target || target.Value == Guid.Empty)
            return false;
        _follow[follower] = target;
        return true;
    }

    public PlayerSocialSnapshot Snapshot(PlayerId playerId)
    {
        Guid? tradeId = _tradeByPlayer.TryGetValue(playerId, out var open)
            ? open
            : null;
        PlayerId? partner = null;
        var tradeAccepted = false;
        var tradeIncoming = false;
        var ownSlots = ImmutableArray<int>.Empty;
        var partnerSlots = ImmutableArray<int>.Empty;
        var ownConfirmed = false;
        var partnerConfirmed = false;
        if (tradeId is { } id && _trades.TryGetValue(id, out var trade))
        {
            partner = trade.Other(playerId);
            tradeAccepted = trade.Accepted;
            tradeIncoming = !trade.Accepted && trade.Responder == playerId;
            ownSlots = trade.Offer(playerId);
            partnerSlots = trade.Offer(partner.Value);
            ownConfirmed = trade.Offerer == playerId
                ? trade.OffererConfirmed
                : trade.ResponderConfirmed;
            partnerConfirmed = trade.Offerer == playerId
                ? trade.ResponderConfirmed
                : trade.OffererConfirmed;
        }
        Guid? guildId = _guildByPlayer.TryGetValue(playerId, out var guild)
            ? guild
            : null;
        var guildName = "";
        if (guildId is { } gid && _guilds.TryGetValue(gid, out var found))
            guildName = found.Name;
        return new(
            Sorted(_friends, playerId),
            Sorted(_ignored, playerId),
            guildId,
            guildName,
            FollowTarget(playerId),
            tradeId,
            partner,
            tradeAccepted,
            tradeIncoming,
            ownSlots,
            partnerSlots,
            ownConfirmed,
            partnerConfirmed);
    }

    public PlayerId? CancelTradeForPlayer(PlayerId playerId)
    {
        if (!_tradeByPlayer.TryGetValue(playerId, out var tradeId) ||
            !_trades.TryGetValue(tradeId, out var trade))
            return null;
        var other = trade.Other(playerId);
        CancelTrade(tradeId);
        return other;
    }

    public GuildSnapshot? Guild(Guid guildId)
    {
        if (!_guilds.TryGetValue(guildId, out var guild))
            return null;
        return new(
            guild.Id,
            guild.Name,
            guild.Leader,
            guild.Members.OrderBy(static value => value.Value).ToImmutableArray());
    }

    public ImmutableArray<AuthoritativeGuildCheckpoint> CaptureGuilds() =>
        _guilds.Values
            .OrderBy(static value => value.Id)
            .Select(static value => new AuthoritativeGuildCheckpoint(
                value.Id,
                value.Name,
                value.Leader,
                value.Members.OrderBy(static member => member.Value)
                    .ToImmutableArray()))
            .ToImmutableArray();

    public void RestorePlayer(
        PlayerId playerId,
        IEnumerable<PlayerId> friends,
        IEnumerable<PlayerId> ignored,
        Guid? guildId)
    {
        ReplaceList(_friends, playerId, friends);
        ReplaceList(_ignored, playerId, ignored);
        PublishOwnerIgnored(playerId);
        if (guildId is { } id && id != Guid.Empty)
            _guildByPlayer[playerId] = id;
    }

    public void RestoreGuilds(
        IEnumerable<AuthoritativeGuildCheckpoint> guilds)
    {
        _guilds.Clear();
        foreach (var guild in guilds)
        {
            if (guild.GuildId == Guid.Empty ||
                string.IsNullOrWhiteSpace(guild.Name))
                continue;
            var members = guild.Members.IsDefault
                ? new HashSet<PlayerId>()
                : guild.Members.ToHashSet();
            _guilds[guild.GuildId] = new MutableGuild(
                guild.GuildId, guild.Name, guild.Leader, members);
            foreach (var member in members)
                _guildByPlayer[member] = guild.GuildId;
        }
    }

    public IntentStatus AddFriend(PlayerId actor, PlayerId target)
    {
        if (actor == target || target.Value == Guid.Empty)
            return IntentStatus.InvalidIntent;
        var list = List(_friends, actor);
        if (list.Contains(target)) return IntentStatus.AlreadyFriends;
        if (list.Count >= MaximumListEntries) return IntentStatus.InventoryFull;
        list.Add(target);
        return IntentStatus.Accepted;
    }

    public IntentStatus RemoveFriend(PlayerId actor, PlayerId target)
    {
        if (!_friends.TryGetValue(actor, out var list) || !list.Remove(target))
            return IntentStatus.NotFriends;
        return IntentStatus.Accepted;
    }

    public IntentStatus Ignore(PlayerId actor, PlayerId target)
    {
        if (actor == target || target.Value == Guid.Empty)
            return IntentStatus.InvalidIntent;
        var list = List(_ignored, actor);
        if (list.Contains(target)) return IntentStatus.AlreadyIgnored;
        if (list.Count >= MaximumListEntries) return IntentStatus.InventoryFull;
        list.Add(target);
        PublishOwnerIgnored(actor);
        if (_friends.TryGetValue(actor, out var friends))
            friends.Remove(target);
        if (_tradeByPlayer.TryGetValue(actor, out var tradeId) &&
            _trades.TryGetValue(tradeId, out var trade) &&
            trade.Other(actor) == target)
            CancelTrade(tradeId);
        if (_follow.TryGetValue(actor, out var followed) && followed == target)
            _follow.Remove(actor);
        return IntentStatus.Accepted;
    }

    public IntentStatus Unignore(PlayerId actor, PlayerId target)
    {
        if (!_ignored.TryGetValue(actor, out var list) || !list.Remove(target))
            return IntentStatus.NotIgnored;
        PublishOwnerIgnored(actor);
        return IntentStatus.Accepted;
    }

    public IntentStatus CreateGuild(
        PlayerId actor, string name, out Guid guildId)
    {
        guildId = Guid.Empty;
        if (_guildByPlayer.ContainsKey(actor))
            return IntentStatus.AlreadyInGuild;
        if (!TryNormalizeGuildName(name, out var normalized))
            return IntentStatus.InvalidGuildName;
        if (_guilds.Values.Any(value =>
                value.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return IntentStatus.InvalidGuildName;
        guildId = Guid.NewGuid();
        _guilds[guildId] = new MutableGuild(
            guildId, normalized, actor, [actor]);
        _guildByPlayer[actor] = guildId;
        return IntentStatus.Accepted;
    }

    public IntentStatus JoinGuild(PlayerId actor, Guid guildId)
    {
        if (_guildByPlayer.ContainsKey(actor))
            return IntentStatus.AlreadyInGuild;
        if (!_guilds.TryGetValue(guildId, out var guild))
            return IntentStatus.GuildNotFound;
        if (guild.Members.Count >= MaximumGuildMembers)
            return IntentStatus.GuildFull;
        guild.Members.Add(actor);
        _guildByPlayer[actor] = guildId;
        return IntentStatus.Accepted;
    }

    public IntentStatus LeaveGuild(PlayerId actor)
    {
        if (!_guildByPlayer.TryGetValue(actor, out var guildId) ||
            !_guilds.TryGetValue(guildId, out var guild))
            return IntentStatus.NotInGuild;
        guild.Members.Remove(actor);
        _guildByPlayer.Remove(actor);
        if (guild.Members.Count == 0)
            _guilds.Remove(guildId);
        else if (guild.Leader == actor)
            guild.Leader = guild.Members.OrderBy(static value => value.Value)
                .First();
        return IntentStatus.Accepted;
    }

    public IntentStatus OfferTrade(
        PlayerId actor,
        PlayerId target,
        out Guid tradeId)
    {
        tradeId = Guid.Empty;
        if (actor == target || target.Value == Guid.Empty)
            return IntentStatus.InvalidIntent;
        if (_tradeByPlayer.ContainsKey(actor) ||
            _tradeByPlayer.ContainsKey(target))
            return IntentStatus.AlreadyTrading;
        if (IsIgnored(target, actor))
            return IntentStatus.Ignored;
        tradeId = Guid.NewGuid();
        _trades[tradeId] = new MutableTrade(tradeId, actor, target);
        _tradeByPlayer[actor] = tradeId;
        _tradeByPlayer[target] = tradeId;
        return IntentStatus.Accepted;
    }

    public IntentStatus RespondTrade(PlayerId actor, Guid tradeId, bool accept)
    {
        if (!_trades.TryGetValue(tradeId, out var trade) ||
            trade.Responder != actor)
            return IntentStatus.TradeNotFound;
        if (!accept)
        {
            CancelTrade(tradeId);
            return IntentStatus.Accepted;
        }
        trade.Accepted = true;
        return IntentStatus.Accepted;
    }

    public IntentStatus SetTradeOffer(
        PlayerId actor,
        Guid tradeId,
        ImmutableArray<int> slots)
    {
        if (!_trades.TryGetValue(tradeId, out var trade) ||
            !trade.Involves(actor) ||
            !trade.Accepted)
            return IntentStatus.TradeNotFound;
        if (slots.IsDefault)
            slots = ImmutableArray<int>.Empty;
        if (slots.Any(static slot => slot < 0) ||
            slots.Distinct().Count() != slots.Length)
            return IntentStatus.InvalidInventorySlot;
        trade.SetOffer(actor, slots);
        trade.ClearConfirmations();
        return IntentStatus.Accepted;
    }

    public IntentStatus ConfirmTrade(PlayerId actor, Guid tradeId)
    {
        if (!_trades.TryGetValue(tradeId, out var trade) ||
            !trade.Involves(actor) ||
            !trade.Accepted)
            return IntentStatus.TradeNotFound;
        trade.Confirm(actor);
        return IntentStatus.Accepted;
    }

    public bool TryTakeReadyTrade(Guid tradeId, out MutableTrade trade)
    {
        trade = null!;
        if (!_trades.TryGetValue(tradeId, out trade!) || !trade.BothConfirmed)
            return false;
        return true;
    }

    public void CompleteTrade(Guid tradeId) => CancelTrade(tradeId);

    public IntentStatus CancelTradeFor(PlayerId actor, Guid tradeId)
    {
        if (!_trades.TryGetValue(tradeId, out var trade) ||
            !trade.Involves(actor))
            return IntentStatus.TradeNotFound;
        CancelTrade(tradeId);
        return IntentStatus.Accepted;
    }

    public MutableTrade? Trade(Guid tradeId) =>
        _trades.TryGetValue(tradeId, out var trade) ? trade : null;

    private void CancelTrade(Guid tradeId)
    {
        if (!_trades.Remove(tradeId, out var trade)) return;
        _tradeByPlayer.Remove(trade.Offerer);
        _tradeByPlayer.Remove(trade.Responder);
    }

    private static HashSet<PlayerId> List(
        Dictionary<PlayerId, HashSet<PlayerId>> source,
        PlayerId player)
    {
        if (!source.TryGetValue(player, out var list))
        {
            list = [];
            source[player] = list;
        }
        return list;
    }

    private static void ReplaceList(
        Dictionary<PlayerId, HashSet<PlayerId>> source,
        PlayerId player,
        IEnumerable<PlayerId> values)
    {
        var list = new HashSet<PlayerId>();
        foreach (var value in values)
        {
            if (value.Value == Guid.Empty || value == player) continue;
            if (list.Count >= MaximumListEntries) break;
            list.Add(value);
        }
        source[player] = list;
    }

    private static ImmutableArray<PlayerId> Sorted(
        Dictionary<PlayerId, HashSet<PlayerId>> source,
        PlayerId player) =>
        source.TryGetValue(player, out var list)
            ? list.OrderBy(static value => value.Value).ToImmutableArray()
            : ImmutableArray<PlayerId>.Empty;

    internal static bool TryNormalizeGuildName(string? value, out string name)
    {
        name = (value ?? "").Trim();
        return name.Length is >= 1 and <= MaximumGuildNameLength &&
               name.All(static character =>
                   !char.IsControl(character) && character != '\n');
    }

    internal sealed class MutableGuild(
        Guid id,
        string name,
        PlayerId leader,
        HashSet<PlayerId> members)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public PlayerId Leader { get; set; } = leader;
        public HashSet<PlayerId> Members { get; } = members;
    }

    internal sealed class MutableTrade(
        Guid id,
        PlayerId offerer,
        PlayerId responder)
    {
        public Guid Id { get; } = id;
        public PlayerId Offerer { get; } = offerer;
        public PlayerId Responder { get; } = responder;
        public bool Accepted { get; set; }
        public ImmutableArray<int> OffererSlots { get; private set; } =
            ImmutableArray<int>.Empty;
        public ImmutableArray<int> ResponderSlots { get; private set; } =
            ImmutableArray<int>.Empty;
        public bool OffererConfirmed { get; private set; }
        public bool ResponderConfirmed { get; private set; }
        public bool BothConfirmed => OffererConfirmed && ResponderConfirmed;

        public bool Involves(PlayerId player) =>
            player == Offerer || player == Responder;

        public PlayerId Other(PlayerId player) =>
            player == Offerer ? Responder : Offerer;

        public ImmutableArray<int> Offer(PlayerId player) =>
            player == Offerer ? OffererSlots : ResponderSlots;

        public void SetOffer(PlayerId player, ImmutableArray<int> slots)
        {
            if (player == Offerer) OffererSlots = slots;
            else ResponderSlots = slots;
        }

        public void Confirm(PlayerId player)
        {
            if (player == Offerer) OffererConfirmed = true;
            else ResponderConfirmed = true;
        }

        public void ClearConfirmations()
        {
            OffererConfirmed = false;
            ResponderConfirmed = false;
        }
    }
}
