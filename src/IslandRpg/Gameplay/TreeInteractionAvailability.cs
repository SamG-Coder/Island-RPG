using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class TreeInteractionAvailability
{
    public static WorldTreeInstance? InstanceAt(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y)
    {
        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            if (instance.X == x && instance.Y == y)
                return instance;
        }
        return null;
    }

    public static TreeLifecycleState StateAt(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y) =>
        InstanceAt(instances, x, y)?.State ??
        TreeLifecycleState.Standing;

    public static bool CanUseStandingTree(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y) =>
        StateAt(instances, x, y) == TreeLifecycleState.Standing;

    public static bool CanGatherSticks(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y)
    {
        var instance = InstanceAt(instances, x, y);
        return instance is null ||
               instance.State == TreeLifecycleState.Standing &&
               instance.SticksRemaining != 0;
    }
}
