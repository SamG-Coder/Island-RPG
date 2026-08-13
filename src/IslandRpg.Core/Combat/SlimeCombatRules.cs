using System.Numerics;

namespace IslandRpg.Gameplay;

/// <summary>Headless rules shared by solo and authoritative slime combat.</summary>
public static class SlimeCombatRules
{
    private const ulong SplitAngleDomain = 0x534C495445414E47UL;
    private const ulong SplitIdDomain = 0x534C495445494420UL;
    private const ulong LootQuantityDomain = 0x4C4F4F5451545920UL;
    private const ulong LootReagentDomain = 0x4C4F4F5452454147UL;
    private const ulong LootCoreDomain = 0x4C4F4F54434F5245UL;

    public const int SplitPowerThreshold = 3;
    public const int MaximumSplitGeneration = 1;
    public const float AttackRange = 1.25f;
    public const float RoamSpeed = .68f;
    public const float ReturnSpeed = 1.05f;
    public const float ChaseSpeed = 1.35f;

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
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static double ReactionDelaySeconds(EnemyKind kind) =>
        kind == EnemyKind.CaveSlime ? .25 : .8;

    public static bool UsesIdleCamouflage(EnemyKind kind) =>
        kind == EnemyKind.GrassSlime;

    public static bool UsesAggroBurrow(EnemyKind kind) =>
        kind == EnemyKind.SandSlime;

    public static float MovementSpeed(EnemyBehavior behavior) => behavior switch
    {
        EnemyBehavior.Chase => ChaseSpeed,
        EnemyBehavior.Return => ReturnSpeed,
        EnemyBehavior.Roam => RoamSpeed,
        _ => 0
    };

    public static SlimeVictimStatus Apply(
        SlimeVictimStatus current,
        EnemyKind kind,
        double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
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

    /// <summary>
    /// Consumes every poison tick due at <paramref name="now"/>. Catch-up keeps
    /// damage independent of server step size and prevents hitches skipping it.
    /// </summary>
    public static SlimeStatusAdvance Advance(
        SlimeVictimStatus current,
        double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        var slowExpired = current.SlowedUntil > 0 &&
                          now >= current.SlowedUntil;
        var rootExpired = current.RootedUntil > 0 &&
                          now >= current.RootedUntil;
        var poisonExpired = current.PoisonedUntil > 0 &&
                            now >= current.PoisonedUntil;
        var ticks = 0;
        if (current.PoisonDamage > 0 &&
            current.NextPoisonTickAt > 0 &&
            current.NextPoisonTickAt < current.PoisonedUntil &&
            now >= current.NextPoisonTickAt)
        {
            var interval = AttackFor(
                EnemyKind.CaveSlime).PoisonIntervalSeconds;
            // Poison is active on [application, deadline). At the deadline,
            // catch up every still-due tick strictly before it, then clear the
            // durable scheduling metadata below.
            var lastEligible = Math.Min(
                now,
                Math.BitDecrement(current.PoisonedUntil));
            if (lastEligible >= current.NextPoisonTickAt)
                ticks = 1 + (int)Math.Floor(
                    (lastEligible - current.NextPoisonTickAt) / interval);
        }

        var next = current;
        if (ticks > 0)
        {
            var interval = AttackFor(
                EnemyKind.CaveSlime).PoisonIntervalSeconds;
            next = next with
            {
                NextPoisonTickAt = Math.Min(
                    current.PoisonedUntil,
                    current.NextPoisonTickAt + ticks * interval)
            };
        }
        if (slowExpired) next = next with { SlowedUntil = 0 };
        if (rootExpired) next = next with { RootedUntil = 0 };
        if (poisonExpired)
            next = next with
            {
                PoisonedUntil = 0,
                NextPoisonTickAt = 0,
                PoisonDamage = 0
            };
        return new(
            next,
            checked(current.PoisonDamage * ticks),
            ticks,
            slowExpired,
            rootExpired,
            poisonExpired);
    }

    public static float SizeScale(int powerLevel) =>
        1 + Math.Clamp(powerLevel - 1, 0, 8) * .055f;

    public static bool CanSplit(int powerLevel, int splitGeneration) =>
        powerLevel >= SplitPowerThreshold &&
        splitGeneration < MaximumSplitGeneration;

    public static SlimeSplitChild[] Split(
        in SlimeSplitSource parent,
        long worldSeed)
    {
        if (parent.EnemyId == Guid.Empty ||
            parent.SpawnerId == Guid.Empty ||
            !float.IsFinite(parent.SpawnPosition.X) ||
            !float.IsFinite(parent.SpawnPosition.Y) ||
            !float.IsFinite(parent.Position.X) ||
            !float.IsFinite(parent.Position.Y) ||
            !float.IsFinite(parent.SizeScale) ||
            parent.SizeScale <= 0 ||
            parent.MaximumHealth <= 0 ||
            parent.SplitGeneration < 0)
            throw new ArgumentException(
                "The slime split source is not well formed.", nameof(parent));
        if (!CanSplit(parent.PowerLevel, parent.SplitGeneration)) return [];
        var children = new SlimeSplitChild[2];
        for (var index = 0; index < children.Length; index++)
        {
            var sequence = (ulong)(parent.SplitGeneration * 2 + index);
            var angle = DeterministicEnemyRandom.UnitFloat(
                worldSeed, parent.EnemyId, sequence,
                SplitAngleDomain) * MathF.Tau;
            var offset = new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * (.35f + index * .18f);
            var health = Math.Max(6, parent.MaximumHealth / 3);
            children[index] = new(
                DeterministicEnemyRandom.StableGuid(
                    worldSeed, parent.EnemyId, sequence, SplitIdDomain),
                parent.SpawnerId,
                parent.Kind,
                parent.SpawnPosition,
                parent.Position + offset,
                parent.WorldLevel,
                Math.Max(1, parent.PowerLevel / 2),
                health,
                Math.Max(.62f, parent.SizeScale * .68f),
                parent.SplitGeneration + 1);
        }
        return children;
    }

    public static SlimeLootDrop[] RollLoot(in SlimeLootSource source)
    {
        if (source.EnemyId == Guid.Empty)
            throw new ArgumentException(
                "A loot roll requires a stable enemy ID.", nameof(source));
        var quantityRoll = DeterministicEnemyRandom.UnitFloat(
            source.WorldSeed, source.EnemyId, 0, LootQuantityDomain);
        var reagentRoll = DeterministicEnemyRandom.UnitFloat(
            source.WorldSeed, source.EnemyId, 0, LootReagentDomain);
        var coreRoll = DeterministicEnemyRandom.UnitFloat(
            source.WorldSeed, source.EnemyId, 0, LootCoreDomain);
        var drops = new List<SlimeLootDrop>(3)
        {
            new(ItemIds.SlimeGel, quantityRoll < .5f ? 1 : 2)
        };
        if (reagentRoll < .38f)
            drops.Add(new(BiomeReagent(source.Kind), 1));
        if (coreRoll < Math.Clamp(.08f + source.PowerLevel * .01f, 0, 1))
            drops.Add(new(ItemIds.SlimeCore, 1));
        return [.. drops];
    }

    public static string BiomeReagent(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => ItemIds.SaltCrystals,
        EnemyKind.GrassSlime => ItemIds.MedicinalHerbs,
        EnemyKind.SandSlime => ItemIds.SaltCrystals,
        EnemyKind.CaveSlime => ItemIds.Coal,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
