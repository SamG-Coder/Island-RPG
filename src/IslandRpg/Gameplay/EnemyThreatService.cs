namespace IslandRpg.Gameplay;

internal static class EnemyThreatService
{
    public static bool HasActiveThreat(
        IEnumerable<EnemyState> enemies, string actorId) =>
        enemies.Any(enemy =>
            enemy.Alive && enemy.TargetId == actorId &&
            enemy.Behavior is EnemyBehavior.Chase or EnemyBehavior.Attack);
}
