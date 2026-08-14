using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal abstract class EnemyController
{
    protected virtual float AttackRange => EnemySpawnerService.AttackRange;
    protected virtual float RoamRadius => EnemySpawnerService.RoamRadius;
    protected virtual float LeashRadius => EnemySpawnerService.LeashRadius;
    protected virtual double ReactionDelaySeconds(EnemyState enemy) => .6;

    public EnemyState Update(
        EnemyState enemy,
        IReadOnlyList<EnemyActorPresence> actors,
        double now,
        float elapsed,
        int worldSeed)
    {
        if (!enemy.Alive)
            return enemy with { Behavior = EnemyBehavior.Dead };
        var target = FindTarget(enemy, actors);
        if (target is { } actor &&
            Vector2.DistanceSquared(actor.Position, enemy.SpawnPosition) <=
            LeashRadius * LeashRadius)
        {
            if (enemy.TargetId != actor.Id)
                return enemy with
                {
                    TargetId = actor.Id,
                    Destination = enemy.Position,
                    Behavior = EnemyBehavior.Idle,
                    Path = null,
                    PathIndex = 0,
                    RoutedDestination = null,
                    AggroReadyAt = now + Math.Max(
                        0, ReactionDelaySeconds(enemy))
                };
            if (now < enemy.AggroReadyAt)
                return enemy with
                {
                    Destination = enemy.Position,
                    Behavior = EnemyBehavior.Idle,
                    Path = null,
                    PathIndex = 0,
                    RoutedDestination = null
                };
            var distance = Vector2.Distance(enemy.Position, actor.Position);
            return enemy with
            {
                TargetId = actor.Id,
                Destination = actor.Position,
                Behavior = distance <= AttackRange
                    ? EnemyBehavior.Attack
                    : EnemyBehavior.Chase
            };
        }
        if (enemy.TargetId is not null || enemy.ProvokedById is not null)
            return enemy with
            {
                TargetId = null,
                ProvokedById = null,
                Destination = enemy.SpawnPosition,
                Behavior = EnemyBehavior.Return,
                Path = null,
                PathIndex = 0,
                RoutedDestination = null,
                NextPathAt = 0,
                NextDecisionAt = now + 1,
                AggroReadyAt = 0
            };
        if (Vector2.DistanceSquared(enemy.Position, enemy.SpawnPosition) >
            RoamRadius * RoamRadius)
            return enemy with
            {
                TargetId = null,
                Destination = enemy.SpawnPosition,
                Behavior = EnemyBehavior.Return
            };
        if (enemy.Behavior is EnemyBehavior.Roam or EnemyBehavior.Return &&
            enemy.Path is { Count: > 0 })
            return enemy;
        if (now < enemy.NextDecisionAt) return enemy;
        var random = new Random(HashCode.Combine(
            worldSeed, enemy.Id, (int)(now / 3)));
        var angle = random.NextSingle() * MathF.Tau;
        var radius = random.NextSingle() * RoamRadius;
        return enemy with
        {
            TargetId = null,
            Destination = enemy.SpawnPosition + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * radius,
            Behavior = EnemyBehavior.Roam,
            NextDecisionAt = now + 2.5 + random.NextDouble() * 3
        };
    }

    protected abstract bool CanTarget(
        EnemyState enemy, EnemyActorPresence actor);

    private EnemyActorPresence? FindTarget(
        EnemyState enemy, IReadOnlyList<EnemyActorPresence> actors) =>
        actors.Where(actor =>
                actor.Alive && actor.WorldLevel == enemy.WorldLevel &&
                actor.CanBeTargeted &&
                CanTarget(enemy, actor))
            .OrderBy(actor =>
                Vector2.DistanceSquared(actor.Position, enemy.Position))
            .Cast<EnemyActorPresence?>()
            .FirstOrDefault();
}

internal sealed class SlimeEnemyController : EnemyController
{
    protected override double ReactionDelaySeconds(EnemyState enemy) =>
        SlimeCombatRules.ReactionDelaySeconds(enemy.Kind);

    protected override bool CanTarget(
        EnemyState enemy, EnemyActorPresence actor) =>
        SlimeCombatRules.CanAcquireTarget(
            enemy.Kind,
            actor.Id == enemy.ProvokedById,
            Vector2.DistanceSquared(actor.Position, enemy.Position));
}
