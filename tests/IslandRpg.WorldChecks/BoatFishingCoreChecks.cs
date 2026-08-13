using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Fishing;
using IslandRpg.Resources;
using IslandRpg.Simulation;

internal static class BoatFishingCoreChecks
{
    public static void Run()
    {
        FishSchoolsAreStableAndCatalogReady();
        TargetSelectionIsEligibleAndDeterministic();
        BoatNavigationIsBoundedAndCornerSafe();
        ProvisioningAndLandingRespectOccupancy();
        ProceduralSpawnAndBoatRulesAreDeterministic();
    }

    private static void FishSchoolsAreStableAndCatalogReady()
    {
        const long seed = 67;
        var source = new ProceduralFishSchoolSource();
        var catalog = new ProceduralResourceCatalog(source);
        var sawSpecies = new HashSet<FishSpecies>();
        var sawBeginnerGuarantee = false;
        for (var chunkY = -3; chunkY <= 3; chunkY++)
        for (var chunkX = -3; chunkX <= 3; chunkX++)
        {
            var chunk = new WorldChunkKey(chunkX, chunkY, 0);
            var first = source.DescribeSchools(seed, chunk);
            var second = source.DescribeSchools(seed, chunk);
            Assert(first.SequenceEqual(second),
                "fish-school generation must be seed stable");
            Assert(first.Count <= ProceduralFishSchoolSource.MaximumPerChunk,
                "fish-school generation must respect its chunk bound");
            Assert(catalog.DescribeChunk(seed, chunk).Select(value => value.Id)
                    .SequenceEqual(first.Select(value => value.Id)),
                "fish schools must plug directly into the resource catalog");
            foreach (var school in first)
            {
                sawSpecies.Add(school.Species);
                var profile = FishingRules.Profile(school.Species);
                Assert(
                    !school.Id.IsEmpty &&
                    school.ItemId == profile.ItemId &&
                    school.RequiredLevel == profile.RequiredLevel &&
                    school.RequiredNetPower == profile.RequiredNetPower &&
                    school.Experience == profile.Experience &&
                    school.SchoolSize == profile.SchoolSize &&
                    school.RegrowthGameSeconds == 0,
                    "a fish descriptor must carry its complete catch policy");
                if (school.Species == FishSpecies.ShoreMinnows)
                {
                    var tileX = (int)MathF.Floor(school.Position.X);
                    var tileY = (int)MathF.Floor(school.Position.Y);
                    var distance = ProceduralFishSchoolSource.DistanceFromShore(
                        seed, tileX, tileY);
                    sawBeginnerGuarantee |= distance is >= 1 and <= 3;
                }
            }
        }
        Assert(FishingRules.CatchProfiles.Count == 6 &&
               sawSpecies.Count >= 4 && sawBeginnerGuarantee,
            "the canonical catalog must define all six species and guaranteed beginner schools");
    }

    private static void TargetSelectionIsEligibleAndDeterministic()
    {
        var advanced = new FishingTargetCandidate(
            new ResourceNodeId(Guid.Parse(
                "fa000000-0000-0000-0000-000000000001")),
            new Vector2(1, 0),
            FishSpecies.OceanMackerel);
        var beginner = new FishingTargetCandidate(
            new ResourceNodeId(Guid.Parse(
                "fa000000-0000-0000-0000-000000000002")),
            new Vector2(6, 0),
            FishSpecies.ShoreMinnows);
        var selected = FishingRules.SelectTarget(
            [advanced, beginner], null, Vector2.Zero, 1, 1);
        var reversed = FishingRules.SelectTarget(
            [beginner, advanced], null, Vector2.Zero, 1, 1);
        var exact = FishingRules.SelectTarget(
            [beginner, advanced], advanced.Id, Vector2.Zero, 1, 1);
        Assert(
            selected.Target == beginner && reversed.Target == beginner &&
            exact.Failure == FishingTargetFailure.FishingLevelRequired &&
            exact.Requirement?.RequiredLevel == 13 &&
            FishingRules.SelectTarget(
                [beginner], null, Vector2.Zero, 1, null).Failure ==
            FishingTargetFailure.FishingNetNotFound &&
            FishingRules.ValidateTarget(
                beginner with { Depleted = true }, Vector2.Zero, 1, 1)
                .Failure == FishingTargetFailure.FishDepleted &&
            FishingRules.ValidateTarget(
                beginner, Vector2.Zero, 1, 1, 3).Failure ==
            FishingTargetFailure.FishNotReachable,
            "target selection must skip inaccessible schools but explain exact failures");
    }

    private static void BoatNavigationIsBoundedAndCornerSafe()
    {
        var open = new TestBoatQuery(
            navigable: point => point.X is >= .5f and <= 4.5f &&
                                point.Y is >= .5f and <= 4.5f);
        var route = BoatRoutePlanner.Find(
            open, new Vector2(.5f, .5f), new Vector2(4.5f, 4.5f));
        Assert(route.Count > 0 && route[^1] == new Vector2(4.5f, 4.5f),
            "boats must find a bounded water route to an exact valid target");

        var directQueries = 0;
        var longOpen = new TestBoatQuery(navigable: _ =>
        {
            directQueries++;
            return true;
        });
        var direct = BoatRoutePlanner.Find(
            longOpen, new Vector2(.5f, .5f),
            new Vector2(400.5f, .5f), maximumVisited: 512);
        Assert(direct.Count == 1 && direct[0] == new Vector2(400.5f, .5f) &&
               directQueries <= 405,
            "clear long water travel must collapse to a bounded direct waypoint");

        var corner = new TestBoatQuery(
            navigable: point =>
                Cell(point) is (0, 0) or (1, 1));
        Assert(BoatRoutePlanner.Find(
                corner, new Vector2(.5f, .5f),
                new Vector2(1.5f, 1.5f)).Count == 0,
            "boats must not cut diagonally through blocked water corners");
        Assert(BoatRoutePlanner.Find(
                open, new Vector2(.5f, .5f),
                new Vector2(4.5f, 4.5f), maximumVisited: 1).Count == 0,
            "boat route work must obey its visit bound");
    }

    private static void ProvisioningAndLandingRespectOccupancy()
    {
        var query = new TestBoatQuery(
            navigable: point => Cell(point).Y == 0,
            initialMooring: point => Cell(point).Y == 0,
            landing: point => Cell(point).Y != 0);
        var first = BoatTravelRules.FindInitialPosition(
            query, Vector2.Zero);
        var second = BoatTravelRules.FindInitialPosition(
            query, Vector2.Zero,
            occupied: point => point == first);
        var landing = BoatTravelRules.FindDisembarkLanding(
            query, first, new Vector2(first.X, 1.5f));
        var alternate = BoatTravelRules.FindDisembarkLanding(
            query, first, new Vector2(first.X, 1.5f),
            occupied: point => point == landing);
        Assert(first != second && landing is not null &&
               alternate is not null && alternate != landing &&
               BoatTravelRules.CanDisembark(query, first, landing.Value),
            "boat and shore provisioning must skip occupied cells deterministically");
    }

    private static void ProceduralSpawnAndBoatRulesAreDeterministic()
    {
        const long seed = 67;
        var first = BoatTravelRules.FindPlayableLandSpawn(seed);
        var second = BoatTravelRules.FindPlayableLandSpawn(seed);
        var query = new ProceduralBoatNavigationQuery(seed);
        var boat = BoatTravelRules.FindInitialPosition(query, first);
        Assert(first == second && query.IsLanding(first) &&
               query.IsInitialMooring(boat) &&
               BoatTravelRules.FindInitialPosition(query, first) == boat,
            "land spawn and initial mooring must share deterministic terrain rules");
    }

    private static (int X, int Y) Cell(Vector2 point) =>
        ((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestBoatQuery(
        Func<Vector2, bool> navigable,
        Func<Vector2, bool>? landing = null,
        Func<Vector2, bool>? initialMooring = null) : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) => navigable(point);

        public bool IsLanding(Vector2 point) =>
            landing?.Invoke(point) ?? !navigable(point);

        public bool IsInitialMooring(Vector2 point) =>
            initialMooring?.Invoke(point) ?? navigable(point);
    }
}
