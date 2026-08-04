using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class SettlementOpeningService
{
    public const float ReconnaissanceRadius = 24;
    public const float ArrivalRadius = 2.5f;

    public static SettlementGroupState AssignScouts(
        SettlementGroupState group,
        IReadOnlyList<VillagerState> villagers,
        Func<Vector2, Vector2> resolveTarget)
    {
        if (group.OpeningStage != SettlementOpeningStage.Reconnaissance ||
            group.ScoutAssignments is { Count: > 0 })
            return group;
        var members = villagers.Where(value =>
                value.Health > 0 && IsMember(group, value.Id))
            .ToArray();
        var scouts = members
            .Where(value => value.Health >= 70 &&
                            value.Energy >= VillagerFatigueService.RestThreshold)
            .OrderByDescending(ScoutSuitability)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        if (scouts.Length == 0)
            scouts = members
                .OrderByDescending(ScoutSuitability)
                .ToArray();
        var assignments = new SettlementScoutAssignment[scouts.Length];
        for (var index = 0; index < scouts.Length; index++)
        {
            var angle = group.CoordinatedReconnaissance
                ? StableUnit(group.Id + group.ReconnaissanceRound) *
                  MathF.Tau +
                  (index - (scouts.Length - 1) * .5f) * .045f
                : index / (float)Math.Max(1, scouts.Length) * MathF.Tau +
                  StableUnit(group.Id + scouts[index].Id) * .65f;
            var desiredTarget = group.Camp + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) *
                ReconnaissanceRadius * (group.ReconnaissanceRound + 1);
            var target = resolveTarget(desiredTarget);
            assignments[index] = new(
                scouts[index].Id, target.X, target.Y, index);
        }
        return group with { ScoutAssignments = assignments };
    }

    public static SettlementGroupState RecordReport(
        SettlementGroupState group,
        SettlementScoutReport report)
    {
        var reports = group.ScoutReports?.ToList() ?? [];
        var reportIndex = reports.FindIndex(value =>
            value.ScoutId == report.ScoutId);
        if (reportIndex < 0) reports.Add(report);
        else reports[reportIndex] = report;
        var assignments = (group.ScoutAssignments ?? [])
            .Select(value => value.ScoutId == report.ScoutId
                ? value with { Reached = true }
                : value)
            .ToArray();
        return group with
        {
            ScoutReports = reports,
            ScoutAssignments = assignments
        };
    }

    public static SettlementGroupState SetScoutWaypoint(
        SettlementGroupState group,
        string scoutId,
        Vector2 waypoint)
    {
        var assignments = (group.ScoutAssignments ?? [])
            .Select(value => value.ScoutId == scoutId
                ? value with
                {
                    WaypointX = waypoint.X,
                    WaypointY = waypoint.Y
                }
                : value)
            .ToArray();
        return group with { ScoutAssignments = assignments };
    }

    public static SettlementGroupState RecordScoutObservation(
        SettlementGroupState group,
        SettlementScoutReport observation)
    {
        var reports = group.ScoutReports?.ToList() ?? [];
        var reportIndex = reports.FindIndex(value =>
            value.ScoutId == observation.ScoutId);
        if (reportIndex < 0)
            reports.Add(observation);
        else if (Prefer(observation, reports[reportIndex]))
            reports[reportIndex] = observation;
        var assignments = (group.ScoutAssignments ?? [])
            .Select(value => value.ScoutId == observation.ScoutId
                ? AdvanceScout(value, observation)
                : value)
            .ToArray();
        return group with
        {
            ScoutReports = reports,
            ScoutAssignments = assignments
        };
    }

    public static SettlementGroupState MarkReported(
        SettlementGroupState group, string scoutId)
    {
        var assignments = (group.ScoutAssignments ?? [])
            .Select(value => value.ScoutId == scoutId
                ? value with { Reported = true }
                : value)
            .ToArray();
        var allReported = assignments.Length > 0 &&
                          assignments.All(value => value.Reported);
        return group with
        {
            ScoutAssignments = assignments,
            OpeningStage = allReported
                ? SettlementOpeningStage.ComparingCamps
                : group.OpeningStage
        };
    }

    public static SettlementScoutReport? BestCamp(
        SettlementGroupState group) =>
        group.ScoutReports?
            .OrderByDescending(IsViableCamp)
            .ThenByDescending(value => value.CampScore)
            .ThenBy(value => value.ScoutId, StringComparer.Ordinal)
            .FirstOrDefault();

    public static SettlementScoutReport? BestViableCamp(
        SettlementGroupState group) =>
        group.ScoutReports?
            .Where(IsViableCamp)
            .OrderByDescending(value => value.CampScore)
            .ThenBy(value => value.ScoutId, StringComparer.Ordinal)
            .FirstOrDefault();

    public static bool IsViableCamp(SettlementScoutReport report) =>
        report.Food && report.Wood && report.Stone && !report.Danger;

    public static SettlementGroupState ContinueReconnaissance(
        SettlementGroupState group) => group with
        {
            OpeningStage = SettlementOpeningStage.Reconnaissance,
            ScoutAssignments = null,
            ScoutReports = null,
            CampResponses = null,
            ReconnaissanceRound = group.ReconnaissanceRound + 1,
            CoordinatedReconnaissance = true
        };

    public static SettlementGroupState DecideCamp(
        SettlementGroupState group,
        IReadOnlyList<VillagerState> villagers)
    {
        if (group.OpeningStage != SettlementOpeningStage.ComparingCamps)
            return group;
        var selected = BestViableCamp(group);
        if (selected is null) return group;
        var responses = villagers.Where(value =>
                value.Health > 0 && IsMember(group, value.Id))
            .Select(value => Response(value, group.LeaderId, selected))
            .ToArray();
        var respondingIds = responses
            .Select(value => value.VillagerId)
            .ToHashSet(StringComparer.Ordinal);
        var remaining = responses
            .Where(value => value.Response != SettlementCampResponseKind.Leave)
            .Select(value => value.VillagerId)
            .Concat(group.MemberIds.Where(value =>
                !respondingIds.Contains(value)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return group with
        {
            CampX = selected.PositionX,
            CampY = selected.PositionY,
            MemberIds = remaining,
            CampResponses = responses,
            OpeningStage = SettlementOpeningStage.MovingToCamp
        };
    }

    public static SettlementGroupState CompleteMove(
        SettlementGroupState group) => group with
        {
            OpeningStage = SettlementOpeningStage.CacheReady
        };

    public static bool IsOpeningActive(SettlementGroupState? group) =>
        group is { OpeningStage: not SettlementOpeningStage.CacheReady };

    private static SettlementCampResponse Response(
        VillagerState villager,
        string leaderId,
        SettlementScoutReport selected)
    {
        if (villager.Id == leaderId)
            return new(villager.Id, SettlementCampResponseKind.Agree,
                "I will stand by the camp I proposed.");
        var trust = villager.Relationships?.FirstOrDefault(value =>
            value.CharacterId == leaderId)?.State.Trust ?? 0;
        var objection = selected.Danger || !selected.Water ||
                        selected.CampScore < 30;
        if (objection && trust < -35 && villager.Boldness >= .72f)
            return new(villager.Id, SettlementCampResponseKind.Leave,
                "I will not follow this plan; I will make my own way.");
        if (objection && (trust < 5 || villager.Boldness >= .55f))
            return new(villager.Id, SettlementCampResponseKind.Object,
                selected.Danger
                    ? "That ground is exposed to danger."
                    : "That site lacks what we need to survive.");
        return new(villager.Id, SettlementCampResponseKind.Agree,
            "I will move with the group and judge the place by its use.");
    }

    private static float ScoutSuitability(VillagerState villager) =>
        villager.Boldness * 45 + villager.Energy * .3f +
        (villager.WorkRole == VillagerWorkRole.Exploration ? 20 : 0) -
        Math.Max(0, 50 - villager.Hunger) * .5f;

    private static SettlementScoutAssignment AdvanceScout(
        SettlementScoutAssignment assignment,
        SettlementScoutReport observation)
    {
        var legs = assignment.LegsCompleted + 1;
        var returning = legs >= VillagerExplorationService.OpeningScoutLegs;
        return assignment with
        {
            WaypointX = null,
            WaypointY = null,
            LegsCompleted = legs,
            Reached = returning,
            Returning = returning
        };
    }

    private static bool Prefer(
        SettlementScoutReport candidate,
        SettlementScoutReport current) =>
        IsViableCamp(candidate) && !IsViableCamp(current) ||
        IsViableCamp(candidate) == IsViableCamp(current) &&
        candidate.CampScore > current.CampScore;

    private static bool IsMember(SettlementGroupState group, string id) =>
        group.MemberIds.Contains(id, StringComparer.Ordinal);

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
