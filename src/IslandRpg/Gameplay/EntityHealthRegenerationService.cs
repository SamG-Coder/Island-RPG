namespace IslandRpg.Gameplay;

internal readonly record struct HealthRegenerationUpdate(
    int Health,
    float Remainder);

internal static class EntityHealthRegenerationService
{
    public const float BaseHealthPerSecond =
        SurvivalService.BaseHungerLossPerSecond;
    public const float LitCampfireHumanMultiplier = 20;
    public const float LitCampfireRange = 3.5f;

    public static HealthRegenerationUpdate Advance(
        int health,
        int maximumHealth,
        float elapsedRealSeconds,
        float multiplier = 1,
        float remainder = 0)
    {
        maximumHealth = Math.Max(1, maximumHealth);
        health = Math.Clamp(health, 0, maximumHealth);
        remainder = Math.Clamp(remainder, 0, .999999f);
        if (health <= 0 || health >= maximumHealth ||
            elapsedRealSeconds <= 0 || multiplier <= 0)
            return new(health, health >= maximumHealth ? 0 : remainder);

        var accumulated = remainder + BaseHealthPerSecond *
            elapsedRealSeconds * multiplier;
        var wholeHealth = (int)MathF.Floor(accumulated);
        var recovered = Math.Min(maximumHealth, health + wholeHealth);
        return new(
            recovered,
            recovered >= maximumHealth
                ? 0
                : accumulated - wholeHealth);
    }
}
