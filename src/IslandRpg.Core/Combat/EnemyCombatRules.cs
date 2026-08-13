namespace IslandRpg.Gameplay;

public readonly record struct EnemyAttackRequest(
    long WorldSeed,
    Guid EnemyId,
    Guid TargetId,
    ulong AttackSequence,
    int PowerLevel,
    int TargetDefenceExperience = 0,
    MeleeCombatStance TargetStance = MeleeCombatStance.Accurate);

public readonly record struct EnemyAttackResolution(
    bool Hit,
    int Damage);

/// <summary>
/// Authoritative enemy attack rules preserving the established slime scaling
/// while deriving rolls solely from stable simulation inputs.
/// </summary>
public static class EnemyCombatRules
{
    public const float AttackRange = SlimeCombatRules.AttackRange;
    public const double AttackIntervalSeconds =
        MeleeCombatService.AttackIntervalSeconds;

    public static EnemyAttackResolution ResolveAttack(
        in EnemyAttackRequest request)
    {
        if (request.EnemyId == Guid.Empty)
            throw new ArgumentException(
                "An enemy attack requires a stable enemy ID.", nameof(request));
        if (request.TargetId == Guid.Empty)
            throw new ArgumentException(
                "An enemy attack requires a stable target ID.", nameof(request));
        if (!Enum.IsDefined(request.TargetStance))
            throw new ArgumentOutOfRangeException(nameof(request));

        var power = Math.Max(1, request.PowerLevel);
        var experience = Math.Max(1, power * 100);
        var key = new CombatRollKey(
            request.WorldSeed,
            request.EnemyId,
            request.TargetId,
            request.AttackSequence);
        var rolls = DeterministicCombatRandom.Create(key);
        var attack = MeleeCombatService.Roll(
            experience,
            experience,
            rolls.HitRoll,
            rolls.DamageRoll);
        if (!attack.Hit) return new(false, 0);

        // Preserve solo accuracy when no defender progression is supplied.
        // Multiplayer defenders contribute their real Defence level; the
        // Defensive stance counts as three additional effective levels.
        var defenceLevel = request.TargetDefenceExperience <= 0
            ? 1
            : SkillService.LevelForExperience(request.TargetDefenceExperience);
        var stanceBonus = request.TargetStance == MeleeCombatStance.Defensive
            ? 3
            : 0;
        var defencePenalty = Math.Clamp(
            (defenceLevel - 1 + stanceBonus) * .006f, 0, .28f);
        var defendedHitChance = Math.Max(
            .10f,
            MeleeCombatService.HitChance(experience) - defencePenalty);
        return rolls.HitRoll < defendedHitChance
            ? new(true, attack.Damage)
            : new(false, 0);
    }

    public static int ApplyDamage(int health, int damage) =>
        Math.Max(0, health - Math.Max(0, damage));
}
