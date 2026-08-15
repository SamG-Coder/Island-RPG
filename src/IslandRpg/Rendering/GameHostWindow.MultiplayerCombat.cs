using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using GameplayEnemyState = IslandRpg.Gameplay.EnemyState;
using ProtocolEnemyState = IslandRpg.Protocol.EnemyState;

namespace IslandRpg.Rendering;

/// <summary>
/// Presentation adapter for server-owned combat. Reliable messages own enemy
/// identity, health, behaviour and combat outcomes; UDP snapshots only refine
/// positions between those messages. Nothing in this file rolls combat,
/// advances enemy AI, grants experience, creates loot or persists game state.
/// </summary>
internal sealed partial class GameHostWindow
{
    private sealed class NetworkEnemyPresentation(ProtocolEnemyState state)
    {
        public ProtocolEnemyState State { get; set; } = state;
        public Vector2 Position { get; set; } = new(state.X, state.Y);
        public Vector2 Velocity { get; set; }
        public bool HasSnapshot { get; set; }
        public CombatStatusFlags PresentedStatusFlags { get; set; } =
            state.StatusFlags;
    }

    private readonly record struct PendingNetworkCombatCommand(
        CombatActionKind Action,
        Guid EnemyId = default);

    private readonly Dictionary<Guid, NetworkEnemyPresentation>
        _networkEnemies = [];
    private readonly Dictionary<ulong, Guid> _networkEnemyIdsByEntity = [];
    private readonly Dictionary<ulong, NetworkEnemyPresentation>
        _networkEnemyEventTombstones = [];
    private readonly Dictionary<ulong, double>
        _networkEnemyEventTombstoneExpiry = [];
    private readonly Dictionary<Guid, PendingNetworkCombatCommand>
        _networkCombatCommands = [];
    private readonly HashSet<Guid> _networkRetiringEnemyIds = [];
    private readonly HashSet<ulong> _networkPresentedSplitSources = [];
    private int _networkPlayerMaximumHealth = AdventureService.BaseMaximumHealth;
    private CombatLifeState _networkPlayerLifeState = CombatLifeState.Alive;
    private CombatStatusFlags _networkPlayerCombatStatus;
    private Guid? _networkAuthoritativeCombatTargetId;

    private void SubscribeNetworkCombat(NetworkGameClient client)
    {
        client.EnemiesChanged += OnNetworkEnemiesChanged;
        client.BoatsChanged += OnNetworkBoatsChanged;
        client.CombatEventsReceived += OnNetworkCombatEventsReceived;
        client.CombatActionCompleted += OnNetworkCombatActionCompleted;
    }

    private void UnsubscribeNetworkCombat(NetworkGameClient client)
    {
        client.EnemiesChanged -= OnNetworkEnemiesChanged;
        client.BoatsChanged -= OnNetworkBoatsChanged;
        client.CombatEventsReceived -= OnNetworkCombatEventsReceived;
        client.CombatActionCompleted -= OnNetworkCombatActionCompleted;
    }

    private void OnNetworkBoatsChanged(
        object? sender, NetworkBoatsChangedEventArgs value) =>
        _networkEvents.Enqueue(() => HandleNetworkBoatsChanged(value));

    private void OnNetworkEnemiesChanged(
        object? sender, NetworkEnemiesChangedEventArgs value) =>
        _networkEvents.Enqueue(() => HandleNetworkEnemiesChanged(value));

    private void OnNetworkCombatEventsReceived(
        object? sender, NetworkCombatEventsEventArgs value) =>
        _networkEvents.Enqueue(() => HandleNetworkCombatEvents(value.Events));

    private void OnNetworkCombatActionCompleted(
        object? sender, NetworkCombatActionResultEventArgs value) =>
        _networkEvents.Enqueue(() =>
            HandleNetworkCombatActionResult(value.Result));

    private void InitializeNetworkCombatProjection()
    {
        ClearNetworkCombatProjection();
        if (_networkClient is null) return;
        SynchronizeNetworkEnemies(_networkClient.State.Enemies.Values);
        if (_networkClient.State.Gameplay is { } gameplay)
            ApplyNetworkCombatPlayerState(gameplay);
    }

    private void ClearNetworkCombatProjection()
    {
        _networkEnemies.Clear();
        _networkEnemyIdsByEntity.Clear();
        _networkEnemyEventTombstones.Clear();
        _networkEnemyEventTombstoneExpiry.Clear();
        _networkCombatCommands.Clear();
        _networkRetiringEnemyIds.Clear();
        _networkPresentedSplitSources.Clear();
        _networkPlayerMaximumHealth = AdventureService.BaseMaximumHealth;
        _networkPlayerLifeState = CombatLifeState.Alive;
        _networkPlayerCombatStatus = CombatStatusFlags.None;
        _networkAuthoritativeCombatTargetId = null;
        _combatEnemyId = null;
        _enemyContextTargetId = null;
        _enemies.Clear();
        _slimeAttackEffects.Clear();
        _playerDefeated = false;
        if (_modalScreen.Active == ModalScreenKind.Death)
            _modalScreen.Close(ModalScreenKind.Death);
    }

    private void HandleNetworkEnemiesChanged(
        NetworkEnemiesChangedEventArgs value)
    {
        if (!IsNetworkWorld || _networkClient is null) return;
        if (value.IsBaseline)
        {
            SynchronizeNetworkEnemies(
                _networkClient.State.Enemies.Values);
            return;
        }

        foreach (var change in value.Changes)
        {
            if (change.State is { } state)
                UpsertNetworkEnemy(state);
            else
                RemoveNetworkEnemy(change.EnemyId);
        }
    }

    private void SynchronizeNetworkEnemies(
        IEnumerable<ProtocolEnemyState> states)
    {
        var materialized = states.ToArray();
        var current = materialized.Select(value => value.EnemyId).ToHashSet();
        foreach (var id in _networkEnemies.Keys
                     .Where(id => !current.Contains(id)).ToArray())
            RemoveNetworkEnemy(id);
        foreach (var state in materialized)
            UpsertNetworkEnemy(state);
        ReconcileNetworkCombatTargetPresentation();
    }

    private void UpsertNetworkEnemy(ProtocolEnemyState state)
    {
        if (!_networkEnemies.TryGetValue(
                state.EnemyId, out var presentation))
        {
            presentation = new(state);
            _networkEnemies.Add(state.EnemyId, presentation);
        }
        else
        {
            var previousState = presentation.State;
            if (presentation.State.EntityId != state.EntityId)
            {
                _networkEnemyIdsByEntity.Remove(
                    presentation.State.EntityId);
                presentation.Position = new(state.X, state.Y);
                presentation.Velocity = Vector2.Zero;
                presentation.HasSnapshot = false;
            }
            presentation.State = state;
            presentation.PresentedStatusFlags = state.StatusFlags;
            var authoritativeRelocation =
                previousState.WorldLevel != state.WorldLevel ||
                (previousState.StatusFlags.HasFlag(
                     CombatStatusFlags.Burrowed) &&
                 !state.StatusFlags.HasFlag(
                     CombatStatusFlags.Burrowed)) ||
                state.Behavior == CombatEnemyBehavior.Dead;
            if (!presentation.HasSnapshot || authoritativeRelocation)
            {
                presentation.Position = new(state.X, state.Y);
                presentation.Velocity = Vector2.Zero;
                presentation.HasSnapshot = false;
            }
        }

        _networkEnemyIdsByEntity[state.EntityId] = state.EnemyId;
        _networkEnemyEventTombstones.Remove(state.EntityId);
        _networkEnemyEventTombstoneExpiry.Remove(state.EntityId);
        _networkRetiringEnemyIds.Remove(state.EnemyId);
        ProjectNetworkEnemy(presentation);
        ReconcileNetworkCombatTargetPresentation();
    }

    private void RemoveNetworkEnemy(Guid enemyId)
    {
        if (!_networkEnemies.Remove(enemyId, out var presentation)) return;
        _networkEnemyIdsByEntity.Remove(presentation.State.EntityId);
        _networkEnemyEventTombstones[presentation.State.EntityId] =
            presentation;
        _networkEnemyEventTombstoneExpiry[presentation.State.EntityId] =
            _clock + Math.Max(3, SlimeSpriteRig.DeathAnimationSeconds);
        if (_enemyContextTargetId == enemyId) _enemyContextTargetId = null;
        ReconcileNetworkCombatTargetPresentation();

        var index = _enemies.FindIndex(value => value.Id == enemyId);
        if (index < 0) return;
        if (_enemies[index].Alive)
        {
            _enemies.RemoveAt(index);
            return;
        }

        // A reliable removal retires gameplay identity immediately. Keeping
        // the already-authorised dead pose briefly is presentation only.
        _networkRetiringEnemyIds.Add(enemyId);
    }

    private void ProjectNetworkEnemy(NetworkEnemyPresentation presentation)
    {
        var state = presentation.State;
        var index = _enemies.FindIndex(value => value.Id == state.EnemyId);
        var previous = index >= 0 ? _enemies[index] : null;
        var kind = NetworkEnemyKind(state.Archetype);
        var behavior = NetworkEnemyBehavior(state.Behavior);
        var position = presentation.Position;
        var destination = presentation.Velocity.LengthSquared > .0001f
            ? position + presentation.Velocity
            : new Vector2(state.X, state.Y);
        var visualAction = previous?.VisualAction ?? EntityAction.Idle;
        var visualActionStartedAt = previous?.VisualActionStartedAt ?? _clock;
        if (behavior == EnemyBehavior.Attack &&
            previous?.Behavior != EnemyBehavior.Attack)
        {
            visualAction = EntityAction.Attack;
            visualActionStartedAt = _clock;
        }
        else if (behavior == EnemyBehavior.Dead &&
                 previous?.Behavior != EnemyBehavior.Dead)
        {
            visualAction = EntityAction.Die;
            visualActionStartedAt = _clock;
        }

        var projected = new GameplayEnemyState(
            state.EnemyId,
            state.ParentEnemyId,
            kind,
            previous?.SpawnPosition ?? position,
            position,
            destination,
            state.WorldLevel,
            NetworkEnemyPower(state.Size),
            state.Health,
            Math.Max(1, state.MaximumHealth),
            behavior,
            state.TargetEntityId == 0
                ? null
                : state.TargetEntityId ==
                  _networkClient?.State.PlayerEntityId
                    ? "player"
                    : $"network:{state.TargetEntityId}",
            VisualAction: visualAction,
            VisualActionStartedAt: visualActionStartedAt,
            SizeScale: NetworkEnemySizeScale(state.Size),
            SplitGeneration: state.ParentEnemyId == Guid.Empty ? 0 : 1);
        if (index >= 0) _enemies[index] = projected;
        else _enemies.Add(projected);
    }

    private void ApplyNetworkEnemySnapshot(
        EntitySnapshot snapshot, float elapsed)
    {
        _ = elapsed;
        if (!_networkEnemyIdsByEntity.TryGetValue(
                snapshot.EntityId, out var enemyId) ||
            !_networkEnemies.TryGetValue(enemyId, out var presentation))
            return;
        var position = new Vector2(snapshot.X, snapshot.Y);
        presentation.Position = position;
        presentation.Velocity = new(
            snapshot.VelocityX, snapshot.VelocityY);
        presentation.HasSnapshot = true;
        var index = _enemies.FindIndex(value => value.Id == enemyId);
        if (index < 0)
        {
            ProjectNetworkEnemy(presentation);
            return;
        }
        var previous = _enemies[index];
        _enemies[index] = previous with
        {
            Position = position,
            Destination = presentation.Velocity.LengthSquared > .0001f
                ? position + presentation.Velocity
                : previous.Destination
        };
    }

    private void UpdateNetworkCombatPresentation(float elapsed)
    {
        _slimeAttackEffects.Update(elapsed);
        foreach (var entityId in _networkEnemyEventTombstoneExpiry
                     .Where(pair => pair.Value <= _clock)
                     .Select(pair => pair.Key).ToArray())
        {
            _networkEnemyEventTombstoneExpiry.Remove(entityId);
            _networkEnemyEventTombstones.Remove(entityId);
        }
        foreach (var id in _networkRetiringEnemyIds.ToArray())
        {
            var index = _enemies.FindIndex(value => value.Id == id);
            if (index < 0)
            {
                _networkRetiringEnemyIds.Remove(id);
                continue;
            }
            var enemy = _enemies[index];
            if (!SlimeSpriteRig.DeathAnimationComplete(
                    _clock - enemy.VisualActionStartedAt)) continue;
            _enemies.RemoveAt(index);
            _networkRetiringEnemyIds.Remove(id);
            foreach (var entityId in _networkEnemyEventTombstones
                         .Where(pair => pair.Value.State.EnemyId == id)
                         .Select(pair => pair.Key).ToArray())
                _networkEnemyEventTombstones.Remove(entityId);
            foreach (var entityId in _networkEnemyEventTombstoneExpiry
                         .Where(pair =>
                             !_networkEnemyEventTombstones.ContainsKey(
                                 pair.Key))
                         .Select(pair => pair.Key).ToArray())
                _networkEnemyEventTombstoneExpiry.Remove(entityId);
        }
    }

    private bool NetworkEnemyHasStatus(
        Guid enemyId, CombatStatusFlags status) =>
        _networkEnemies.TryGetValue(enemyId, out var presentation) &&
        presentation.PresentedStatusFlags.HasFlag(status);

    private void SendNetworkCombatTarget(Guid enemyId)
    {
        if (_networkClient?.TryGetEnemyReference(
                enemyId, out var reference) != true)
        {
            _chatUi.AddMessage(
                "That enemy is no longer available.",
                ChatMessageStyle.Warning);
            return;
        }
        PrepareNetworkCombatInteraction();
        var target = _networkEnemies.TryGetValue(enemyId, out var enemy)
            ? enemy.Position
            : NetworkActionPosition;
        _enemyCombatPathTarget = target;
        _enemyCombatRepathAt =
            _clock + MeleeCombatService.MovingTargetRepathSeconds;
        SendNetworkWalk(
            WorldActionReach.StandOff(
                NetworkActionPosition, target, WorldActionReach.Melee),
            preserveCombatAction: true);
        SendNetworkCombatCommand(
            new SetCombatTargetAction(reference),
            new(CombatActionKind.SetTarget, enemyId));
    }

    private void CancelNetworkCombatTarget(bool sendWhenIdle = false)
    {
        var hasPendingTarget = _networkCombatCommands.Values.Any(value =>
            value.Action == CombatActionKind.SetTarget);
        if (_networkCombatCommands.Values.Any(value =>
                value.Action == CombatActionKind.Cancel)) return;
        if (!sendWhenIdle && _combatEnemyId is null && !hasPendingTarget)
            return;
        SendNetworkCombatCommand(
            new CancelCombatAction(),
            new(CombatActionKind.Cancel));
    }

    private void SendNetworkCombatStance(MeleeCombatStance stance) =>
        SendNetworkCombatCommand(
            new SetCombatStanceAction(ToNetworkCombatStance(stance)),
            new(CombatActionKind.SetStance));

    private void PrepareNetworkCombatInteraction()
    {
        _pendingNetworkWorldAction = null;
        StopNetworkRepeatedConstruction();
        CancelNetworkCaveInteraction();
        CancelNetworkResourceInteraction();
        CancelNetworkFishingPresentation();
        ReleaseNetworkCookingPresentation();
        _moveMarker = null;
    }

    private void SendNetworkRespawn()
    {
        if (_networkPlayerLifeState != CombatLifeState.Dead ||
            _networkCombatCommands.Values.Any(value =>
                value.Action == CombatActionKind.Respawn)) return;
        SendNetworkCombatCommand(
            new RespawnAction(), new(CombatActionKind.Respawn));
    }

    private void SendNetworkCombatCommand(
        CombatActionPayload payload,
        PendingNetworkCombatCommand pending)
    {
        if (_networkClient?.IsConnected != true)
        {
            SendNetworkAction(payload);
            return;
        }
        var commandId = Guid.NewGuid();
        _networkCombatCommands[commandId] = pending;
        SendNetworkAction(payload, commandId);
    }

    private void HandleNetworkCombatActionResult(
        CombatActionResultMessage result)
    {
        if (!_networkCombatCommands.Remove(
                result.CommandId, out var pending)) return;
        if (!result.Accepted)
        {
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected the combat action " +
                      $"({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
            return;
        }

        switch (pending.Action)
        {
            case CombatActionKind.SetTarget:
                ReconcileNetworkCombatTargetPresentation();
                if (_networkAuthoritativeCombatTargetId != pending.EnemyId ||
                    !_networkEnemies.TryGetValue(
                        pending.EnemyId, out var target))
                {
                    break;
                }
                AnnounceCombatTarget(
                    true,
                    EnemyDisplayName(NetworkEnemyKind(
                        target.State.Archetype)).ToLowerInvariant(),
                    ChatMessageStyle.Warning);
                break;
            case CombatActionKind.Cancel:
                ReconcileNetworkCombatTargetPresentation();
                break;
        }
    }

    private void ReconcileNetworkCombatTargetPresentation()
    {
        if (_networkAuthoritativeCombatTargetId is not { } targetId ||
            !_networkEnemies.TryGetValue(targetId, out var target) ||
            target.State.Health <= 0 ||
            target.State.Behavior == CombatEnemyBehavior.Dead)
        {
            if (_combatEnemyId is not null)
                ClearNetworkCombatTargetPresentation();
            return;
        }
        if (_combatEnemyId == targetId) return;
        _combatTargetId = null;
        _combatVillagerId = null;
        _combatEnemyId = targetId;
    }

    private void ClearNetworkCombatTargetPresentation()
    {
        _combatTargetId = null;
        _combatVillagerId = null;
        _combatEnemyId = null;
        if (_player?.Action == EntityAction.Attack) _player.Stop();
    }

    private void UpdateNetworkMeleePresentation()
    {
        if (_player is null ||
            _combatEnemyId is not { } enemyId ||
            !_networkEnemies.TryGetValue(enemyId, out var enemy) ||
            enemy.State.Health <= 0 ||
            enemy.State.Behavior == CombatEnemyBehavior.Dead)
            return;
        var target = enemy.Position;
        if (Vector2.Distance(_player.Position, target) >
            WorldActionReach.Melee + .22f)
        {
            if (MeleeCombatService.ShouldRequestMovingTargetPath(
                    false,
                    _clock,
                    _enemyCombatRepathAt,
                    (System.Numerics.Vector2)_enemyCombatPathTarget,
                    (System.Numerics.Vector2)target))
            {
                _enemyCombatRepathAt = _clock +
                    MeleeCombatService.MovingTargetRepathSeconds;
                _enemyCombatPathTarget = target;
                SendNetworkWalk(
                    WorldActionReach.StandOff(
                        NetworkActionPosition, target, WorldActionReach.Melee),
                    preserveCombatAction: true);
            }
            return;
        }

        var impactDelay = MeleeImpactDelay();
        if (_swingStartedForAttackAt != _nextMeleeAttackAt &&
            _clock < _nextMeleeAttackAt - impactDelay)
        {
            if (_player.Action == EntityAction.Attack &&
                _clock >= _meleeReturnToIdleAt)
                _player.Stop();
            return;
        }
        if (_clock >= _nextMeleeAttackAt - impactDelay &&
            _swingStartedForAttackAt != _nextMeleeAttackAt)
            BeginNetworkMeleeSwing(target);
        else
            _player.AttackAt(target);
        if (_clock < _nextMeleeAttackAt) return;
        _nextMeleeAttackAt = _clock + MeleeCombatService.AttackIntervalSeconds;
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();
    }

    private void BeginNetworkMeleeSwing(Vector2 target)
    {
        _player!.RestartAttackAt(target);
        _swingStartedForAttackAt = _nextMeleeAttackAt;
        SendNetworkPresentSkill(
            EntityAction.Attack,
            (float)(MeleeImpactDelay() + MeleeRecoveryDelay()));
    }

    private void ApplyNetworkCombatPlayerState(
        NetworkPlayerGameplayState state)
    {
        _networkPlayerMaximumHealth = Math.Max(1, state.MaximumHealth);
        _networkPlayerCombatStatus = state.CombatStatusFlags;
        _networkAuthoritativeCombatTargetId = state.CombatTargetEnemyId;
        var wasDefeated = _playerDefeated;
        _networkPlayerLifeState = state.LifeState;
        var defeated = state.LifeState == CombatLifeState.Dead;
        _playerDefeated = defeated;
        if (defeated)
        {
            ClearNetworkCombatTargetPresentation();
            _player?.Die();
            _deathMessage = state.RespawnTick > _networkWorldClockTick
                ? "The server is preparing your respawn."
                : "Choose respawn when you are ready.";
            _deathOverlayAt = _clock + DeathAnimationSeconds();
            if (!wasDefeated)
                _chatUi.AddMessage(
                    "You have been defeated.",
                    ChatMessageStyle.Warning);
            return;
        }

        ReconcileNetworkCombatTargetPresentation();
        if (!wasDefeated) return;
        _modalScreen.Close(ModalScreenKind.Death);
        if (_player?.Action == EntityAction.Die) _player.Stop();
        _chatUi.AddMessage(
            "You return to the world.", ChatMessageStyle.Action);
    }

    private void HandleNetworkCombatEvents(
        IReadOnlyList<CombatEvent> events)
    {
        if (!IsNetworkWorld) return;
        foreach (var combatEvent in events)
            PresentNetworkCombatEvent(combatEvent);
    }

    private void PresentNetworkCombatEvent(CombatEvent combatEvent)
    {
        if (combatEvent.WorldLevel != _activeWorldLevel) return;
        switch (combatEvent.Kind)
        {
            case CombatEventKind.AttackStarted:
                PresentNetworkAttack(combatEvent);
                PresentNetworkMiss(combatEvent);
                break;
            case CombatEventKind.Damage:
                PresentNetworkDamage(combatEvent);
                break;
            case CombatEventKind.StatusApplied:
                PresentNetworkStatus(combatEvent, applied: true);
                break;
            case CombatEventKind.StatusExpired:
                PresentNetworkStatus(combatEvent, applied: false);
                break;
            case CombatEventKind.Death:
                PresentNetworkDeath(combatEvent);
                break;
            case CombatEventKind.Split:
                PresentNetworkSplit(combatEvent);
                break;
            case CombatEventKind.LootDropped:
                _chatUi.AddMessage(
                    "The slime leaves loot behind.",
                    ChatMessageStyle.Action);
                break;
        }
    }

    private void PresentNetworkAttack(CombatEvent combatEvent)
    {
        var targetPosition = NetworkCombatEntityPosition(
            combatEvent.TargetEntityId,
            new(combatEvent.X, combatEvent.Y));
        if (TryGetNetworkEnemy(
                combatEvent.SourceEntityId, out var enemy))
        {
            SetNetworkEnemyVisualAction(
                enemy.State.EnemyId, EntityAction.Attack);
            var sourceWorld = EnemyEffectWorld(enemy.Position) +
                              new Vector2(0, -7);
            var targetWorld = EnemyEffectWorld(targetPosition) +
                              new Vector2(0, -18);
            _slimeAttackEffects.Burst(
                NetworkEnemyKind(enemy.State.Archetype),
                sourceWorld, targetWorld,
                unchecked((int)combatEvent.EventOrdinal));
            PlaySlimeAttackSound(
                NetworkEnemyKind(enemy.State.Archetype));
            return;
        }

        if (combatEvent.SourceEntityId ==
            _networkClient?.State.PlayerEntityId)
        {
            if (_player?.Action != EntityAction.Attack)
                _player?.RestartAttackAt(targetPosition);
        }
        else if (_networkActors.TryGetValue(
                     combatEvent.SourceEntityId, out var actor) &&
                 actor.Action != EntityAction.Attack)
            actor.RestartAttackAt(targetPosition);
    }

    private void PresentNetworkDamage(CombatEvent combatEvent)
    {
        // A combat resolution is represented by exactly one event: misses use
        // AttackStarted and hits use Damage. Present the authored swing for a
        // hit before showing its impact; this never resolves damage locally.
        PresentNetworkAttack(combatEvent);
        var hit = combatEvent.Amount > 0;
        if (TryGetNetworkEnemy(
                combatEvent.TargetEntityId, out var enemy))
        {
            ShowEntityImpact(
                EnemyFeedbackKey(enemy.State.EnemyId),
                Math.Max(0, combatEvent.Amount), hit);
            return;
        }
        if (combatEvent.TargetEntityId ==
                _networkClient?.State.PlayerEntityId &&
            _activePlayer is not null)
        {
            ShowEntityImpact(
                PlayerFeedbackKey(_activePlayer.Id),
                Math.Max(0, combatEvent.Amount), hit);
            if (hit) InterruptOpenItemContainer();
        }
    }

    private void PresentNetworkMiss(CombatEvent combatEvent)
    {
        if (TryGetNetworkEnemy(
                combatEvent.TargetEntityId, out var enemy))
        {
            ShowEntityImpact(
                EnemyFeedbackKey(enemy.State.EnemyId), 0, false);
            return;
        }
        if (combatEvent.TargetEntityId ==
                _networkClient?.State.PlayerEntityId &&
            _activePlayer is not null)
            ShowEntityImpact(
                PlayerFeedbackKey(_activePlayer.Id), 0, false);
    }

    private void PresentNetworkStatus(
        CombatEvent combatEvent, bool applied)
    {
        var flag = NetworkCombatStatusFlag(combatEvent.StatusEffect);
        if (TryGetNetworkEnemy(
                combatEvent.TargetEntityId, out var enemy))
        {
            if (applied) enemy.PresentedStatusFlags |= flag;
            else enemy.PresentedStatusFlags &= ~flag;
            ProjectNetworkEnemy(enemy);
            return;
        }
        if (!applied || combatEvent.TargetEntityId !=
            _networkClient?.State.PlayerEntityId) return;
        var message = combatEvent.StatusEffect switch
        {
            CombatStatusEffect.Slow =>
                "The splash drenches you and slows your steps!",
            CombatStatusEffect.Root =>
                "Vines coil around your feet and hold you fast!",
            CombatStatusEffect.Poison =>
                "Venomous slime burns through your blood!",
            _ => null
        };
        if (message is not null)
            _chatUi.AddMessage(message, ChatMessageStyle.Warning);
    }

    private void PresentNetworkDeath(CombatEvent combatEvent)
    {
        if (!TryGetNetworkEnemy(
                combatEvent.TargetEntityId, out var enemy) &&
            !TryGetNetworkEnemy(
                combatEvent.SourceEntityId, out enemy)) return;
        var index = _enemies.FindIndex(value =>
            value.Id == enemy.State.EnemyId);
        if (index < 0)
        {
            ProjectNetworkEnemy(enemy);
            index = _enemies.FindIndex(value =>
                value.Id == enemy.State.EnemyId);
        }
        if (index >= 0)
            _enemies[index] = _enemies[index] with
            {
                Health = 0,
                Behavior = EnemyBehavior.Dead,
                TargetId = null,
                VisualAction = EntityAction.Die,
                VisualActionStartedAt = _clock
            };
        if (_combatEnemyId == enemy.State.EnemyId)
            _combatEnemyId = null;
        _chatUi.AddMessage(
            $"The {EnemyDisplayName(NetworkEnemyKind(
                enemy.State.Archetype)).ToLowerInvariant()} dissolves.",
            ChatMessageStyle.Action);
    }

    private void PresentNetworkSplit(CombatEvent combatEvent)
    {
        var parentEntityId = combatEvent.SourceEntityId != 0
            ? combatEvent.SourceEntityId
            : combatEvent.TargetEntityId;
        if (!_networkPresentedSplitSources.Add(
                parentEntityId) ||
            !TryGetNetworkEnemy(
                parentEntityId, out var enemy)) return;
        var kind = NetworkEnemyKind(enemy.State.Archetype);
        _slimeAttackEffects.SplitBurst(
            kind,
            EnemyEffectWorld(new(combatEvent.X, combatEvent.Y)),
            unchecked((int)combatEvent.EventOrdinal));
        PlaySlimeSplitSound(kind);
        _chatUi.AddMessage(
            $"The large {EnemyDisplayName(kind).ToLowerInvariant()} " +
            "bursts into smaller slimes!",
            ChatMessageStyle.Warning);
    }

    private bool TryGetNetworkEnemy(
        ulong entityId, out NetworkEnemyPresentation presentation)
    {
        if (_networkEnemyIdsByEntity.TryGetValue(
                entityId, out var enemyId) &&
            _networkEnemies.TryGetValue(enemyId, out presentation!))
            return true;
        if (_networkEnemyEventTombstones.TryGetValue(
                entityId, out presentation!))
            return true;
        presentation = null!;
        return false;
    }

    private Vector2 NetworkCombatEntityPosition(
        ulong entityId, Vector2 fallback)
    {
        if (TryGetNetworkEnemy(entityId, out var enemy))
            return enemy.Position;
        if (entityId == _networkClient?.State.PlayerEntityId &&
            _player is not null)
            return _player.Position;
        return _networkActors.TryGetValue(entityId, out var actor)
            ? actor.Position
            : fallback;
    }

    private void SetNetworkEnemyVisualAction(
        Guid enemyId, EntityAction action)
    {
        var index = _enemies.FindIndex(value => value.Id == enemyId);
        if (index < 0) return;
        _enemies[index] = _enemies[index] with
        {
            VisualAction = action,
            VisualActionStartedAt = _clock
        };
    }

    private int ActivePlayerMaximumHealth() =>
        IsNetworkWorld
            ? Math.Max(1, _networkPlayerMaximumHealth)
            : _activePlayer is null
                ? AdventureService.BaseMaximumHealth
                : AdventureService.MaximumHealth(
                    _activePlayer.AdventureExperience);

    private static EnemyKind NetworkEnemyKind(
        CombatEnemyArchetype value) => value switch
    {
        CombatEnemyArchetype.WaterSlime => EnemyKind.WaterSlime,
        CombatEnemyArchetype.GrassSlime => EnemyKind.GrassSlime,
        CombatEnemyArchetype.SandSlime => EnemyKind.SandSlime,
        CombatEnemyArchetype.CaveSlime => EnemyKind.CaveSlime,
        _ => EnemyKind.WaterSlime
    };

    private static EnemyBehavior NetworkEnemyBehavior(
        CombatEnemyBehavior value) => value switch
    {
        CombatEnemyBehavior.Chasing => EnemyBehavior.Chase,
        CombatEnemyBehavior.Attacking => EnemyBehavior.Attack,
        CombatEnemyBehavior.Burrowed => EnemyBehavior.Idle,
        CombatEnemyBehavior.Dead => EnemyBehavior.Dead,
        _ => EnemyBehavior.Idle
    };

    private static int NetworkEnemyPower(CombatEnemySize value) =>
        value switch
        {
            CombatEnemySize.Large => 4,
            CombatEnemySize.Medium => 2,
            _ => 1
        };

    private static float NetworkEnemySizeScale(CombatEnemySize value) =>
        value switch
        {
            CombatEnemySize.Small => .68f,
            CombatEnemySize.Large => 1.28f,
            _ => 1
        };

    private static CombatStatusFlags NetworkCombatStatusFlag(
        CombatStatusEffect value) => value switch
    {
        CombatStatusEffect.Slow => CombatStatusFlags.Slowed,
        CombatStatusEffect.Root => CombatStatusFlags.Rooted,
        CombatStatusEffect.Poison => CombatStatusFlags.Poisoned,
        CombatStatusEffect.Hide => CombatStatusFlags.Hidden,
        CombatStatusEffect.Burrow => CombatStatusFlags.Burrowed,
        _ => CombatStatusFlags.None
    };

    private static CombatStance ToNetworkCombatStance(
        MeleeCombatStance value) => value switch
    {
        MeleeCombatStance.Aggressive => CombatStance.Aggressive,
        MeleeCombatStance.Defensive => CombatStance.Defensive,
        _ => CombatStance.Balanced
    };

    private static MeleeCombatStance FromNetworkCombatStance(
        CombatStance value) => value switch
    {
        CombatStance.Aggressive => MeleeCombatStance.Aggressive,
        CombatStance.Defensive => MeleeCombatStance.Defensive,
        _ => MeleeCombatStance.Accurate
    };
}
