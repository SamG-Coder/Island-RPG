namespace IslandRpg.Gameplay;

internal static class VillagerIntentPriorityService
{
    public static bool HasUrgentOverride(VillagerState villager) =>
        villager.Health > 0 &&
        (villager.Hunger <= 35 ||
         villager.Health <= 20 ||
         villager.ConflictIntent != VillagerConflictIntent.None ||
         villager.Action == EntityAction.Attack);

    public static bool HasCommittedWork(VillagerState villager) =>
        villager.Health > 0 &&
        (HasAssignedProject(villager) ||
         villager.GoalObjectId is not null ||
         VillagerPromisePlanService.HasActiveWork(villager));

    public static bool HasAssignedProject(VillagerState villager) =>
        villager.Health > 0 && villager.ProjectAssignment is not null;

    public static bool ShouldProtectCommittedWork(
        VillagerState villager) =>
        HasCommittedWork(villager) &&
        !HasUrgentOverride(villager) &&
        !VillagerFatigueService.ShouldRest(villager);
}
