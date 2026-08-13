namespace IslandRpg.Gameplay;

/// <summary>
/// Compatibility adapter for the solo renderer-owned enemy model. New
/// authoritative code consumes <see cref="SlimeCombatRules"/> directly.
/// </summary>
internal static class SlimeAbilityService
{
    public const int SplitPowerThreshold = SlimeCombatRules.SplitPowerThreshold;
    public const int MaximumSplitGeneration =
        SlimeCombatRules.MaximumSplitGeneration;

    public static SlimeAttackAbility AttackFor(EnemyKind kind) =>
        SlimeCombatRules.AttackFor(kind);

    public static SlimeVictimStatus Apply(
        SlimeVictimStatus current, EnemyKind kind, double now)
        => SlimeCombatRules.Apply(current, kind, now);

    public static SlimeStatusAdvance Advance(
        SlimeVictimStatus current, double now)
        => SlimeCombatRules.Advance(current, now);

    public static float SizeScale(int powerLevel) =>
        SlimeCombatRules.SizeScale(powerLevel);

    public static bool CanSplit(EnemyState enemy) =>
        SlimeCombatRules.CanSplit(
            enemy.PowerLevel, enemy.SplitGeneration);

    public static EnemyState[] Split(EnemyState enemy, long worldSeed)
    {
        var split = SlimeCombatRules.Split(
            new(
                enemy.Id,
                enemy.SpawnerId,
                enemy.Kind,
                (System.Numerics.Vector2)enemy.SpawnPosition,
                (System.Numerics.Vector2)enemy.Position,
                enemy.WorldLevel,
                enemy.PowerLevel,
                enemy.MaximumHealth,
                enemy.SizeScale,
                enemy.SplitGeneration),
            worldSeed);
        return split.Select(child =>
        {
            var position = (OpenTK.Mathematics.Vector2)child.Position;
            return new EnemyState(
                child.EnemyId,
                child.SpawnerId,
                child.Kind,
                (OpenTK.Mathematics.Vector2)child.SpawnPosition,
                position,
                position,
                child.WorldLevel,
                child.PowerLevel,
                child.Health,
                child.Health,
                NextDecisionAt: 0,
                SizeScale: child.SizeScale,
                SplitGeneration: child.SplitGeneration);
        }).ToArray();
    }
}
