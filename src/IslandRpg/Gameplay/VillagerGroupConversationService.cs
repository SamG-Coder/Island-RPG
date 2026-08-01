namespace IslandRpg.Gameplay;

internal sealed record VillagerGroupConversationLine(
    string SpeakerId,
    string Text,
    string Purpose,
    bool UseAi = false);

internal static class VillagerGroupConversationService
{
    public static IReadOnlyList<VillagerGroupConversationLine> OpeningCouncil(
        IReadOnlyList<VillagerState> villagers,
        VillagerLeadershipResult result)
    {
        var living = villagers.Where(value => value.Health > 0)
            .OrderBy(TurnOrder)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var names = living.ToDictionary(
            value => value.Id, value => value.Name, StringComparer.Ordinal);
        var lines = new List<VillagerGroupConversationLine>(
            living.Length * 2 + 4);
        for (var index = 0; index < living.Length; index++)
        {
            var villager = living[index];
            lines.Add(new(
                villager.Id,
                Introduction(villager, lines.Count),
                "introduction",
                UseAi: index == living.Length - 1));
        }
        var facilitator = living
            .OrderByDescending(value => value.Sociability)
            .ThenByDescending(value => value.Boldness)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .First();
        lines.Add(new(
            facilitator.Id,
            "We know one another now. Who is willing to step forward and coordinate us?",
            "nomination-call"));
        var candidates = result.Votes
            .GroupBy(value => value.CandidateId)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(group => group.Key);
        var proposalIndex = 0;
        foreach (var candidateId in candidates)
        {
            lines.Add(new(
                candidateId,
                proposalIndex++ switch
                {
                    0 => "We need one place to regroup, a builder who stays there, and named people bringing supplies.",
                    1 => "Count our food and tools first. Then assign each supply to someone who can actually obtain it.",
                    _ => "Choose a defensible camp, keep the worksite visible, and meet again if the plan stops working."
                },
                "proposal",
                UseAi: true));
        }
        foreach (var vote in result.Votes)
            lines.Add(new(
                vote.VoterId,
                vote.CandidateId == result.LeaderId
                    ? $"I support {names[result.LeaderId]} coordinating us."
                    : $"I would choose {names[vote.CandidateId]}, not {names[result.LeaderId]}.",
                vote.CandidateId == result.LeaderId
                    ? "support"
                    : "dissent",
                UseAi: vote.CandidateId != result.LeaderId));
        lines.Add(new(
            result.LeaderId,
            "I hear the disagreement. We will establish the worksite here, then judge the plan by whether it succeeds.",
            "decision",
            UseAi: true));
        return lines;
    }

    public static System.Numerics.Vector2 CircleOffset(
        string villagerId,
        int index,
        int count)
    {
        var hash = StableHash(villagerId);
        var angleJitter = ((hash & 255) / 255f - .5f) * .28f;
        var radius = 3.1f + ((hash >> 8) & 255) / 255f * 1.1f;
        var angle = index / (float)Math.Max(1, count) * MathF.Tau +
                    angleJitter;
        return new(
            MathF.Cos(angle) * radius,
            MathF.Sin(angle) * radius);
    }

    private static string Introduction(VillagerState villager, int turn)
    {
        var trade = string.IsNullOrWhiteSpace(villager.Persona?.PriorTrade)
            ? "whatever work is needed"
            : villager.Persona!.PriorTrade;
        return ((turn + (int)(villager.Boldness * 3)) % 5) switch
        {
            0 => $"I am {villager.Name}. I was {Article(trade)} {trade}. Who wants to speak next?",
            1 => $"My name is {villager.Name}. I know the work of {trade}; use me where that helps.",
            2 => $"Call me {villager.Name}. Before the wreck I worked in {trade}. That is what I can offer.",
            3 => $"I am {villager.Name}. My trade was {trade}. Tell me what the camp needs most.",
            _ => $"{villager.Name}. I made my living through {trade}. I would hear what the rest of you know."
        };
    }

    private static string Article(string value) =>
        value.Length > 0 && "aeiou".Contains(
            char.ToLowerInvariant(value[0]))
            ? "an"
            : "a";

    private static uint TurnOrder(VillagerState villager)
    {
        var hash = StableHash(villager.Id);
        hash ^= (uint)MathF.Round(villager.Sociability * 100);
        hash *= 16777619;
        hash ^= (uint)MathF.Round(villager.Boldness * 100);
        return hash;
    }

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }
}
