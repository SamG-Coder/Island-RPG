using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;

namespace IslandRpg.Simulation;

public sealed partial class AuthoritativeWorldSession
{
    private readonly AuthoritativeSocialDirectory _social = new();

    public PlayerSocialSnapshot GetSocial(PlayerId playerId)
    {
        EnterOwner();
        try
        {
            return _social.Snapshot(playerId);
        }
        finally
        {
            ExitOwner();
        }
    }

    public GuildSnapshot? GetGuild(Guid guildId)
    {
        EnterOwner();
        try
        {
            return _social.Guild(guildId);
        }
        finally
        {
            ExitOwner();
        }
    }

    public bool IsIgnored(PlayerId owner, PlayerId other) =>
        _social.IsIgnoredPublished(owner, other);

    private IntentResult ProcessSocialIntent(
        MutableActor actor,
        SocialIntent intent)
    {
        PlayerId? notify = intent.TargetPlayerId.Value != Guid.Empty
            ? intent.TargetPlayerId
            : null;
        if (intent.TradeId != Guid.Empty &&
            _social.Trade(intent.TradeId) is { } open)
            notify = open.Other(actor.Identity.PlayerId);

        var status = intent.Kind switch
        {
            SocialCommandKind.AddFriend =>
                _social.AddFriend(actor.Identity.PlayerId, intent.TargetPlayerId),
            SocialCommandKind.RemoveFriend =>
                _social.RemoveFriend(
                    actor.Identity.PlayerId, intent.TargetPlayerId),
            SocialCommandKind.Ignore =>
                _social.Ignore(actor.Identity.PlayerId, intent.TargetPlayerId),
            SocialCommandKind.Unignore =>
                _social.Unignore(actor.Identity.PlayerId, intent.TargetPlayerId),
            SocialCommandKind.CreateGuild =>
                ProcessCreateGuild(actor, intent),
            SocialCommandKind.JoinGuild =>
                _social.JoinGuild(actor.Identity.PlayerId, intent.GuildId),
            SocialCommandKind.LeaveGuild =>
                _social.LeaveGuild(actor.Identity.PlayerId),
            SocialCommandKind.Follow =>
                ProcessFollow(actor, intent.TargetPlayerId),
            SocialCommandKind.StopFollow =>
                ProcessStopFollow(actor),
            SocialCommandKind.OfferTrade =>
                ProcessOfferTrade(actor, intent.TargetPlayerId),
            SocialCommandKind.RespondTrade =>
                _social.RespondTrade(
                    actor.Identity.PlayerId, intent.TradeId, intent.Accept),
            SocialCommandKind.SetTradeOffer =>
                _social.SetTradeOffer(
                    actor.Identity.PlayerId, intent.TradeId, intent.OfferSlots),
            SocialCommandKind.ConfirmTrade =>
                ProcessConfirmTrade(actor, intent.TradeId),
            SocialCommandKind.CancelTrade =>
                _social.CancelTradeFor(
                    actor.Identity.PlayerId, intent.TradeId),
            _ => IntentStatus.InvalidIntent
        };

        if (status != IntentStatus.Accepted)
            return Rejected(
                status,
                actor,
                SocialDetail(status),
                intent.CommandId);
        return Accepted(actor, intent.CommandId) with
        {
            Social = CollectSocialPublications(actor.Identity.PlayerId, notify)
        };
    }

    private ImmutableArray<PlayerSocialPublication> StopFollowPublication(
        MutableActor actor)
    {
        if (_social.FollowTarget(actor.Identity.PlayerId) is null)
            return default;
        _social.StopFollow(actor.Identity.PlayerId);
        return CollectSocialPublications(actor.Identity.PlayerId, null);
    }

    private ImmutableArray<PlayerSocialPublication> CollectSocialPublications(
        PlayerId actorId,
        PlayerId? notify)
    {
        var builder = ImmutableArray.CreateBuilder<PlayerSocialPublication>();
        builder.Add(new(actorId, _social.Snapshot(actorId)));
        if (notify is { } other && other != actorId)
            builder.Add(new(other, _social.Snapshot(other)));
        return builder.ToImmutable();
    }

    private static string SocialDetail(IntentStatus status) => status switch
    {
        IntentStatus.Ignored => "That player is ignoring you.",
        IntentStatus.AlreadyFriends => "That player is already a friend.",
        IntentStatus.NotFriends => "That player is not a friend.",
        IntentStatus.AlreadyIgnored => "That player is already ignored.",
        IntentStatus.NotIgnored => "That player is not ignored.",
        IntentStatus.AlreadyInGuild => "You are already in a guild.",
        IntentStatus.NotInGuild => "You are not in a guild.",
        IntentStatus.GuildNotFound => "That guild does not exist.",
        IntentStatus.GuildFull => "That guild is full.",
        IntentStatus.InvalidGuildName => "That guild name is not available.",
        IntentStatus.TradeNotFound => "That trade is no longer open.",
        IntentStatus.TradeNotReady => "The trade is not ready to complete.",
        IntentStatus.AlreadyTrading => "A trade is already open.",
        IntentStatus.NotFollowing => "You are not following anyone.",
        IntentStatus.OutOfRange => "You are too far away.",
        IntentStatus.UnknownPlayer => "That player is not here.",
        _ => "The social command was rejected."
    };

    private IntentStatus ProcessCreateGuild(
        MutableActor actor, SocialIntent intent)
    {
        var status = _social.CreateGuild(
            actor.Identity.PlayerId, intent.Text, out _);
        return status;
    }

    private IntentStatus ProcessFollow(MutableActor actor, PlayerId targetId)
    {
        if (!TryGetActor(targetId, out var target) || !target.Connected)
            return IntentStatus.UnknownPlayer;
        if (_social.IsIgnored(targetId, actor.Identity.PlayerId))
            return IntentStatus.Ignored;
        if (target.WorldLevel != actor.WorldLevel)
            return IntentStatus.WorldLevelMismatch;
        return _social.TryFollow(actor.Identity.PlayerId, targetId)
            ? IntentStatus.Accepted
            : IntentStatus.InvalidIntent;
    }

    private IntentStatus ProcessStopFollow(MutableActor actor)
    {
        if (_social.FollowTarget(actor.Identity.PlayerId) is null)
            return IntentStatus.NotFollowing;
        _social.StopFollow(actor.Identity.PlayerId);
        return IntentStatus.Accepted;
    }

    private IntentStatus ProcessOfferTrade(
        MutableActor actor, PlayerId targetId)
    {
        if (!TryGetActor(targetId, out var target) || !target.Connected)
            return IntentStatus.UnknownPlayer;
        if (_social.IsIgnored(targetId, actor.Identity.PlayerId))
            return IntentStatus.Ignored;
        if (target.WorldLevel != actor.WorldLevel ||
            Vector2.DistanceSquared(actor.Position, target.Position) >
            AuthoritativeSocialDirectory.TradeRange *
            AuthoritativeSocialDirectory.TradeRange)
            return IntentStatus.OutOfRange;
        return _social.OfferTrade(
            actor.Identity.PlayerId, targetId, out _);
    }

    private IntentStatus ProcessConfirmTrade(
        MutableActor actor, Guid tradeId)
    {
        var status = _social.ConfirmTrade(actor.Identity.PlayerId, tradeId);
        if (status != IntentStatus.Accepted)
            return status;
        if (!_social.TryTakeReadyTrade(tradeId, out var trade))
            return IntentStatus.Accepted;
        if (!TryGetActor(trade.Offerer, out var offerer) ||
            !TryGetActor(trade.Responder, out var responder))
        {
            _social.CompleteTrade(tradeId);
            return IntentStatus.TradeNotReady;
        }

        if (!TrySwapTradeInventories(offerer, responder, trade))
        {
            trade.ClearConfirmations();
            return IntentStatus.TradeNotReady;
        }

        _social.CompleteTrade(tradeId);
        return IntentStatus.Accepted;
    }

    private static bool TrySwapTradeInventories(
        MutableActor offerer,
        MutableActor responder,
        AuthoritativeSocialDirectory.MutableTrade trade)
    {
        var first = offerer.Gameplay.Inventory.Clone();
        var second = responder.Gameplay.Inventory.Clone();
        if (!TryTakeOffer(first, trade.OffererSlots, out var given) ||
            !TryTakeOffer(second, trade.ResponderSlots, out var received))
            return false;
        foreach (var item in given)
            if (!second.TryAdd(item.ItemId, item.Quantity))
                return false;
        foreach (var item in received)
            if (!first.TryAdd(item.ItemId, item.Quantity))
                return false;
        offerer.Gameplay.Inventory = first;
        responder.Gameplay.Inventory = second;
        offerer.Gameplay.InventoryRevision = checked(
            offerer.Gameplay.InventoryRevision + 1);
        responder.Gameplay.InventoryRevision = checked(
            responder.Gameplay.InventoryRevision + 1);
        offerer.Gameplay.ActorRevision = checked(
            offerer.Gameplay.ActorRevision + 1);
        responder.Gameplay.ActorRevision = checked(
            responder.Gameplay.ActorRevision + 1);
        return true;
    }

    private static bool TryTakeOffer(
        InventoryContainer inventory,
        ImmutableArray<int> slots,
        out List<(string ItemId, int Quantity)> taken)
    {
        taken = [];
        if (slots.IsDefaultOrEmpty) return true;
        foreach (var slot in slots)
        {
            if ((uint)slot >= (uint)inventory.Capacity ||
                inventory[slot] is not { } stack ||
                string.IsNullOrWhiteSpace(stack.ItemId))
                return false;
            if (!inventory.TryTake(slot, 1, out var item) ||
                item is null)
                return false;
            taken.Add((item.ItemId, 1));
        }
        return true;
    }

    private void AdvanceFollowRoutes()
    {
        foreach (var (followerId, targetId) in _social.Followers.ToArray())
        {
            if (!TryGetActor(followerId, out var follower) ||
                !follower.Connected ||
                !TryGetActor(targetId, out var target) ||
                !target.Connected ||
                follower.WorldLevel != target.WorldLevel)
            {
                _social.StopFollow(followerId);
                continue;
            }

            if (Vector2.DistanceSquared(follower.Position, target.Position) <=
                AuthoritativeSocialDirectory.FollowDistance *
                AuthoritativeSocialDirectory.FollowDistance)
            {
                follower.ClearRoute();
                continue;
            }

            var stand = AuthoritativeSocialDirectory.StandNear(
                follower.Position, target.Position);
            if (follower.Destination is { } current &&
                Vector2.DistanceSquared(current, stand) <
                AuthoritativeSocialDirectory.FollowRetargetDistance *
                AuthoritativeSocialDirectory.FollowRetargetDistance)
                continue;

            var route = GridPathfinder.Find(
                _navigation,
                follower.Position,
                stand,
                _limits.MaximumPathSearchVisited,
                worldLevel: follower.WorldLevel,
                obstacleSource: _obstacles);
            if (route.Count == 0) continue;
            if (route.Count > _limits.MaximumPathWaypoints)
                route = route.Take(_limits.MaximumPathWaypoints).ToArray();
            follower.ReplaceRoute(route);
        }
    }
}
