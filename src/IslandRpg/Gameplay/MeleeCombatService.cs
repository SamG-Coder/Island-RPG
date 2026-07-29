using IslandRpg.Persistence;

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

    public static MeleeAttackRoll Roll(
        int attackExperience,
        int strengthExperience,
        float hitRoll,
        float damageRoll)
    {
        var attack = SkillService.LevelForExperience(attackExperience);
        var strength = SkillService.LevelForExperience(strengthExperience);
        var chance = Math.Clamp(.62f + (attack - 1) * .012f, .62f, .90f);
        if (hitRoll >= chance) return new(false, 0, 0);
        var maximumHit = 1 + (strength - 1) / 3;
        var damage = 1 + (int)MathF.Floor(
            Math.Clamp(damageRoll, 0, .9999f) * maximumHit);
        return new(true, damage, damage * 4);
    }

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
