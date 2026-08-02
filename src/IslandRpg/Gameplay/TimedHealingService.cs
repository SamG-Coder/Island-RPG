namespace IslandRpg.Gameplay;

internal readonly record struct TimedHealingState(
    float RemainingHealth,
    float RemainingSeconds,
    float FractionalHealth = 0)
{
    public bool Active => RemainingHealth > 0 && RemainingSeconds > 0;
}

internal readonly record struct TimedHealingUpdate(
    int Health,
    TimedHealingState State);

internal static class TimedHealingService
{
    public static TimedHealingState Start(FoodEffect effect) =>
        effect.TimedHealing > 0 && effect.TimedHealingSeconds > 0
            ? new(effect.TimedHealing, effect.TimedHealingSeconds)
            : default;

    public static TimedHealingUpdate Advance(
        int health,
        int maximumHealth,
        float elapsed,
        TimedHealingState state)
    {
        if (health <= 0 || health >= maximumHealth || elapsed <= 0 ||
            !state.Active)
            return new(health, health >= maximumHealth ? default : state);
        var duration = Math.Min(elapsed, state.RemainingSeconds);
        var healing = state.RemainingHealth * duration / state.RemainingSeconds;
        var accumulated = state.FractionalHealth + healing;
        var whole = (int)MathF.Floor(accumulated);
        var recovered = Math.Min(maximumHealth, health + whole);
        var remainingSeconds = state.RemainingSeconds - duration;
        var remainingHealth = Math.Max(0, state.RemainingHealth - healing);
        var next = remainingSeconds <= 0 || remainingHealth <= 0 ||
                   recovered >= maximumHealth
            ? default
            : new TimedHealingState(
                remainingHealth,
                remainingSeconds,
                accumulated - whole);
        return new(recovered, next);
    }
}
