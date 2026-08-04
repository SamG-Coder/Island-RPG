namespace IslandRpg.Gameplay;

internal enum SettlementViolenceSeverity : byte
{
    Assault,
    GrievousAssault,
    AttemptedMurder
}

internal enum SettlementJusticeOutcome : byte
{
    Warning,
    Restitution,
    Avoidance,
    CollectiveDefense,
    Exile
}

internal sealed record SettlementJusticeCase(
    string AttackerId,
    string VictimId,
    SettlementViolenceSeverity Severity,
    SettlementJusticeOutcome Outcome,
    int RecentAttackCount,
    double FiledGameSeconds,
    int RestitutionRemaining = 0,
    bool Resolved = false);

internal static class SettlementJusticeService
{
    public const double IncidentWindowGameSeconds = 15 * 60;

    public static bool IsExiled(
        SettlementGroupState? group,
        string? actorId) =>
        group?.ActiveJusticeCase is
        {
            Outcome: SettlementJusticeOutcome.Exile,
            Resolved: false
        } activeCase &&
        !string.IsNullOrWhiteSpace(actorId) &&
        activeCase.AttackerId.Equals(actorId, StringComparison.Ordinal);

    public static SettlementJusticeCase Judge(
        SettlementGroupState group,
        VillagerState leader,
        VillagerState victim,
        string attackerId,
        int recentAttackCount,
        bool attackerArmed,
        IReadOnlyList<VillagerState> livingMembers,
        double gameSeconds)
    {
        var severity = Classify(
            victim.Health, recentAttackCount, attackerArmed);
        var supporters = livingMembers.Count(member =>
            member.Id != victim.Id && member.Id != attackerId &&
            SupportsSanction(member, victim, attackerId));
        var attackerIsMember = SettlementGroupService.IsMember(
            group, attackerId);
        var remainingMembers = group.MemberIds.Count -
                               (attackerIsMember ? 1 : 0);
        var moralAuthority = leader.Honesty >= .45f ||
                             leader.Id == victim.Id || supporters > 0;

        var outcome = severity switch
        {
            SettlementViolenceSeverity.AttemptedMurder
                when attackerIsMember && remainingMembers >= 3 &&
                     moralAuthority => SettlementJusticeOutcome.Exile,
            SettlementViolenceSeverity.AttemptedMurder =>
                SettlementJusticeOutcome.CollectiveDefense,
            SettlementViolenceSeverity.GrievousAssault
                when supporters > 0 || leader.Boldness >= .6f =>
                SettlementJusticeOutcome.CollectiveDefense,
            SettlementViolenceSeverity.GrievousAssault =>
                SettlementJusticeOutcome.Avoidance,
            _ when recentAttackCount >= 2 =>
                SettlementJusticeOutcome.Restitution,
            _ => SettlementJusticeOutcome.Warning
        };
        return new(
            attackerId, victim.Id, severity, outcome,
            Math.Max(1, recentAttackCount), gameSeconds,
            RestitutionRemaining:
                outcome == SettlementJusticeOutcome.Restitution ? 1 : 0);
    }

    public static SettlementViolenceSeverity Classify(
        int victimHealth,
        int recentAttackCount,
        bool attackerArmed) =>
        victimHealth <= 20 || recentAttackCount >= 6 ||
        attackerArmed && victimHealth <= 35
            ? SettlementViolenceSeverity.AttemptedMurder
            : victimHealth <= 55 || recentAttackCount >= 3
                ? SettlementViolenceSeverity.GrievousAssault
                : SettlementViolenceSeverity.Assault;

    public static bool SupportsSanction(
        VillagerState member,
        VillagerState victim,
        string attackerId)
    {
        if (member.Health <= 0) return false;
        var victimRelationship = member.Relationships?.FirstOrDefault(value =>
            value.CharacterId == victim.Id)?.State ?? default;
        var attackerRelationship = member.Relationships?.FirstOrDefault(value =>
            value.CharacterId == attackerId)?.State ?? default;
        return VillagerRelationshipClassifier.WillDefend(
                   victimRelationship,
                   victim.Id == member.RecognizedLeaderId) ||
               member.Honesty >= .65f ||
               attackerRelationship.Resentment >= 20;
    }

    public static SettlementJusticeCase ResolveRestitution(
        SettlementJusticeCase activeCase,
        string giverId,
        string recipientId) =>
        !activeCase.Resolved &&
        activeCase.Outcome == SettlementJusticeOutcome.Restitution &&
        activeCase.AttackerId == giverId &&
        activeCase.VictimId == recipientId
            ? activeCase with
            {
                RestitutionRemaining = 0,
                Resolved = true
            }
            : activeCase;

    public static SettlementJusticeCase PreserveEscalation(
        SettlementJusticeCase? previous,
        SettlementJusticeCase proposed)
    {
        if (previous is null || previous.Resolved ||
            previous.AttackerId != proposed.AttackerId ||
            proposed.FiledGameSeconds - previous.FiledGameSeconds >
            IncidentWindowGameSeconds ||
            Rank(previous.Outcome) <= Rank(proposed.Outcome))
            return proposed;
        return proposed with
        {
            Severity = previous.Severity > proposed.Severity
                ? previous.Severity : proposed.Severity,
            Outcome = previous.Outcome,
            RestitutionRemaining = previous.Outcome ==
                                   SettlementJusticeOutcome.Restitution
                ? previous.RestitutionRemaining : 0
        };
    }

    public static string LeaderLine(
        SettlementJusticeCase activeCase,
        string attackerName,
        string victimName) => activeCase.Outcome switch
        {
            SettlementJusticeOutcome.Warning =>
                $"{attackerName}, stop. We will not accept violence against {victimName}.",
            SettlementJusticeOutcome.Restitution =>
                $"{attackerName}, you harmed {victimName}. Offer them an item in restitution.",
            SettlementJusticeOutcome.Avoidance =>
                $"Keep away from {attackerName}. Do not face them alone.",
            SettlementJusticeOutcome.CollectiveDefense =>
                $"Stand together. Protect {victimName} from {attackerName}!",
            SettlementJusticeOutcome.Exile =>
                $"{attackerName}, you tried to kill one of us. You are cast out. Leave our camp now.",
            _ => "This violence must end."
        };

    private static int Rank(SettlementJusticeOutcome outcome) => outcome switch
    {
        SettlementJusticeOutcome.Warning => 0,
        SettlementJusticeOutcome.Restitution => 1,
        SettlementJusticeOutcome.Avoidance => 2,
        SettlementJusticeOutcome.CollectiveDefense => 3,
        SettlementJusticeOutcome.Exile => 4,
        _ => 0
    };
}
