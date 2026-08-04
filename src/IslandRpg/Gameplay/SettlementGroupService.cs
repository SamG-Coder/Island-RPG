using IslandRpg.World;
using OpenTK.Mathematics;
using System.Text.Json.Serialization;

namespace IslandRpg.Gameplay;

internal sealed record SettlementGroupState(
    string Id,
    string LeaderId,
    IReadOnlyList<string> MemberIds,
    float CampX,
    float CampY,
    int WorldLevel,
    double FormedGameSeconds,
    float CacheRadius = SettlementGroupService.DefaultCacheRadius,
    IReadOnlyList<SettlementLocationReport>? SharedLocations = null,
    SettlementOpeningStage OpeningStage = SettlementOpeningStage.CacheReady,
    IReadOnlyList<SettlementScoutAssignment>? ScoutAssignments = null,
    IReadOnlyList<SettlementScoutReport>? ScoutReports = null,
    IReadOnlyList<SettlementCampResponse>? CampResponses = null,
    SettlementJusticeCase? ActiveJusticeCase = null,
    SettlementExclusionState? Exclusion = null,
    SocialIncidentAftermathState? ActiveAftermath = null,
    int ReconnaissanceRound = 0,
    bool CoordinatedReconnaissance = false)
{
    [JsonIgnore]
    public Vector2 Camp => new(CampX, CampY);
}

internal sealed record SettlementLocationReport(
    VillagerLocationType Type,
    float PositionX,
    float PositionY,
    int WorldLevel,
    float Confidence,
    double LastObservedGameSeconds,
    string ReporterId);

internal enum SettlementOpeningStage : byte
{
    Reconnaissance,
    ComparingCamps,
    MovingToCamp,
    CacheReady
}

internal sealed record SettlementScoutAssignment(
    string ScoutId,
    float TargetX,
    float TargetY,
    int Sector,
    bool Reached = false,
    bool Reported = false,
    float? WaypointX = null,
    float? WaypointY = null,
    int LegsCompleted = 0,
    bool Returning = false);

internal sealed record SettlementScoutReport(
    string ScoutId,
    float PositionX,
    float PositionY,
    bool Water,
    bool Food,
    bool Wood,
    bool Stone,
    bool Danger,
    bool DefensibleGround,
    float CampScore,
    double GameSeconds);

internal enum SettlementCampResponseKind : byte
{
    Agree,
    Object,
    Leave
}

internal sealed record SettlementCampResponse(
    string VillagerId,
    SettlementCampResponseKind Response,
    string Reason);

internal static class SettlementGroupService
{
    public const float DefaultCacheRadius = 4;

    public static SettlementGroupState Form(
        string worldId,
        string leaderId,
        IEnumerable<string> memberIds,
        Vector2 camp,
        int worldLevel,
        double gameSeconds) =>
        new(
            $"group-{worldId}",
            leaderId,
            memberIds.Distinct(StringComparer.Ordinal).ToArray(),
            camp.X,
            camp.Y,
            worldLevel,
            gameSeconds,
            OpeningStage: SettlementOpeningStage.Reconnaissance);

    public static bool IsMember(
        SettlementGroupState? group, string actorId) =>
        group?.MemberIds.Contains(actorId, StringComparer.Ordinal) == true;

    public static SettlementGroupState IncludeMember(
        SettlementGroupState group, string? actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || IsMember(group, actorId))
            return group;
        return group with
        {
            MemberIds = group.MemberIds.Append(actorId).ToArray()
        };
    }

    public static SettlementGroupState RemoveMember(
        SettlementGroupState group,
        string? actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !IsMember(group, actorId))
            return group;
        return group with
        {
            MemberIds = group.MemberIds.Where(value =>
                !value.Equals(actorId, StringComparison.Ordinal)).ToArray()
        };
    }

    public static bool CanAccess(
        VillagerState villager,
        string? characterOwnerId,
        string? groupOwnerId) =>
        string.IsNullOrWhiteSpace(characterOwnerId) &&
        string.IsNullOrWhiteSpace(groupOwnerId) ||
        string.Equals(characterOwnerId, villager.Id,
            StringComparison.Ordinal) ||
        !string.IsNullOrWhiteSpace(groupOwnerId) &&
        string.Equals(groupOwnerId, villager.SettlementGroupId,
            StringComparison.Ordinal);

    public static bool CanAccess(
        SettlementGroupState? group,
        string actorId,
        string? characterOwnerId,
        string? groupOwnerId) =>
        string.IsNullOrWhiteSpace(characterOwnerId) &&
        string.IsNullOrWhiteSpace(groupOwnerId) ||
        string.Equals(characterOwnerId, actorId,
            StringComparison.Ordinal) ||
        group is not null &&
        string.Equals(groupOwnerId, group.Id,
            StringComparison.Ordinal) &&
        IsMember(group, actorId);

    public static bool IsInCache(
        SettlementGroupState group,
        WorldGroundObject item) =>
        item.GroupOwnerId == group.Id &&
        Vector2.DistanceSquared(
            new(item.X, item.Y), group.Camp) <=
        group.CacheRadius * group.CacheRadius;

    public static bool IsSharedSupply(string? groupOwnerId) =>
        !string.IsNullOrWhiteSpace(groupOwnerId);

    public static WorldGroundObject ClaimForGroup(
        WorldGroundObject item, SettlementGroupState group) =>
        item with
        {
            OwnerId = null,
            GroupOwnerId = group.Id
        };

    public static SettlementGroupState ReportDiscoveries(
        SettlementGroupState group,
        VillagerState reporter)
    {
        if (!IsMember(group, reporter.Id) ||
            reporter.LocationMemories is not { Count: > 0 })
            return group;
        var reports = group.SharedLocations?.ToList() ?? [];
        foreach (var memory in reporter.LocationMemories.Where(value =>
                     value.Confidence >=
                     VillagerLocationMemoryService.MinimumUsefulConfidence))
        {
            var index = reports.FindIndex(value =>
                value.Type == memory.Type &&
                value.WorldLevel == memory.WorldLevel &&
                Vector2.DistanceSquared(
                    new(value.PositionX, value.PositionY),
                    new(memory.PositionX, memory.PositionY)) <=
                VillagerLocationMemoryService.MatchRadius *
                VillagerLocationMemoryService.MatchRadius);
            var report = new SettlementLocationReport(
                memory.Type,
                memory.PositionX,
                memory.PositionY,
                memory.WorldLevel,
                memory.Confidence,
                memory.LastObservedGameSeconds,
                reporter.Id);
            if (index < 0) reports.Add(report);
            else if (reports[index].LastObservedGameSeconds <=
                     report.LastObservedGameSeconds)
                reports[index] = report;
        }
        if (reports.Count > 128)
            reports = reports
                .OrderByDescending(value => value.LastObservedGameSeconds)
                .Take(128)
                .ToList();
        if ((group.SharedLocations ?? []).SequenceEqual(reports))
            return group;
        return group with { SharedLocations = reports };
    }

    public static VillagerState LearnReports(
        VillagerState villager,
        SettlementGroupState group,
        double gameSeconds)
    {
        if (!IsMember(group, villager.Id) ||
            group.SharedLocations is not { Count: > 0 })
            return villager;
        var result = villager;
        foreach (var report in group.SharedLocations)
            result = VillagerLocationMemoryService.Remember(
                result,
                report.Type,
                new(report.PositionX, report.PositionY),
                report.WorldLevel,
                gameSeconds,
                confidence: Math.Clamp(report.Confidence * .8f, 0, 1),
                clearFailedLocation: false);
        return result;
    }
}
