using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

/// <summary>
/// Small deterministic fresh-world encounter ring. It is server-authored,
/// bounded, navigation checked, and seeded only when no combat checkpoint was
/// restored. Checkpoints thereafter own the exact identities and state.
/// </summary>
internal static class ProceduralEnemyBootstrap
{
    private const int SurfaceEnemyCount = 12;
    private const int UndergroundEnemyCount = 4;
    private static readonly Guid IdentityDomain =
        new("c07b61b3-2d5c-5c24-a223-8e9b3dc9a011");

    public static IReadOnlyList<AuthoritativeEnemySeed> Create(
        long worldSeed,
        Vector2 origin,
        IWorldNavigationQuery navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var result = new List<AuthoritativeEnemySeed>(
            SurfaceEnemyCount + UndergroundEnemyCount);
        AddLevel(result, worldSeed, origin, navigation, 0,
            SurfaceEnemyCount, 7.5f, 19f);
        AddLevel(result, worldSeed, origin, navigation, -1,
            UndergroundEnemyCount, 5f, 14f);
        return result;
    }

    private static void AddLevel(
        ICollection<AuthoritativeEnemySeed> result,
        long worldSeed,
        Vector2 origin,
        IWorldNavigationQuery navigation,
        int worldLevel,
        int count,
        float minimumRadius,
        float maximumRadius)
    {
        if (!navigation.SupportsWorldLevel(worldLevel)) return;
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var id = DeterministicEnemyRandom.StableGuid(
                worldSeed,
                IdentityDomain,
                checked((ulong)(worldLevel + 2) * 10_000UL +
                    (ulong)ordinal),
                0x534C_494D_4553UL);
            if (!TryPosition(worldSeed, id, origin, navigation, worldLevel,
                    minimumRadius, maximumRadius, out var position))
                continue;
            var kind = worldLevel < 0
                ? EnemyKind.CaveSlime
                : SurfaceKind(worldSeed, id, position, navigation);
            var power = 1 + (int)(DeterministicEnemyRandom.UnitFloat(
                worldSeed, id, 0, 0x504F_5745_52UL) * 6);
            result.Add(new AuthoritativeEnemySeed(
                new EnemyId(id),
                kind,
                position,
                worldLevel,
                power));
        }
    }

    private static bool TryPosition(
        long worldSeed,
        Guid id,
        Vector2 origin,
        IWorldNavigationQuery navigation,
        int worldLevel,
        float minimumRadius,
        float maximumRadius,
        out Vector2 position)
    {
        // Fixed attempts prevent malformed/procedurally blocked regions from
        // turning bootstrap into an unbounded search.
        for (ulong attempt = 0; attempt < 48; attempt++)
        {
            var angle = DeterministicEnemyRandom.UnitFloat(
                worldSeed, id, attempt, 0x414E_474C_45UL) * MathF.Tau;
            var radius = minimumRadius +
                DeterministicEnemyRandom.UnitFloat(
                    worldSeed, id, attempt, 0x5241_4449_55UL) *
                (maximumRadius - minimumRadius);
            var candidate = origin + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * radius;
            candidate = new Vector2(
                MathF.Round(candidate.X * 4) / 4,
                MathF.Round(candidate.Y * 4) / 4);
            if (!navigation.CanStandAt(candidate, worldLevel)) continue;
            position = candidate;
            return true;
        }
        position = default;
        return false;
    }

    private static EnemyKind SurfaceKind(
        long worldSeed,
        Guid id,
        Vector2 position,
        IWorldNavigationQuery navigation)
    {
        // Navigation supplies the authoritative water classification. Sand and
        // vegetation remain deterministic encounter variety without requiring
        // renderer biome data in the headless server.
        if (navigation.IsWading(position, 0)) return EnemyKind.WaterSlime;
        var roll = DeterministicEnemyRandom.UnitFloat(
            worldSeed, id, 0, 0x4B49_4E44UL);
        return roll switch
        {
            < .55f => EnemyKind.GrassSlime,
            < .88f => EnemyKind.SandSlime,
            _ => position.LengthSquared() % 2 < 1
                ? EnemyKind.GrassSlime
                : EnemyKind.SandSlime
        };
    }
}
