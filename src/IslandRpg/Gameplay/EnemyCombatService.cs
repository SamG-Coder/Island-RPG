namespace IslandRpg.Gameplay;

internal static class EnemyCombatService
{
    public static EnemyState ApplyHit(
        EnemyState enemy, int damage, string attackerId)
    {
        if (!enemy.Alive || damage <= 0 ||
            string.IsNullOrWhiteSpace(attackerId))
            return enemy;
        return EnemySpawnerService.Provoke(
            enemy with
            {
                Health = Math.Max(0, enemy.Health - damage)
            },
            attackerId);
    }
}
