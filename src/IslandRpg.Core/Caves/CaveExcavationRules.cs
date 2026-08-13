using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.World;

namespace IslandRpg.Caves;

/// <summary>
/// The persisted lifecycle of one excavation endpoint. Every endpoint keeps
/// its own stable identity; a <see cref="CavePortalLink"/> explicitly joins
/// the distinct surface and underground objects at one snapped position.
/// </summary>
public enum ExcavationKind : byte
{
    None = 0,
    DigSite = 1,
    ShallowHole = 2,
    OpenShaft = 3,
    RopedEntrance = 4
}

public enum ExcavationTerrainKind : byte
{
    NotDiggable = 0,
    Sand = 1,
    Soil = 2,
    Rock = 3,
    Other = 4
}

/// <summary>
/// Terrain policy needed by an authoritative excavation transaction. The
/// item identifier is deliberately data rather than an inventory mutation;
/// the owning aggregate remains responsible for awarding or dropping it.
/// </summary>
public readonly record struct ExcavationTerrain(
    ExcavationTerrainKind Kind,
    int MaximumHealth,
    string RewardItemId)
{
    public bool IsDiggable =>
        Kind != ExcavationTerrainKind.NotDiggable &&
        MaximumHealth > 0 &&
        !string.IsNullOrWhiteSpace(RewardItemId);
}

public readonly record struct CaveExcavationState(
    Guid Id,
    ExcavationKind Kind,
    Vector2 Position,
    int Health,
    int MaximumHealth);

public readonly record struct CaveExcavationStrikeResult(
    CaveExcavationState State,
    int Damage,
    int ExperienceGained)
{
    public bool Completed => State.Kind != ExcavationKind.DigSite;
}

/// <summary>
/// A linked shaft has distinct object identities on the surface and
/// underground because both endpoints coexist in one aggregate dictionary.
/// The explicit pair and shared position prevent host code from inferring the
/// relationship from rendered objects or coordinates.
/// </summary>
public readonly record struct CavePortalLink(
    Guid SurfaceObjectId,
    Guid UndergroundObjectId,
    Vector2 Position,
    int SurfaceWorldLevel,
    int UndergroundWorldLevel,
    bool Traversable);

public enum CaveCompassDirection : byte
{
    Nearby = 0,
    North = 1,
    NorthEast = 2,
    East = 3,
    SouthEast = 4,
    South = 5,
    SouthWest = 6,
    West = 7,
    NorthWest = 8
}

public readonly record struct CaveProspect(
    Vector2 Position,
    float Distance,
    CaveCompassDirection Direction);

/// <summary>
/// Narrow deterministic world seam used by both the dedicated authority and
/// focused in-memory tests. Aggregate occupancy and actor navigation remain
/// outside this environment.
/// </summary>
public interface ICaveExcavationEnvironment
{
    Vector2 Snap(Vector2 position);

    ExcavationTerrain TerrainAt(Vector2 position);

    bool IsSurfaceDiggable(Vector2 position);

    bool IsCaveBelow(Vector2 position);
}

/// <summary>
/// Headless procedural excavation environment. It consumes the same surface
/// material and cave-density samplers as solo generation, without chunks,
/// persistence, OpenTK, rendering, or UI dependencies.
/// </summary>
public sealed class ProceduralCaveExcavationEnvironment(long worldSeed) :
    ICaveExcavationEnvironment
{
    public long WorldSeed { get; } = worldSeed;

    public Vector2 Snap(Vector2 position) =>
        CaveExcavationRules.Snap(position);

    public ExcavationTerrain TerrainAt(Vector2 position)
    {
        if (!CaveExcavationRules.IsFinite(position))
            return CaveExcavationRules.NotDiggableTerrain;
        var tileX = (int)MathF.Floor(position.X);
        var tileY = (int)MathF.Floor(position.Y);
        var material = ProceduralSurfaceTerrain.ClassifyAt(
            WorldSeed, tileX, tileY).Material;
        return CaveExcavationRules.Terrain(MaterialKind(material));
    }

    public bool IsSurfaceDiggable(Vector2 position)
    {
        if (!TerrainAt(position).IsDiggable) return false;
        var tileX = (int)MathF.Floor(position.X);
        var tileY = (int)MathF.Floor(position.Y);
        var north = Surface(
            ProceduralSurfaceTerrain.RawHeightAt(
                WorldSeed, tileX, tileY));
        var east = Surface(
            ProceduralSurfaceTerrain.RawHeightAt(
                WorldSeed, tileX + 1, tileY));
        var south = Surface(
            ProceduralSurfaceTerrain.RawHeightAt(
                WorldSeed, tileX + 1, tileY + 1));
        var west = Surface(
            ProceduralSurfaceTerrain.RawHeightAt(
                WorldSeed, tileX, tileY + 1));
        var highest = Math.Max(Math.Max(north, east), Math.Max(south, west));
        var lowest = Math.Min(Math.Min(north, east), Math.Min(south, west));
        return highest - lowest <= CaveExcavationRules.MaximumSlopeSteps;
    }

    public bool IsCaveBelow(Vector2 position) =>
        CaveExcavationRules.IsFinite(position) &&
        ProceduralUndergroundTerrain.Density(
            WorldSeed, position.X, position.Y) >=
        ProceduralUndergroundTerrain.Boundary;

    /// <summary>
    /// Uses one short-lived topology cache for the bounded search. The
    /// environment deliberately does not retain that cache because a server
    /// may prospect arbitrarily distant regions over its lifetime.
    /// </summary>
    public bool TryProspect(
        Vector2 origin,
        out CaveProspect prospect,
        int radius = CaveExcavationRules.ProspectRadius)
    {
        var sampling = new ProceduralUndergroundTerrain.SamplingContext(
            WorldSeed);
        return CaveExcavationRules.TryProspect(
            origin,
            position => sampling.Density(position.X, position.Y) >=
                        ProceduralUndergroundTerrain.Boundary,
            out prospect,
            radius);
    }

    private static byte Surface(byte height) =>
        height <= 2 ? (byte)0 : height;

    private static ExcavationTerrainKind MaterialKind(
        ProceduralSurfaceTerrain.Material material) => material switch
    {
        ProceduralSurfaceTerrain.Material.DeepWater or
            ProceduralSurfaceTerrain.Material.ShallowWater or
            ProceduralSurfaceTerrain.Material.RiverWater or
            ProceduralSurfaceTerrain.Material.MangroveShallows =>
                ExcavationTerrainKind.NotDiggable,
        ProceduralSurfaceTerrain.Material.Beach or
            ProceduralSurfaceTerrain.Material.DesertSand =>
                ExcavationTerrainKind.Sand,
        ProceduralSurfaceTerrain.Material.Mud or
            ProceduralSurfaceTerrain.Material.Grassland or
            ProceduralSurfaceTerrain.Material.DryGrass =>
                ExcavationTerrainKind.Soil,
        ProceduralSurfaceTerrain.Material.Rock or
            ProceduralSurfaceTerrain.Material.Highland =>
                ExcavationTerrainKind.Rock,
        _ => ExcavationTerrainKind.Other
    };
}

/// <summary>
/// Canonical excavation lifecycle. Methods are pure and reject malformed
/// states so host code cannot accidentally create an unlinked or traversable
/// shaft through presentation-side item changes.
/// </summary>
public static class CaveExcavationRules
{
    public const int SurfaceWorldLevel = 0;
    public const int UndergroundWorldLevel = -1;
    public const int ProspectRadius = 32;
    public const int MaximumSlopeSteps = 2;

    public const string DigSiteItemId = "dig_site";
    public const string ShallowHoleItemId = "shallow_hole";
    public const string OpenShaftItemId = "cave_hole";
    public const string RopedEntranceItemId = "cave_entrance";
    public const string RopeItemId = "rope";
    public const string DirtItemId = "dirt";
    public const string SandItemId = "sand";

    public static ExcavationTerrain NotDiggableTerrain { get; } =
        new(ExcavationTerrainKind.NotDiggable, 0, "");

    public static ExcavationTerrain Terrain(
        ExcavationTerrainKind kind) => kind switch
    {
        ExcavationTerrainKind.Sand =>
            new(kind, 30, SandItemId),
        ExcavationTerrainKind.Soil =>
            new(kind, 50, DirtItemId),
        ExcavationTerrainKind.Rock =>
            new(kind, 100, DirtItemId),
        ExcavationTerrainKind.Other =>
            new(kind, 70, DirtItemId),
        _ => NotDiggableTerrain
    };

    public static ExcavationKind KindForItemId(string? itemId) =>
        itemId switch
        {
            DigSiteItemId => ExcavationKind.DigSite,
            ShallowHoleItemId => ExcavationKind.ShallowHole,
            OpenShaftItemId => ExcavationKind.OpenShaft,
            RopedEntranceItemId => ExcavationKind.RopedEntrance,
            _ => ExcavationKind.None
        };

    public static string ItemIdForKind(ExcavationKind kind) => kind switch
    {
        ExcavationKind.DigSite => DigSiteItemId,
        ExcavationKind.ShallowHole => ShallowHoleItemId,
        ExcavationKind.OpenShaft => OpenShaftItemId,
        ExcavationKind.RopedEntrance => RopedEntranceItemId,
        _ => ""
    };

    public static Vector2 Snap(Vector2 position)
    {
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position));
        return new(
            MathF.Floor(position.X) + .5f,
            MathF.Floor(position.Y) + .5f);
    }

    public static CaveExcavationState Begin(
        Guid id,
        Vector2 position,
        ExcavationTerrain terrain)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(
                "An excavation requires a stable identity.", nameof(id));
        if (!terrain.IsDiggable)
            throw new ArgumentException(
                "The selected terrain cannot be excavated.",
                nameof(terrain));
        return new(
            id,
            ExcavationKind.DigSite,
            Snap(position),
            terrain.MaximumHealth,
            terrain.MaximumHealth);
    }

    public static CaveExcavationStrikeResult Strike(
        CaveExcavationState site,
        int diggingExperience,
        int shovelPower,
        bool caveBelow)
    {
        if (!IsValid(site) || site.Kind != ExcavationKind.DigSite)
            throw new ArgumentException(
                "Only an active dig site can be excavated.", nameof(site));
        var damage = Math.Min(
            site.Health,
            DiggingDamage(diggingExperience, shovelPower));
        var health = site.Health - damage;
        var kind = health > 0
            ? ExcavationKind.DigSite
            : caveBelow
                ? ExcavationKind.OpenShaft
                : ExcavationKind.ShallowHole;
        var next = site with { Kind = kind, Health = health };
        var experience = damage +
            (health == 0 ? site.MaximumHealth / 5 : 0);
        return new(next, damage, experience);
    }

    public static int DiggingDamage(
        int diggingExperience,
        int shovelPower = 1) =>
        8 + SkillService.LevelForExperience(diggingExperience) / 4 +
        (Math.Max(1, shovelPower) - 1) * 4;

    public static bool CanRestore(CaveExcavationState state) =>
        IsValid(state) && state.Kind == ExcavationKind.DigSite;

    public static bool CanFill(CaveExcavationState state) =>
        IsValid(state) && state.Kind is
            ExcavationKind.ShallowHole or ExcavationKind.OpenShaft;

    public static string RequiredFillItem(ExcavationTerrain terrain) =>
        terrain.IsDiggable ? terrain.RewardItemId : "";

    public static bool CanFillWith(
        CaveExcavationState state,
        ExcavationTerrain terrain,
        string materialItemId) =>
        CanFill(state) && terrain.IsDiggable &&
        string.Equals(
            terrain.RewardItemId,
            materialItemId,
            StringComparison.Ordinal);

    public static bool TryInstallRope(
        CaveExcavationState state,
        out CaveExcavationState entrance)
    {
        entrance = state;
        if (!IsValid(state) || state.Kind != ExcavationKind.OpenShaft)
            return false;
        entrance = state with { Kind = ExcavationKind.RopedEntrance };
        return true;
    }

    public static bool TryTakeRope(
        CaveExcavationState state,
        out CaveExcavationState openShaft)
    {
        openShaft = state;
        if (!IsValid(state) || state.Kind != ExcavationKind.RopedEntrance)
            return false;
        openShaft = state with { Kind = ExcavationKind.OpenShaft };
        return true;
    }

    public static bool TryPortalLink(
        CaveExcavationState surfaceState,
        Guid undergroundObjectId,
        out CavePortalLink portal)
    {
        portal = default;
        if (!IsValid(surfaceState) || undergroundObjectId == Guid.Empty ||
            undergroundObjectId == surfaceState.Id ||
            surfaceState.Kind is not (
                ExcavationKind.OpenShaft or
                ExcavationKind.RopedEntrance))
            return false;
        portal = new(
            surfaceState.Id,
            undergroundObjectId,
            surfaceState.Position,
            SurfaceWorldLevel,
            UndergroundWorldLevel,
            surfaceState.Kind == ExcavationKind.RopedEntrance);
        return true;
    }

    public static bool TryDestinationLevel(
        CaveExcavationState state,
        int currentWorldLevel,
        out int destinationWorldLevel)
    {
        destinationWorldLevel = currentWorldLevel;
        if (!IsValid(state) ||
            state.Kind != ExcavationKind.RopedEntrance)
            return false;
        destinationWorldLevel = currentWorldLevel switch
        {
            SurfaceWorldLevel => UndergroundWorldLevel,
            UndergroundWorldLevel => SurfaceWorldLevel,
            _ => currentWorldLevel
        };
        return destinationWorldLevel != currentWorldLevel;
    }

    public static float Opacity(CaveExcavationState state)
    {
        if (!IsValid(state) || state.Kind != ExcavationKind.DigSite)
            return 1f;
        var progress = 1f - Math.Clamp(
            state.Health / (float)state.MaximumHealth, 0f, 1f);
        return .22f + progress * .78f;
    }

    public static bool TryProspect(
        ICaveExcavationEnvironment environment,
        Vector2 origin,
        out CaveProspect prospect,
        int radius = ProspectRadius)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return TryProspect(
            origin, environment.IsCaveBelow, out prospect, radius);
    }

    internal static bool TryProspect(
        Vector2 origin,
        Func<Vector2, bool> isCaveBelow,
        out CaveProspect prospect,
        int radius = ProspectRadius)
    {
        ArgumentNullException.ThrowIfNull(isCaveBelow);
        prospect = default;
        if (!IsFinite(origin) || radius <= 0 || radius > ProspectRadius)
            return false;
        var bestDistanceSquared = float.MaxValue;
        var best = default(Vector2);
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        for (var offsetX = -radius; offsetX <= radius; offsetX++)
        {
            if (offsetX == 0 && offsetY == 0) continue;
            var distanceSquared =
                offsetX * offsetX + offsetY * offsetY;
            if (distanceSquared > radius * radius ||
                distanceSquared >= bestDistanceSquared)
                continue;
            var candidate = origin + new Vector2(offsetX, offsetY);
            if (!isCaveBelow(candidate)) continue;
            bestDistanceSquared = distanceSquared;
            best = candidate;
        }
        if (bestDistanceSquared == float.MaxValue) return false;
        prospect = new(
            best,
            MathF.Sqrt(bestDistanceSquared),
            CompassDirection(best - origin));
        return true;
    }

    public static string DirectionName(CaveCompassDirection direction) =>
        direction switch
        {
            CaveCompassDirection.North => "north",
            CaveCompassDirection.NorthEast => "north-east",
            CaveCompassDirection.East => "east",
            CaveCompassDirection.SouthEast => "south-east",
            CaveCompassDirection.South => "south",
            CaveCompassDirection.SouthWest => "south-west",
            CaveCompassDirection.West => "west",
            CaveCompassDirection.NorthWest => "north-west",
            _ => "nearby"
        };

    public static bool IsValid(CaveExcavationState state)
    {
        if (state.Id == Guid.Empty || !IsFinite(state.Position) ||
            state.Position != Snap(state.Position) ||
            state.MaximumHealth <= 0 || state.Health < 0 ||
            state.Health > state.MaximumHealth)
            return false;
        return state.Kind switch
        {
            ExcavationKind.DigSite => state.Health > 0,
            ExcavationKind.ShallowHole or
                ExcavationKind.OpenShaft or
                ExcavationKind.RopedEntrance => state.Health == 0,
            _ => false
        };
    }

    internal static bool IsFinite(Vector2 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y);

    private static CaveCompassDirection CompassDirection(Vector2 delta)
    {
        var horizontal = delta.X switch
        {
            < -.5f => -1,
            > .5f => 1,
            _ => 0
        };
        var vertical = delta.Y switch
        {
            < -.5f => -1,
            > .5f => 1,
            _ => 0
        };
        return (vertical, horizontal) switch
        {
            (-1, -1) => CaveCompassDirection.NorthWest,
            (-1, 0) => CaveCompassDirection.North,
            (-1, 1) => CaveCompassDirection.NorthEast,
            (0, -1) => CaveCompassDirection.West,
            (0, 1) => CaveCompassDirection.East,
            (1, -1) => CaveCompassDirection.SouthWest,
            (1, 0) => CaveCompassDirection.South,
            (1, 1) => CaveCompassDirection.SouthEast,
            _ => CaveCompassDirection.Nearby
        };
    }
}
