using System.Numerics;
using IslandRpg.Navigation;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class AuthoritativeNavigationChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "authoritative paths avoid water obstacles slopes and corners",
            AvoidsInvalidTerrainAndObstacles);
        checks.Add(
            "unreachable walks reject atomically and preserve the prior route",
            UnreachableReplacementIsAtomic);
        checks.Add(
            "blocked destinations resolve only to a reachable nearby stand point",
            BlockedDestinationUsesReachableStandPoint);
        checks.Add(
            "replacement stop and disconnect clear bounded routes",
            RoutesReplaceAndClear);
        checks.Add(
            "world levels remain authoritative in movement snapshots",
            WorldLevelIsAuthoritative);
        checks.Add(
            "procedural navigation is stable across seams and negative coordinates",
            ProceduralCoordinatesAreDeterministic);
        checks.Add(
            "procedural cave navigation is headless and deterministic",
            ProceduralCaveNavigationIsDeterministic);
        checks.Add(
            "authoritative movement consumes the full sixty tick budget",
            SixtyTickDistanceIsExact);
    }

    private static void AvoidsInvalidTerrainAndObstacles()
    {
        var world = new TestNavigationQuery();
        world.Blocked.Add((2, 0));
        var obstacles = new FixedObstacles(
        [
            new NavigationObstacle(new Vector2(.75f, .75f), .3f, .3f)
        ]);
        var path = GridPathfinder.Find(
            world,
            Vector2.Zero,
            new Vector2(1, 0),
            obstacles: obstacles.GetObstacles(0));
        CheckAssert.True(path.Count > 1,
            "a water cell on the direct line must create a detour");
        CheckAssert.True(path.All(point =>
                world.CanStandAt(point, 0) &&
                !obstacles.Values.Any(obstacle => obstacle.Contains(point))),
            "every routed waypoint must avoid water and obstacles");

        var corner = new TestNavigationQuery();
        corner.Blocked.Add((1, 0));
        corner.Blocked.Add((0, 1));
        CheckAssert.Equal(0,
            GridPathfinder.Find(
                corner,
                new Vector2(.125f, .125f),
                new Vector2(.375f, .375f),
                maximumVisited: 1).Count,
            "diagonal movement must not cut through two blocked corners");

        var slope = new TestNavigationQuery();
        slope.Heights[(1, 0)] = 5;
        slope.Heights[(0, 1)] = 5;
        slope.Heights[(1, 1)] = 5;
        CheckAssert.Equal(0,
            GridPathfinder.Find(
                slope,
                new Vector2(.125f, .125f),
                new Vector2(.375f, .125f),
                maximumVisited: 1).Count,
            "a rise above the shared four-height traversal limit must be rejected");
    }

    private static void UnreachableReplacementIsAtomic()
    {
        var world = new TestNavigationQuery();
        var session = NewSession(world);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, Vector2.Zero);
        var accepted = SendWalk(
            session, connection, joined, 1, new Vector2(4, 0));
        CheckAssert.True(accepted.Accepted,
            "the initial clear route must be accepted");
        var original = session.CaptureSnapshot().Actors[0].Destination;

        for (var y = -12; y <= 12; y++)
        for (var x = 8; x <= 32; x++)
            world.Blocked.Add((x, y));
        var rejected = SendWalk(
            session, connection, joined, 2, new Vector2(5, 0));
        CheckAssert.Equal(IntentStatus.PathUnreachable, rejected.Status,
            "a fully sealed target must be rejected by the authority");
        CheckAssert.Equal(original,
            session.CaptureSnapshot().Actors[0].Destination,
            "failed replacement must leave the accepted route untouched");
    }

    private static void BlockedDestinationUsesReachableStandPoint()
    {
        var world = new TestNavigationQuery();
        world.Blocked.Add((4, 0));
        var session = NewSession(world);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, Vector2.Zero);
        var requested = new Vector2(1.125f, .125f);
        var result = SendWalk(
            session, connection, joined, 1, requested);
        CheckAssert.True(result.Accepted,
            "an interaction-style blocked target should resolve nearby");
        var endpoint = session.CaptureSnapshot().Actors[0].Destination;
        CheckAssert.True(endpoint is { } value && value != requested &&
                         world.CanStandAt(value, 0),
            "the published endpoint must be the resolved traversable stand point");
    }

    private static void RoutesReplaceAndClear()
    {
        var session = NewSession(new TestNavigationQuery());
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, Vector2.Zero);
        CheckAssert.True(SendWalk(
                session, connection, joined, 1, new Vector2(8, 0)).Accepted,
            "the first route must be accepted");
        CheckAssert.True(SendWalk(
                session, connection, joined, 2, new Vector2(0, 3)).Accepted,
            "a later route must replace the first route");
        CheckAssert.Equal<Vector2?>(new Vector2(0, 3),
            session.CaptureSnapshot().Actors[0].Destination,
            "the published destination must be the replacement endpoint");

        var stop = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            3,
            StopIntent.Instance));
        session.Drain();
        CheckAssert.True(stop.GetAwaiter().GetResult().Accepted,
            "stop must be accepted");
        CheckAssert.True(
            session.CaptureSnapshot().Actors[0].Destination is null,
            "stop must clear every queued waypoint");

        SendWalk(session, connection, joined, 4, new Vector2(2, 0));
        var disconnect = session.EnqueueDisconnectAsync(new DisconnectRequest(
            connection,
            joined.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(disconnect.GetAwaiter().GetResult().Accepted,
            "disconnect must succeed");
        var actor = session.CaptureSnapshot().Actors[0];
        CheckAssert.True(actor.Destination is null && actor.Velocity == Vector2.Zero,
            "disconnect must clear every queued waypoint and velocity");
    }

    private static void WorldLevelIsAuthoritative()
    {
        var session = NewSession(new TestNavigationQuery());
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, Vector2.Zero, worldLevel: -1);
        CheckAssert.Equal(-1, session.CaptureSnapshot().Actors[0].WorldLevel,
            "the actor snapshot must preserve its authoritative world level");
        var rejected = SendWalk(
            session, connection, joined, 1, Vector2.One, worldLevel: 0);
        CheckAssert.Equal(IntentStatus.WorldLevelMismatch, rejected.Status,
            "a walk command cannot switch world levels");
        CheckAssert.True(
            session.CaptureSnapshot().Actors[0].Destination is null,
            "a level mismatch must reject without installing a route");
    }

    private static void ProceduralCoordinatesAreDeterministic()
    {
        var first = new ProceduralSurfaceNavigationQuery(73_731);
        var second = new ProceduralSurfaceNavigationQuery(73_731);
        Vector2[] samples =
        [
            new(-32.001f, -31.999f),
            new(-.001f, .001f),
            new(31.999f, 32.001f),
            new(64.001f, -64.001f)
        ];
        foreach (var sample in samples)
        {
            CheckAssert.Equal(first.CanStandAt(sample, 0),
                second.CanStandAt(sample, 0),
                "seeded terrain passability must be coordinate stable");
            CheckAssert.Equal(first.HeightAt(sample, 0),
                second.HeightAt(sample, 0),
                "seeded terrain height must be coordinate stable");
            CheckAssert.Equal(first.IsWading(sample, 0),
                second.IsWading(sample, 0),
                "seeded water classification must be coordinate stable");
        }
    }

    private static void SixtyTickDistanceIsExact()
    {
        var limits = SimulationLimits.Default with
        {
            ActorMovementSpeed = 2.8f,
            DestinationArrivalDistance = 0
        };
        var session = NewSession(new TestNavigationQuery(), limits);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, Vector2.Zero);
        CheckAssert.True(SendWalk(
                session, connection, joined, 1, new Vector2(20, 0)).Accepted,
            "the long clear route must be accepted");
        for (var tick = 0; tick < 60; tick++) session.Tick();
        var actor = session.CaptureSnapshot().Actors[0];
        CheckAssert.True(MathF.Abs(actor.Position.X - 2.8f) < .0001f &&
                         MathF.Abs(actor.Position.Y) < .0001f,
            $"sixty flat ticks must travel exactly one second: {actor.Position}");
    }

    private static void ProceduralCaveNavigationIsDeterministic()
    {
        const long seed = 73_731;
        var first = new ProceduralWorldNavigationQuery(seed);
        var second = new ProceduralWorldNavigationQuery(seed);
        CheckAssert.True(first.SupportsWorldLevel(-1) &&
                         first.SupportsWorldLevel(0) &&
                         !first.SupportsWorldLevel(1),
            "the canonical query must expose only authored world levels");

        var foundFloor = false;
        for (var y = -48; y <= 48 && !foundFloor; y++)
        for (var x = -48; x <= 48 && !foundFloor; x++)
        {
            var point = new Vector2(x + .5f, y + .5f);
            if (!first.CanStandAt(point, -1)) continue;
            foundFloor = true;
            CheckAssert.True(second.CanStandAt(point, -1),
                "the same seed must reproduce underground passability");
            CheckAssert.Equal(first.HeightAt(point, -1),
                second.HeightAt(point, -1),
                "the same seed must reproduce underground floor height");
            CheckAssert.False(first.IsWading(point, -1),
                "underground water does not use the surface wading penalty");

            var route = GridPathfinder.Find(
                first, point, point + new Vector2(.25f, 0),
                worldLevel: -1);
            CheckAssert.True(route.Count > 0,
                "a cave-floor actor must be routable by the headless query");
        }

        CheckAssert.True(foundFloor,
            "the deterministic cave fixture must contain walkable floor");
        CheckAssert.False(first.CanStandAt(new(float.NaN, 0), -1),
            "non-finite cave coordinates must be rejected");
    }

    private static AuthoritativeWorldSession NewSession(
        IWorldNavigationQuery navigation,
        SimulationLimits? limits = null,
        IWorldNavigationObstacleSource? obstacles = null) => new(
            limits,
            new DeterministicIdentitySource(),
            new SessionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            navigation,
            obstacles);

    private static JoinResult Join(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        Vector2 spawn,
        int worldLevel = 0)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            "Pathfinder",
            spawn,
            SpawnWorldLevel: worldLevel));
        session.Drain();
        var result = pending.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "the test actor must join");
        return result;
    }

    private static IntentResult SendWalk(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        JoinResult joined,
        long sequence,
        Vector2 destination,
        int worldLevel = 0)
    {
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            sequence,
            new WalkIntent(destination, worldLevel)));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private sealed class TestNavigationQuery : IWorldNavigationQuery
    {
        public HashSet<(int X, int Y)> Blocked { get; } = [];

        public Dictionary<(int X, int Y), float> Heights { get; } = [];

        public bool SupportsWorldLevel(int worldLevel) =>
            worldLevel is -1 or 0;

        public bool CanStandAt(Vector2 point, int worldLevel) =>
            SupportsWorldLevel(worldLevel) &&
            !Blocked.Contains(Cell(point));

        public float HeightAt(Vector2 point, int worldLevel) =>
            Heights.GetValueOrDefault(Cell(point));

        public bool IsWading(Vector2 point, int worldLevel) => false;

        private static (int X, int Y) Cell(Vector2 point) =>
            (WorldPlacementGrid.Cell(point.X),
             WorldPlacementGrid.Cell(point.Y));
    }

    private sealed class FixedObstacles(
        IReadOnlyList<NavigationObstacle> values) :
        IWorldNavigationObstacleSource
    {
        public IReadOnlyList<NavigationObstacle> Values { get; } = values;

        public IReadOnlyList<NavigationObstacle> GetObstacles(int worldLevel) =>
            Values;
    }

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse("40000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() =>
            new("authoritative-navigation-check");
    }
}
