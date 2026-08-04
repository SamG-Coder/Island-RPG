using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum SettlementExclusionStage : byte
{
    Outside,
    Grace,
    FinalWarning,
    Enforcement
}

internal sealed record SettlementExclusionPolicy(
    float Radius,
    float DisengageRadius,
    double InitialGraceGameSeconds,
    double ReentryGraceGameSeconds,
    double FinalWarningGameSeconds)
{
    public static SettlementExclusionPolicy Default { get; } = new(
        Radius: 12,
        DisengageRadius: 14,
        InitialGraceGameSeconds: 15 *
            VillagerSimulation.GameSecondsPerRealSecond,
        ReentryGraceGameSeconds: 6 *
            VillagerSimulation.GameSecondsPerRealSecond,
        FinalWarningGameSeconds: 8 *
            VillagerSimulation.GameSecondsPerRealSecond);
}

internal sealed record SettlementExclusionState(
    string ActorId,
    SettlementExclusionStage Stage,
    double DeadlineGameSeconds,
    int Entries,
    double UpdatedGameSeconds);

internal readonly record struct SettlementExclusionTransition(
    SettlementExclusionState State,
    SettlementExclusionStage PreviousStage,
    bool Changed);

internal static class SettlementExclusionService
{
    public const int MaximumResponders = 3;

    public static bool CanEnforce(VillagerState villager) =>
        villager.Health > 25 &&
        villager.Hunger > VillagerFoodService.UrgentHungerThreshold &&
        villager.Energy >= VillagerFatigueService.RestThreshold;

    public static HashSet<string> SelectResponders(
        IEnumerable<VillagerState> members)
    {
        var eligible = members.Where(CanEnforce).ToArray();
        var count = Math.Min(
            MaximumResponders,
            Math.Max(1, (eligible.Length + 2) / 3));
        return eligible
            .OrderByDescending(value =>
                value.Boldness * 50 + value.Health + value.Hunger * .5f)
            .ThenBy(value => value.Id)
            .Take(count)
            .Select(value => value.Id)
            .ToHashSet();
    }

    public static SettlementExclusionTransition Advance(
        SettlementExclusionPolicy policy,
        SettlementExclusionState? current,
        string actorId,
        Vector2 actorPosition,
        Vector2 camp,
        double gameSeconds)
    {
        var distanceSquared = Vector2.DistanceSquared(actorPosition, camp);
        var inside = distanceSquared <= policy.Radius * policy.Radius;
        if (current is null || current.ActorId != actorId)
        {
            var initial = new SettlementExclusionState(
                actorId,
                inside ? SettlementExclusionStage.Grace :
                    SettlementExclusionStage.Outside,
                inside ? gameSeconds + policy.InitialGraceGameSeconds : 0,
                inside ? 1 : 0,
                gameSeconds);
            return new(initial, SettlementExclusionStage.Outside, true);
        }

        var previous = current.Stage;
        SettlementExclusionState next;
        switch (current.Stage)
        {
            case SettlementExclusionStage.Outside when inside:
                next = current with
                {
                    Stage = SettlementExclusionStage.Grace,
                    DeadlineGameSeconds = gameSeconds +
                        policy.ReentryGraceGameSeconds,
                    Entries = current.Entries + 1,
                    UpdatedGameSeconds = gameSeconds
                };
                break;
            case SettlementExclusionStage.Grace when !inside:
            case SettlementExclusionStage.FinalWarning when !inside:
                next = Outside(current, gameSeconds);
                break;
            case SettlementExclusionStage.Enforcement when
                distanceSquared > policy.DisengageRadius *
                policy.DisengageRadius:
                next = Outside(current, gameSeconds);
                break;
            case SettlementExclusionStage.Grace when
                gameSeconds >= current.DeadlineGameSeconds:
                next = current with
                {
                    Stage = SettlementExclusionStage.FinalWarning,
                    DeadlineGameSeconds = gameSeconds +
                        policy.FinalWarningGameSeconds,
                    UpdatedGameSeconds = gameSeconds
                };
                break;
            case SettlementExclusionStage.FinalWarning when
                gameSeconds >= current.DeadlineGameSeconds:
                next = current with
                {
                    Stage = SettlementExclusionStage.Enforcement,
                    DeadlineGameSeconds = 0,
                    UpdatedGameSeconds = gameSeconds
                };
                break;
            default:
                return new(current, previous, false);
        }
        return new(next, previous, next.Stage != previous);
    }

    private static SettlementExclusionState Outside(
        SettlementExclusionState current,
        double gameSeconds) => current with
        {
            Stage = SettlementExclusionStage.Outside,
            DeadlineGameSeconds = 0,
            UpdatedGameSeconds = gameSeconds
        };
}
