namespace IslandRpg.Gameplay;

/// <summary>
/// Cross-layer membership bounds for entities carried by one complete world
/// snapshot. The aggregate is deliberately small enough to fit in one bounded
/// reliable keyframe at <c>EntitySnapshot.WireSize</c> bytes per member.
/// </summary>
public static class NetworkPopulationLimits
{
    public const int MaximumActors = 1_024;

    public const int MaximumBoats = 256;

    public const int MaximumEnemies = CombatPopulationLimits.MaximumEnemies;

    public const int MaximumSnapshotEntities =
        MaximumActors + MaximumBoats + MaximumEnemies;
}
