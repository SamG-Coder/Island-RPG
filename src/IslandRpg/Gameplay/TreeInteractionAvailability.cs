using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class TreeInteractionAvailability
{
    public static TreeLifecycleState StateAt(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y)
    {
        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            if (instance.X == x && instance.Y == y)
                return instance.State;
        }
        return TreeLifecycleState.Standing;
    }

    public static bool CanUseStandingTree(
        IReadOnlyList<WorldTreeInstance> instances,
        int x,
        int y) =>
        StateAt(instances, x, y) == TreeLifecycleState.Standing;
}
