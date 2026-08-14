using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Gameplay;
using IslandRpg.Navigation;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner, fixed-step authority for multiplayer slime combat. Reliable
/// revisions change only for semantic state; position and velocity still flow
/// through the normal high-frequency session snapshot stream.
/// </summary>
public sealed class AuthoritativeCombatTransactions
{
    internal const int DefaultRespawnDelayTicks = 300;

    private readonly long _worldSeed;
    private readonly IWorldNavigationQuery _navigation;
    private readonly AuthoritativeCombatOptions _options;
    private readonly Dictionary<EnemyId, MutableEnemy> _enemies = [];
    private ulong _nextEventOrdinal = 1;
    private uint _nextSpawnOrdinal = 1;
    private int? _ownerThreadId;

    public AuthoritativeCombatTransactions(
        long worldSeed,
        IWorldNavigationQuery? navigation = null,
        AuthoritativeCombatOptions? options = null)
    {
        _worldSeed = worldSeed;
        _navigation = navigation ?? OpenWorldNavigationQuery.Instance;
        _options = (options ?? new AuthoritativeCombatOptions()).ValidatedCopy();
    }

    public Vector2 RespawnPosition => _options.RespawnPosition;

    /// <summary>
    /// Applies a non-combat lethal state through the same canonical death
    /// transition and event stream used by combat attacks.
    /// </summary>
    public CombatActorMutation? ApplyEnvironmentalDeath(
        CombatActorInput actor,
        PlayerGameplaySnapshot gameplay,
        long tick,
        out CombatEventSnapshot? combatEvent)
    {
        EnsureOwner();
        combatEvent = null;
        if (actor.Gameplay.LifeState == ActorLifeState.Dead ||
            gameplay.Health > 0)
            return null;
        gameplay = DeathGameplay(
            gameplay, tick, _options.RespawnDelayTicks);
        combatEvent = CreateEvent(tick, CombatEventKind.ActorDied,
            actor.ActorId);
        return new CombatActorMutation(actor.ActorId, gameplay,
            ClearMovement: true);
    }

    public AuthoritativeEnemySnapshot Seed(AuthoritativeEnemySeed seed)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(seed);
        if (_enemies.Count >= _options.MaximumEnemies || seed.EnemyId.IsEmpty ||
            !Enum.IsDefined(seed.Kind) || seed.Revision == 0 ||
            !Finite(seed.Position) || !_navigation.SupportsWorldLevel(seed.WorldLevel) ||
            !_navigation.CanStandAt(seed.Position, seed.WorldLevel) ||
            seed.PowerLevel <= 0 || seed.Health < 0 || seed.MaximumHealth < 0 ||
            seed.SplitGeneration is < 0 or > SlimeCombatRules.MaximumSplitGeneration ||
            seed.ParentEnemyId is { IsEmpty: true } ||
            _enemies.ContainsKey(seed.EnemyId))
            throw new ArgumentException("The authoritative enemy seed is invalid.",
                nameof(seed));

        var maximumHealth = seed.MaximumHealth > 0
            ? seed.MaximumHealth
            : checked(16 + seed.PowerLevel * 4);
        var health = seed.Health > 0 ? seed.Health : maximumHealth;
        if (health > maximumHealth)
            throw new ArgumentException("Enemy health exceeds its maximum.",
                nameof(seed));
        var networkId = DeriveNetworkEntityId(seed.EnemyId);
        if (_enemies.Values.Any(value => value.NetworkEntityId == networkId))
            throw new ArgumentException("The enemy network identity is duplicated.",
                nameof(seed));
        var ordinal = seed.SpawnOrdinal == 0
            ? _nextSpawnOrdinal++
            : seed.SpawnOrdinal;
        if (seed.SpawnOrdinal > 0)
            _nextSpawnOrdinal = Math.Max(_nextSpawnOrdinal,
                checked(seed.SpawnOrdinal + 1));
        if (_enemies.Values.Any(value => value.SpawnOrdinal == ordinal))
            throw new ArgumentException("The enemy spawn ordinal is duplicated.",
                nameof(seed));
        var enemy = new MutableEnemy(
            seed.EnemyId, networkId, seed.Kind,
            seed.SpawnPosition ?? seed.Position, seed.Position,
            seed.WorldLevel, seed.PowerLevel, health, maximumHealth,
            SlimeCombatRules.SizeScale(seed.PowerLevel), seed.Revision,
            seed.ParentEnemyId, ordinal, seed.SplitGeneration);
        _enemies.Add(enemy.EnemyId, enemy);
        return enemy.ToSnapshot(default, 0);
    }

    public ImmutableArray<AuthoritativeEnemySnapshot> CaptureEnemies(
        IReadOnlyDictionary<ActorId, ulong>? actorNetworkIds = null,
        double now = 0) =>
        _enemies.Values
            .OrderBy(static value => value.EnemyId.Value)
            .Select(value => value.ToSnapshot(
                value.TargetActorId is { } target && actorNetworkIds is not null &&
                actorNetworkIds.TryGetValue(target, out var networkId)
                    ? target
                    : null,
                value.TargetActorId is { } id && actorNetworkIds is not null &&
                actorNetworkIds.TryGetValue(id, out var resolved)
                    ? resolved
                    : 0,
                now))
            .ToImmutableArray();

    /// <summary>
    /// Clears every enemy reference to an expired disconnected actor. This is
    /// an immediate semantic transition so an intervening checkpoint cannot
    /// retain a target that no longer exists in the session registry.
    /// </summary>
    public ImmutableArray<EnemyStateDelta> ForgetActor(ActorId actorId)
    {
        EnsureOwner();
        if (actorId.Value == Guid.Empty)
            throw new ArgumentException(
                "A valid actor identity is required.", nameof(actorId));
        var deltas = ImmutableArray.CreateBuilder<EnemyStateDelta>();
        foreach (var enemy in _enemies.Values
                     .Where(value => value.TargetActorId == actorId)
                     .OrderBy(static value => value.EnemyId.Value))
        {
            var previous = enemy.ToSnapshot(actorId, 0);
            enemy.TargetActorId = null;
            enemy.ReactionReadyTick = 0;
            enemy.BurrowEmergeTick = 0;
            if (enemy.Alive)
            {
                enemy.Behavior = EnemyBehavior.Return;
                enemy.Velocity = Vector2.Zero;
            }
            enemy.Revision = checked(enemy.Revision + 1);
            deltas.Add(new EnemyStateDelta(
                EnemyChangeKind.Updated,
                previous,
                enemy.ToSnapshot(null, 0)));
        }
        return deltas.ToImmutable();
    }

    public AuthoritativeEnemySnapshot CaptureEnemy(
        EnemyId enemyId,
        ulong targetNetworkEntityId = 0,
        double now = 0)
    {
        EnsureOwner();
        if (!_enemies.TryGetValue(enemyId, out var enemy))
            throw new KeyNotFoundException("The enemy does not exist.");
        return enemy.ToSnapshot(
            enemy.TargetActorId, targetNetworkEntityId, now);
    }

    public CombatTransactionResult SetTarget(
        CombatActorInput actor,
        WorldTransactionContext context,
        EnemyReference reference,
        long tick)
    {
        EnsureOwner();
        var validation = ValidateActor(actor, context);
        if (validation is not null) return validation;
        if (!reference.IsWellFormed ||
            !_enemies.TryGetValue(reference.EnemyId, out var enemy))
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.EnemyNotFound, "The enemy does not exist.");
        if (enemy.Revision != reference.ExpectedRevision)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.StaleEnemyRevision,
                "The enemy revision is stale.");
        if (!enemy.Alive)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.EnemyDead, "The enemy is already dead.");
        if (enemy.WorldLevel != actor.WorldLevel)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.WrongWorldLevel,
                "The actor and enemy are on different world levels.");

        // Choosing a target is actor-private intent. It does not change the
        // enemy semantic revision until the enemy itself acquires a target.
        var gameplay = actor.Gameplay with
        {
            CombatTargetEnemyId = enemy.EnemyId,
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        return Accepted(context, gameplay);
    }

    public CombatTransactionResult CancelTarget(
        CombatActorInput actor,
        WorldTransactionContext context,
        long tick)
    {
        EnsureOwner();
        var validation = ValidateActor(actor, context, allowDead: true);
        if (validation is not null) return validation;
        var gameplay = actor.Gameplay with
        {
            CombatTargetEnemyId = null,
            NextCombatAttackTick = 0,
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        return Accepted(context, gameplay,
            CreateEvent(tick, CombatEventKind.TargetCancelled, actor.ActorId));
    }

    public CombatTransactionResult SetStance(
        CombatActorInput actor,
        WorldTransactionContext context,
        MeleeCombatStance stance)
    {
        EnsureOwner();
        var validation = ValidateActor(actor, context, allowDead: true);
        if (validation is not null) return validation;
        if (!Enum.IsDefined(stance))
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.InvalidStance,
                "The combat stance is invalid.");
        var gameplay = actor.Gameplay with
        {
            CombatStance = stance,
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        return Accepted(context, gameplay);
    }

    public CombatTransactionResult Respawn(
        CombatActorInput actor,
        WorldTransactionContext context,
        long tick)
    {
        EnsureOwner();
        var validation = ValidateActor(actor, context, allowDead: true);
        if (validation is not null) return validation;
        if (actor.Gameplay.LifeState != ActorLifeState.Dead)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.ActorAlive, "The actor is already alive.");
        if (tick < actor.Gameplay.RespawnAvailableTick)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.RespawnLocked,
                "The respawn delay has not elapsed.");
        var gameplay = RespawnGameplay(actor.Gameplay);
        return Accepted(context, gameplay,
            CreateEvent(tick, CombatEventKind.ActorRespawned, actor.ActorId));
    }

    internal static PlayerGameplaySnapshot DeathGameplay(
        PlayerGameplaySnapshot gameplay,
        long tick,
        int respawnDelayTicks)
    {
        var respawnAvailableTick = checked(tick + respawnDelayTicks);
        var actorRevision = checked(gameplay.ActorRevision + 1);
        return gameplay with
        {
            Health = 0,
            LifeState = ActorLifeState.Dead,
            RespawnAvailableTick = respawnAvailableTick,
            CombatStatus = default,
            CombatTargetEnemyId = null,
            NextCombatAttackTick = 0,
            TimedHealingRemainingHealth = 0,
            TimedHealingRemainingSeconds = 0,
            TimedHealingFractionalHealth = 0,
            ActorRevision = actorRevision
        };
    }

    internal static PlayerGameplaySnapshot RespawnGameplay(
        PlayerGameplaySnapshot gameplay)
    {
        var maximumHealth = Math.Max(1, gameplay.MaximumHealth);
        var recoveryHealth = Math.Max(1, maximumHealth / 2);
        var actorRevision = checked(gameplay.ActorRevision + 1);
        return gameplay with
        {
            Health = recoveryHealth,
            Hunger = 25,
            WellFedSeconds = 0,
            LifeState = ActorLifeState.Alive,
            RespawnAvailableTick = 0,
            CombatStatus = default,
            CombatTargetEnemyId = null,
            NextCombatAttackTick = 0,
            ActorRevision = actorRevision
        };
    }

    /// <summary>
    /// Fixed-step deterministic enemy AI. Stable ID order is intentional: if
    /// multiple attackers kill one actor on the same step, exactly one death
    /// transition is committed and later attackers observe it.
    /// </summary>
    public CombatAdvanceResult Advance(
        double elapsedSeconds,
        long tick,
        IReadOnlyList<CombatActorInput> actors)
    {
        EnsureOwner();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        ArgumentNullException.ThrowIfNull(actors);
        var mutableActors = actors.ToDictionary(
            static value => value.ActorId,
            static value => new PendingActor(value));
        var deltas = ImmutableArray.CreateBuilder<EnemyStateDelta>();
        var events = ImmutableArray.CreateBuilder<CombatEventSnapshot>();
        var lootDrops = ImmutableArray.CreateBuilder<CombatLootDropRequest>();
        var now = tick * SimulationTiming.FixedDeltaSeconds;

        var retired = _enemies.Values
            .Where(value => !value.Alive && value.DeathRemovalTick > 0 &&
                            tick >= value.DeathRemovalTick)
            .OrderBy(static value => value.EnemyId.Value)
            .ToArray();
        foreach (var enemy in retired)
        {
            deltas.Add(new(EnemyChangeKind.Removed,
                enemy.ToSnapshot(null, 0), null));
            _enemies.Remove(enemy.EnemyId);
        }

        AdvanceStatuses(mutableActors, tick, now, events);

        var spawned = new List<MutableEnemy>();
        foreach (var actor in mutableActors.Values.OrderBy(static value =>
                     value.Input.ActorId.Value))
            AdvancePlayerCombat(actor, tick, now, elapsedSeconds, deltas,
                events, spawned, lootDrops);

        foreach (var child in spawned)
            _enemies.Add(child.EnemyId, child);

        foreach (var enemy in _enemies.Values.OrderBy(static value =>
                     value.EnemyId.Value))
        {
            if (!enemy.Alive) continue;
            var previous = enemy.ToSnapshot(enemy.TargetActorId,
                NetworkId(mutableActors, enemy.TargetActorId));
            var semanticChanged = false;
            if (!TryTarget(enemy, mutableActors, out var target))
            {
                if (enemy.TargetActorId is not null ||
                    enemy.Behavior is EnemyBehavior.Chase or EnemyBehavior.Attack)
                {
                    enemy.TargetActorId = null;
                    enemy.ReactionReadyTick = 0;
                    enemy.BurrowEmergeTick = 0;
                    enemy.Behavior = EnemyBehavior.Return;
                    semanticChanged = true;
                }
                MoveTowards(enemy, enemy.SpawnPosition, elapsedSeconds);
                if (Vector2.DistanceSquared(enemy.Position,
                        enemy.SpawnPosition) <= .01f)
                {
                    enemy.Position = enemy.SpawnPosition;
                    enemy.Velocity = Vector2.Zero;
                    if (enemy.Behavior != EnemyBehavior.Idle)
                    {
                        enemy.Behavior = EnemyBehavior.Idle;
                        semanticChanged = true;
                    }
                }
            }
            else
            {
                if (enemy.TargetActorId != target.Input.ActorId)
                {
                    enemy.TargetActorId = target.Input.ActorId;
                    enemy.ReactionReadyTick = checked(tick +
                        ReactionTicks(enemy.Kind));
                    if (SlimeCombatRules.UsesAggroBurrow(enemy.Kind))
                        enemy.BurrowEmergeTick = enemy.ReactionReadyTick;
                    semanticChanged = true;
                }
                if (enemy.BurrowEmergeTick > tick ||
                    tick < enemy.ReactionReadyTick)
                {
                    enemy.Velocity = Vector2.Zero;
                    if (enemy.Behavior != EnemyBehavior.Chase)
                    {
                        enemy.Behavior = EnemyBehavior.Chase;
                        semanticChanged = true;
                    }
                    if (semanticChanged)
                    {
                        enemy.Revision = checked(enemy.Revision + 1);
                        deltas.Add(new(EnemyChangeKind.Updated, previous,
                            enemy.ToSnapshot(enemy.TargetActorId,
                                NetworkId(mutableActors,
                                    enemy.TargetActorId))));
                    }
                    continue;
                }
                if (enemy.BurrowEmergeTick > 0)
                {
                    enemy.Position = ResolveBurrowEmergence(
                        enemy, target.Input.Position);
                    enemy.BurrowEmergeTick = 0;
                    enemy.ReactionReadyTick = tick;
                    semanticChanged = true;
                }
                var distanceSquared = Vector2.DistanceSquared(
                    enemy.Position, target.Input.Position);
                if (distanceSquared <=
                    _options.EnemyAttackRange * _options.EnemyAttackRange)
                {
                    enemy.Velocity = Vector2.Zero;
                    if (enemy.Behavior != EnemyBehavior.Attack)
                    {
                        enemy.Behavior = EnemyBehavior.Attack;
                        semanticChanged = true;
                    }
                    if (tick >= enemy.NextAttackTick)
                    {
                        ResolveEnemyAttack(enemy, target, tick, now, events);
                        semanticChanged = true;
                    }
                }
                else
                {
                    if (enemy.Behavior != EnemyBehavior.Chase)
                    {
                        enemy.Behavior = EnemyBehavior.Chase;
                        semanticChanged = true;
                    }
                    MoveTowards(enemy, target.Input.Position, elapsedSeconds);
                }
            }

            if (semanticChanged)
            {
                enemy.Revision = checked(enemy.Revision + 1);
                deltas.Add(new(EnemyChangeKind.Updated, previous,
                    enemy.ToSnapshot(enemy.TargetActorId,
                        NetworkId(mutableActors, enemy.TargetActorId))));
            }
        }

        var mutations = mutableActors.Values
            .Where(static value => value.Changed)
            .OrderBy(static value => value.Input.ActorId.Value)
            .Select(static value => new CombatActorMutation(
                value.Input.ActorId, value.Gameplay,
                value.Position == value.Input.Position ? null : value.Position,
                ClearMovement: value.Gameplay.LifeState == ActorLifeState.Dead))
            .ToImmutableArray();
        return new(deltas.ToImmutable(), events.ToImmutable(), mutations,
            lootDrops.ToImmutable());
    }

    public AuthoritativeCombatCheckpoint CaptureCheckpoint()
    {
        EnsureOwner();
        return new(_worldSeed, _nextEventOrdinal, _nextSpawnOrdinal,
            _enemies.Values.OrderBy(static value => value.EnemyId.Value)
            .Select(static value => value.ToCheckpoint())
                .ToImmutableArray());
    }

    public void ValidateCheckpoint(AuthoritativeCombatCheckpoint checkpoint)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.WorldSeed != _worldSeed || checkpoint.NextEventOrdinal == 0 ||
            checkpoint.NextSpawnOrdinal == 0 || checkpoint.Enemies.IsDefault ||
            checkpoint.Enemies.Length > _options.MaximumEnemies)
            throw new InvalidDataException("The combat checkpoint is invalid.");
        var ids = new HashSet<EnemyId>();
        var networkIds = new HashSet<ulong>();
        var ordinals = new HashSet<uint>();
        foreach (var value in checkpoint.Enemies)
        {
            if (value.EnemyId.IsEmpty || !ids.Add(value.EnemyId) ||
                !networkIds.Add(DeriveNetworkEntityId(value.EnemyId)) ||
                value.Revision == 0 || !Enum.IsDefined(value.Kind) ||
                !Enum.IsDefined(value.Behavior) || !Finite(value.SpawnPosition) ||
                !Finite(value.Position) || !Finite(value.Velocity) ||
                !_navigation.SupportsWorldLevel(value.WorldLevel) ||
                value.PowerLevel <= 0 || value.MaximumHealth <= 0 ||
                value.Health is < 0 || value.Health > value.MaximumHealth ||
                !float.IsFinite(value.SizeScale) || value.SizeScale <= 0 ||
                value.SpawnOrdinal == 0 || !ordinals.Add(value.SpawnOrdinal) ||
                value.SpawnOrdinal >= checkpoint.NextSpawnOrdinal ||
                value.NextAttackTick < 0 ||
                value.DeathRemovalTick < 0 ||
                value.ReactionReadyTick < 0 ||
                value.BurrowEmergeTick < 0 ||
                value.BurrowEmergeTick > 0 &&
                value.Kind != EnemyKind.SandSlime ||
                value.BurrowEmergeTick > value.ReactionReadyTick ||
                value.SplitGeneration is < 0 or > SlimeCombatRules.MaximumSplitGeneration ||
                value.ParentEnemyId is { IsEmpty: true } ||
                !ValidStatus(value.Status) ||
                (value.Health == 0) != (value.Behavior == EnemyBehavior.Dead) ||
                (value.Health == 0) != (value.DeathRemovalTick > 0))
                throw new InvalidDataException("The combat checkpoint has an invalid enemy.");
        }
    }

    public void RestoreCheckpoint(AuthoritativeCombatCheckpoint checkpoint)
    {
        EnsureOwner();
        if (_enemies.Count != 0 || _nextEventOrdinal != 1 || _nextSpawnOrdinal != 1)
            throw new InvalidOperationException(
                "A combat checkpoint can restore only a pristine aggregate.");
        ValidateCheckpoint(checkpoint);
        foreach (var value in checkpoint.Enemies)
        {
            var enemy = MutableEnemy.FromCheckpoint(value,
                DeriveNetworkEntityId(value.EnemyId));
            _enemies.Add(enemy.EnemyId, enemy);
        }
        _nextEventOrdinal = checkpoint.NextEventOrdinal;
        _nextSpawnOrdinal = checkpoint.NextSpawnOrdinal;
    }

    public static ulong DeriveNetworkEntityId(EnemyId enemyId)
    {
        if (enemyId.IsEmpty)
            throw new ArgumentException("An enemy identity is required.",
                nameof(enemyId));
        Span<byte> input = stackalloc byte[32];
        input.Clear();
        "IRPG-ENEMY-ENTITY"u8.CopyTo(input);
        enemyId.Value.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        // Enemy transport identities use the 01 high-bit namespace. Actors
        // use 00 and boats use 10, so independently derived IDs cannot alias.
        return (BinaryPrimitives.ReadUInt64BigEndian(digest) &
                ~(3UL << 62)) | (1UL << 62);
    }

    private void AdvanceStatuses(
        Dictionary<ActorId, PendingActor> actors,
        long tick,
        double now,
        ImmutableArray<CombatEventSnapshot>.Builder events)
    {
        foreach (var actor in actors.Values.OrderBy(static value =>
                     value.Input.ActorId.Value))
        {
            // A durable actor can remain in the session registry while its
            // client is disconnected (and every restored actor starts in
            // that state). Absolute status deadlines remain durable, but no
            // combat mutation is applied until the owner reconnects.
            if (!actor.Input.Connected ||
                actor.Gameplay.LifeState == ActorLifeState.Dead)
                continue;
            var advance = SlimeCombatRules.Advance(actor.Gameplay.CombatStatus, now);
            if (advance.Status != actor.Gameplay.CombatStatus ||
                advance.PoisonDamage > 0)
            {
                actor.Gameplay = actor.Gameplay with
                {
                    CombatStatus = advance.Status,
                    Health = Math.Max(0,
                        actor.Gameplay.Health - advance.PoisonDamage),
                    ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
                };
                actor.Changed = true;
                if (advance.SlowExpired)
                    events.Add(CreateEvent(tick, CombatEventKind.StatusExpired,
                        actor.Input.ActorId, status: SlimeStatusKind.Slow));
                if (advance.RootExpired)
                    events.Add(CreateEvent(tick, CombatEventKind.StatusExpired,
                        actor.Input.ActorId, status: SlimeStatusKind.Root));
                if (advance.PoisonExpired)
                    events.Add(CreateEvent(tick, CombatEventKind.StatusExpired,
                        actor.Input.ActorId, status: SlimeStatusKind.Poison));
                if (advance.PoisonDamage > 0)
                    events.Add(CreateEvent(tick, CombatEventKind.EnemyAttacked,
                        actor.Input.ActorId, damage: advance.PoisonDamage,
                        hit: true, status: SlimeStatusKind.Poison));
                MarkDead(actor, tick, events);
            }
        }
    }

    private void AdvancePlayerCombat(
        PendingActor actor,
        long tick,
        double now,
        double elapsedSeconds,
        ImmutableArray<EnemyStateDelta>.Builder deltas,
        ImmutableArray<CombatEventSnapshot>.Builder events,
        List<MutableEnemy> spawned,
        ImmutableArray<CombatLootDropRequest>.Builder lootDrops)
    {
        if (actor.Gameplay.CombatTargetEnemyId is not { } targetId)
            return;
        if (!_enemies.TryGetValue(targetId, out var enemy) || !enemy.Alive ||
            enemy.WorldLevel != actor.Input.WorldLevel)
        {
            actor.Gameplay = actor.Gameplay with
            {
                CombatTargetEnemyId = null,
                NextCombatAttackTick = 0,
                ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
            };
            actor.Changed = true;
            // Target cancellation is presentation-only and disconnected
            // actors have no observer. Still repair their durable state before
            // the connectivity gate so a retired corpse cannot leave an
            // unrestorable actor foreign key behind.
            if (actor.Input.Connected)
                events.Add(CreateEvent(tick, CombatEventKind.TargetCancelled,
                    actor.Input.ActorId, targetId));
            return;
        }
        if (!actor.Input.Connected ||
            actor.Gameplay.LifeState != ActorLifeState.Alive ||
            actor.Gameplay.Health <= 0)
            return;

        var distanceSquared = Vector2.DistanceSquared(actor.Position,
            enemy.Position);
        if (distanceSquared >
            _options.PlayerAttackRange * _options.PlayerAttackRange)
        {
            MoveActorTowards(actor, enemy.Position, now, elapsedSeconds);
            return;
        }
        if (tick < actor.Gameplay.NextCombatAttackTick) return;

        var previous = enemy.ToSnapshot(enemy.TargetActorId,
            enemy.TargetActorId == actor.Input.ActorId
                ? actor.Input.NetworkEntityId
                : 0);
        var attackSequence = checked(actor.Gameplay.CombatAttackSequence + 1);
        var inventory = actor.Gameplay.Inventory.Slots
            .OrderBy(static value => value.Slot)
            .Select(static value => value.ItemId)
            .ToArray();
        var resolution = CombatResolver.Resolve(new CombatAttackRequest(
            new CombatRollKey(_worldSeed, actor.Input.ActorId.Value,
                enemy.EnemyId.Value, attackSequence),
            new CombatProgression(
                actor.Gameplay.AttackExperience,
                actor.Gameplay.StrengthExperience,
                actor.Gameplay.DefenceExperience),
            actor.Gameplay.CombatStance,
            inventory));
        var adventure = AdventureService.AwardFromAction(
            actor.Gameplay.AdventureExperience,
            resolution.ExperienceGained);
        var maximumHealth = AdventureService.MaximumHealth(adventure.Experience);
        actor.Gameplay = actor.Gameplay with
        {
            AttackExperience = resolution.Progression.AttackExperience,
            StrengthExperience = resolution.Progression.StrengthExperience,
            DefenceExperience = resolution.Progression.DefenceExperience,
            AdventureExperience = adventure.Experience,
            MaximumHealth = maximumHealth,
            Health = Math.Min(maximumHealth, actor.Gameplay.Health),
            CombatAttackSequence = attackSequence,
            NextCombatAttackTick = checked(tick +
                _options.PlayerAttackIntervalTicks),
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        actor.Changed = true;
        if (resolution.Attack.Hit)
            enemy.Health = EnemyCombatRules.ApplyDamage(
                enemy.Health, resolution.Attack.Damage);
        if (enemy.TargetActorId is null)
        {
            enemy.TargetActorId = actor.Input.ActorId;
            enemy.ReactionReadyTick = checked(tick +
                ReactionTicks(enemy.Kind));
            if (SlimeCombatRules.UsesAggroBurrow(enemy.Kind))
                enemy.BurrowEmergeTick = enemy.ReactionReadyTick;
        }
        if (enemy.Health <= 0)
            KillEnemy(enemy, actor, tick, deltas, events, spawned, lootDrops,
                previous);
        else
        {
            enemy.Behavior = EnemyBehavior.Chase;
            enemy.Revision = checked(enemy.Revision + 1);
            deltas.Add(new(EnemyChangeKind.Updated, previous,
                enemy.ToSnapshot(enemy.TargetActorId,
                    enemy.TargetActorId == actor.Input.ActorId
                        ? actor.Input.NetworkEntityId
                        : 0)));
        }
        events.Add(CreateEvent(tick, CombatEventKind.PlayerAttacked,
            actor.Input.ActorId, enemy.EnemyId, resolution.Attack.Damage,
            resolution.Attack.Hit));
    }

    private void KillEnemy(
        MutableEnemy enemy,
        PendingActor actor,
        long tick,
        ImmutableArray<EnemyStateDelta>.Builder deltas,
        ImmutableArray<CombatEventSnapshot>.Builder events,
        List<MutableEnemy> spawned,
        ImmutableArray<CombatLootDropRequest>.Builder lootDrops,
        AuthoritativeEnemySnapshot previous)
    {
        enemy.Health = 0;
        enemy.Velocity = Vector2.Zero;
        enemy.TargetActorId = null;
        enemy.Behavior = EnemyBehavior.Dead;
        enemy.DeathRemovalTick = checked(tick + _options.DeathRetentionTicks);
        enemy.Revision = checked(enemy.Revision + 1);
        deltas.Add(new(EnemyChangeKind.Updated, previous,
            enemy.ToSnapshot(null, 0)));
        actor.Gameplay = actor.Gameplay with
        {
            CombatTargetEnemyId = null,
            NextCombatAttackTick = 0,
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        actor.Changed = true;
        events.Add(CreateEvent(tick, CombatEventKind.EnemyDied,
            actor.Input.ActorId, enemy.EnemyId));

        var drops = SlimeCombatRules.RollLoot(new SlimeLootSource(
                _worldSeed, enemy.EnemyId.Value, enemy.Kind, enemy.PowerLevel))
            .Select(static value => new CombatLootSnapshot(
                value.ItemId, value.Quantity))
            .ToImmutableArray();
        events.Add(CreateEvent(tick, CombatEventKind.LootRolled,
            actor.Input.ActorId, enemy.EnemyId, loot: drops));
        lootDrops.Add(new(
            DeterministicEnemyRandom.StableGuid(
                _worldSeed, enemy.EnemyId.Value, 0,
                0x4C4F4F5442414720UL),
            enemy.EnemyId,
            actor.Input.ActorId,
            enemy.Position,
            enemy.WorldLevel,
            drops));

        if (_enemies.Count + spawned.Count + 2 > _options.MaximumEnemies)
            return;
        var children = SlimeCombatRules.Split(new SlimeSplitSource(
            enemy.EnemyId.Value, enemy.EnemyId.Value, enemy.Kind,
            enemy.SpawnPosition, enemy.Position, enemy.WorldLevel,
            enemy.PowerLevel, enemy.MaximumHealth, enemy.SizeScale,
            enemy.SplitGeneration), _worldSeed);
        if (children.Length == 0) return;
        var childIds = ImmutableArray.CreateBuilder<EnemyId>(children.Length);
        foreach (var child in children)
        {
            var childId = new EnemyId(child.EnemyId);
            if (_enemies.ContainsKey(childId) ||
                spawned.Any(value => value.EnemyId == childId))
                throw new InvalidOperationException(
                    "The deterministic slime split produced a duplicate identity.");
            var mutable = new MutableEnemy(
                childId, DeriveNetworkEntityId(childId), child.Kind,
                child.SpawnPosition, child.Position, child.WorldLevel,
                child.PowerLevel, child.Health, child.Health,
                child.SizeScale, 1, enemy.EnemyId, _nextSpawnOrdinal++,
                child.SplitGeneration);
            spawned.Add(mutable);
            childIds.Add(childId);
            deltas.Add(new(EnemyChangeKind.Added, null,
                mutable.ToSnapshot(null, 0)));
        }
        events.Add(CreateEvent(tick, CombatEventKind.EnemySplit,
            actor.Input.ActorId, enemy.EnemyId,
            spawnedEnemyIds: childIds.ToImmutable()));
    }

    private void MoveActorTowards(
        PendingActor actor,
        Vector2 target,
        double now,
        double elapsedSeconds)
    {
        var difference = target - actor.Position;
        var distance = difference.Length();
        if (!float.IsFinite(distance) || distance <= .0001f) return;
        var multiplier = actor.Gameplay.CombatStatus.MovementMultiplier(now);
        var terrainMultiplier = ActorMovementService.TerrainSpeedMultiplier(
            _navigation.IsWading(actor.Position, actor.Input.WorldLevel),
            _navigation.HeightAt(actor.Position, actor.Input.WorldLevel),
            _navigation.HeightAt(target, actor.Input.WorldLevel));
        var movement = MathF.Min(distance,
            _options.PlayerChaseSpeed * terrainMultiplier * multiplier *
            (float)elapsedSeconds);
        var next = actor.Position + difference / distance * movement;
        if (!_navigation.CanStandAt(next, actor.Input.WorldLevel)) return;
        actor.Position = next;
        actor.Changed = true;
    }

    private void ResolveEnemyAttack(
        MutableEnemy enemy,
        PendingActor target,
        long tick,
        double now,
        ImmutableArray<CombatEventSnapshot>.Builder events)
    {
        enemy.AttackSequence = checked(enemy.AttackSequence + 1);
        enemy.NextAttackTick = checked(tick + _options.EnemyAttackIntervalTicks);
        var attack = EnemyCombatRules.ResolveAttack(new EnemyAttackRequest(
            _worldSeed, enemy.EnemyId.Value, target.Input.ActorId.Value,
            enemy.AttackSequence, enemy.PowerLevel,
            target.Gameplay.DefenceExperience, target.Gameplay.CombatStance));
        var status = SlimeCombatRules.AttackFor(enemy.Kind).Status;
        var gameplay = target.Gameplay;
        if (attack.Hit)
        {
            gameplay = gameplay with
            {
                Health = EnemyCombatRules.ApplyDamage(gameplay.Health, attack.Damage),
                CombatStatus = SlimeCombatRules.Apply(
                    gameplay.CombatStatus, enemy.Kind, now),
                ActorRevision = checked(gameplay.ActorRevision + 1)
            };
            target.Gameplay = gameplay;
            target.Changed = true;
            if (status != SlimeStatusKind.None)
                events.Add(CreateEvent(tick, CombatEventKind.StatusApplied,
                    target.Input.ActorId, enemy.EnemyId, status: status));
            MarkDead(target, tick, events);
        }
        events.Add(CreateEvent(tick, CombatEventKind.EnemyAttacked,
            target.Input.ActorId, enemy.EnemyId, attack.Damage, attack.Hit,
            status));
    }

    private void MarkDead(
        PendingActor actor,
        long tick,
        ImmutableArray<CombatEventSnapshot>.Builder events)
    {
        if (actor.Gameplay.Health > 0 ||
            actor.Gameplay.LifeState == ActorLifeState.Dead) return;
        actor.Gameplay = actor.Gameplay with
        {
            Health = 0,
            LifeState = ActorLifeState.Dead,
            RespawnAvailableTick = checked(tick + _options.RespawnDelayTicks),
            CombatStatus = default,
            CombatTargetEnemyId = null,
            NextCombatAttackTick = 0,
            ActorRevision = checked(actor.Gameplay.ActorRevision + 1)
        };
        actor.Changed = true;
        events.Add(CreateEvent(tick, CombatEventKind.ActorDied,
            actor.Input.ActorId));
    }

    private bool TryTarget(
        MutableEnemy enemy,
        Dictionary<ActorId, PendingActor> actors,
        out PendingActor target)
    {
        if (enemy.TargetActorId is { } existing &&
            actors.TryGetValue(existing, out var acquired) &&
            CanKeepTarget(enemy, acquired))
        {
            target = acquired;
            return true;
        }
        target = actors.Values
            .Where(value => CanAcquireTarget(enemy, value))
            .OrderBy(value => Vector2.DistanceSquared(
                value.Input.Position, enemy.Position))
            .ThenBy(static value => value.Input.ActorId.Value)
            .FirstOrDefault()!;
        return target is not null;
    }

    private bool CanKeepTarget(MutableEnemy enemy, PendingActor actor) =>
        IsEligibleTarget(enemy, actor) &&
        Vector2.DistanceSquared(actor.Input.Position, enemy.SpawnPosition) <=
            _options.LeashRange * _options.LeashRange;

    private bool CanAcquireTarget(MutableEnemy enemy, PendingActor actor) =>
        CanKeepTarget(enemy, actor) &&
        SlimeCombatRules.CanAcquireTarget(
            enemy.Kind,
            provoked: false,
            Vector2.DistanceSquared(actor.Input.Position, enemy.Position));

    private static bool IsEligibleTarget(
        MutableEnemy enemy, PendingActor actor) =>
        actor.Input.Connected && actor.Gameplay.LifeState == ActorLifeState.Alive &&
        actor.Gameplay.Health > 0 && actor.Input.WorldLevel == enemy.WorldLevel;

    private static int ReactionTicks(EnemyKind kind) =>
        Math.Max(1, (int)Math.Ceiling(
            SlimeCombatRules.ReactionDelaySeconds(kind) *
            SimulationTiming.TicksPerSecond));

    private Vector2 ResolveBurrowEmergence(
        MutableEnemy enemy,
        Vector2 target)
    {
        var angle = DeterministicEnemyRandom.UnitFloat(
            _worldSeed, enemy.EnemyId.Value, enemy.SpawnOrdinal,
            0x425552524F57454DUL) * MathF.Tau;
        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var candidate = target + direction * MathF.Min(
            _options.EnemyAttackRange * .75f, .75f);
        return _navigation.CanStandAt(candidate, enemy.WorldLevel)
            ? candidate
            : enemy.Position;
    }

    private void MoveTowards(MutableEnemy enemy, Vector2 target, double elapsed)
    {
        var difference = target - enemy.Position;
        var distance = difference.Length();
        if (!float.IsFinite(distance) || distance <= .0001f)
        {
            enemy.Velocity = Vector2.Zero;
            return;
        }
        var speed = SlimeCombatRules.MovementSpeed(enemy.Behavior);
        var movement = MathF.Min(distance, speed * (float)elapsed);
        var next = enemy.Position + difference / distance * movement;
        if (!_navigation.CanStandAt(next, enemy.WorldLevel))
        {
            enemy.Velocity = Vector2.Zero;
            return;
        }
        enemy.Velocity = difference / distance * speed;
        enemy.Position = next;
    }

    private CombatTransactionResult? ValidateActor(
        CombatActorInput actor,
        WorldTransactionContext context,
        bool allowDead = false)
    {
        if (actor.ActorId.Value == Guid.Empty || context.ActorId != actor.ActorId ||
            context.CommandId == Guid.Empty)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.InvalidCommand, "The combat command is invalid.");
        if (context.ExpectedInventoryRevision != actor.Gameplay.Inventory.Revision)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.StaleInventoryRevision,
                "The inventory revision is stale.");
        if (context.ExpectedActorRevision != actor.Gameplay.ActorRevision)
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.StaleActorRevision,
                "The actor revision is stale.");
        if (!allowDead && (actor.Gameplay.LifeState == ActorLifeState.Dead ||
                           actor.Gameplay.Health <= 0))
            return Rejected(context, actor.Gameplay,
                CombatTransactionStatus.DeadActor, "The actor is dead.");
        return null;
    }

    private CombatEventSnapshot CreateEvent(
        long tick,
        CombatEventKind kind,
        ActorId? actorId = null,
        EnemyId? enemyId = null,
        int damage = 0,
        bool hit = false,
        SlimeStatusKind status = SlimeStatusKind.None,
        ImmutableArray<CombatLootSnapshot> loot = default,
        ImmutableArray<EnemyId> spawnedEnemyIds = default) => new(
        _nextEventOrdinal++, tick, kind, actorId, enemyId,
        damage, hit, status, loot, spawnedEnemyIds);

    private static CombatTransactionResult Accepted(
        WorldTransactionContext context,
        PlayerGameplaySnapshot gameplay,
        CombatEventSnapshot? combatEvent = null) => new(
        context.CommandId, CombatTransactionStatus.Accepted,
        gameplay, Event: combatEvent);

    private static CombatTransactionResult Rejected(
        WorldTransactionContext context,
        PlayerGameplaySnapshot gameplay,
        CombatTransactionStatus status,
        string detail) => new(context.CommandId, status, gameplay, Detail: detail);

    private static ulong NetworkId(
        IReadOnlyDictionary<ActorId, PendingActor> actors,
        ActorId? actorId) => actorId is { } id &&
                             actors.TryGetValue(id, out var actor)
        ? actor.Input.NetworkEntityId
        : 0;

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool ValidStatus(SlimeVictimStatus value) =>
        double.IsFinite(value.SlowedUntil) && value.SlowedUntil >= 0 &&
        double.IsFinite(value.RootedUntil) && value.RootedUntil >= 0 &&
        double.IsFinite(value.PoisonedUntil) && value.PoisonedUntil >= 0 &&
        double.IsFinite(value.NextPoisonTickAt) && value.NextPoisonTickAt >= 0 &&
        value.PoisonDamage >= 0;

    private void EnsureOwner()
    {
        var threadId = Environment.CurrentManagedThreadId;
        _ownerThreadId ??= threadId;
        if (_ownerThreadId != threadId)
            throw new InvalidOperationException(
                "Combat transactions must execute on their owning simulation thread.");
    }

    private sealed class PendingActor(CombatActorInput input)
    {
        public CombatActorInput Input { get; } = input;
        public PlayerGameplaySnapshot Gameplay { get; set; } = input.Gameplay;
        public Vector2 Position { get; set; } = input.Position;
        public bool Changed { get; set; }
    }

    private sealed class MutableEnemy
    {
        public MutableEnemy(
            EnemyId enemyId, ulong networkEntityId, EnemyKind kind,
            Vector2 spawnPosition, Vector2 position, int worldLevel,
            int powerLevel, int health, int maximumHealth, float sizeScale,
            uint revision, EnemyId? parentEnemyId, uint spawnOrdinal,
            int splitGeneration)
        {
            EnemyId = enemyId;
            NetworkEntityId = networkEntityId;
            Kind = kind;
            SpawnPosition = spawnPosition;
            Position = position;
            WorldLevel = worldLevel;
            PowerLevel = powerLevel;
            Health = health;
            MaximumHealth = maximumHealth;
            SizeScale = sizeScale;
            Revision = revision;
            ParentEnemyId = parentEnemyId;
            SpawnOrdinal = spawnOrdinal;
            SplitGeneration = splitGeneration;
            Behavior = health > 0 ? EnemyBehavior.Idle : EnemyBehavior.Dead;
        }

        public EnemyId EnemyId { get; }
        public ulong NetworkEntityId { get; }
        public uint Revision { get; set; }
        public EnemyKind Kind { get; }
        public EnemyBehavior Behavior { get; set; }
        public Vector2 SpawnPosition { get; }
        public Vector2 Position { get; set; }
        public Vector2 Velocity { get; set; }
        public int WorldLevel { get; }
        public int PowerLevel { get; }
        public int Health { get; set; }
        public int MaximumHealth { get; }
        public float SizeScale { get; }
        public SlimeVictimStatus Status { get; set; }
        public ActorId? TargetActorId { get; set; }
        public EnemyId? ParentEnemyId { get; }
        public uint SpawnOrdinal { get; }
        public ulong AttackSequence { get; set; }
        public long NextAttackTick { get; set; }
        public int SplitGeneration { get; }
        public long DeathRemovalTick { get; set; }
        public long ReactionReadyTick { get; set; }
        public long BurrowEmergeTick { get; set; }
        public bool Alive => Health > 0 && Behavior != EnemyBehavior.Dead;

        public AuthoritativeEnemySnapshot ToSnapshot(
            ActorId? targetActorId,
            ulong targetNetworkEntityId,
            double now = 0) => new(
            EnemyId, NetworkEntityId, Revision, Kind, Behavior,
            SpawnPosition, Position, Velocity, WorldLevel, PowerLevel,
            Health, MaximumHealth, SizeScale,
            StatusFlags(now),
            targetActorId,
            targetNetworkEntityId, ParentEnemyId, SpawnOrdinal,
            AttackSequence, NextAttackTick, SplitGeneration,
            DeathRemovalTick, ReactionReadyTick, BurrowEmergeTick);

        public AuthoritativeEnemyCheckpoint ToCheckpoint() => new(
            EnemyId, Revision, Kind, Behavior, SpawnPosition, Position,
            Velocity, WorldLevel, PowerLevel, Health, MaximumHealth,
            SizeScale, Status, TargetActorId, ParentEnemyId, SpawnOrdinal,
            AttackSequence, NextAttackTick, SplitGeneration,
            DeathRemovalTick, ReactionReadyTick, BurrowEmergeTick);

        public static MutableEnemy FromCheckpoint(
            AuthoritativeEnemyCheckpoint value,
            ulong networkEntityId) => new(
                value.EnemyId, networkEntityId, value.Kind,
                value.SpawnPosition, value.Position, value.WorldLevel,
                value.PowerLevel, value.Health, value.MaximumHealth,
                value.SizeScale, value.Revision, value.ParentEnemyId,
                value.SpawnOrdinal, value.SplitGeneration)
            {
                Behavior = value.Behavior,
                Velocity = value.Velocity,
                Status = value.Status,
                TargetActorId = value.TargetActorId,
                AttackSequence = value.AttackSequence,
                NextAttackTick = value.NextAttackTick,
                DeathRemovalTick = value.DeathRemovalTick,
                ReactionReadyTick = value.ReactionReadyTick,
                BurrowEmergeTick = value.BurrowEmergeTick
            };

        private CombatStatusFlags StatusFlags(double now)
        {
            var flags = CombatStatusFlags.None;
            if (now < Status.SlowedUntil) flags |= CombatStatusFlags.Slowed;
            if (now < Status.RootedUntil) flags |= CombatStatusFlags.Rooted;
            if (now < Status.PoisonedUntil) flags |= CombatStatusFlags.Poisoned;
            if (SlimeCombatRules.UsesIdleCamouflage(Kind) &&
                Behavior == EnemyBehavior.Idle && TargetActorId is null)
                flags |= CombatStatusFlags.Hidden;
            if (BurrowEmergeTick > 0)
                flags |= CombatStatusFlags.Burrowed;
            return flags;
        }
    }
}
