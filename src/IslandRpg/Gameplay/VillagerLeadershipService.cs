namespace IslandRpg.Gameplay;

internal sealed record VillagerLeadershipVote(
    string VoterId,
    string CandidateId,
    float Support);

internal sealed record VillagerLeadershipResult(
    string LeaderId,
    IReadOnlyList<VillagerLeadershipVote> Votes,
    bool Contested);

internal static class VillagerLeadershipService
{
    public const double MinimumLeadershipTenureGameSeconds = 6 * 60 * 60;
    private static readonly SkillType[] LeadershipSkills =
    [
        SkillType.Woodcutting, SkillType.Farming, SkillType.Fishing,
        SkillType.Cooking, SkillType.Firemaking, SkillType.Crafting,
        SkillType.Digging, SkillType.Mining
    ];

    public static VillagerLeadershipResult? HoldCouncil(
        IReadOnlyList<VillagerState> villagers)
    {
        var living = villagers.Where(value => value.Health > 0).ToArray();
        if (living.Length < 2) return null;
        var votes = new VillagerLeadershipVote[living.Length];
        for (var voterIndex = 0; voterIndex < living.Length; voterIndex++)
        {
            var voter = living[voterIndex];
            var best = living[0];
            var bestSupport = float.MinValue;
            foreach (var candidate in living)
            {
                var support = Support(voter, candidate);
                if (support > bestSupport ||
                    support == bestSupport &&
                    string.CompareOrdinal(candidate.Id, best.Id) < 0)
                {
                    best = candidate;
                    bestSupport = support;
                }
            }
            votes[voterIndex] = new(voter.Id, best.Id, bestSupport);
        }
        var ranked = votes.GroupBy(value => value.CandidateId)
            .Select(group => new
            {
                CandidateId = group.Key,
                Votes = group.Count(),
                Support = group.Sum(value => value.Support)
            })
            .OrderByDescending(value => value.Votes)
            .ThenByDescending(value => value.Support)
            .ThenBy(value => value.CandidateId, StringComparer.Ordinal)
            .ToArray();
        return new(
            ranked[0].CandidateId,
            votes,
            ranked.Length > 1 && ranked[0].Votes - ranked[1].Votes <= 1);
    }

    public static IReadOnlyList<VillagerState> ApplyCouncil(
        IReadOnlyList<VillagerState> villagers,
        VillagerLeadershipResult result,
        double gameSeconds)
    {
        var leaderName = villagers.First(value =>
            value.Id == result.LeaderId).Name;
        return villagers.Select(villager =>
        {
            if (villager.Health <= 0) return villager;
            var vote = result.Votes.First(value =>
                value.VoterId == villager.Id);
            var supported = vote.CandidateId == result.LeaderId;
            var memories = villager.Memories?.ToList() ?? [];
            var relationships = villager.Relationships?.ToList() ?? [];
            if (villager.Id != result.LeaderId)
            {
                var relationshipIndex = relationships.FindIndex(value =>
                    value.CharacterId == result.LeaderId);
                var relationship = relationshipIndex >= 0
                    ? relationships[relationshipIndex]
                    : new VillagerRelationship(result.LeaderId, default);
                relationship = relationship with
                {
                    State = (relationship.State with
                    {
                        Respect = relationship.State.Respect +
                            (supported ? 2 : .5f),
                        Trust = relationship.State.Trust +
                            (supported ? 1 : .25f)
                    }).Clamp()
                };
                if (relationshipIndex >= 0)
                    relationships[relationshipIndex] = relationship;
                else
                    relationships.Add(relationship);
            }
            memories.Add(new(
                Guid.NewGuid(),
                result.Contested ? "leadership-contested" : "leadership-council",
                result.LeaderId,
                null,
                1,
                gameSeconds,
                Sentiment: supported ? 8 : -6,
                Summary: supported
                    ? $"Supported {leaderName} to coordinate the settlement."
                    : $"Preferred someone else, but {leaderName} was chosen to coordinate the settlement."));
            if (memories.Count > VillagerSimulation.MaximumMemories)
                memories.RemoveRange(
                    0, memories.Count - VillagerSimulation.MaximumMemories);
            var released = villager.Need == VillagerNeed.Social ||
                           villager.Activity is
                               VillagerActivity.Conversing or
                               VillagerActivity.Reflecting or
                               VillagerActivity.Following or
                               VillagerActivity.Socializing;
            return villager with
            {
                RecognizedLeaderId = result.LeaderId,
                NextLeadershipChallengeGameSeconds = gameSeconds +
                    MinimumLeadershipTenureGameSeconds,
                Relationships = relationships,
                Memories = memories,
                Need = released ? VillagerNeed.Idle : villager.Need,
                Activity = released
                    ? VillagerActivity.Idle
                    : villager.Activity,
                ActivityUntilGameSeconds = released
                    ? 0
                    : villager.ActivityUntilGameSeconds,
                ConversationPartnerId = released
                    ? null
                    : villager.ConversationPartnerId,
                FollowingActorId = released
                    ? null
                    : villager.FollowingActorId,
                Action = released ? EntityAction.Idle : villager.Action,
                ActionTime = released ? 0 : villager.ActionTime,
                TargetX = released ? null : villager.TargetX,
                TargetY = released ? null : villager.TargetY,
                NextDecisionGameSeconds = released
                    ? gameSeconds
                    : villager.NextDecisionGameSeconds
            };
        }).ToArray();
    }

    public static bool IsLeader(VillagerState villager) =>
        villager.Health > 0 &&
        villager.RecognizedLeaderId == villager.Id;

    public static (VillagerState Leader, VillagerState Worker)
        ApplyMissedAssignment(
            VillagerState leader,
            VillagerState worker,
            string projectItemId,
            double gameSeconds)
    {
        var projectName = ItemCatalog.Get(projectItemId).Name;
        leader = ChangeRelationship(
            leader, worker.Id, trust: -.5f, respect: -1,
            resentment: .5f);
        worker = ChangeRelationship(
            worker, leader.Id, trust: -.25f, respect: -.5f,
            resentment: 1);
        var memories = worker.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(), "missed-assignment", leader.Id, null,
            .9f, gameSeconds, -8,
            $"{leader.Name} challenged {worker.Name} for making no progress on the {projectName}."));
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveAt(0);
        return (leader, worker with { Memories = memories });
    }

    private static VillagerState ChangeRelationship(
        VillagerState villager,
        string otherId,
        float trust,
        float respect,
        float resentment)
    {
        var relationships = villager.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == otherId);
        var relationship = index >= 0
            ? relationships[index]
            : new VillagerRelationship(otherId, default);
        relationship = relationship with
        {
            State = (relationship.State with
            {
                Trust = relationship.State.Trust + trust,
                Respect = relationship.State.Respect + respect,
                Resentment = relationship.State.Resentment + resentment
            }).Clamp()
        };
        if (index >= 0) relationships[index] = relationship;
        else relationships.Add(relationship);
        return villager with { Relationships = relationships };
    }

    public static VillagerState? SelectChallenger(
        IReadOnlyList<VillagerState> villagers,
        double gameSeconds)
    {
        var leaderId = villagers.FirstOrDefault(value =>
            value.Health > 0 && value.RecognizedLeaderId is not null)?
            .RecognizedLeaderId;
        if (leaderId is null) return null;
        var planStalled = villagers.Any(value =>
            VillagerSettlementProjectService.IsStalled(value, gameSeconds));
        if (!planStalled) return null;
        return villagers.Where(value =>
                value.Health > 0 &&
                value.Id != leaderId &&
                value.NextLeadershipChallengeGameSeconds <= gameSeconds &&
                value.Boldness >= .55f &&
                IsDissenter(value, leaderId))
            .OrderByDescending(value => value.Boldness)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsDissenter(VillagerState villager, string leaderId) =>
        villager.Memories?.Any(memory =>
            memory.Kind == "leadership-contested" &&
            memory.SubjectId == leaderId &&
            memory.Sentiment < 0) == true;

    private static float Support(
        VillagerState voter,
        VillagerState candidate)
    {
        var skills = LeadershipSkills
            .Sum(skill => VillagerSkillService.Level(candidate, skill));
        var usefulTools = VillagerCapabilityMemory.VisibleTools(
            candidate.Inventory).Count;
        var relationship = voter.Relationships?.FirstOrDefault(value =>
            value.CharacterId == candidate.Id)?.State;
        var prestige = skills * 2 + usefulTools * 8 +
                       candidate.Honesty * 18 + candidate.Sociability * 10;
        var dominance = candidate.Boldness * (8 + voter.Boldness * 8);
        var trust = relationship is null
            ? 0
            : relationship.Value.Trust * 2 +
              relationship.Value.Respect * 2 -
              relationship.Value.Fear - relationship.Value.Resentment;
        var selfClaim = voter.Id == candidate.Id
            ? voter.Boldness * 12 - 5
            : 0;
        return prestige + dominance + trust + selfClaim;
    }
}
