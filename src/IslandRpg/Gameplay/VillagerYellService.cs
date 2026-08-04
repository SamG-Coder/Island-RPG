using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class VillagerYellService
{
    public const float HearingRadius = 24;
    public const int MaximumResponders = 3;
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
