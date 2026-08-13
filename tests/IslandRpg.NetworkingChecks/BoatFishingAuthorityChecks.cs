using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Fishing;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Server;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused boat/fishing authority checks registered by the networking runner.
/// </summary>
internal static class BoatFishingAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("boat provisioning is stable unique and collision safe",
            ProvisioningIsStableAndUnique);
        checks.Add("island join and boat provisioning commit atomically",
            IslandJoinProvisionIsAtomic);
        checks.Add("boat access occupancy route stop and landing are authoritative",
            BoatLifecycleIsAuthoritative);
        checks.Add("boat route completion advances semantic revision once",
            RouteCompletionPublishesFinalRevision);
        checks.Add("boat routes reserve cells and never overlap",
            BoatRoutesReserveCellsWithoutOverlap);
        checks.Add("boat routes cannot cross during one authority step",
            BoatRoutesCannotCrossDuringOneStep);
        checks.Add("boat route planning spam is cadence bounded and durable",
            RoutePlanningSpamIsBoundedAndDurable);
        checks.Add("boat route command receipts precede planning cadence",
            RouteReceiptReplayPrecedesPlanningCadence);
        checks.Add("boat checkpoint rejects occupancy and network identity conflicts",
            CheckpointRejectsInvalidIdentityAndOccupancy);
        checks.Add("fishing validates equipment skill range cadence stock and XP",
            FishingLifecycleIsAuthoritative);
        checks.Add("fishing miss and catch rolls survive checkpoint restore",
            FishingRollsAreDeterministicAcrossRestart);
        checks.Add("session fishing derives aboard position and reach",
            SessionFishingUsesAuthoritativeBoatPosition);
        checks.Add("boat session movement restart and receipts remain authoritative",
            SessionBoatLifecycleIsDurableAndIdempotent);
    }

    private static void ProvisioningIsStableAndUnique()
    {
        var navigation = new TestBoatNavigation();
        var authority = new AuthoritativeBoatTransactions(navigation);
        var ownerA = Player(1);
        var ownerB = Player(2);
        var first = authority.ProvisionPlayerBoat(ownerA, Vector2.Zero);
        var replay = authority.ProvisionPlayerBoat(ownerA, new(99, 99));
        var second = authority.ProvisionPlayerBoat(ownerB, Vector2.Zero);
        CheckAssert.Equal(first.BoatId, replay.BoatId,
            "provisioning the same owner must reuse its stable boat");
        CheckAssert.Equal(first.Position, replay.Position,
            "reprovisioning must not relocate a durable boat");
        CheckAssert.False(first.BoatId == second.BoatId,
            "distinct owners require distinct stable boat IDs");
        CheckAssert.False(first.NetworkEntityId == second.NetworkEntityId,
            "distinct boats require distinct network entity IDs");
        CheckAssert.False(Cell(first.Position) == Cell(second.Position),
            "deterministic provisioning must skip occupied moorings");
        CheckAssert.Throws<ArgumentException>(() => authority.Seed(new(
                new BoatId(Guid.NewGuid()), Player(3), first.Position)),
            "manual seeds must not overlap existing boat cells");
    }

    private static void IslandJoinProvisionIsAtomic()
    {
        var session = new AuthoritativeWorldSession(
            identitySource: new FixedIdentitySource(),
            boatTransactions: new AuthoritativeBoatTransactions(
                new NoMooringBoatNavigation()));
        var connection = ClientConnectionId.New();
        var rejected = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Boat tester", new(.5f, 1.5f),
            ProvisionBoat: true));
        session.Drain();
        CheckAssert.Equal(JoinStatus.InvalidRequest,
            rejected.GetAwaiter().GetResult().Status,
            "a join must fail when no authoritative mooring exists");
        CheckAssert.Equal(0, session.ActorCount,
            "failed boat provisioning must not publish an actor");
        CheckAssert.Equal(0, session.CaptureBoats().Length,
            "failed boat provisioning must not publish a partial boat");

        var retried = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Boat tester", new(.5f, 1.5f)));
        session.Drain();
        CheckAssert.True(retried.GetAwaiter().GetResult().Accepted,
            "the failed join must not retain its connection mapping");

        var island = Session(
            new SessionId(Guid.Parse(
                "bd000000-0000-0000-0000-000000000021")),
            new TestBoatNavigation());
        var islandConnection = ClientConnectionId.New();
        BoatStateDelta? provisioned = null;
        island.BoatStateCommitted += delta => provisioned = delta;
        var joined = island.EnqueueJoinAsync(new JoinRequest(
            islandConnection, "Island tester", new(.5f, 1.5f),
            ProvisionBoat: true));
        island.Drain();
        var result = joined.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted && result.Boat is not null,
            "a valid island join must return its atomic boat baseline");
        CheckAssert.Equal(result.Identity.PlayerId,
            result.Boat!.OwnerPlayerId,
            "the atomic boat must belong to the joined player");
        CheckAssert.True(provisioned is
            { Kind: BoatChangeKind.Added, Current: not null },
            "a committed atomic provision must publish one Added event");
    }

    private static void BoatLifecycleIsAuthoritative()
    {
        var authority = new AuthoritativeBoatTransactions(
            new TestBoatNavigation(),
            new AuthoritativeBoatTransactionOptions
            {
                MovementSpeed = 4,
                MaximumPathSearchVisited = 2_048,
                MaximumRouteWaypoints = 128
            });
        var owner = Player(10);
        var intruder = Player(11);
        var boat = authority.Seed(new(
            new BoatId(Guid.NewGuid()), owner, new(.5f, .5f),
            GroupId: "crew"));
        var intruderActor = Actor(intruder, Actor(11), new(.5f, 1.5f));
        var denied = authority.Execute(intruderActor,
            new BoardBoatTransaction(Context(intruderActor), Ref(boat)));
        CheckAssert.Equal(BoatTransactionStatus.AccessDenied, denied.Status,
            "a non-owner outside the group must not board");

        var actorId = Actor(10);
        var actor = Actor(owner, actorId, new(.5f, 1.5f));
        var board = authority.Execute(actor,
            new BoardBoatTransaction(Context(actor), Ref(boat)));
        CheckAssert.True(board.Accepted,
            "the owner should board from interaction range");
        CheckAssert.Equal(boat.BoatId,
            board.ActorTransition!.BoardedBoatId!.Value,
            "boarding must publish the attached boat");
        boat = board.BoatDelta!.Current!;
        actor = actor with { Gameplay = board.Gameplay, Position = boat.Position };

        var occupied = authority.Execute(intruderActor,
            new BoardBoatTransaction(Context(intruderActor), Ref(boat)));
        CheckAssert.Equal(BoatTransactionStatus.BoatOccupied, occupied.Status,
            "a second actor must not share the one-person boat");

        var stale = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor),
                new(boat.BoatId, boat.Revision - 1), new(4.5f, .5f)));
        CheckAssert.Equal(BoatTransactionStatus.StaleBoatRevision, stale.Status,
            "route changes require the exact boat revision");
        var unreachable = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor), Ref(boat),
                new(200, 200)));
        CheckAssert.Equal(BoatTransactionStatus.RouteUnreachable,
            unreachable.Status,
            "authority must reject a target with no bounded water route");
        authority.Advance(.2);

        var move = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor), Ref(boat),
                new(4.5f, .5f)));
        CheckAssert.True(move.Accepted &&
                         move.BoatDelta!.Current!.Destination is not null,
            "a target-only move must produce an authority-owned route");
        boat = move.BoatDelta!.Current!;
        var movingRevision = boat.Revision;
        var partial = authority.Advance(.1);
        CheckAssert.Equal(0, partial.Length,
            "in-flight transform integration must not emit semantic churn");
        CheckAssert.Equal(movingRevision,
            authority.Capture(boat.BoatId).Revision,
            "in-flight movement must preserve semantic revision");
        var stop = authority.Execute(actor,
            new StopBoatTransaction(Context(actor),
                Ref(authority.Capture(boat.BoatId))));
        CheckAssert.True(stop.Accepted &&
                         stop.BoatDelta!.Current!.Destination is null,
            "explicit stop must clear the authority route");

        boat = stop.BoatDelta!.Current!;
        var landed = authority.Execute(actor,
            new DisembarkBoatTransaction(Context(actor), Ref(boat),
                new(.5f, 1.5f)));
        CheckAssert.True(landed.Accepted &&
                         landed.ActorTransition!.BoardedBoatId is null,
            "valid shore disembark must clear occupancy");
    }

    private static void RouteCompletionPublishesFinalRevision()
    {
        var authority = new AuthoritativeBoatTransactions(
            new TestBoatNavigation(),
            new AuthoritativeBoatTransactionOptions { MovementSpeed = 10 });
        var owner = Player(20);
        var actorId = Actor(20);
        var boat = authority.Seed(new(
            new BoatId(Guid.NewGuid()), owner, new(.5f, .5f)));
        var actor = Actor(owner, actorId, new(.5f, 1.5f));
        var boarded = authority.Execute(actor,
            new BoardBoatTransaction(Context(actor), Ref(boat)));
        boat = boarded.BoatDelta!.Current!;
        actor = actor with { Gameplay = boarded.Gameplay, Position = boat.Position };
        var moved = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor), Ref(boat),
                new(1.5f, .5f)));
        boat = moved.BoatDelta!.Current!;
        var completed = authority.Advance(1);
        CheckAssert.Equal(1, completed.Length,
            "route completion must publish one semantic delta");
        CheckAssert.Equal(boat.Revision + 1,
            completed[0].Current!.Revision,
            "arrival must advance the boat revision exactly once");
        CheckAssert.True(completed[0].Current!.Destination is null,
            "arrival must publish a cleared route");
        CheckAssert.Equal(new Vector2(1.5f, .5f),
            completed[0].Current!.Position,
            "arrival must publish the final target transform");
    }

    private static void BoatRoutesReserveCellsWithoutOverlap()
    {
        var navigation = new OpenWaterBoatNavigation();
        var authority = new AuthoritativeBoatTransactions(
            navigation,
            new AuthoritativeBoatTransactionOptions
            {
                MovementSpeed = 10,
                MaximumPathSearchVisited = 2_048,
                MaximumRouteWaypoints = 128
            });
        var ownerA = Player(22);
        var ownerB = Player(23);
        var actorA = Actor(ownerA, Actor(22), new(.5f, 1.5f));
        var actorB = Actor(ownerB, Actor(23), new(2.5f, 3.5f));
        var boatA = authority.Seed(new(
            new BoatId(Guid.Parse(
                "bd400000-0000-0000-0000-000000000001")),
            ownerA,
            new(.5f, .5f)));
        var boatB = authority.Seed(new(
            new BoatId(Guid.Parse(
                "bd400000-0000-0000-0000-000000000002")),
            ownerB,
            new(2.5f, 2.5f)));

        var boardedA = authority.Execute(actorA,
            new BoardBoatTransaction(Context(actorA), Ref(boatA)));
        boatA = boardedA.BoatDelta!.Current!;
        actorA = actorA with
        {
            Gameplay = boardedA.Gameplay,
            Position = boatA.Position
        };
        var boardedB = authority.Execute(actorB,
            new BoardBoatTransaction(Context(actorB), Ref(boatB)));
        boatB = boardedB.BoatDelta!.Current!;
        actorB = actorB with
        {
            Gameplay = boardedB.Gameplay,
            Position = boatB.Position
        };

        var occupiedDestination = authority.Execute(actorA,
            new MoveBoatTransaction(
                Context(actorA), Ref(boatA), boatB.Position));
        CheckAssert.Equal(BoatTransactionStatus.RouteUnreachable,
            occupiedDestination.Status,
            "route admission must reject another boat's occupied cell");
        authority.Advance(.25);

        var sharedDestination = new Vector2(2.5f, .5f);
        var moveA = authority.Execute(actorA,
            new MoveBoatTransaction(
                Context(actorA), Ref(boatA), sharedDestination));
        var moveB = authority.Execute(actorB,
            new MoveBoatTransaction(
                Context(actorB), Ref(boatB), sharedDestination));
        CheckAssert.True(moveA.Accepted && moveB.Accepted,
            "independent routes may converge only under fixed-step arbitration");

        var transitions = authority.Advance(1);
        CheckAssert.Equal(2, transitions.Length,
            "arrival and collision-stop must each publish one semantic delta");
        var currentA = authority.Capture(boatA.BoatId);
        var currentB = authority.Capture(boatB.BoatId);
        CheckAssert.False(Cell(currentA.Position) == Cell(currentB.Position),
            "stable reservation order must stop a converging boat before overlap");
        CheckAssert.Equal(sharedDestination, currentA.Position,
            "the lower stable boat ID should win the contested destination");
        CheckAssert.True(currentB.Destination is null,
            "the losing converging route must be stopped authoritatively");

        var checkpoint = authority.CaptureCheckpoint();
        var restored = new AuthoritativeBoatTransactions(navigation);
        restored.RestoreCheckpoint(checkpoint);
        var restoredBoats = restored.CaptureBoats();
        CheckAssert.Equal(2, restoredBoats.Length,
            "collision-free movement must remain checkpoint-restorable");
        CheckAssert.False(
            Cell(restoredBoats[0].Position) == Cell(restoredBoats[1].Position),
            "checkpoint restore must preserve the collision invariant");
    }

    private static void BoatRoutesCannotCrossDuringOneStep()
    {
        var navigation = new OpenWaterBoatNavigation();
        var authority = new AuthoritativeBoatTransactions(
            navigation,
            new AuthoritativeBoatTransactionOptions
            {
                MovementSpeed = 10,
                MaximumPathSearchVisited = 2_048,
                MaximumRouteWaypoints = 128
            });
        var ownerA = Player(24);
        var ownerB = Player(25);
        var actorA = Actor(ownerA, Actor(24), new(.5f, 1.5f));
        var actorB = Actor(ownerB, Actor(25), new(2.5f, 1.5f));
        var boatA = authority.Seed(new(
            new BoatId(Guid.Parse(
                "bd400000-0000-0000-0000-000000000011")),
            ownerA,
            new(.5f, .5f)));
        var boatB = authority.Seed(new(
            new BoatId(Guid.Parse(
                "bd400000-0000-0000-0000-000000000012")),
            ownerB,
            new(2.5f, .5f)));

        var boardedA = authority.Execute(actorA,
            new BoardBoatTransaction(Context(actorA), Ref(boatA)));
        boatA = boardedA.BoatDelta!.Current!;
        actorA = actorA with
        {
            Gameplay = boardedA.Gameplay,
            Position = boatA.Position
        };
        var boardedB = authority.Execute(actorB,
            new BoardBoatTransaction(Context(actorB), Ref(boatB)));
        boatB = boardedB.BoatDelta!.Current!;
        actorB = actorB with
        {
            Gameplay = boardedB.Gameplay,
            Position = boatB.Position
        };

        var targetA = new Vector2(2.5f, 2.5f);
        var targetB = new Vector2(.5f, 2.5f);
        var moveA = authority.Execute(actorA,
            new MoveBoatTransaction(Context(actorA), Ref(boatA), targetA));
        var moveB = authority.Execute(actorB,
            new MoveBoatTransaction(Context(actorB), Ref(boatB), targetB));
        CheckAssert.True(moveA.Accepted && moveB.Accepted,
            "distinct routes may be admitted before their paths converge");

        var transitions = authority.Advance(1);
        CheckAssert.Equal(2, transitions.Length,
            "a completed crossing winner and stopped loser must both publish");
        var currentA = authority.Capture(boatA.BoatId);
        var currentB = authority.Capture(boatB.BoatId);
        CheckAssert.Equal(targetA, currentA.Position,
            "stable boat ordering should let the lower ID finish first");
        CheckAssert.True(currentB.Destination is null,
            "the later crossing route must stop before entering reserved cells");
        CheckAssert.False(Cell(currentA.Position) == Cell(currentB.Position),
            "crossing arbitration must preserve unique occupied cells");
    }

    private static void RoutePlanningSpamIsBoundedAndDurable()
    {
        var navigation = new TestBoatNavigation();
        var calls = 0;
        IReadOnlyList<Vector2> CountedPlanner(
            IBoatNavigationQuery query,
            Vector2 start,
            Vector2 target,
            int maximumVisited)
        {
            calls++;
            return BoatRoutePlanner.Find(
                query, start, target, maximumVisited);
        }

        var options = new AuthoritativeBoatTransactionOptions
        {
            PlanningCadenceSeconds = .25,
            MaximumPlansPerAdvance = 1,
            MaximumPathSearchVisited = 2_048,
            MaximumRouteWaypoints = 128
        };
        var authority = new AuthoritativeBoatTransactions(
            navigation, options,
            AuthoritativeBoatTransactions.DeriveNetworkEntityId,
            CountedPlanner);
        var owner = Player(21);
        var actor = Actor(owner, Actor(21), new(.5f, 1.5f));
        var boat = authority.Seed(new(
            new BoatId(Guid.NewGuid()), owner, new(.5f, .5f)));
        var boarded = authority.Execute(actor,
            new BoardBoatTransaction(Context(actor), Ref(boat)));
        boat = boarded.BoatDelta!.Current!;
        actor = actor with
        {
            Gameplay = boarded.Gameplay,
            Position = boat.Position
        };

        var target = new Vector2(3.5f, .5f);
        var first = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor), Ref(boat), target));
        CheckAssert.True(first.Accepted && calls == 1,
            "the first route command should invoke the planner exactly once");
        boat = first.BoatDelta!.Current!;
        var coalesced = authority.Execute(actor,
            new MoveBoatTransaction(Context(actor), Ref(boat), target));
        CheckAssert.True(coalesced.Accepted &&
                         coalesced.BoatDelta is null && calls == 1,
            "the active semantic destination must coalesce without replanning");
        CheckAssert.Equal(boat.Revision,
            coalesced.BoatDelta?.Current?.Revision ?? boat.Revision,
            "a coalesced private result must retain the current boat revision");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var rejected = authority.Execute(actor,
                new MoveBoatTransaction(
                    Context(actor), Ref(boat), new(4.5f, .5f)));
            CheckAssert.Equal(
                BoatTransactionStatus.PlanningCadenceLocked,
                rejected.Status,
                "distinct route spam must hit the server planning cadence");
        }
        CheckAssert.Equal(1, calls,
            "cadence-rejected commands must never enter A*");
        CheckAssert.Equal(boat.Revision,
            authority.Capture(boat.BoatId).Revision,
            "cadence rejection and coalescing must not churn revisions");

        var checkpoint = authority.CaptureCheckpoint();
        CheckAssert.True(
            checkpoint.Boats[0].PlanningCooldownSeconds > 0,
            "the exact remaining planning cooldown must be checkpointed");
        var restoredCalls = 0;
        IReadOnlyList<Vector2> RestoredPlanner(
            IBoatNavigationQuery query,
            Vector2 start,
            Vector2 destination,
            int maximumVisited)
        {
            restoredCalls++;
            return BoatRoutePlanner.Find(
                query, start, destination, maximumVisited);
        }
        var restored = new AuthoritativeBoatTransactions(
            navigation, options,
            AuthoritativeBoatTransactions.DeriveNetworkEntityId,
            RestoredPlanner);
        restored.RestoreCheckpoint(checkpoint);
        var restoredBoat = restored.Capture(boat.BoatId);
        var restartLocked = restored.Execute(actor,
            new MoveBoatTransaction(
                Context(actor), Ref(restoredBoat), new(4.5f, .5f)));
        CheckAssert.Equal(BoatTransactionStatus.PlanningCadenceLocked,
            restartLocked.Status,
            "restart must not reset the durable planning cadence");
        CheckAssert.Equal(0, restoredCalls,
            "a restored cooldown must reject before invoking A*");
        restored.Advance(.25);
        restoredBoat = restored.Capture(boat.BoatId);
        var afterCadence = restored.Execute(actor,
            new MoveBoatTransaction(
                Context(actor), Ref(restoredBoat), new(4.5f, .5f)));
        CheckAssert.True(afterCadence.Accepted && restoredCalls == 1,
            "normal route replacement must resume after fixed authority time");
    }

    private static void RouteReceiptReplayPrecedesPlanningCadence()
    {
        var calls = 0;
        IReadOnlyList<Vector2> CountedPlanner(
            IBoatNavigationQuery query,
            Vector2 start,
            Vector2 target,
            int maximumVisited)
        {
            calls++;
            return BoatRoutePlanner.Find(
                query, start, target, maximumVisited);
        }
        var navigation = new TestBoatNavigation();
        var boats = new AuthoritativeBoatTransactions(
            navigation,
            new AuthoritativeBoatTransactionOptions
            {
                PlanningCadenceSeconds = 1,
                MaximumPlansPerAdvance = 1,
                MaximumPathSearchVisited = 2_048,
                MaximumRouteWaypoints = 128
            },
            AuthoritativeBoatTransactions.DeriveNetworkEntityId,
            CountedPlanner);
        var session = new AuthoritativeWorldSession(
            identitySource: new FixedIdentitySource(),
            sessionId: new(Guid.Parse(
                "bd000000-0000-0000-0000-000000000022")),
            boatTransactions: boats);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var provisioned = session.EnqueueProvisionPlayerBoatAsync(
            joined.Identity.PlayerId);
        session.Drain();
        var boat = provisioned.GetAwaiter().GetResult();
        var board = Send(session, connection, joined.Identity.PlayerId, 1,
            new BoardBoatIntent(
                Guid.NewGuid(), joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision, Ref(boat)));
        boat = session.CaptureBoats().Single();
        var move = new MoveBoatIntent(
            Guid.NewGuid(), board.InventoryRevision, board.ActorRevision,
            Ref(boat), new(3.5f, .5f));
        var accepted = Send(session, connection, joined.Identity.PlayerId,
            2, move);
        var replay = Send(session, connection, joined.Identity.PlayerId,
            3, move);
        CheckAssert.True(accepted.Accepted && replay.Accepted &&
                         replay.Duplicate && calls == 1,
            "the receipt cache must replay a duplicate before cadence checks");

        boat = session.CaptureBoats().Single();
        var distinct = Send(session, connection, joined.Identity.PlayerId,
            4, new MoveBoatIntent(
                Guid.NewGuid(), accepted.InventoryRevision,
                accepted.ActorRevision, Ref(boat), new(4.5f, .5f)));
        CheckAssert.Equal(IntentStatus.BoatPlanningLocked, distinct.Status,
            "a distinct command inside cadence must be explicitly rate limited");
        CheckAssert.Equal(IslandRpg.Protocol.CommandRejectionCode.RateLimited,
            DedicatedServer.MapRejection(distinct.Status),
            "the dedicated server must expose boat planning cadence as rate limited");
        CheckAssert.Equal(1, calls,
            "a distinct rate-limited command must not invoke the planner");
    }

    private static void CheckpointRejectsInvalidIdentityAndOccupancy()
    {
        var navigation = new TestBoatNavigation();
        var authority = new AuthoritativeBoatTransactions(navigation);
        var owner = Player(30);
        var boat = authority.Seed(new(
            new BoatId(Guid.NewGuid()), owner, new(.5f, .5f)));
        var actorId = Actor(30);
        var boarded = authority.Execute(
            Actor(owner, actorId, new(.5f, 1.5f)),
            new BoardBoatTransaction(Context(Actor(owner, actorId,
                new(.5f, 1.5f))), Ref(boat)));
        var checkpoint = authority.CaptureCheckpoint();
        var duplicateOccupancy = checkpoint with
        {
            Boats = [
                checkpoint.Boats[0],
                checkpoint.Boats[0] with
                {
                    BoatId = new(Guid.NewGuid()),
                    Position = new(1.5f, .5f)
                }]
        };
        CheckAssert.Throws<InvalidDataException>(() =>
                new AuthoritativeBoatTransactions(navigation)
                    .RestoreCheckpoint(duplicateOccupancy),
            "one actor must not occupy two restored boats");

        var collisionAuthority = new AuthoritativeBoatTransactions(
            navigation, null, _ => 7);
        var first = new AuthoritativeBoatCheckpoint(
            new(Guid.NewGuid()), owner, null, null, null,
            new(.5f, .5f), Vector2.UnitY, 0, 1, []);
        var second = first with
        {
            BoatId = new(Guid.NewGuid()),
            OwnerPlayerId = Player(31),
            Position = new(1.5f, .5f)
        };
        CheckAssert.Throws<InvalidDataException>(() =>
                collisionAuthority.RestoreCheckpoint(new([first, second])),
            "restoration must reject derived network entity ID collisions");

        var roundedFacing = checkpoint with
        {
            Boats = [checkpoint.Boats[0] with
            {
                Facing = new(-.71805626f, -.695985f)
            }]
        };
        var restored = new AuthoritativeBoatTransactions(navigation);
        restored.RestoreCheckpoint(roundedFacing);
        var restoredFacing = restored.Capture(boat.BoatId).Facing;
        CheckAssert.True(MathF.Abs(restoredFacing.LengthSquared() - 1) < 1e-6f,
            "JSON-rounded facing must restore to a canonical unit vector");
        CheckAssert.True(boarded.Accepted,
            "the fixture must establish an occupied boat");
    }

    private static void FishingLifecycleIsAuthoritative()
    {
        var descriptor = FishDescriptor();
        var catalog = new FixedResourceCatalog(descriptor);
        var authority = new AuthoritativeResourceTransactions(
            91, catalog,
            new AuthoritativeResourceTransactionOptions
            {
                InteractionRange = 8,
                FishCadence = new(.1)
            });
        var actor = ResourceActor([
            (ItemIds.PrimitiveFishingNet, 1)]);
        var context = ResourceContext(actor);
        var wrongSlot = authority.Execute(actor, new CatchFishTransaction(
            context, Node(descriptor), 1, 2.4f, 0));
        CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
            wrongSlot.Status,
            "fishing must use the exact selected net slot");
        var outOfRangeActor = actor with { Position = new(-8, -8) };
        var outOfRange = authority.Execute(outOfRangeActor,
            new CatchFishTransaction(
                context with { CommandId = Guid.NewGuid() },
                Node(descriptor), 0, 2.4f, 0));
        CheckAssert.Equal(ResourceTransactionStatus.OutOfRange,
            outOfRange.Status,
            "fishing must validate interaction range");

        var herring = FishDescriptor(
            FishSpecies.SilverHerring,
            new ResourceNodeId(Guid.Parse(
                "bd100000-0000-0000-0000-000000000002")));
        var highTierAuthority = new AuthoritativeResourceTransactions(
            91, new FixedResourceCatalog(herring));
        var levelLocked = highTierAuthority.Execute(actor,
            new CatchFishTransaction(
                ResourceContext(actor), Node(herring), 0, 2.4f, 0));
        CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
            levelLocked.Status,
            "the server must reject fish above the actor's Fishing level");
        CheckAssert.True(levelLocked.Detail.Contains(
                "Fishing level 5", StringComparison.Ordinal),
            "level rejection must expose the canonical requirement");
        CheckAssert.Equal(0,
            highTierAuthority.CaptureCheckpoint().ActorCadences.Length,
            "skill rejection must not consume the fishing cadence");

        var levelFive = actor with
        {
            Gameplay = actor.Gameplay with
            {
                FishingExperience = FishingRules.ExperienceForLevel(5)
            }
        };
        var netLocked = highTierAuthority.Execute(levelFive,
            new CatchFishTransaction(
                ResourceContext(levelFive), Node(herring), 0, 2.4f, 0));
        CheckAssert.Equal(ResourceTransactionStatus.MissingTool,
            netLocked.Status,
            "the server must reject a net below the species power");
        CheckAssert.True(netLocked.Detail.Contains(
                "power 2", StringComparison.Ordinal),
            "net rejection must expose the canonical power requirement");

        ResourceTransactionResult result = null!;
        var attempts = 0;
        while (attempts++ < 64)
        {
            result = authority.Execute(actor, new CatchFishTransaction(
                ResourceContext(actor), Node(authority, descriptor), 0,
                2.4f, attempts * .11));
            CheckAssert.True(result.Accepted,
                "a ready valid fishing attempt should resolve");
            if (result.FishingOutcome is { Caught: true }) break;
        }
        CheckAssert.True(result.FishingOutcome is { Caught: true },
            "deterministic attempts must eventually catch the level-one fish");
        CheckAssert.Equal(1, result.RewardQuantity(ItemIds.RawMinnows),
            "a catch must grant the canonical species item");
        CheckAssert.Equal(1, result.NodeDelta!.Current.Remaining,
            "a catch must consume exactly one school stock");
        CheckAssert.True(result.Gameplay!.Value.FishingExperience > 0,
            "a catch must grant Fishing XP");
        CheckAssert.True(result.Gameplay!.Value.AdventureExperience > 0,
            "a catch must share progress with Adventure XP");
        actor = actor with { Gameplay = result.Gameplay.Value };
        var locked = authority.Execute(actor, new CatchFishTransaction(
            ResourceContext(actor), Node(authority, descriptor), 0,
            2.4f, attempts * .11));
        CheckAssert.Equal(ResourceTransactionStatus.CadenceLocked,
            locked.Status,
            "server cadence must reject animation-speed retries");
        ResourceTransactionResult depleted = null!;
        for (var index = 1; index <= 64; index++)
        {
            var state = authority.CaptureChunk(descriptor.Chunk).Nodes
                .SingleOrDefault();
            if (state?.Depleted == true) break;
            depleted = authority.Execute(actor, new CatchFishTransaction(
                ResourceContext(actor), Node(authority, descriptor), 0,
                2.4f, attempts * .11 + index * .11));
            if (depleted.Accepted && depleted.Gameplay is { } gameplay)
                actor = actor with { Gameplay = gameplay };
        }
        var exhausted = authority.Execute(actor, new CatchFishTransaction(
            ResourceContext(actor), Node(authority, descriptor), 0,
            2.4f, attempts * .11 + 20));
        CheckAssert.Equal(ResourceTransactionStatus.Depleted, exhausted.Status,
            "the sparse school must remain exhausted after its final catch");
    }

    private static void FishingRollsAreDeterministicAcrossRestart()
    {
        var descriptor = FishDescriptor();
        var catalog = new FixedResourceCatalog(descriptor);
        const long worldSeed = 91;
        var options = new AuthoritativeResourceTransactionOptions
        {
            FishCadence = new(.1)
        };
        WorldTransactionActorInput? selectedActor = null;
        for (var value = 100; value <= 1_000; value++)
        {
            var actor = ResourceActor(
                [(ItemIds.PrimitiveFishingNet, 1)], Actor(value));
            var trial = new AuthoritativeResourceTransactions(
                worldSeed, catalog, options);
            var first = trial.Execute(actor, new CatchFishTransaction(
                ResourceContext(actor), Node(descriptor), 0, 2.4f, 0));
            if (first.FishingOutcome is not { Caught: false }) continue;
            var second = trial.Execute(actor, new CatchFishTransaction(
                ResourceContext(actor), Node(trial, descriptor), 0,
                2.4f, .11));
            if (second.FishingOutcome is { Caught: true })
            {
                selectedActor = actor;
                break;
            }
        }
        CheckAssert.True(selectedActor is not null,
            "a deterministic miss-then-catch fixture must exist");

        var source = new AuthoritativeResourceTransactions(
            worldSeed, catalog, options);
        var miss = source.Execute(selectedActor!, new CatchFishTransaction(
            ResourceContext(selectedActor!), Node(descriptor), 0, 2.4f, 0));
        CheckAssert.True(miss.Accepted &&
                         miss.FishingOutcome is { Caught: false } &&
                         miss.NodeDelta is null && miss.ChunkDelta is null,
            "an authoritative miss must commit cadence without fake stock");
        var checkpoint = source.CaptureCheckpoint();
        CheckAssert.Equal(1UL,
            checkpoint.ActorCadences.Single().ActionOrdinal,
            "the persisted miss must advance the deterministic roll ordinal");

        var left = new AuthoritativeResourceTransactions(
            worldSeed, catalog, options);
        var right = new AuthoritativeResourceTransactions(
            worldSeed, catalog, options);
        left.RestoreCheckpoint(checkpoint);
        right.RestoreCheckpoint(checkpoint);
        var leftCatch = left.Execute(selectedActor!, new CatchFishTransaction(
            ResourceContext(selectedActor!), Node(left, descriptor), 0,
            2.4f, .11));
        var rightCatch = right.Execute(selectedActor!,
            new CatchFishTransaction(
                ResourceContext(selectedActor!), Node(right, descriptor), 0,
                2.4f, .11));
        CheckAssert.True(leftCatch.FishingOutcome is { Caught: true } &&
                         rightCatch.FishingOutcome is { Caught: true },
            "the first post-restart roll must reproduce the expected catch");
        CheckAssert.Equal(leftCatch.FishingOutcome,
            rightCatch.FishingOutcome,
            "restored authorities must reproduce the typed fishing outcome");
        CheckAssert.SequenceEqual(leftCatch.Rewards, rightCatch.Rewards,
            "restored authorities must reproduce catch rewards");
        CheckAssert.Equal(leftCatch.Gameplay!.Value.FishingExperience,
            rightCatch.Gameplay!.Value.FishingExperience,
            "restored authorities must reproduce Fishing XP");
        CheckAssert.Equal(leftCatch.NodeDelta!.Current,
            rightCatch.NodeDelta!.Current,
            "restored authorities must reproduce sparse school depletion");
    }

    private static void SessionFishingUsesAuthoritativeBoatPosition()
    {
        var descriptor = FishDescriptor(
            position: new(3.2f, .5f), initialRemaining: 4);
        var catalog = new FixedResourceCatalog(descriptor);
        var resources = new AuthoritativeResourceTransactions(
            91, catalog,
            new AuthoritativeResourceTransactionOptions
            {
                InteractionRange = 3,
                FishCadence = new(.1)
            });
        var sessionId = new SessionId(Guid.Parse(
            "bd000000-0000-0000-0000-000000000011"));
        var navigation = new TestBoatNavigation();
        var session = Session(sessionId, navigation, resources);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection,
            [new InitialInventoryItem(ItemIds.PrimitiveFishingNet)]);

        var landAttempt = Send(session, connection,
            joined.Identity.PlayerId, 1,
            new CatchFishIntent(
                Guid.Parse("bd000000-0000-0000-0000-000000000012"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                Node(descriptor), 0));
        CheckAssert.Equal(IntentStatus.OutOfRange, landAttempt.Status,
            "the land actor must not receive boat fishing reach");

        var provision = session.EnqueueProvisionPlayerBoatAsync(
            joined.Identity.PlayerId);
        session.Drain();
        var boat = provision.GetAwaiter().GetResult();
        var board = Send(session, connection, joined.Identity.PlayerId, 2,
            new BoardBoatIntent(
                Guid.Parse("bd000000-0000-0000-0000-000000000013"),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                Ref(boat)));
        CheckAssert.True(board.Accepted,
            "the fishing actor must board its provisioned boat");
        var actor = session.CaptureSnapshot().Actors.Single();
        var fishCommandId = Guid.Parse(
            "bd000000-0000-0000-0000-000000000014");
        var fishIntent = new CatchFishIntent(
            fishCommandId,
            actor.Gameplay.Inventory.Revision,
            actor.Gameplay.ActorRevision,
            Node(descriptor), 0);
        var aboardAttempt = Send(session, connection,
            joined.Identity.PlayerId, 3, fishIntent);
        CheckAssert.True(aboardAttempt.Accepted &&
                         aboardAttempt.ResourceTransaction?.FishingOutcome
                             is not null,
            "session fishing must derive position and reach from occupancy");

        var checkpoint = session.CaptureCheckpoint();
        var restoredResources = new AuthoritativeResourceTransactions(
            91, catalog,
            new AuthoritativeResourceTransactionOptions
            {
                InteractionRange = 3,
                FishCadence = new(.1)
            });
        var restored = Session(
            sessionId, navigation, restoredResources);
        restored.RestoreCheckpoint(checkpoint);
        var reconnect = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            reconnect, joined.Identity.PlayerId, joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnectPending.GetAwaiter().GetResult().Accepted,
            "the fishing actor must reconnect after checkpoint restore");
        var replay = Send(restored, reconnect, joined.Identity.PlayerId,
            4, fishIntent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "a restored catch receipt must not execute fishing twice");
        CheckAssert.SequenceEqual(
            checkpoint.Resources!.ActorCadences,
            restored.CaptureCheckpoint().Resources!.ActorCadences,
            "receipt replay must preserve the exact fishing cadence ordinal");
    }

    private static void SessionBoatLifecycleIsDurableAndIdempotent()
    {
        var sessionId = new SessionId(Guid.Parse(
            "bd000000-0000-0000-0000-000000000001"));
        var navigation = new TestBoatNavigation();
        var session = Session(sessionId, navigation);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection);
        var provisionPending = session.EnqueueProvisionPlayerBoatAsync(
            joined.Identity.PlayerId);
        session.Drain();
        var boat = provisionPending.GetAwaiter().GetResult();
        CheckAssert.Equal(1, session.CaptureSnapshot().Boats.Length,
            "queued provisioning must publish the island-start boat");

        var boardId = Guid.Parse(
            "bd000000-0000-0000-0000-000000000002");
        var boardIntent = new BoardBoatIntent(
            boardId, joined.Gameplay.Inventory.Revision,
            joined.Gameplay.ActorRevision, Ref(boat));
        var board = Send(session, connection, joined.Identity.PlayerId,
            1, boardIntent);
        CheckAssert.True(board.Accepted &&
                         board.BoatTransaction?.Accepted == true,
            "session must route boarding through boat authority");
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(boat.BoatId, actor.BoardedBoatId!.Value,
            "actor snapshot must expose its authoritative attachment");

        var walking = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, 2,
            new WalkIntent(new(4.5f, 1.5f))));
        session.Drain();
        CheckAssert.Equal(IntentStatus.AlreadyAboard,
            walking.GetAwaiter().GetResult().Status,
            "ordinary walk commands must not desynchronize a rider");

        boat = session.CaptureBoats().Single();
        var moveIntent = new MoveBoatIntent(
            Guid.Parse("bd000000-0000-0000-0000-000000000003"),
            actor.Gameplay.Inventory.Revision,
            actor.Gameplay.ActorRevision,
            Ref(boat), new(3.5f, .5f));
        var move = Send(session, connection, joined.Identity.PlayerId,
            3, moveIntent);
        CheckAssert.True(move.Accepted,
            "the boarded actor should start a target-only boat route");
        boat = session.CaptureBoats().Single();
        var movingRevision = boat.Revision;
        var movingDestination = boat.Destination;
        var genericStop = session.EnqueueIntentAsync(new ActorCommand(
            connection, joined.Identity.PlayerId, 4, StopIntent.Instance));
        session.Drain();
        CheckAssert.Equal(IntentStatus.AlreadyAboard,
            genericStop.GetAwaiter().GetResult().Status,
            "generic Stop must require the exact typed boat stop path");
        boat = session.CaptureBoats().Single();
        CheckAssert.Equal(movingRevision, boat.Revision,
            "rejected generic Stop must not mutate the boat revision");
        CheckAssert.Equal(movingDestination, boat.Destination,
            "rejected generic Stop must not clear the boat route");
        for (var tick = 0; tick < 120; tick++) session.Tick();
        actor = session.CaptureSnapshot().Actors.Single();
        boat = session.CaptureBoats().Single();
        CheckAssert.Equal(boat.Position, actor.Position,
            "fixed ticks must keep the rider exactly on the boat");
        CheckAssert.True(boat.Destination is null && boat.Revision >= 4,
            "arrival must clear the route and publish a fresh revision");

        var checkpoint = session.CaptureCheckpoint();
        var restored = Session(sessionId, navigation);
        restored.RestoreCheckpoint(checkpoint);
        var restoredBoat = restored.CaptureBoats().Single();
        var restoredActor = restored.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(restoredBoat.BoatId,
            restoredActor.BoardedBoatId!.Value,
            "checkpoint restore must reconstruct canonical occupancy");
        CheckAssert.Equal(restoredBoat.Position, restoredActor.Position,
            "checkpoint restore must preserve rider transform consistency");

        var reconnect = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            reconnect, joined.Identity.PlayerId, joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnectPending.GetAwaiter().GetResult().Accepted,
            "the boarded player should reconnect after restart");
        var replay = Send(restored, reconnect, joined.Identity.PlayerId,
            5, boardIntent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "the restored board command receipt must replay idempotently");
        CheckAssert.Equal(restoredBoat.Revision,
            restored.CaptureBoats().Single().Revision,
            "receipt replay must not mutate the boat twice");
    }

    private static AuthoritativeWorldSession Session(
        SessionId id,
        IBoatNavigationQuery navigation,
        AuthoritativeResourceTransactions? resources = null) => new(
            identitySource: new FixedIdentitySource(),
            sessionId: id,
            resourceTransactions: resources,
            boatTransactions: new AuthoritativeBoatTransactions(
                navigation,
                new AuthoritativeBoatTransactionOptions
                {
                    MovementSpeed = 6,
                    MaximumPathSearchVisited = 2_048,
                    MaximumRouteWaypoints = 128
                }));

    private static JoinResult Join(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        IReadOnlyList<InitialInventoryItem>? inventory = null)
    {
        var pending = session.EnqueueJoinAsync(new(
            connection, "Boat tester", new(.5f, 1.5f), inventory));
        session.Drain();
        var joined = pending.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, "the boat test actor should join");
        return joined;
    }

    private static IntentResult Send(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        PlayerId player,
        long sequence,
        GameplayIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new(
            connection, player, sequence, intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static BoatTransactionActorInput Actor(
        PlayerId player,
        ActorId actor,
        Vector2 position) => new(
            actor, player, position, 0, EmptyGameplay());

    private static WorldTransactionContext Context(
        BoatTransactionActorInput actor) => new(
        Guid.NewGuid(), actor.ActorId,
        actor.Gameplay.ActorRevision,
        actor.Gameplay.Inventory.Revision);

    private static BoatReference Ref(AuthoritativeBoatSnapshot boat) =>
        new(boat.BoatId, boat.Revision);

    private static PlayerGameplaySnapshot EmptyGameplay() => new(
        1, 100, 100, 0, 0, 0,
        new(1, Enumerable.Range(0, PlayerInventory.Capacity)
            .Select(static slot => new InventorySlotSnapshot(slot, null, 0))
            .ToImmutableArray()));

    private static WorldTransactionActorInput ResourceActor(
        IReadOnlyList<(string Item, int Quantity)> items,
        ActorId? actorId = null)
    {
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
            PlayerInventory.Capacity);
        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
            slots.Add(slot < items.Count
                ? new(slot, items[slot].Item, items[slot].Quantity)
                : new(slot, null, 0));
        return new(actorId ?? Actor(80), Vector2.Zero, 0,
            new PlayerGameplaySnapshot(
                1, 100, 100, 0, 0, 0,
                new(1, slots.MoveToImmutable())));
    }

    private static WorldTransactionContext ResourceContext(
        WorldTransactionActorInput actor) => new(
            Guid.NewGuid(), actor.ActorId,
            actor.Gameplay.ActorRevision,
            actor.Gameplay.Inventory.Revision);

    private static ResourceNodeDescriptor FishDescriptor(
        FishSpecies species = FishSpecies.ShoreMinnows,
        ResourceNodeId? id = null,
        Vector2 position = default,
        int initialRemaining = 2) => new(
        id ?? new ResourceNodeId(Guid.Parse(
            "bd100000-0000-0000-0000-000000000001")),
        ResourceNodeKind.FishSchool,
        new(0, 0, 0),
        position == default ? new(1, 0) : position,
        (int)species,
        InitialRemaining: initialRemaining);

    private static ResourceNodeReference Node(
        ResourceNodeDescriptor descriptor) => new(
            descriptor.Id, descriptor.Chunk, 0, 0);

    private static ResourceNodeReference Node(
        AuthoritativeResourceTransactions authority,
        ResourceNodeDescriptor descriptor)
    {
        var chunk = authority.CaptureChunk(descriptor.Chunk);
        var state = chunk.Nodes.SingleOrDefault(value =>
            value.Id == descriptor.Id);
        return new(descriptor.Id, descriptor.Chunk,
            state?.NodeRevision ?? 0, chunk.ResourceChunkRevision);
    }

    private static PlayerId Player(int value) => new(Guid.Parse(
        $"bd200000-0000-0000-0000-{value:D12}"));

    private static ActorId Actor(int value) => new(Guid.Parse(
        $"bd300000-0000-0000-0000-{value:D12}"));

    private static (int X, int Y) Cell(Vector2 value) =>
        ((int)MathF.Floor(value.X), (int)MathF.Floor(value.Y));

    private sealed class FixedResourceCatalog(
        ResourceNodeDescriptor descriptor) : IResourceDescriptorResolver
    {
        public bool TryResolve(
            long worldSeed,
            ResourceNodeReference reference,
            out ResourceNodeDescriptor resolved)
        {
            resolved = descriptor;
            return reference.Id == descriptor.Id &&
                   reference.Chunk == descriptor.Chunk;
        }

        public IReadOnlyList<ResourceNodeDescriptor> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk) =>
            chunk == descriptor.Chunk ? [descriptor] : [];
    }

    private sealed class TestBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.Y is >= 0 and < 1 && point.X is >= 0 and < 10;

        public bool IsLanding(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.Y is >= 1 and < 3 && point.X is >= 0 and < 10;

        public bool IsInitialMooring(Vector2 point) => IsNavigable(point);
    }

    private sealed class OpenWaterBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.X is >= 0 and < 6 && point.Y is >= 0 and < 6;

        public bool IsLanding(Vector2 point) => !IsNavigable(point);

        public bool IsInitialMooring(Vector2 point) => IsNavigable(point);
    }

    private sealed class NoMooringBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) => false;

        public bool IsLanding(Vector2 point) => true;

        public bool IsInitialMooring(Vector2 point) => false;
    }

    private sealed class FixedIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            Player(90), Actor(90));

        public ReconnectToken CreateReconnectToken() => new(
            Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
    }
}
