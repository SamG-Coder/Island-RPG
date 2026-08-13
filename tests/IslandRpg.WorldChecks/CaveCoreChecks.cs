using System.Numerics;
using IslandRpg.Caves;

internal static class CaveCoreChecks
{
    public static void Run()
    {
        TerrainAndStrikeLifecycle();
        PortalAndRestorationLifecycle();
        ProceduralEnvironmentIsDeterministic();
        ProspectingIsBoundedAndTruthful();
    }

    private static void TerrainAndStrikeLifecycle()
    {
        var sand = CaveExcavationRules.Terrain(
            ExcavationTerrainKind.Sand);
        var rock = CaveExcavationRules.Terrain(
            ExcavationTerrainKind.Rock);
        Assert(
            sand == new ExcavationTerrain(
                ExcavationTerrainKind.Sand, 30,
                CaveExcavationRules.SandItemId) &&
            rock == new ExcavationTerrain(
                ExcavationTerrainKind.Rock, 100,
                CaveExcavationRules.DirtItemId),
            "terrain hardness and excavation rewards must preserve solo policy");

        var id = Guid.NewGuid();
        var site = CaveExcavationRules.Begin(
            id, new Vector2(-1.1f, 4.9f), sand);
        Assert(
            site.Id == id && site.Position == new Vector2(-1.5f, 4.5f) &&
            site.Kind == ExcavationKind.DigSite &&
            site.Health == 30 && site.MaximumHealth == 30,
            "a new excavation must use one stable ID and floor-snapped tile centre");

        var totalExperience = 0;
        while (site.Kind == ExcavationKind.DigSite)
        {
            var strike = CaveExcavationRules.Strike(
                site, diggingExperience: 0, shovelPower: 1,
                caveBelow: true);
            Assert(strike.Damage is > 0 and <= 8,
                "a novice stone-shovel strike must preserve bounded solo damage");
            totalExperience += strike.ExperienceGained;
            site = strike.State;
        }
        Assert(
            site.Kind == ExcavationKind.OpenShaft && site.Health == 0 &&
            totalExperience == sand.MaximumHealth +
            sand.MaximumHealth / 5,
            "completion must award damage XP plus the authored one-fifth bonus");

        var shallow = CaveExcavationRules.Strike(
            CaveExcavationRules.Begin(Guid.NewGuid(), Vector2.Zero, sand),
            diggingExperience: 0,
            shovelPower: 100,
            caveBelow: false).State;
        Assert(shallow.Kind == ExcavationKind.ShallowHole,
            "a completed excavation over solid ground must remain shallow");
    }

    private static void PortalAndRestorationLifecycle()
    {
        var surfaceId = Guid.NewGuid();
        var undergroundId = Guid.NewGuid();
        var open = new CaveExcavationState(
            surfaceId,
            ExcavationKind.OpenShaft,
            new Vector2(7.5f, -2.5f),
            Health: 0,
            MaximumHealth: 50);
        Assert(
            CaveExcavationRules.TryPortalLink(
                open, undergroundId, out var unroped) &&
            unroped.SurfaceObjectId == surfaceId &&
            unroped.UndergroundObjectId == undergroundId &&
            unroped.Position == open.Position &&
            !unroped.Traversable,
            "linked portal endpoints must use distinct IDs and one exact position");
        Assert(
            CaveExcavationRules.TryInstallRope(open, out var entrance) &&
            CaveExcavationRules.TryPortalLink(
                entrance, undergroundId, out var secured) &&
            secured.Traversable &&
            CaveExcavationRules.TryDestinationLevel(
                entrance,
                CaveExcavationRules.SurfaceWorldLevel,
                out var underground) &&
            underground == CaveExcavationRules.UndergroundWorldLevel &&
            CaveExcavationRules.TryDestinationLevel(
                entrance,
                CaveExcavationRules.UndergroundWorldLevel,
                out var surface) &&
            surface == CaveExcavationRules.SurfaceWorldLevel,
            "only a rope-secured shaft may traverse between linked levels");
        Assert(
            CaveExcavationRules.TryTakeRope(entrance, out var reopened) &&
            reopened == open &&
            !CaveExcavationRules.TryDestinationLevel(
                reopened,
                CaveExcavationRules.SurfaceWorldLevel,
                out _),
            "recovering a rope must preserve the shaft but disable traversal");

        var soil = CaveExcavationRules.Terrain(
            ExcavationTerrainKind.Soil);
        Assert(
            CaveExcavationRules.CanFillWith(
                open, soil, CaveExcavationRules.DirtItemId) &&
            !CaveExcavationRules.CanFillWith(
                open, soil, CaveExcavationRules.SandItemId) &&
            !CaveExcavationRules.CanFill(entrance),
            "open holes require their excavated material and secured shafts require rope removal first");
    }

    private static void ProceduralEnvironmentIsDeterministic()
    {
        const long seed = 9_187;
        var first = new ProceduralCaveExcavationEnvironment(seed);
        var second = new ProceduralCaveExcavationEnvironment(seed);
        var samples = new[]
        {
            new Vector2(58.5f, 39.5f),
            new Vector2(-17.2f, 3.9f),
            new Vector2(100.1f, -90.7f)
        };
        foreach (var sample in samples)
        {
            Assert(
                first.Snap(sample) == second.Snap(sample) &&
                first.TerrainAt(sample) == second.TerrainAt(sample) &&
                first.IsSurfaceDiggable(sample) ==
                    second.IsSurfaceDiggable(sample) &&
                first.IsCaveBelow(sample) == second.IsCaveBelow(sample),
                "the headless excavation environment must be seed deterministic");
        }
        Assert(
            first.TerrainAt(
                new Vector2(float.NaN, 0)) ==
            CaveExcavationRules.NotDiggableTerrain,
            "non-finite client coordinates must fail closed");
    }

    private static void ProspectingIsBoundedAndTruthful()
    {
        var environment = new FakeEnvironment(
            new Vector2(6.5f, -2.5f));
        Assert(
            CaveExcavationRules.TryProspect(
                environment,
                new Vector2(.5f, .5f),
                out var prospect) &&
            prospect.Position == new Vector2(6.5f, -2.5f) &&
            prospect.Distance <= CaveExcavationRules.ProspectRadius &&
            prospect.Direction == CaveCompassDirection.NorthEast,
            "prospecting must report the nearest truthful cave bearing inside its bound");
        Assert(
            !CaveExcavationRules.TryProspect(
                new FakeEnvironment(new Vector2(40.5f, .5f)),
                new Vector2(.5f, .5f),
                out _),
            "prospecting must not reveal cave-bearing ground outside its radius");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeEnvironment(Vector2 cave) :
        ICaveExcavationEnvironment
    {
        public Vector2 Snap(Vector2 position) =>
            CaveExcavationRules.Snap(position);

        public ExcavationTerrain TerrainAt(Vector2 position) =>
            CaveExcavationRules.Terrain(ExcavationTerrainKind.Soil);

        public bool IsSurfaceDiggable(Vector2 position) => true;

        public bool IsCaveBelow(Vector2 position) =>
            Vector2.DistanceSquared(position, cave) < .01f;
    }
}
