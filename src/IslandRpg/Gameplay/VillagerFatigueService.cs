namespace IslandRpg.Gameplay;

internal static class VillagerFatigueService
{
    public const float MaximumEnergy = 100;
    public const float RestThreshold = 20;
    public const float RestResumeThreshold = 50;

    public static VillagerState Advance(
        VillagerState state,
        double gameSeconds)
    {
        if (state.Health <= 0 ||
            state.LastEnergyGameSeconds is null)
            return state with { LastEnergyGameSeconds = gameSeconds };
        var elapsedRealSeconds = Math.Max(
            0,
            (gameSeconds - state.LastEnergyGameSeconds.Value) /
            VillagerSimulation.GameSecondsPerRealSecond);
        if (elapsedRealSeconds <= 0) return state;
        var rate = RatePerRealSecond(state);
        return state with
        {
            Energy = Math.Clamp(
                state.Energy + (float)(rate * elapsedRealSeconds),
                0,
                MaximumEnergy),
            LastEnergyGameSeconds = gameSeconds
        };
    }

    public static bool ShouldRest(VillagerState state) =>
        state.Health > 0 &&
        !HasEssentialOverride(state) &&
        (state.Energy < RestThreshold ||
         state.Activity == VillagerActivity.Resting &&
         state.Energy < RestResumeThreshold);

    public static VillagerState BeginRest(
        VillagerState state,
        double gameSeconds) => state.Health <= 0
        ? state
        : state with
        {
            Need = VillagerNeed.Idle,
            Activity = VillagerActivity.Resting,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            GoalObjectId = null,
            NextDecisionGameSeconds = gameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };

    public static float MovementEffectiveness(float energy) =>
        .55f + .45f * Math.Clamp(energy / MaximumEnergy, 0, 1);

    public static float WorkEffectiveness(float energy) =>
        .6f + .4f * Math.Clamp(energy / MaximumEnergy, 0, 1);

    public static double AdjustedWorkDuration(
        double duration,
        float energy) =>
        duration / WorkEffectiveness(energy);

    private static bool HasEssentialOverride(VillagerState state) =>
        state.Hunger <= 35 ||
        state.Health <= 20 ||
        state.ConflictIntent != VillagerConflictIntent.None ||
        state.Action == EntityAction.Attack;

    private static float RatePerRealSecond(VillagerState state) =>
        state.Action switch
        {
            EntityAction.Move => -.35f,
            EntityAction.Attack => -1f,
            EntityAction.Work or EntityAction.Gather or
                EntityAction.Dig or EntityAction.Mine or
                EntityAction.Fish => -.7f,
            EntityAction.Idle when
                state.Activity == VillagerActivity.Resting => 1.5f,
            EntityAction.Idle => .5f,
            _ => 0
        };
}
