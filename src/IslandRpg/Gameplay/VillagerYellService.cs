using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class VillagerYellService
{
    public const float HearingRadius = 24;
    public const double CooldownRealSeconds = 8;

    public static bool CanYell(VillagerState caller, double gameSeconds) =>
        caller.Health > 0 && caller.NextYellGameSeconds <= gameSeconds;

    public static VillagerState MarkYelled(
        VillagerState caller,
        double gameSeconds) => caller with
        {
            NextYellGameSeconds = gameSeconds + CooldownRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond
        };

    public static bool CanHearAndRespond(
        VillagerState candidate,
        VillagerState caller)
    {
        if (candidate.Health <= 25 ||
            candidate.Hunger <= VillagerFoodService.UrgentHungerThreshold ||
            candidate.Energy < VillagerFatigueService.RestThreshold ||
            candidate.WorldLevel != caller.WorldLevel)
            return false;
        return Vector2.DistanceSquared(
            new(candidate.PositionX, candidate.PositionY),
            new(caller.PositionX, caller.PositionY)) <=
            HearingRadius * HearingRadius;
    }

    public static bool ShouldAnswer(
        VillagerState candidate,
        VillagerState caller,
        string aggressorId,
        in RelationshipState relationship,
        bool sameSettlement)
    {
        if (!CanHearAndRespond(candidate, caller) ||
            candidate.Boldness < .45f ||
            candidate.ConflictTargetId is { } existingTarget &&
            existingTarget != aggressorId)
            return false;
        var callerIsLeader = caller.Id == candidate.RecognizedLeaderId;
        var kind = VillagerRelationshipClassifier.Classify(
            relationship, callerIsLeader);
        if (kind is VillagerRelationshipKind.Rival or
            VillagerRelationshipKind.Enemy or
            VillagerRelationshipKind.FearedEnemy)
            return false;
        return VillagerRelationshipClassifier.WillDefend(
                   relationship, callerIsLeader) ||
               sameSettlement &&
               (callerIsLeader || candidate.Sociability >= .55f ||
                candidate.Honesty >= .65f);
    }
}

internal static class VillagerFacingService
{
    public static VillagerState Face(
        VillagerState villager,
        Vector2 target)
    {
        var direction = target - new Vector2(
            villager.PositionX, villager.PositionY);
        if (direction.LengthSquared <= .0001f) return villager;
        direction = direction.Normalized();
        return villager with
        {
            FacingX = direction.X,
            FacingY = direction.Y
        };
    }
}
