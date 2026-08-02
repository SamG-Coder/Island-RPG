namespace IslandRpg.Gameplay;

internal static class EnemyCombatService
{
    public static EnemyState ApplyHit(
        EnemyState enemy, int damage, string attackerId,
        double visualSeconds = 0)
    {
        if (!enemy.Alive || damage <= 0 ||
            string.IsNullOrWhiteSpace(attackerId))
            return enemy;
        var health = Math.Max(0, enemy.Health - damage);
        return health > 0
            ? EnemySpawnerService.Provoke(
                enemy with { Health = health }, attackerId)
            : enemy with
            {
                Health = 0,
                Behavior = EnemyBehavior.Dead,
                TargetId = null,
                ProvokedById = attackerId,
                Destination = enemy.Position,
                Path = null,
                PathIndex = 0,
                RoutedDestination = null,
                VisualAction = EntityAction.Die,
                VisualActionStartedAt = visualSeconds
            };
    }
}
