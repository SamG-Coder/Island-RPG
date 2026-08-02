using IslandRpg.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum MeleeCombatStance
{
    Accurate,
    Aggressive,
    Defensive
}

internal readonly record struct MeleeAttackRoll(
    bool Hit,
    int Damage,
    int Experience);

internal static class MeleeCombatService
{
    public const float AttackRange = .82f;
    public const float AttackIntervalSeconds = 2.4f;
    public const float HitSplatSeconds = 1.15f;
    public const int TrainingDummyMaximumHealth = 100;
    public const float MovingTargetRepathSeconds = .35f;
    public const float MovingTargetRepathDistance = .5f;

    public static bool ShouldRepathMovingTarget(
        double clock,
        double nextRepathAt,
        in Vector2 previousTarget,
        in Vector2 currentTarget) =>
        clock >= nextRepathAt ||
        Vector2.DistanceSquared(
            previousTarget, currentTarget) >
        MovingTargetRepathDistance * MovingTargetRepathDistance;

    public static bool ShouldRequestMovingTargetPath(
        bool pathPending,
        double clock,
        double nextRepathAt,
        in Vector2 previousTarget,
        in Vector2 currentTarget) =>
        !pathPending && ShouldRepathMovingTarget(
            clock, nextRepathAt, previousTarget, currentTarget);

    public static float InteractionRange(Vector2 direction)
    {
        if (direction.LengthSquared < .0001f)
            return AttackRange;
        direction = direction.Normalized();
        var projectedPixelsPerTile = new Vector2(
            (direction.X - direction.Y) * 48,
            (direction.X + direction.Y) * 24).Length;
        const float desiredVisualSeparation = 40;
        return Math.Clamp(
            desiredVisualSeparation /
            Math.Max(1, projectedPixelsPerTile),
            AttackRange,
            1.22f);
    }

    public static MeleeAttackRoll Roll(
        int attackExperience,
        int strengthExperience,
        float hitRoll,
        float damageRoll,
        string?[]? inventory = null)
    {
        var attack = SkillService.LevelForExperience(attackExperience);
        var strength = SkillService.LevelForExperience(strengthExperience);
        var chance = Math.Clamp(.62f + (attack - 1) * .012f, .62f, .90f);
        if (hitRoll >= chance) return new(false, 0, 0);
        var maximumHit = 1 + (strength - 1) / 3;
        var damage = 1 + (int)MathF.Floor(
            Math.Clamp(damageRoll, 0, .9999f) * maximumHit) +
            KnifeDamageBonus(inventory);
        return new(true, damage, damage * 4);
    }

    public static int KnifeDamageBonus(string?[]? inventory) =>
        PlayerInventory.BestKnife(inventory)?.KnifePower ?? 0;

    public static int ExperienceForStance(
        PlayerProfile player,
        MeleeCombatStance stance) =>
        stance switch
        {
            MeleeCombatStance.Accurate => player.AttackExperience,
            MeleeCombatStance.Aggressive => player.StrengthExperience,
            _ => player.DefenceExperience
        };
}
