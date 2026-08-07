using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum SlimeStatusKind
{
    None,
    Slow,
    Root,
    Poison
}

internal readonly record struct SlimeAttackAbility(
    SlimeStatusKind Status,
    double DurationSeconds,
    float MovementMultiplier = 1,
    int PoisonDamage = 0,
    double PoisonIntervalSeconds = 1);

internal readonly record struct SlimeVictimStatus(
    double SlowedUntil = 0,
    double RootedUntil = 0,
    double PoisonedUntil = 0,
    double NextPoisonTickAt = 0,
    int PoisonDamage = 0)
{
    public float MovementMultiplier(double now) =>
        now < RootedUntil ? 0 : now < SlowedUntil ? .58f : 1;
}

internal readonly record struct SlimeStatusAdvance(
    SlimeVictimStatus Status,
    int PoisonDamage);

internal static class SlimeAbilityService
{
    public const int SplitPowerThreshold = 3;
    public const int MaximumSplitGeneration = 1;

    public static SlimeAttackAbility AttackFor(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => new(
            SlimeStatusKind.Slow, 3.5, MovementMultiplier: .58f),
        EnemyKind.GrassSlime => new(
            SlimeStatusKind.Root, 1.25, MovementMultiplier: 0),
        EnemyKind.SandSlime => default,
        EnemyKind.CaveSlime => new(
            SlimeStatusKind.Poison, 5, PoisonDamage: 1,
            PoisonIntervalSeconds: 1),
        _ => default
    };

    public static SlimeVictimStatus Apply(
        SlimeVictimStatus current, EnemyKind kind, double now)
    {
        var ability = AttackFor(kind);
        return ability.Status switch
        {
            SlimeStatusKind.Slow => current with
            {
                SlowedUntil = Math.Max(
                    current.SlowedUntil, now + ability.DurationSeconds)
            },
            SlimeStatusKind.Root => current with
            {
                RootedUntil = Math.Max(
                    current.RootedUntil, now + ability.DurationSeconds)
            },
            SlimeStatusKind.Poison => current with
            {
                PoisonedUntil = Math.Max(
                    current.PoisonedUntil, now + ability.DurationSeconds),
                NextPoisonTickAt = current.NextPoisonTickAt > now
                    ? current.NextPoisonTickAt
                    : now + ability.PoisonIntervalSeconds,
                PoisonDamage = Math.Max(
                    current.PoisonDamage, ability.PoisonDamage)
            },
            _ => current
        };
    }

    public static SlimeStatusAdvance Advance(
        SlimeVictimStatus current, double now)
    {
        if (current.PoisonDamage <= 0 ||
            now < current.NextPoisonTickAt ||
            now >= current.PoisonedUntil)
            return new(current, 0);
        var interval = AttackFor(EnemyKind.CaveSlime).PoisonIntervalSeconds;
        return new(
            current with
            {
                NextPoisonTickAt = Math.Min(
                    current.PoisonedUntil,
                    current.NextPoisonTickAt + interval)
            },
            current.PoisonDamage);
    }

    public static float SizeScale(int powerLevel) =>
        1 + Math.Clamp(powerLevel - 1, 0, 8) * .055f;

    public static bool CanSplit(EnemyState enemy) =>
        enemy.PowerLevel >= SplitPowerThreshold &&
        enemy.SplitGeneration < MaximumSplitGeneration;

    public static EnemyState[] Split(EnemyState enemy, int seed)
    {
        if (!CanSplit(enemy)) return [];
        var random = new Random(HashCode.Combine(seed, enemy.Id));
        var children = new EnemyState[2];
        for (var index = 0; index < children.Length; index++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var offset = new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * (.35f + index * .18f);
            var health = Math.Max(6, enemy.MaximumHealth / 3);
            var position = enemy.Position + offset;
            children[index] = new(
                Guid.NewGuid(), enemy.SpawnerId, enemy.Kind,
                enemy.SpawnPosition, position, position,
                enemy.WorldLevel, Math.Max(1, enemy.PowerLevel / 2),
                health, health,
                NextDecisionAt: 0,
                SizeScale: Math.Max(.62f, enemy.SizeScale * .68f),
                SplitGeneration: enemy.SplitGeneration + 1);
        }
        return children;
    }
}
