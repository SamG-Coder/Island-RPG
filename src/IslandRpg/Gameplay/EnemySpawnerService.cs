using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum EnemyKind { WaterSlime, GrassSlime, SandSlime, CaveSlime }
internal enum EnemyBehavior { Idle, Roam, Chase, Return, Attack, Dead }

internal readonly record struct EnemySpawnEntry(EnemyKind Kind, int Weight = 1);

internal sealed record EnemySpawnerState(
    Guid Id,
    Vector2 Position,
    int WorldLevel,
    Biome Biome,
    IReadOnlyList<EnemySpawnEntry> Entries,
    int MaximumAlive = 6,
    double RecoveryUntil = 0,
    int Wave = 0,
    bool WaveStarted = false);

internal sealed record EnemyState(
    Guid Id,
    Guid SpawnerId,
    EnemyKind Kind,
    Vector2 SpawnPosition,
    Vector2 Position,
    Vector2 Destination,
    int WorldLevel,
    int PowerLevel,
    int Health,
    int MaximumHealth,
    EnemyBehavior Behavior = EnemyBehavior.Idle,
    string? TargetId = null,
    string? ProvokedById = null,
    double NextDecisionAt = 0,
    IReadOnlyList<Vector2>? Path = null,
    int PathIndex = 0,
    Vector2? RoutedDestination = null,
    double NextPathAt = 0,
    EntityAction VisualAction = EntityAction.Idle,
    double VisualActionStartedAt = 0,
    double AggroReadyAt = 0)
{
    public bool Alive => Health > 0;
}

internal readonly record struct EnemyActorPresence(
    string Id, Vector2 Position, int WorldLevel, bool Alive,
    int PowerLevel = 1, bool IsPlayer = false);

internal readonly record struct EnemySpawnerUpdate(
    EnemySpawnerState Spawner,
    IReadOnlyList<EnemyState> Enemies,
    bool Active,
    bool StartedRecovery,
    bool SpawnedWave);

internal static class EnemySpawnerService
{
    private static readonly EnemyController SlimeController =
        new SlimeEnemyController();
    public const float ActivationRadius = 24f;
    public const float SpawnRadius = 3.5f;
    public const float RoamRadius = 4f;
    public const float LeashRadius = 8f;
    public const float CaveAggroRadius = 5f;
    public const float AttackRange = 1.25f;
    public const double RecoverySeconds = 45;

    public static bool Supports(EnemyKind kind, Biome biome, int level) =>
        kind switch
        {
            EnemyKind.WaterSlime =>
                level == (int)WorldLevel.Overworld && biome == Biome.Beach,
            EnemyKind.GrassSlime =>
                level == (int)WorldLevel.Overworld &&
                biome is Biome.Grassland or Biome.DryGrass,
            EnemyKind.SandSlime =>
                level == (int)WorldLevel.Overworld &&
                biome is Biome.DesertSand or Biome.CrackedEarth,
            EnemyKind.CaveSlime => level == (int)WorldLevel.Underground,
            _ => false
        };

    public static EnemySpawnerUpdate Update(
        EnemySpawnerState spawner,
        IReadOnlyList<EnemyState> enemies,
        IReadOnlyList<EnemyActorPresence> actors,
        double now,
        int worldSeed)
    {
        var activeActors = actors.Where(actor =>
            actor.Alive && actor.WorldLevel == spawner.WorldLevel &&
            Vector2.DistanceSquared(actor.Position, spawner.Position) <=
            ActivationRadius * ActivationRadius).ToArray();
        var active = activeActors.Length > 0;
        var living = enemies.Where(enemy =>
            enemy.SpawnerId == spawner.Id && enemy.Alive).ToList();
        if (!active)
            return new(spawner, living, false, false, false);

        var startedRecovery = false;
        if (spawner.WaveStarted && living.Count == 0 &&
            spawner.RecoveryUntil <= 0)
        {
            spawner = spawner with { RecoveryUntil = now + RecoverySeconds };
            startedRecovery = true;
        }
        if (living.Count == 0 && now >= spawner.RecoveryUntil &&
            (!spawner.WaveStarted || !startedRecovery))
        {
            var count = AdaptiveCount(spawner.MaximumAlive, activeActors);
            var power = AdaptivePower(activeActors);
            for (var index = 0; index < count; index++)
                living.Add(Spawn(
                    spawner, index, power, worldSeed, activeActors));
            spawner = spawner with
            {
                Wave = spawner.Wave + 1,
                WaveStarted = true,
                RecoveryUntil = 0
            };
            return new(spawner, living, true, false, true);
        }
        return new(spawner, living, true, startedRecovery, false);
    }

    public static EnemyState UpdateController(
        EnemyState enemy,
        IReadOnlyList<EnemyActorPresence> actors,
        double now,
        float elapsed,
        int worldSeed)
        => SlimeController.Update(
            enemy, actors, now, elapsed, worldSeed);

    public static EnemyState Provoke(EnemyState enemy, string attackerId) =>
        enemy.Alive ? enemy with { ProvokedById = attackerId } : enemy;

    private static int AdaptiveCount(
        int maximum, IReadOnlyList<EnemyActorPresence> actors)
    {
        var power = actors.Sum(actor => Math.Max(1, actor.PowerLevel));
        return Math.Clamp(1 + actors.Count + power / 12, 1, maximum);
    }

    private static int AdaptivePower(IReadOnlyList<EnemyActorPresence> actors) =>
        Math.Clamp((int)Math.Ceiling(
            actors.Average(actor => Math.Max(1, actor.PowerLevel)) * .65f), 1, 50);

    private static EnemyState Spawn(
        EnemySpawnerState spawner, int index, int power, int seed,
        IReadOnlyList<EnemyActorPresence> actors)
    {
        var valid = spawner.Entries.Where(entry =>
            entry.Weight > 0 && Supports(
                entry.Kind, spawner.Biome, spawner.WorldLevel)).ToArray();
        if (valid.Length == 0)
            throw new InvalidOperationException(
                "Enemy spawner has no biome-compatible spawn entries.");
        var random = new Random(HashCode.Combine(seed, spawner.Id, spawner.Wave, index));
        var totalWeight = valid.Sum(entry => entry.Weight);
        var roll = random.Next(totalWeight);
        var entry = valid[0];
        foreach (var candidate in valid)
        {
            if (roll < candidate.Weight) { entry = candidate; break; }
            roll -= candidate.Weight;
        }
        var position = spawner.Position;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var radius = 2 + random.NextSingle() * (SpawnRadius - 2);
            var candidate = spawner.Position +
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            position = candidate;
            if (actors.All(actor => Vector2.DistanceSquared(
                    actor.Position, candidate) >= 5 * 5))
                break;
        }
        var health = 16 + power * 4;
        return new(
            Guid.NewGuid(), spawner.Id, entry.Kind, position, position, position,
            spawner.WorldLevel, power, health, health);
    }

}
