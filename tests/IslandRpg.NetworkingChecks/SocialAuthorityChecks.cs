using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class SocialAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("social trade commits only when both sides confirm",
            MutualTradeCommitsOnce);
        checks.Add("social ignore blocks trade follow and is restored",
            IgnoreBlocksAndReconnectRestoresLists);
        checks.Add("social follow then cancel uses movement authority",
            FollowThenCancel);
        checks.Add("social follow stands beside the target not on them",
            FollowStandsBesideTarget);
        checks.Add("social walk clears follow so follow again works",
            WalkClearsFollowAndAllowsFollowAgain);
        checks.Add("social guilds friends and ignore lists are server owned",
            GuildFriendIgnoreMembership);
        checks.Add("social disconnect cancels an open trade",
            DisconnectCancelsOpenTrade);
    }

    private static void MutualTradeCommitsOnce()
    {
        var session = new AuthoritativeWorldSession();
        var first = Join(session, "Elara", new Vector2(1, 0), "large_rock");
        var second = Join(session, "Aveline", new Vector2(1.2f, 0), "sticks");
        var offer = Command(session, first, new SocialIntent(
            Guid.NewGuid(), first.InventoryRevision, first.ActorRevision,
            SocialCommandKind.OfferTrade, second.PlayerId));
        CheckAssert.True(offer.Accepted, "a nearby player must be able to offer trade");
        var tradeId = session.GetSocial(first.PlayerId).OpenTradeId;
        CheckAssert.True(tradeId is { } id && id != Guid.Empty,
            "an open trade must be visible on the offerer");
        var respond = Command(session, second, new SocialIntent(
            Guid.NewGuid(), second.InventoryRevision, second.ActorRevision,
            SocialCommandKind.RespondTrade, TradeId: tradeId!.Value,
            Accept: true));
        CheckAssert.True(respond.Accepted, "the partner must accept the trade");
        var setFirst = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            session.GetSocial(first.PlayerId).OpenTradeId is null
                ? first.InventoryRevision
                : Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.SetTradeOffer,
            TradeId: tradeId.Value,
            OfferSlots: [Slot(session, first.PlayerId, "large_rock")]));
        CheckAssert.True(setFirst.Accepted, "the offerer must set a slot offer");
        var setSecond = Command(session, second, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, second.PlayerId).Inventory.Revision,
            Snapshot(session, second.PlayerId).ActorRevision,
            SocialCommandKind.SetTradeOffer,
            TradeId: tradeId.Value,
            OfferSlots: [Slot(session, second.PlayerId, "sticks")]));
        CheckAssert.True(setSecond.Accepted, "the partner must set a slot offer");
        var confirmFirst = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.ConfirmTrade,
            TradeId: tradeId.Value));
        CheckAssert.True(confirmFirst.Accepted, "first confirm must wait for the partner");
        CheckAssert.Equal(1, Count(session, first.PlayerId, "large_rock"),
            "inventory must stay put until both sides confirm");
        var confirmSecond = Command(session, second, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, second.PlayerId).Inventory.Revision,
            Snapshot(session, second.PlayerId).ActorRevision,
            SocialCommandKind.ConfirmTrade,
            TradeId: tradeId.Value));
        CheckAssert.True(confirmSecond.Accepted, "second confirm must commit the trade");
        CheckAssert.Equal(1, Count(session, first.PlayerId, "sticks"),
            "the offerer must receive the partner item");
        CheckAssert.Equal(1, Count(session, second.PlayerId, "large_rock"),
            "the partner must receive the offered item");
        CheckAssert.True(
            session.GetSocial(first.PlayerId).OpenTradeId is null,
            "a committed trade must close");

        var missing = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.OfferTrade,
            new PlayerId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))));
        CheckAssert.False(missing.Accepted,
            "trade must reject a missing partner");
    }

    private static void IgnoreBlocksAndReconnectRestoresLists()
    {
        var session = new AuthoritativeWorldSession();
        var first = Join(session, "Elara", new Vector2(0, 0));
        var second = Join(session, "Aveline", new Vector2(1, 0));
        var ignore = Command(session, second, new SocialIntent(
            Guid.NewGuid(), second.InventoryRevision, second.ActorRevision,
            SocialCommandKind.Ignore, first.PlayerId));
        CheckAssert.True(ignore.Accepted, "ignore must add the other player");
        CheckAssert.True(
            session.GetSocial(second.PlayerId).Ignored.Contains(first.PlayerId),
            "ignore must appear on the owning list");
        var trade = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.OfferTrade, second.PlayerId));
        CheckAssert.Equal(IntentStatus.Ignored, trade.Status,
            "ignore must block incoming trade");
        var follow = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.Follow, second.PlayerId));
        CheckAssert.Equal(IntentStatus.Ignored, follow.Status,
            "ignore must block incoming follow");

        var checkpoint = session.CaptureCheckpoint();
        var restored = new AuthoritativeWorldSession(sessionId: checkpoint.SessionId);
        restored.RestoreCheckpoint(checkpoint);
        CheckAssert.True(
            restored.GetSocial(second.PlayerId).Ignored.Contains(first.PlayerId),
            "reconnect restore must keep the ignore list");
        CheckAssert.True(restored.IsIgnored(second.PlayerId, first.PlayerId),
            "restored ignore must still block the same player");
    }

    private static void FollowThenCancel()
    {
        var session = new AuthoritativeWorldSession();
        var follower = Join(session, "Elara", new Vector2(0, 0));
        var target = Join(session, "Aveline", new Vector2(8, 0));
        var follow = Command(session, follower, new SocialIntent(
            Guid.NewGuid(), follower.InventoryRevision, follower.ActorRevision,
            SocialCommandKind.Follow, target.PlayerId));
        CheckAssert.True(follow.Accepted, "follow must start against a live player");
        CheckAssert.Equal(
            target.PlayerId,
            session.GetSocial(follower.PlayerId).FollowTarget,
            "follow must record the server-owned target");
        for (var step = 0; step < 30; step++)
            session.Tick();
        var afterActor = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == follower.PlayerId);
        CheckAssert.True(
            afterActor.Position.X > 0.2f,
            "follow must walk the follower using movement authority");
        var stop = Command(session, follower, new SocialIntent(
            Guid.NewGuid(),
            afterActor.Gameplay.Inventory.Revision,
            afterActor.Gameplay.ActorRevision,
            SocialCommandKind.StopFollow));
        CheckAssert.True(stop.Accepted, "the follower must be able to cancel");
        CheckAssert.True(
            session.GetSocial(follower.PlayerId).FollowTarget is null,
            "cancel must clear the follow target");
    }

    private static void FollowStandsBesideTarget()
    {
        var session = new AuthoritativeWorldSession();
        var follower = Join(session, "Elara", new Vector2(0, 0));
        var target = Join(session, "Aveline", new Vector2(6, 0));
        var follow = Command(session, follower, new SocialIntent(
            Guid.NewGuid(), follower.InventoryRevision, follower.ActorRevision,
            SocialCommandKind.Follow, target.PlayerId));
        CheckAssert.True(follow.Accepted, "follow must start");
        for (var step = 0; step < 240; step++)
            session.Tick();
        var after = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == follower.PlayerId);
        var lead = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == target.PlayerId);
        var distance = Vector2.Distance(after.Position, lead.Position);
        CheckAssert.True(
            after.Position.X > 1.5f,
            "follow must walk the follower toward the target");
        CheckAssert.True(
            distance >= 1.55f,
            "follow must stop beside the target, not on their tile");
    }

    private static void WalkClearsFollowAndAllowsFollowAgain()
    {
        var session = new AuthoritativeWorldSession();
        var follower = Join(session, "Elara", new Vector2(0, 0));
        var target = Join(session, "Aveline", new Vector2(4, 0));
        var follow = Command(session, follower, new SocialIntent(
            Guid.NewGuid(), follower.InventoryRevision, follower.ActorRevision,
            SocialCommandKind.Follow, target.PlayerId));
        CheckAssert.True(follow.Accepted, "first follow must start");
        var walk = session.EnqueueIntentAsync(new ActorCommand(
            follower.Connection,
            follower.PlayerId,
            NextSequence(session, follower.PlayerId),
            new WalkIntent(new Vector2(0, 3))));
        session.Drain();
        var walked = walk.GetAwaiter().GetResult();
        CheckAssert.True(walked.Accepted, "a click-away walk must be accepted");
        CheckAssert.True(
            session.GetSocial(follower.PlayerId).FollowTarget is null,
            "clicking away must mark the player as not following");
        CheckAssert.False(
            walked.Social.IsDefaultOrEmpty,
            "walk must publish the cleared follow list to the owner");
        var again = Command(session, follower, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, follower.PlayerId).Inventory.Revision,
            Snapshot(session, follower.PlayerId).ActorRevision,
            SocialCommandKind.Follow, target.PlayerId));
        CheckAssert.True(again.Accepted, "follow again must work after a click-away");
        CheckAssert.Equal(
            target.PlayerId,
            session.GetSocial(follower.PlayerId).FollowTarget,
            "the second follow must record the same target");
    }

    private static void GuildFriendIgnoreMembership()
    {
        var session = new AuthoritativeWorldSession();
        var first = Join(session, "Elara", Vector2.Zero);
        var second = Join(session, "Aveline", new Vector2(1, 0));
        var friend = Command(session, first, new SocialIntent(
            Guid.NewGuid(), first.InventoryRevision, first.ActorRevision,
            SocialCommandKind.AddFriend, second.PlayerId));
        CheckAssert.True(friend.Accepted, "adding a friend must succeed");
        CheckAssert.True(
            session.GetSocial(first.PlayerId).Friends.Contains(second.PlayerId),
            "the friend must appear on the owning list");
        var unfriend = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.RemoveFriend, second.PlayerId));
        CheckAssert.True(unfriend.Accepted, "removing a friend must succeed");
        CheckAssert.False(
            session.GetSocial(first.PlayerId).Friends.Contains(second.PlayerId),
            "the friend must leave the owning list");

        var create = Command(session, first, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, first.PlayerId).Inventory.Revision,
            Snapshot(session, first.PlayerId).ActorRevision,
            SocialCommandKind.CreateGuild,
            Text: "Oak Guard"));
        CheckAssert.True(create.Accepted, "creating a guild must succeed");
        var guildId = session.GetSocial(first.PlayerId).GuildId;
        CheckAssert.True(guildId is { } created && created != Guid.Empty,
            "the creator must be in the new guild");
        var join = Command(session, second, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, second.PlayerId).Inventory.Revision,
            Snapshot(session, second.PlayerId).ActorRevision,
            SocialCommandKind.JoinGuild,
            GuildId: guildId!.Value));
        CheckAssert.True(join.Accepted, "a second player must be able to join");
        CheckAssert.Equal(
            2,
            session.GetGuild(guildId.Value)!.Value.Members.Length,
            "the guild roster must include both members");
        var leave = Command(session, second, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, second.PlayerId).Inventory.Revision,
            Snapshot(session, second.PlayerId).ActorRevision,
            SocialCommandKind.LeaveGuild));
        CheckAssert.True(leave.Accepted, "leaving a guild must succeed");
        CheckAssert.True(
            session.GetSocial(second.PlayerId).GuildId is null,
            "the leaving player must no longer be in a guild");
    }

    private static void DisconnectCancelsOpenTrade()
    {
        var session = new AuthoritativeWorldSession();
        var first = Join(session, "Elara", new Vector2(0, 0));
        var second = Join(session, "Aveline", new Vector2(1, 0));
        var offer = Command(session, first, new SocialIntent(
            Guid.NewGuid(), first.InventoryRevision, first.ActorRevision,
            SocialCommandKind.OfferTrade, second.PlayerId));
        CheckAssert.True(offer.Accepted, "offer must open a trade");
        CheckAssert.True(
            session.GetSocial(first.PlayerId).OpenTradeId is not null,
            "both players must be locked into the open trade");
        var disconnect = session.EnqueueDisconnectAsync(new DisconnectRequest(
            first.Connection, first.PlayerId));
        session.Drain();
        var result = disconnect.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "disconnect must succeed");
        CheckAssert.True(
            session.GetSocial(second.PlayerId).OpenTradeId is null,
            "disconnect must cancel the leftover trade");
        var retry = Command(session, second, new SocialIntent(
            Guid.NewGuid(),
            Snapshot(session, second.PlayerId).Inventory.Revision,
            Snapshot(session, second.PlayerId).ActorRevision,
            SocialCommandKind.OfferTrade, first.PlayerId));
        CheckAssert.Equal(IntentStatus.UnknownPlayer, retry.Status,
            "the disconnected partner is gone, but the trade lock must not be AlreadyTrading");
    }

    private sealed record JoinedPlayer(
        ClientConnectionId Connection,
        PlayerId PlayerId,
        uint InventoryRevision,
        uint ActorRevision);

    private static JoinedPlayer Join(
        AuthoritativeWorldSession session,
        string name,
        Vector2 position,
        string? itemId = null)
    {
        var connection = ClientConnectionId.New();
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            name,
            position,
            itemId is null ? null : [new InitialInventoryItem(itemId)]));
        session.Drain();
        var joined = pending.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, $"{name} must join");
        var gameplay = Snapshot(session, joined.Identity.PlayerId);
        return new(
            connection,
            joined.Identity.PlayerId,
            gameplay.Inventory.Revision,
            gameplay.ActorRevision);
    }

    private static IntentResult Command(
        AuthoritativeWorldSession session,
        JoinedPlayer player,
        SocialIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            player.Connection,
            player.PlayerId,
            NextSequence(session, player.PlayerId),
            intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static long NextSequence(
        AuthoritativeWorldSession session,
        PlayerId playerId)
    {
        var actor = session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == playerId);
        return actor.LastProcessedCommandSequence + 1;
    }

    private static PlayerGameplaySnapshot Snapshot(
        AuthoritativeWorldSession session,
        PlayerId playerId) =>
        session.CaptureSnapshot().Actors.Single(value =>
            value.PlayerId == playerId).Gameplay;

    private static int Slot(
        AuthoritativeWorldSession session,
        PlayerId playerId,
        string itemId)
    {
        var gameplay = Snapshot(session, playerId);
        var slot = gameplay.Inventory.Slots.FirstOrDefault(value =>
            value.ItemId == itemId);
        CheckAssert.True(slot.ItemId == itemId, $"missing {itemId}");
        return slot.Slot;
    }

    private static int Count(
        AuthoritativeWorldSession session,
        PlayerId playerId,
        string itemId) =>
        Snapshot(session, playerId).Inventory.Slots.Count(value =>
            value.ItemId == itemId);
}
