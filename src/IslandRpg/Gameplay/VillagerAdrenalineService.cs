namespace IslandRpg.Gameplay;

internal static class VillagerAdrenalineService
{
    public const float MaximumStress = 100;
    public const float BaseEnergyBoost = 35;
    public const float StressCost = 30;
    public const double ActiveRealSeconds = 8;
    public const double CooldownRealSeconds = 60;
    private const float StressRecoveryPerRealSecond = .12f;

    public static bool IsActive(VillagerState state, double gameSeconds) =>
        state.Health > 0 && state.AdrenalineUntilGameSeconds > gameSeconds;

    public static VillagerState Advance(
        VillagerState state,
        double gameSeconds,
        bool immediateDanger)
    {
        if (state.Health <= 0) return state;
        var last = state.LastAdrenalineGameSeconds ?? gameSeconds;
        var elapsedRealSeconds = Math.Max(
            0, (gameSeconds - last) /
               VillagerSimulation.GameSecondsPerRealSecond);
        var stress = IsActive(state, gameSeconds)
            ? state.AdrenalineStress
            : Math.Max(
                0,
                state.AdrenalineStress -
                (float)elapsedRealSeconds * StressRecoveryPerRealSecond);
        state = state with
        {
            AdrenalineStress = stress,
            LastAdrenalineGameSeconds = gameSeconds
        };
        if (!immediateDanger ||
            state.Energy >= VillagerFatigueService.MaximumEnergy ||
            state.AdrenalineCooldownUntilGameSeconds > gameSeconds)
            return state;

        var stressPenalty = .5f * stress / MaximumStress;
        var boost = BaseEnergyBoost * (1 - stressPenalty);
        return state with
        {
            Energy = Math.Min(
                VillagerFatigueService.MaximumEnergy,
                state.Energy + boost),
            AdrenalineStress = Math.Min(
                MaximumStress, stress + StressCost),
            AdrenalineUntilGameSeconds = gameSeconds +
                ActiveRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond,
            AdrenalineCooldownUntilGameSeconds = gameSeconds +
                CooldownRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond
        };
    }
}
