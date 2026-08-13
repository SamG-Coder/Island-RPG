using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Simulation;
using ProtocolCombatEvent = IslandRpg.Protocol.CombatEvent;
using ProtocolCombatEventKind = IslandRpg.Protocol.CombatEventKind;
using ProtocolCombatStatusFlags = IslandRpg.Protocol.CombatStatusFlags;
using SimulationCombatEventKind = IslandRpg.Simulation.CombatEventKind;
using SimulationCombatStatusFlags = IslandRpg.Simulation.CombatStatusFlags;

namespace IslandRpg.Server;

/// <summary>
/// The combat wire boundary. Clients name an action and expected revisions;
/// every position, target identity, result, and replicated state is projected
/// from the single-owner Simulation aggregate.
/// </summary>
internal static class CombatActionProtocolAdapter
{
    public static CombatGameplayIntent ToIntent(
        ActionCommandMessage command,
        CombatActionPayload action) => action switch
        {
            SetCombatTargetAction target => new SetCombatTargetIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                new EnemyReference(
                    new EnemyId(target.Enemy.EnemyId),
                    target.Enemy.ExpectedRevision)),
            CancelCombatAction => new CancelCombatIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision),
            SetCombatStanceAction stance => new SetCombatStanceIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                ToSimulationStance(stance.Stance)),
            RespawnAction => new RespawnIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision),
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                "The combat action is not supported by this authority.")
        };

    public static CombatActionResultMessage ToPrivateResult(
        ulong sequence,
        ulong tick,
        ActionCommandMessage command,
        CombatActionPayload action,
        IntentResult result)
    {
        var transaction = result.CombatTransaction;
        var enemy = action is SetCombatTargetAction target
            ? target.Enemy
            : default;
        var revision = transaction?.EnemyDelta?.Current?.Revision ??
            transaction?.EnemyDelta?.Previous?.Revision ??
            enemy.ExpectedRevision;
        return new CombatActionResultMessage(
            sequence,
            tick,
            command.CommandId,
            action.Action,
            enemy,
            result.Accepted,
            DedicatedServer.MapRejection(result.Status),
            result.Error ?? transaction?.Detail ?? string.Empty,
            result.ActorRevision,
            result.InventoryRevision,
            revision);
    }

    public static EnemyBaselineMessage ToBaseline(
        ulong sequence,
        ulong tick,
        IReadOnlyList<AuthoritativeEnemySnapshot> enemies) => new(
        sequence,
        tick,
        enemies.OrderBy(static enemy => enemy.EnemyId.Value)
            .Select(ToState)
            .ToArray());

    public static EnemyDeltaBatchMessage? ToPublicDelta(
        ulong sequence,
        ulong tick,
        EnemyStateDelta? delta)
    {
        if (delta is null) return null;
        var previous = delta.Previous;
        var current = delta.Current;
        var id = current?.EnemyId ?? previous?.EnemyId ??
            throw new InvalidOperationException(
                "Enemy delta omitted both authoritative states.");
        var expectedRevision = previous?.Revision ?? 0;
        var currentRevision = current?.Revision ?? checked(expectedRevision + 1);
        if (currentRevision <= expectedRevision)
            throw new InvalidOperationException(
                "A public enemy delta must advance its semantic revision.");
        return new EnemyDeltaBatchMessage(
            sequence,
            tick,
            [new EnemyDelta(
                current is null ? EnemyDeltaKind.Remove : EnemyDeltaKind.Upsert,
                new CombatEnemyReference(id.Value, expectedRevision),
                currentRevision,
                current is null ? null : ToState(current))]);
    }

    public static EnemyState ToState(AuthoritativeEnemySnapshot value) => new(
        value.EnemyId.Value,
        value.NetworkEntityId,
        value.Revision,
        ToArchetype(value.Kind),
        ToSize(value.SizeScale),
        ToBehavior(value.Behavior),
        ToStatusFlags(value.StatusFlags),
        value.Position.X,
        value.Position.Y,
        checked((short)value.WorldLevel),
        value.Health,
        value.MaximumHealth,
        value.TargetNetworkEntityId,
        value.ParentEnemyId?.Value ?? Guid.Empty,
        value.SpawnOrdinal);

    /// <summary>
    /// Converts a persisted semantic event to one presentation event. Loot is
    /// only an audiovisual receipt here; the actual items are committed as an
    /// authoritative world container by Simulation and follow world deltas.
    /// </summary>
    public static ProtocolCombatEvent? ToEvent(
        CombatEventSnapshot value,
        IReadOnlyDictionary<EnemyId, AuthoritativeEnemySnapshot> enemies,
        IReadOnlyDictionary<ActorId, (ulong EntityId, float X, float Y,
            int WorldLevel)> actors)
    {
        if (value.Kind == SimulationCombatEventKind.TargetCancelled)
            return null;

        enemies.TryGetValue(value.EnemyId ?? default, out var enemy);
        actors.TryGetValue(value.ActorId ?? default, out var actor);
        var actorEntity = actor.EntityId;
        var enemyEntity = enemy?.NetworkEntityId ??
            (value.EnemyId is { } enemyId
                ? AuthoritativeCombatTransactions.DeriveNetworkEntityId(enemyId)
                : 0);
        var enemyIsSource = value.Kind is
            SimulationCombatEventKind.EnemyAttacked or
            SimulationCombatEventKind.StatusApplied;
        var source = enemyIsSource ? enemyEntity : actorEntity;
        var target = enemyIsSource ? actorEntity : enemyEntity;
        if (value.Kind == SimulationCombatEventKind.ActorDied)
            (source, target) = (0, actorEntity);
        else if (value.Kind == SimulationCombatEventKind.StatusExpired)
            (source, target) = (0, actorEntity);
        else if (value.Kind == SimulationCombatEventKind.ActorRespawned)
            (source, target) = (actorEntity, 0);
        else if (value.Kind is SimulationCombatEventKind.EnemyDied or
                 SimulationCombatEventKind.EnemySplit or
                 SimulationCombatEventKind.LootRolled)
            (source, target) = (enemyEntity, 0);

        var positionX = enemy?.Position.X ?? actor.X;
        var positionY = enemy?.Position.Y ?? actor.Y;
        var worldLevel = enemy?.WorldLevel ?? actor.WorldLevel;
        var related = value.SpawnedEnemyIds.IsDefaultOrEmpty
            ? 0
            : AuthoritativeCombatTransactions.DeriveNetworkEntityId(
                value.SpawnedEnemyIds[0]);
        return new ProtocolCombatEvent(
            value.EventOrdinal,
            ToEventKind(value),
            source,
            target,
            value.Damage,
            ToStatusEffect(value.Status),
            positionX,
            positionY,
            checked((short)worldLevel),
            related);
    }

    private static MeleeCombatStance ToSimulationStance(CombatStance value) =>
        value switch
        {
            CombatStance.Balanced => MeleeCombatStance.Accurate,
            CombatStance.Aggressive => MeleeCombatStance.Aggressive,
            CombatStance.Defensive => MeleeCombatStance.Defensive,
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                "The combat stance is invalid.")
        };

    public static CombatStance ToProtocolStance(MeleeCombatStance value) =>
        value switch
        {
            MeleeCombatStance.Accurate => CombatStance.Balanced,
            MeleeCombatStance.Aggressive => CombatStance.Aggressive,
            MeleeCombatStance.Defensive => CombatStance.Defensive,
            _ => throw new InvalidOperationException(
                "Simulation produced an invalid combat stance.")
        };

    public static CombatLifeState ToLifeState(ActorLifeState value) =>
        value switch
        {
            ActorLifeState.Alive => CombatLifeState.Alive,
            ActorLifeState.Dead => CombatLifeState.Dead,
            _ => throw new InvalidOperationException(
                "Simulation produced an invalid actor life state.")
        };

    public static ProtocolCombatStatusFlags ToStatusFlags(
        SimulationCombatStatusFlags value)
    {
        var result = ProtocolCombatStatusFlags.None;
        if ((value & SimulationCombatStatusFlags.Slowed) != 0)
            result |= ProtocolCombatStatusFlags.Slowed;
        if ((value & SimulationCombatStatusFlags.Rooted) != 0)
            result |= ProtocolCombatStatusFlags.Rooted;
        if ((value & SimulationCombatStatusFlags.Poisoned) != 0)
            result |= ProtocolCombatStatusFlags.Poisoned;
        if ((value & SimulationCombatStatusFlags.Hidden) != 0)
            result |= ProtocolCombatStatusFlags.Hidden;
        if ((value & SimulationCombatStatusFlags.Burrowed) != 0)
            result |= ProtocolCombatStatusFlags.Burrowed;
        return result;
    }

    private static CombatEnemyArchetype ToArchetype(EnemyKind value) =>
        value switch
        {
            EnemyKind.WaterSlime => CombatEnemyArchetype.WaterSlime,
            EnemyKind.GrassSlime => CombatEnemyArchetype.GrassSlime,
            EnemyKind.SandSlime => CombatEnemyArchetype.SandSlime,
            EnemyKind.CaveSlime => CombatEnemyArchetype.CaveSlime,
            _ => throw new InvalidOperationException(
                "Simulation produced an invalid enemy kind.")
        };

    private static CombatEnemySize ToSize(float scale) => scale switch
    {
        < .85f => CombatEnemySize.Small,
        < 1.18f => CombatEnemySize.Medium,
        _ => CombatEnemySize.Large
    };

    private static CombatEnemyBehavior ToBehavior(EnemyBehavior value) =>
        value switch
        {
            EnemyBehavior.Idle or EnemyBehavior.Roam => CombatEnemyBehavior.Idle,
            EnemyBehavior.Chase or EnemyBehavior.Return =>
                CombatEnemyBehavior.Chasing,
            EnemyBehavior.Attack => CombatEnemyBehavior.Attacking,
            EnemyBehavior.Dead => CombatEnemyBehavior.Dead,
            _ => throw new InvalidOperationException(
                "Simulation produced an invalid enemy behavior.")
        };

    private static ProtocolCombatEventKind ToEventKind(
        CombatEventSnapshot value) => value.Kind switch
        {
            SimulationCombatEventKind.PlayerAttacked or
                SimulationCombatEventKind.EnemyAttacked => value.Hit
                    ? ProtocolCombatEventKind.Damage
                    : ProtocolCombatEventKind.AttackStarted,
            SimulationCombatEventKind.StatusApplied =>
                ProtocolCombatEventKind.StatusApplied,
            SimulationCombatEventKind.StatusExpired =>
                ProtocolCombatEventKind.StatusExpired,
            SimulationCombatEventKind.ActorDied or
                SimulationCombatEventKind.EnemyDied =>
                ProtocolCombatEventKind.Death,
            SimulationCombatEventKind.ActorRespawned =>
                ProtocolCombatEventKind.Respawn,
            SimulationCombatEventKind.EnemySplit =>
                ProtocolCombatEventKind.Split,
            SimulationCombatEventKind.LootRolled =>
                ProtocolCombatEventKind.LootDropped,
            _ => throw new InvalidOperationException(
                "Simulation produced an unsupported combat event.")
        };

    private static CombatStatusEffect ToStatusEffect(SlimeStatusKind value) =>
        value switch
        {
            SlimeStatusKind.None => CombatStatusEffect.None,
            SlimeStatusKind.Slow => CombatStatusEffect.Slow,
            SlimeStatusKind.Root => CombatStatusEffect.Root,
            SlimeStatusKind.Poison => CombatStatusEffect.Poison,
            _ => throw new InvalidOperationException(
                "Simulation produced an invalid combat status effect.")
        };
}
