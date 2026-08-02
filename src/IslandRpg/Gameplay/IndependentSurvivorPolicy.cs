using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class IndependentSurvivorPolicy
{
    public const int MinimumSettlementPopulation = 3;
    public const double CampDecisionDelayGameSeconds = 20 * 60;

    public static bool IsIndependentPopulation(int livingPopulation) =>
        livingPopulation is > 0 and < MinimumSettlementPopulation;

    public static bool IsIndependent(
        VillagerState villager, int livingPopulation) =>
        villager.IndependentByChoice ||
        IsIndependentPopulation(livingPopulation);

    public static bool CanFormSettlement(int livingPopulation) =>
        livingPopulation >= MinimumSettlementPopulation;

    public static VillagerState ConsiderPersonalCamp(
        VillagerState villager,
        int livingPopulation,
        double gameSeconds)
    {
        if (villager.Health <= 0 ||
            !IsIndependent(villager, livingPopulation) ||
            villager.PersonalCampX is not null ||
            gameSeconds - villager.AwakenedGameSeconds <
            CampDecisionDelayGameSeconds)
            return villager;
        var memories = villager.LocationMemories?
            .Where(value => value.WorldLevel == villager.WorldLevel &&
                            value.Type != VillagerLocationType.Danger &&
                            VillagerLocationMemoryService.ConfidenceAt(
                                value, gameSeconds) >=
                            VillagerLocationMemoryService.MinimumUsefulConfidence)
            .ToArray() ?? [];
        var current = new Vector2(villager.PositionX, villager.PositionY);
        var candidates = memories.Select(value =>
                new Vector2(value.PositionX, value.PositionY))
            .Append(current)
            .ToArray();
        var best = current;
        var bestScore = float.MinValue;
        foreach (var candidate in candidates)
        {
            if (!VillagerLocationMemoryService.CanVisit(
                    villager, candidate, gameSeconds))
                continue;
            var score = memories.Sum(memory =>
            {
                var distance = Vector2.Distance(
                    candidate,
                    new(memory.PositionX, memory.PositionY));
                var value = memory.Type switch
                {
                    VillagerLocationType.FoodSource => 28,
                    VillagerLocationType.FishingSpot => 24,
                    VillagerLocationType.WoodSource => 20,
                    _ => 8
                };
                return Math.Max(0, value - distance);
            });
            if (score <= bestScore) continue;
            bestScore = score;
            best = candidate;
        }
        return villager with
        {
            PersonalCampX = best.X,
            PersonalCampY = best.Y,
            PersonalCampWorldLevel = villager.WorldLevel
        };
    }

    public static Vector2? PersonalCamp(VillagerState villager) =>
        villager.PersonalCampX is { } x &&
        villager.PersonalCampY is { } y &&
        villager.PersonalCampWorldLevel == villager.WorldLevel
            ? new(x, y)
            : null;

    public static IReadOnlySet<string> LeadershipDepartures(
        IReadOnlyList<VillagerState> villagers,
        VillagerLeadershipResult result)
    {
        var living = villagers.Where(value =>
            value.Health > 0 && !value.IndependentByChoice).ToArray();
        if (living.Length < 2) return new HashSet<string>();
        var candidates = result.Votes
            .Where(value => value.CandidateId != result.LeaderId)
            .GroupBy(value => value.CandidateId)
            .Select(group => new
            {
                Id = group.Key,
                Votes = group.Count()
            })
            .Join(
                living,
                value => value.Id,
                value => value.Id,
                (support, villager) => new
                {
                    Villager = villager,
                    support.Votes
                })
            .OrderByDescending(value => value.Votes)
            .ThenByDescending(value => value.Villager.Boldness)
            .ThenBy(value => value.Villager.Id, StringComparer.Ordinal);
        var departed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var trust = candidate.Villager.Relationships?.FirstOrDefault(
                value => value.CharacterId == result.LeaderId)?.State.Trust ?? 0;
            var chance = .06f + candidate.Villager.Boldness * .34f +
                         (1 - candidate.Villager.Sociability) * .24f +
                         (result.Contested ? .16f : 0) +
                         Math.Min(.12f, candidate.Votes * .03f) -
                         Math.Max(0, trust) * .003f;
            if (StableUnit(candidate.Villager.Id + result.LeaderId) < chance)
            {
                departed.Add(candidate.Villager.Id);
                break;
            }
        }
        return departed;
    }

    private static float StableUnit(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return (hash & 65535) / 65535f;
    }
}
