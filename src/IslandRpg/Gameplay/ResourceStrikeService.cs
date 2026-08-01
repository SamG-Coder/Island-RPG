namespace IslandRpg.Gameplay;

internal readonly record struct ResourceStrikeResult(
    bool Hit,
    int Damage,
    int Health,
    SkillExperienceChange Experience)
{
    public bool Depleted => Health == 0;
}

internal static class ResourceStrikeService
{
    public static ResourceStrikeResult Woodcut(
        int experience,
        int health,
        int maximumHealth,
        int toolPower,
        float accuracyRoll,
        float damageRoll)
    {
        var strike = WoodcuttingSkill.Roll(
            experience, accuracyRoll, damageRoll, toolPower);
        return Resolve(
            strike.Hit, strike.Damage, experience, health,
            strike.Hit && strike.Damage >= health
                ? Math.Max(10, maximumHealth / 5)
                : 0);
    }

    public static ResourceStrikeResult Mine(
        int experience,
        int health,
        int toolPower,
        int completionExperience,
        float accuracyRoll,
        float damageRoll)
    {
        var strike = MiningSkill.Roll(
            experience, accuracyRoll, damageRoll, toolPower);
        return Resolve(
            strike.Hit, strike.Damage, experience, health,
            strike.Hit && strike.Damage >= health
                ? completionExperience
                : 0);
    }

    private static ResourceStrikeResult Resolve(
        bool hit,
        int rolledDamage,
        int experience,
        int health,
        int completionExperience)
    {
        var currentHealth = Math.Max(0, health);
        if (!hit || currentHealth == 0)
            return new(false, 0, currentHealth,
                SkillService.AwardExperience(experience, 0));
        var damage = Math.Min(currentHealth, Math.Max(0, rolledDamage));
        var remainingHealth = currentHealth - damage;
        return new(
            true,
            damage,
            remainingHealth,
            SkillService.AwardExperience(
                experience, damage + completionExperience));
    }
}
