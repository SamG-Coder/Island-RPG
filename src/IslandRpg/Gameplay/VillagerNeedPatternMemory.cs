namespace IslandRpg.Gameplay;

/// <summary>
/// Retains short need histories and projects recurring trends into a small,
/// actionable planning horizon.
/// </summary>
internal static class VillagerNeedPatternMemory
{
    public const string HungerSampleKind = "need-pattern:hunger";
    public const int SamplesPerPerson = 4;
    public const double PlanningHorizonRealSeconds = 180;
    public const float FoodPlanningThreshold = 35;

    public static VillagerState ObserveHunger(
        VillagerState observer,
        string subjectId,
        string subjectName,
        float hunger,
        double gameSeconds)
    {
        var memories = observer.Memories?.ToList() ?? [];
        var samples = memories
            .Select((memory, index) => (memory, index))
            .Where(value =>
                value.memory.Kind == HungerSampleKind &&
                value.memory.SubjectId == subjectId)
            .OrderBy(value => value.memory.GameSeconds)
            .ToArray();
        if (samples.Length > 0 &&
            gameSeconds <= samples[^1].memory.GameSeconds)
            return observer;
        while (samples.Length >= SamplesPerPerson)
        {
            memories.RemoveAt(samples[0].index);
            samples = memories
                .Select((memory, index) => (memory, index))
                .Where(value =>
                    value.memory.Kind == HungerSampleKind &&
                    value.memory.SubjectId == subjectId)
                .OrderBy(value => value.memory.GameSeconds)
                .ToArray();
        }
        memories.Add(new(
            Guid.NewGuid(),
            HungerSampleKind,
            subjectId,
            null,
            .95f,
            gameSeconds,
            Summary: $"{subjectName}'s hunger was {MathF.Round(hunger)}.",
            ObservedValue: hunger));
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - VillagerSimulation.MaximumMemories);
        return observer with { Memories = memories };
    }

    public static float ForecastHunger(
        VillagerState observer,
        string subjectId,
        double gameSeconds,
        double horizonRealSeconds = PlanningHorizonRealSeconds)
    {
        var samples = observer.Memories?
            .Where(memory =>
                memory.Kind == HungerSampleKind &&
                memory.SubjectId == subjectId &&
                memory.ObservedValue is not null)
            .OrderBy(memory => memory.GameSeconds)
            .TakeLast(SamplesPerPerson)
            .ToArray() ?? [];
        if (samples.Length == 0) return SurvivalService.MaximumHunger;
        var latest = samples[^1];
        if (samples.Length == 1) return latest.ObservedValue!.Value;
        var earliest = samples[0];
        var elapsed = latest.GameSeconds - earliest.GameSeconds;
        if (elapsed <= 0) return latest.ObservedValue!.Value;
        var changePerGameSecond =
            (latest.ObservedValue!.Value - earliest.ObservedValue!.Value) /
            elapsed;
        var futureGameSeconds = Math.Max(
            0, gameSeconds - latest.GameSeconds) +
            Math.Max(0, horizonRealSeconds) *
            VillagerSimulation.GameSecondsPerRealSecond;
        return Math.Clamp(
            latest.ObservedValue.Value +
            (float)(changePerGameSecond * futureGameSeconds),
            0,
            SurvivalService.MaximumHunger);
    }

    public static bool NeedsFoodSoon(
        VillagerState observer,
        string subjectId,
        double gameSeconds) =>
        ForecastHunger(observer, subjectId, gameSeconds) <=
        FoodPlanningThreshold;
}
