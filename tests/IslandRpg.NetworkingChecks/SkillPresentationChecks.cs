using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class SkillPresentationChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "pickup gather expires and a second pickup restarts the clip",
            PickupGatherExpiresAndSecondPickupRestarts);
        checks.Add(
            "begin-skill publishes Work without waiting for a tree strike",
            BeginSkillPublishesWorkWithoutStrike);
        checks.Add(
            "present gather then pickup keeps the original clip expiry",
            PresentGatherThenPickupKeepsOriginalExpiry);
        checks.Add(
            "windup then pickup commit does not start a second Gather",
            WindupThenPickupCommitDoesNotStartSecondGather);
        checks.Add(
            "begin-skill publishes Attack for remotes",
            BeginSkillPublishesAttack);
        checks.Add(
            "begin-skill publishes Fish for remotes",
            BeginSkillPublishesFish);
        checks.Add(
            "present Idle after Fish clears the published clip",
            PresentIdleAfterFishClearsClip);
        checks.Add(
            "begin-skill publishes Build and Idle clears Work",
            BeginSkillPublishesBuildAndIdleClearsWork);
    }

    private static void PickupGatherExpiresAndSecondPickupRestarts()
    {
        var session = NewSession();
        var firstObject = session.SeedWorldObject(new(
            Guid.Parse("a1000000-0000-0000-0000-000000000001"),
            ItemIds.Logs, new Vector2(1, 0)));
        var secondObject = session.SeedWorldObject(new(
            Guid.Parse("a1000000-0000-0000-0000-000000000002"),
            ItemIds.Logs, new Vector2(1.1f, 0)));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var firstRevision = session.CaptureWorldChunkRevision(firstObject.Chunk);
        var beginFirst = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Gather));
        CheckAssert.True(beginFirst.Accepted, "the first pickup begin must publish Gather");
        var first = Send(
            session, connection, joined, 2,
            new PickUpWorldObjectIntent(
                Guid.Parse("a2000000-0000-0000-0000-000000000001"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                Handle(firstObject, firstRevision)));
        CheckAssert.True(first.Accepted, "the first pickup must commit");

        var afterFirst = session.CaptureSnapshot().Actors.Single();
        var firstPacked = afterFirst.AnimationState;
        CheckAssert.Equal(
            EntityAction.Gather,
            ActorSkillStance.UnpackAction(firstPacked),
            "pickup must publish Gather for remotes");
        CheckAssert.True(
            ActorSkillStance.UnpackGeneration(firstPacked) > 0,
            "pickup must advance the clip generation");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Gather,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "the wire snapshot must carry Gather");
        CheckAssert.True(
            player.State.HasFlag(NetworkEntityState.Interacting),
            "remotes must see interacting during pickup");

        for (var tick = 0; tick < ActorSkillStance.OneShotTicks; tick++)
            session.Tick();

        var afterExpiry = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(afterExpiry.AnimationState),
            "a finished pickup must clear Gather so remotes stop the clip");

        var secondRevision = session.CaptureWorldChunkRevision(
            secondObject.Chunk);
        var actor = session.CaptureSnapshot().Actors.Single();
        var beginSecond = Send(
            session, connection, joined, 3,
            new PresentSkillIntent(EntityAction.Gather));
        CheckAssert.True(beginSecond.Accepted,
            "the second pickup begin must publish a new Gather clip");
        var second = Send(
            session, connection, joined, 4,
            new PickUpWorldObjectIntent(
                Guid.Parse("a2000000-0000-0000-0000-000000000002"),
                actor.Gameplay.Inventory.Revision,
                actor.Gameplay.ActorRevision,
                Handle(secondObject, secondRevision)));
        CheckAssert.True(second.Accepted, "the second pickup must commit");

        var afterSecond = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Gather,
            ActorSkillStance.UnpackAction(afterSecond.AnimationState),
            "a second pickup must publish Gather again");
        CheckAssert.True(
            ActorSkillStance.UnpackGeneration(afterSecond.AnimationState) !=
            ActorSkillStance.UnpackGeneration(firstPacked),
            "a second pickup must restart the gather clip");
    }

    private static void BeginSkillPublishesWorkWithoutStrike()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Work));
        CheckAssert.True(present.Accepted,
            "begin-skill must accept a published Work clip");

        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Work,
            ActorSkillStance.UnpackAction(actor.AnimationState),
            "beginning a tree cut must publish Work before any strike");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Work,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "the wire snapshot must carry Work without a resource mutation");
        CheckAssert.True(
            player.State.HasFlag(NetworkEntityState.Interacting),
            "remotes must see interacting as soon as chopping begins");
        CheckAssert.Equal(
            1U,
            actor.Gameplay.ActorRevision,
            "begin-skill must not mutate authoritative actor gameplay");
    }

    private static void PresentGatherThenPickupKeepsOriginalExpiry()
    {
        var session = NewSession();
        var worldObject = session.SeedWorldObject(new(
            Guid.Parse("a1000000-0000-0000-0000-000000000011"),
            ItemIds.Logs, new Vector2(1, 0)));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Gather));
        CheckAssert.True(present.Accepted,
            "pickup begin must publish Gather before the mutation");
        var begun = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Gather,
            ActorSkillStance.UnpackAction(begun.AnimationState),
            "the begin snapshot must already be Gather");
        var begunPacked = begun.AnimationState;

        var pick = Send(
            session, connection, joined, 2,
            new PickUpWorldObjectIntent(
                Guid.Parse("a2000000-0000-0000-0000-000000000011"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                Handle(
                    worldObject,
                    session.CaptureWorldChunkRevision(worldObject.Chunk))));
        CheckAssert.True(pick.Accepted, "the pickup mutation must commit");
        var afterPick = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            begunPacked,
            afterPick.AnimationState,
            "an accepted pickup must not restart or extend the live Gather clip");

        for (var tick = 0; tick < ActorSkillStance.OneShotTicks - 1; tick++)
            session.Tick();
        CheckAssert.Equal(
            EntityAction.Gather,
            ActorSkillStance.UnpackAction(
                session.CaptureSnapshot().Actors.Single().AnimationState),
            "the original 0.75s window must still be playing after the commit");

        session.Tick();
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(
                session.CaptureSnapshot().Actors.Single().AnimationState),
            "the stance must go Idle at the begin-time 0.75s, not a later accept window");
    }

    private static void WindupThenPickupCommitDoesNotStartSecondGather()
    {
        var session = NewSession();
        var worldObject = session.SeedWorldObject(new(
            Guid.Parse("a1000000-0000-0000-0000-000000000021"),
            ItemIds.Logs, new Vector2(1, 0)));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Gather));
        CheckAssert.True(present.Accepted,
            "the real pickup path publishes Gather at begin");
        var begunGeneration = ActorSkillStance.UnpackGeneration(
            session.CaptureSnapshot().Actors.Single().AnimationState);

        for (var tick = 0; tick < ActorSkillStance.OneShotTicks; tick++)
            session.Tick();
        var afterWindup = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(afterWindup.AnimationState),
            "after the single-player 0.75s windup remotes must already be Idle");
        CheckAssert.Equal(
            begunGeneration,
            ActorSkillStance.UnpackGeneration(afterWindup.AnimationState),
            "expiry must keep the begin generation");

        var actor = session.CaptureSnapshot().Actors.Single();
        var pick = Send(
            session, connection, joined, 2,
            new PickUpWorldObjectIntent(
                Guid.Parse("a2000000-0000-0000-0000-000000000021"),
                actor.Gameplay.Inventory.Revision,
                actor.Gameplay.ActorRevision,
                Handle(
                    worldObject,
                    session.CaptureWorldChunkRevision(worldObject.Chunk))));
        CheckAssert.True(pick.Accepted,
            "pickup after the windup must still commit");
        var afterPick = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(afterPick.AnimationState),
            "a late pickup commit must not start a second Gather");
        CheckAssert.Equal(
            begunGeneration,
            ActorSkillStance.UnpackGeneration(afterPick.AnimationState),
            "a late pickup commit must not bump the clip generation");
    }

    private static void BeginSkillPublishesAttack()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Attack, 1.05f));
        CheckAssert.True(present.Accepted,
            "begin-attack must accept a published Attack clip");
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Attack,
            ActorSkillStance.UnpackAction(actor.AnimationState),
            "beginning a melee swing must publish Attack for remotes");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Attack,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "the wire snapshot must carry Attack");
        CheckAssert.True(
            player.State.HasFlag(NetworkEntityState.Interacting),
            "remotes must see interacting while the swing plays");
    }

    private static void BeginSkillPublishesFish()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Fish));
        CheckAssert.True(present.Accepted,
            "begin-fishing must accept a published Fish clip");
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Fish,
            ActorSkillStance.UnpackAction(actor.AnimationState),
            "beginning to fish must publish Fish for remotes");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Fish,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "the wire snapshot must carry Fish");
        CheckAssert.True(
            player.State.HasFlag(NetworkEntityState.Interacting),
            "remotes must see interacting while fishing");
    }

    private static void PresentIdleAfterFishClearsClip()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Fish));
        CheckAssert.True(present.Accepted,
            "begin-fishing must accept a published Fish clip");
        var cleared = Send(
            session, connection, joined, 2,
            new PresentSkillIntent(EntityAction.Idle));
        CheckAssert.True(cleared.Accepted,
            "present Idle must be accepted as a cancel of Fish");
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(actor.AnimationState),
            "canceling fishing must publish Idle so remotes stop Fish");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "the wire snapshot must drop Fish after Idle");
        CheckAssert.False(
            player.State.HasFlag(NetworkEntityState.Interacting),
            "remotes must not stay interacting after fishing cancels");
    }

    private static void BeginSkillPublishesBuildAndIdleClearsWork()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var present = Send(
            session, connection, joined, 1,
            new PresentSkillIntent(EntityAction.Build));
        CheckAssert.True(present.Accepted,
            "begin-build must accept a published Build clip");
        CheckAssert.Equal(
            EntityAction.Build,
            ActorSkillStance.UnpackAction(
                session.CaptureSnapshot().Actors.Single().AnimationState),
            "beginning construction must publish Build for remotes");
        var work = Send(
            session, connection, joined, 2,
            new PresentSkillIntent(EntityAction.Work));
        CheckAssert.True(work.Accepted, "Work presentation must accept");
        var cleared = Send(
            session, connection, joined, 3,
            new PresentSkillIntent(EntityAction.Idle));
        CheckAssert.True(cleared.Accepted,
            "present Idle must clear a looping Work clip");
        var entities = DedicatedServer.MaterializeSnapshotEntities(
            session.CaptureSnapshot());
        var player = entities.Single(value =>
            value.EntityKind == NetworkEntityKind.Player);
        CheckAssert.Equal(
            EntityAction.Idle,
            ActorSkillStance.UnpackAction(player.AnimationState),
            "Idle must drop Work on the wire");
    }

    private static AuthoritativeWorldSession NewSession() => new(
        identitySource: new DeterministicIdentitySource(),
        sessionId: new SessionId(Guid.Parse(
            "a9000000-0000-0000-0000-000000000001")));

    private static JoinResult Join(
        AuthoritativeWorldSession session, ClientConnectionId connection)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Skill Tester", Vector2.Zero));
        session.Drain();
        var result = pending.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "the skill fixture actor must join");
        return result;
    }

    private static IntentResult Send(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        JoinResult joined,
        long sequence,
        SessionIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, sequence, intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static WorldObjectHandle Handle(
        AuthoritativeWorldObjectSnapshot value, uint chunkRevision) => new(
            value.ObjectId, value.Chunk, value.ObjectRevision, chunkRevision,
            value.ContainerRevision);

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        private int _next;

        public PlayerIdentity CreatePlayerIdentity()
        {
            var index = ++_next;
            return new PlayerIdentity(
                new PlayerId(Guid.Parse(
                    $"aa000000-0000-0000-0000-{index:D12}")),
                new ActorId(Guid.Parse(
                    $"ab000000-0000-0000-0000-{index:D12}")));
        }

        public ReconnectToken CreateReconnectToken() =>
            new($"skill-presentation-secret-{_next}");
    }
}