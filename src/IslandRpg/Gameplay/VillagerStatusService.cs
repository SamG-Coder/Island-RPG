namespace IslandRpg.Gameplay;

internal static class VillagerStatusService
{
    public static string CurrentThought(
        VillagerState villager,
        double gameSeconds,
        bool controllerBusy = false)
    {
        if (villager.Health <= 0) return "Dead.";
        if (!string.IsNullOrWhiteSpace(
                villager.LastDeliberation?.PrivateThought))
            return villager.LastDeliberation.PrivateThought;
        if (villager.Activity == VillagerActivity.Resting)
            return $"Resting until energy reaches " +
                   $"{VillagerFatigueService.RestResumeThreshold:0}.";
        if (villager.Activity == VillagerActivity.Blocked)
            return "Blocked; waiting before trying a different target.";
        if (controllerBusy)
            return $"Performing {villager.Action.ToString().ToLowerInvariant()}.";
        var waitRealSeconds = Math.Max(
            0,
            (villager.NextDecisionGameSeconds - gameSeconds) /
            VillagerSimulation.GameSecondsPerRealSecond);
        if (waitRealSeconds > .05)
            return $"Waiting {waitRealSeconds:0.0}s before replanning.";
        if (villager.ProjectAssignment is { } project)
            return project.BuilderId == villager.Id
                ? $"Coordinating the {ItemCatalog.Get(project.ProjectItemId).Name}."
                : $"Helping with the {ItemCatalog.Get(project.ProjectItemId).Name}.";
        return villager.WorkRole switch
        {
            VillagerWorkRole.Food => "Looking for the next food task.",
            VillagerWorkRole.Wood => "Looking for the next wood task.",
            VillagerWorkRole.Crafting => "Looking for the next crafting task.",
            VillagerWorkRole.Exploration => "Looking for the next exploration task.",
            _ => $"Considering the need to " +
                 $"{villager.Need.ToString().ToLowerInvariant()}."
        };
    }

    public static float SecondsUntilDecision(
        VillagerState villager,
        double gameSeconds) => (float)Math.Max(
            0,
            (villager.NextDecisionGameSeconds - gameSeconds) /
            VillagerSimulation.GameSecondsPerRealSecond);
}
