using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum SocialAftermathRole : byte
{
    AidVictim,
    GuardVictim,
    ShareAccount,
    ConfrontAggressor
}

internal sealed record SocialAftermathAssignment(
    string ActorId,
    SocialAftermathRole Role,
    string TargetId,
    bool Completed = false);

internal sealed record SocialIncidentAftermathState(
    Guid IncidentId,
    string AggressorId,
    string VictimId,
    double CreatedGameSeconds,
    double ReadyGameSeconds,
    double ExpiresGameSeconds,
    IReadOnlyList<SocialAftermathAssignment> Assignments);

internal static class SocialIncidentAftermathService
{
    public const int MaximumAssignments = 4;
    public const double DurationRealSeconds = 45;

    public static SocialIncidentAftermathState Begin(
        SocialIncidentAftermathState? current,
        SettlementGroupState group,
        VillagerState victim,
        string aggressorId,
        IReadOnlyList<VillagerState> members,
        double gameSeconds)
    {
        if (current is not null &&
            current.AggressorId == aggressorId &&
            current.VictimId == victim.Id &&
            gameSeconds <= current.ExpiresGameSeconds)
            return current;
        var available = members.Where(value =>
                value.Health > 0 && value.Id != victim.Id &&
                value.Id != aggressorId)
            .ToList();
        var assignments = new List<SocialAftermathAssignment>(
            MaximumAssignments);
        var confrontingLeader = available.FirstOrDefault(value =>
            value.Id == group.LeaderId);
        if (confrontingLeader is not null)
            available.Remove(confrontingLeader);
        AddBest(
            assignments, available, SocialAftermathRole.AidVictim,
            victim.Id, value =>
                value.Sociability * 50 + value.Honesty * 20 -
                Distance(value, victim));
        AddBest(
            assignments, available, SocialAftermathRole.GuardVictim,
            victim.Id, value =>
                value.Boldness * 55 + value.Honesty * 15 -
                Distance(value, victim));
        var accountListener = members
            .Where(value => value.Health > 0 &&
                            value.Id != victim.Id &&
                            value.Id != aggressorId &&
                            value.Id != group.LeaderId)
            .OrderBy(value => Distance(value, victim))
            .ThenBy(value => value.Id)
            .FirstOrDefault();
        if (accountListener is not null)
        {
            assignments.Add(new(
                victim.Id, SocialAftermathRole.ShareAccount,
                accountListener.Id));
        }
        if (assignments.Count < MaximumAssignments &&
            confrontingLeader is not null)
            assignments.Add(new(
                confrontingLeader.Id,
                SocialAftermathRole.ConfrontAggressor,
                aggressorId));
        var ready = gameSeconds +
            VillagerConflictService.ConflictDurationGameSeconds;
        return new(
            Guid.NewGuid(), aggressorId, victim.Id, gameSeconds,
            ready,
            ready + DurationRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond,
            assignments.Take(MaximumAssignments).ToArray());
    }

    public static SocialIncidentAftermathState Complete(
        SocialIncidentAftermathState state,
        string actorId) => state with
        {
            Assignments = state.Assignments.Select(value =>
                    value.ActorId == actorId
                        ? value with { Completed = true }
                        : value)
                .ToArray()
        };

    public static bool Finished(
        SocialIncidentAftermathState state,
        double gameSeconds) =>
        gameSeconds >= state.ExpiresGameSeconds ||
        state.Assignments.All(value => value.Completed);

    public static VillagerState RecordCompletedInteraction(
        VillagerState observer,
        SocialIncidentAftermathState incident,
        SocialAftermathAssignment assignment,
        string aggressorName,
        string victimName,
        string targetName,
        double gameSeconds)
    {
        var kind = $"aftermath-{assignment.Role.ToString().ToLowerInvariant()}";
        var memories = observer.Memories?.ToList() ?? [];
        if (!memories.Any(value =>
                value.EventId == incident.IncidentId && value.Kind == kind))
        {
            var confidence = assignment.Role ==
                             SocialAftermathRole.ShareAccount
                ? .45f + observer.Honesty * .4f
                : 1;
            memories.Add(new(
                incident.IncidentId, kind, assignment.TargetId,
                null, confidence, gameSeconds,
                assignment.Role == SocialAftermathRole.ConfrontAggressor
                    ? -15 : 10,
                Summary(assignment.Role, aggressorName,
                    victimName, targetName)));
            if (memories.Count > VillagerSimulation.MaximumMemories)
                memories.RemoveRange(
                    0, memories.Count -
                       VillagerSimulation.MaximumMemories);
        }
        var relationships = observer.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            value.CharacterId == assignment.TargetId);
        var relationship = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(assignment.TargetId, default);
        var delta = assignment.Role switch
        {
            SocialAftermathRole.AidVictim =>
                new RelationshipState(Trust: 4, Affection: 3, Respect: 2),
            SocialAftermathRole.GuardVictim =>
                new RelationshipState(Trust: 2, Respect: 3),
            SocialAftermathRole.ShareAccount =>
                new RelationshipState(Trust: 1),
            _ => new RelationshipState(
                Trust: -3, Resentment: 6)
        };
        relationship = relationship with
        {
            State = Add(relationship.State, delta).Clamp()
        };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = relationship;
        else
            relationships.Add(relationship);
        return observer with
        {
            Memories = memories,
            Relationships = relationships
        };
    }

    public static VillagerState RecordReceivedSupport(
        VillagerState victim,
        SocialIncidentAftermathState incident,
        SocialAftermathAssignment assignment,
        string helperName,
        double gameSeconds)
    {
        if (assignment.Role is not
            (SocialAftermathRole.AidVictim or
             SocialAftermathRole.GuardVictim))
            return victim;
        var memories = victim.Memories?.ToList() ?? [];
        var kind = assignment.Role == SocialAftermathRole.AidVictim
            ? "aftermath-received-aid"
            : "aftermath-received-guard";
        if (!memories.Any(value =>
                value.EventId == incident.IncidentId &&
                value.Kind == kind &&
                value.SubjectId == assignment.ActorId))
            memories.Add(new(
                incident.IncidentId, kind, assignment.ActorId,
                null, 1, gameSeconds, 15,
                assignment.Role == SocialAftermathRole.AidVictim
                    ? $"{helperName} checked on me after the attack."
                    : $"{helperName} guarded me after the attack."));
        var relationships = victim.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == assignment.ActorId);
        var relationship = index >= 0
            ? relationships[index]
            : new VillagerRelationship(assignment.ActorId, default);
        var gratitude = assignment.Role == SocialAftermathRole.AidVictim
            ? 12 : 6;
        relationship = relationship with
        {
            State = Add(
                relationship.State,
                new(Trust: 4, Affection: 2,
                    Gratitude: gratitude)).Clamp()
        };
        if (index >= 0) relationships[index] = relationship;
        else relationships.Add(relationship);
        return victim with
        {
            Memories = memories.TakeLast(
                VillagerSimulation.MaximumMemories).ToArray(),
            Relationships = relationships
        };
    }

    public static VillagerState RecordHeardAccount(
        VillagerState listener,
        VillagerState speaker,
        SocialIncidentAftermathState incident,
        string aggressorName,
        string victimName,
        double gameSeconds)
    {
        var memories = listener.Memories?.ToList() ?? [];
        const string kind = "aftermath-heard-account";
        if (!memories.Any(value =>
                value.EventId == incident.IncidentId &&
                value.Kind == kind))
            memories.Add(new(
                incident.IncidentId, kind, incident.AggressorId,
                null, .45f + speaker.Honesty * .4f,
                gameSeconds, -8,
                $"{speaker.Name} told me that {aggressorName} attacked {victimName}."));
        var relationships = listener.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == speaker.Id);
        var relationship = index >= 0
            ? relationships[index]
            : new VillagerRelationship(speaker.Id, default);
        relationship = relationship with
        {
            State = Add(
                relationship.State,
                new(Trust: Math.Max(0, speaker.Honesty * 2))).Clamp()
        };
        if (index >= 0) relationships[index] = relationship;
        else relationships.Add(relationship);
        return listener with
        {
            Memories = memories.TakeLast(
                VillagerSimulation.MaximumMemories).ToArray(),
            Relationships = relationships
        };
    }

    public static string Speech(
        SocialAftermathAssignment assignment,
        string aggressorName,
        string victimName,
        string targetName) => assignment.Role switch
        {
            SocialAftermathRole.AidVictim =>
                $"{victimName}, are you hurt? I came as soon as I could.",
            SocialAftermathRole.GuardVictim =>
                $"I will stay near {victimName} until the danger has passed.",
            SocialAftermathRole.ShareAccount =>
                $"{targetName}, {aggressorName} attacked me. Keep watch.",
            _ => $"{aggressorName}, what you did to {victimName} will not be forgotten."
        };

    private static string Summary(
        SocialAftermathRole role,
        string aggressorName,
        string victimName,
        string targetName) => role switch
        {
            SocialAftermathRole.AidVictim =>
                $"Checked on {victimName} after {aggressorName}'s attack.",
            SocialAftermathRole.GuardVictim =>
                $"Guarded {victimName} after {aggressorName}'s attack.",
            SocialAftermathRole.ShareAccount =>
                $"Told {targetName} that {aggressorName} attacked {victimName}.",
            _ => $"Confronted {aggressorName} about attacking {victimName}."
        };

    private static void AddBest(
        ICollection<SocialAftermathAssignment> assignments,
        ICollection<VillagerState> available,
        SocialAftermathRole role,
        string targetId,
        Func<VillagerState, float> score)
    {
        if (available.Count == 0) return;
        var actor = available.OrderByDescending(score)
            .ThenBy(value => value.Id).First();
        assignments.Add(new(actor.Id, role, targetId));
        available.Remove(actor);
    }

    private static float Distance(
        VillagerState first,
        VillagerState second) => Vector2.Distance(
        new(first.PositionX, first.PositionY),
        new(second.PositionX, second.PositionY));

    private static RelationshipState Add(
        in RelationshipState first,
        in RelationshipState second) => new(
        first.Trust + second.Trust,
        first.Affection + second.Affection,
        first.Respect + second.Respect,
        first.Fear + second.Fear,
        first.Gratitude + second.Gratitude,
        first.Resentment + second.Resentment);
}
