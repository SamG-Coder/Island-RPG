using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using OpenTK.Mathematics;

internal static class SkillPresentationChecks
{
    public static void Run()
    {
        VegetationContextDispatchesGatherFibre();
        RemotePickupRestartsGatherClip();
        RemoteIdleSnapshotClearsGatherClip();
        StancePacksGenerationWithoutLosingAction();
        BerryGatherExpiryMatchesFarmingSkill();
        AttackStanceAndApproachMatchSinglePlayer();
        FishingStanceAndReachMatchSinglePlayer();
        ShoreFishingStartsAtNetReachLikePickup();
        BuildAndCancelReachMatchSinglePlayer();
        ConstructionWaitsForServerPoseBeforeCommit();
        InRangeWindupStillDispatchesAfterActionTimeReset();
        OutOfRangeResourceRejectRetries();
        Console.WriteLine(
            "Skill presentation checks passed: remote Idle stops Gather, " +
            "pickup begin-then-commit, berry duration matches FarmingSkill, " +
            "in-range windup still commits after ActionTime reset.");
    }

    private static void VegetationContextDispatchesGatherFibre()
    {
        var labels = VegetationContextRules.Labels(berries: false);
        Assert(
            labels[0] == VegetationContextRules.GatherFibresLabel &&
            VegetationContextRules.Resolve(0, berries: false) ==
            VegetationContextRules.Choice.GatherFibres &&
            VegetationContextRules.ResourceAction(
                VegetationContextRules.Resolve(0, berries: false)) ==
            ResourceActionKind.GatherFibre &&
            VegetationContextRules.ResourceAction(
                VegetationContextRules.Resolve(0, berries: true)) ==
            ResourceActionKind.GatherBerries,
            "network right-click vegetation must dispatch GatherFibre the same way as single-player");
    }

    private static void RemotePickupRestartsGatherClip()
    {
        var entity = new WorldEntity(Vector2.Zero);
        entity.PresentSkill(EntityAction.Gather, 1, Vector2.UnitX);
        Assert(
            entity.Action == EntityAction.Gather &&
            entity.ActionTime == 0 &&
            entity.VisualGeneration == 1,
            "the first remote pickup must start Gather");
        entity.AdvanceAction(.4f);
        entity.PresentSkill(EntityAction.Gather, 1, Vector2.UnitX);
        Assert(
            entity.ActionTime >= .39,
            "the same generation must not restart the clip");
        entity.PresentSkill(EntityAction.Gather, 2, Vector2.UnitX);
        Assert(
            entity.Action == EntityAction.Gather &&
            entity.ActionTime == 0 &&
            entity.VisualGeneration == 2,
            "a second pickup generation must restart Gather");
    }

    private static void RemoteIdleSnapshotClearsGatherClip()
    {
        var entity = new WorldEntity(new Vector2(2, 3));
        var gather = ActorSkillStance.Pack(
            ActorSkillStance.Begin(
                EntityAction.Gather, ActorSkillStance.Idle, 0));
        NetworkRemotePresentation.Apply(
            entity,
            new Vector2(2, 3),
            Vector2.Zero,
            NetworkEntityState.None,
            gather,
            .05f);
        Assert(
            entity.Action == EntityAction.Gather,
            "the remote consume path must start Gather from a pickup snapshot");
        entity.AdvanceAction(.4f);
        var idle = ActorSkillStance.Pack(
            ActorSkillStance.Advance(
                ActorSkillStance.Begin(
                    EntityAction.Gather, ActorSkillStance.Idle, 0),
                ActorSkillStance.OneShotTicks));
        NetworkRemotePresentation.Apply(
            entity,
            new Vector2(2, 3),
            Vector2.Zero,
            NetworkEntityState.None,
            idle,
            .05f);
        Assert(
            entity.Action == EntityAction.Idle &&
            ActorSkillStance.UnpackAction(idle) == EntityAction.Idle,
            "an expired pickup Idle snapshot must stop the remote Gather clip");
    }

    private static void StancePacksGenerationWithoutLosingAction()
    {
        var first = ActorSkillStance.Begin(
            EntityAction.Gather, ActorSkillStance.Idle, 10);
        var second = ActorSkillStance.Begin(EntityAction.Gather, first, 12);
        var packed = ActorSkillStance.Pack(second);
        Assert(
            first.Action == EntityAction.Gather &&
            second.Generation != first.Generation &&
            ActorSkillStance.UnpackAction(packed) == EntityAction.Gather &&
            ActorSkillStance.UnpackGeneration(packed) == second.Generation &&
            ActorSkillStance.Advance(first, 10 + ActorSkillStance.OneShotTicks)
                .Action == EntityAction.Idle,
            "pickup stance must expire and pack a new generation for remotes");
    }

    private static void BerryGatherExpiryMatchesFarmingSkill()
    {
        var sickle = ItemCatalog.Get(ItemIds.BronzeSickle);
        var seconds = FarmingSkill.GatherSeconds(sickle);
        var berry = ActorSkillStance.Begin(
            EntityAction.Gather, ActorSkillStance.Idle, 0, seconds);
        var berryTicks = ActorSkillStance.TicksForSeconds(seconds);
        Assert(
            seconds < ActorSkillStance.OneShotSeconds &&
            berryTicks < ActorSkillStance.OneShotTicks &&
            berry.ExpiresAtTick == berryTicks &&
            ActorSkillStance.Advance(berry, berryTicks).Action ==
            EntityAction.Idle &&
            ActorSkillStance.FromAcceptedIntent(
                new GatherBerriesIntent(
                    Guid.NewGuid(), 1, 1, default, -1),
                berry,
                1).ExpiresAtTick == berry.ExpiresAtTick &&
            ActorSkillStance.FromAcceptedIntent(
                new GatherBerriesIntent(
                    Guid.NewGuid(), 1, 1, default, -1),
                ActorSkillStance.Advance(berry, berryTicks),
                berryTicks).Action == EntityAction.Idle,
            "berry gather expiry must use FarmingSkill.GatherSeconds, and a late commit must not start a new clip");
    }

    private static void AttackStanceAndApproachMatchSinglePlayer()
    {
        var from = new Vector2(4, 1);
        var to = new Vector2(1, 1);
        var stand = WorldActionReach.StandOff(from, to, WorldActionReach.Melee);
        var swing = ActorSkillStance.Begin(
            EntityAction.Attack, ActorSkillStance.Idle, 0, 1.05);
        var entity = new WorldEntity(to);
        NetworkRemotePresentation.Apply(
            entity, to, Vector2.Zero, NetworkEntityState.None,
            ActorSkillStance.Pack(swing), .05f);
        var playedAttack = entity.Action == EntityAction.Attack;
        var afterIdle = ActorSkillStance.Advance(
            swing, ActorSkillStance.TicksForSeconds(1.05));
        NetworkRemotePresentation.Apply(
            entity, to, Vector2.Zero, NetworkEntityState.None,
            ActorSkillStance.Pack(afterIdle), .05f);
        Assert(
            WorldActionReach.Melee == MeleeCombatService.AttackRange &&
            MathF.Abs((stand - to).Length - WorldActionReach.Melee) < .0001f &&
            ActorSkillStance.IsPublished(EntityAction.Attack) &&
            !ActorSkillStance.IsLooping(EntityAction.Attack) &&
            playedAttack &&
            entity.Action == EntityAction.Idle,
            "melee must walk to the single-player attack range and remotes must play then clear Attack");
    }

    private static void FishingStanceAndReachMatchSinglePlayer()
    {
        const float reach = 1.5f;
        var from = new Vector2(6, 1);
        var to = new Vector2(1, 1);
        var stand = WorldActionReach.StandOff(from, to, reach);
        var begun = ActorSkillStance.Begin(
            EntityAction.Fish, ActorSkillStance.Idle, 0);
        var afterCatch = ActorSkillStance.FromAcceptedIntent(
            new CatchFishIntent(Guid.NewGuid(), 1, 1, default, 0),
            begun, 10);
        var lateCatch = ActorSkillStance.FromAcceptedIntent(
            new CatchFishIntent(Guid.NewGuid(), 1, 1, default, 0),
            ActorSkillStance.Idle, 10);
        var afterIdle = ActorSkillStance.FromAcceptedIntent(
            new PresentSkillIntent(EntityAction.Idle), begun, 11);
        var entity = new WorldEntity(stand);
        NetworkRemotePresentation.Apply(
            entity, stand, Vector2.Zero, NetworkEntityState.None,
            ActorSkillStance.Pack(begun), .05f);
        const float boatDeck = .45f;
        var boatReach = reach + boatDeck;
        Assert(
            !WorldActionReach.InRange(from, to, reach) &&
            WorldActionReach.InRange(stand, to, reach) &&
            ActorSkillStance.IsPublished(EntityAction.Fish) &&
            ActorSkillStance.CanPresent(EntityAction.Idle) &&
            ActorSkillStance.IsLooping(EntityAction.Fish) &&
            afterCatch.Action == EntityAction.Fish &&
            afterCatch.Generation == begun.Generation &&
            lateCatch.Action == EntityAction.Idle &&
            afterIdle.Action == EntityAction.Idle &&
            entity.Action == EntityAction.Fish &&
            boatReach == 1.95f &&
            (stand - to).Length + boatDeck == boatReach,
            "fishing must start at net reach, remotes must hold Fish from begin not the catch, and cancel Idle must clear Fish");
    }

    private static void ShoreFishingStartsAtNetReachLikePickup()
    {
        const float reach = 1.5f;
        var from = new Vector2(6, 1);
        var to = new Vector2(1, 1);
        var stand = WorldActionReach.StandOff(from, to, reach);
        var beside = to + new Vector2(reach * .5f, 0);
        Assert(
            !GameHostWindow.NetworkShoreFishingInStartRange(from, to, reach) &&
            GameHostWindow.NetworkShoreFishingInStartRange(stand, to, reach) &&
            GameHostWindow.NetworkShoreFishingInStartRange(beside, to, reach) &&
            WorldActionReach.InRange(stand, to, reach),
            "shore fishing must start at net reach the same way pickup starts at its stand-off");
    }

    private static void BuildAndCancelReachMatchSinglePlayer()
    {
        var begun = ActorSkillStance.Begin(
            EntityAction.Build, ActorSkillStance.Idle, 0);
        var afterHammer = ActorSkillStance.FromAcceptedIntent(
            new BuildConstructionIntent(Guid.NewGuid(), 1, 1, default),
            begun, 10);
        var afterIdle = ActorSkillStance.FromAcceptedIntent(
            new PresentSkillIntent(EntityAction.Idle), begun, 11);
        var afterWorkIdle = ActorSkillStance.FromAcceptedIntent(
            new PresentSkillIntent(EntityAction.Idle),
            ActorSkillStance.Begin(
                EntityAction.Work, ActorSkillStance.Idle, 0),
            11);
        Assert(
            ActorSkillStance.IsLooping(EntityAction.Build) &&
            ActorSkillStance.IsPublished(EntityAction.Build) &&
            afterHammer.Action == EntityAction.Build &&
            afterHammer.Generation == begun.Generation &&
            afterIdle.Action == EntityAction.Idle &&
            afterWorkIdle.Action == EntityAction.Idle &&
            WorldActionReach.BoatBoard == 1.25f &&
            WorldActionReach.CookStew == .82f &&
            WorldActionReach.Placeable(null) ==
            WorldActionReach.GroundPickup,
            "build must stay Build through hammer commits, Idle must clear looping Work/Build, and place/board/stew ranges must match single-player");
    }

    private static void InRangeWindupStillDispatchesAfterActionTimeReset()
    {
        const float duration = .75f;
        Assert(
            !GameHostWindow.NetworkResourceWindupReady(
                actionTime: 0, duration, clock: 10, commitAt: 10.75) &&
            GameHostWindow.NetworkResourceWindupReady(
                actionTime: duration, duration, clock: 10, commitAt: 10.75) &&
            GameHostWindow.NetworkResourceWindupReady(
                actionTime: 0, duration, clock: 10.75, commitAt: 10.75) &&
            !GameHostWindow.NetworkResourceWindupReady(
                actionTime: 0, duration, clock: 10, commitAt: 0),
            "an in-range gather must commit after the clip even if ActionTime is reset");
    }

    private static void OutOfRangeResourceRejectRetries()
    {
        Assert(
            GameHostWindow.ShouldRetryNetworkResourceReject(
                false,
                CommandRejectionCode.Impossible,
                "The resource is outside interaction range.") &&
            GameHostWindow.ShouldRetryNetworkResourceReject(
                false, CommandRejectionCode.OutOfOrder, "stale") &&
            !GameHostWindow.ShouldRetryNetworkResourceReject(
                false,
                CommandRejectionCode.Impossible,
                "The carried inventory cannot hold gathered fibre.") &&
            !GameHostWindow.ShouldRetryNetworkResourceReject(
                true, CommandRejectionCode.None, string.Empty),
            "an in-range start that the server still sees as far must retry, " +
            "but a full inventory must not");
    }

    private static void ConstructionWaitsForServerPoseBeforeCommit()
    {
        var site = new Vector2(8, 3);
        var client = WorldActionReach.StandOff(
            new Vector2(1, 3), site, WorldActionReach.Construction);
        var authorityStillWalking = new Vector2(2, 3);
        Assert(
            WorldActionReach.InRange(
                client, site, WorldActionReach.Construction) &&
            !GameHostWindow.NetworkWorldActionReadyToCommit(
                client, authorityStillWalking, site,
                WorldActionReach.Construction) &&
            GameHostWindow.NetworkWorldActionReadyToCommit(
                client, client, site, WorldActionReach.Construction) &&
            GameHostWindow.ShouldRetryNetworkWorldActionReject(
                false, "OutOfRange") &&
            GameHostWindow.DescribeNetworkActionRejection(
                new ActionResultMessage(
                    1, 1, Guid.NewGuid(), false,
                    CommandRejectionCode.Impossible, "OutOfRange",
                    1, 1)).Contains("stand closer", StringComparison.Ordinal),
            "a construction commit must wait until the server pose is in range, then retry OutOfRange");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}