using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Guid? _combatTargetId;
    private string? _combatVillagerId;
    private Guid? _combatEnemyId;
    private double _villagerCombatRepathAt;
    private Vector2 _villagerCombatPathTarget;
    private double _enemyCombatRepathAt;
    private Vector2 _enemyCombatPathTarget;
    private readonly Dictionary<string, double>
        _villagerAttackReactionAt = [];
    private double _nextSettlementExclusionCheckAt;
    private readonly EntityActionCooldowns _actionCooldowns = new();
    private double _nextMeleeAttackAt;
    private double _swingStartedForAttackAt;
    private double _meleeReturnToIdleAt;
    private bool _combatLeftWasDown;

    private static readonly MeleeCombatStance[] MeleeStances =
        Enum.GetValues<MeleeCombatStance>();

    private void QueueTrainingDummyAttack(WorldGroundObject dummy) =>
        _worldActions.QueueTrainingDummyAttack(dummy);

    internal void BeginTrainingDummyCombat(Guid dummyId)
    {
        if (_player is null ||
            FindGroundObject(dummyId) is not { } dummy ||
            dummy.ItemId != ItemIds.TrainingDummy)
            return;
        var targetChanged = _combatTargetId != dummyId ||
                            _combatVillagerId is not null ||
                            _combatEnemyId is not null;
        _combatTargetId = dummyId;
        _combatVillagerId = null;
        _combatEnemyId = null;
        var target = new Vector2(dummy.X, dummy.Y);
        var readyAt = PlayerMeleeReadyAt();
        if (readyAt <= _clock)
        {
            _nextMeleeAttackAt = _clock + MeleeImpactDelay();
            _swingStartedForAttackAt = _nextMeleeAttackAt;
            _player.RestartAttackAt(target);
        }
        else if (_swingStartedForAttackAt == _nextMeleeAttackAt)
        {
            // Re-selecting a target during the current swing may adjust facing,
            // but must never restart the animation or its impact timing.
            _player.AttackAt(target);
        }
        AnnounceCombatTarget(
            targetChanged,
            "the training dummy",
            ChatMessageStyle.Action);
    }

    internal void BeginVillagerCombat(string villagerId)
    {
        if (_player is null) return;
        var index = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (index < 0) return;
        var villager = _villagers[index];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        var targetChanged = _combatVillagerId != villager.Id ||
                            _combatTargetId is not null ||
                            _combatEnemyId is not null;
        _combatTargetId = null;
        _combatVillagerId = villager.Id;
        _combatEnemyId = null;
        AnnounceCombatTarget(
            targetChanged, villager.Name, ChatMessageStyle.Warning);
        if (Vector2.Distance(_player.Position, target) >
            MeleeCombatService.AttackRange + .3f)
        {
            _worldActions.QueueVillagerAttack(villager);
            return;
        }
        var readyAt = PlayerMeleeReadyAt();
        if (readyAt <= _clock)
        {
            _nextMeleeAttackAt = _clock + MeleeImpactDelay();
            _swingStartedForAttackAt = _nextMeleeAttackAt;
            _player.RestartAttackAt(target);
        }
        else
        {
            _nextMeleeAttackAt = readyAt;
            _swingStartedForAttackAt = 0;
            _player.AttackAt(target);
        }
    }

    internal void BeginEnemyCombat(Guid enemyId)
    {
        if (_player is null) return;
        var enemy = _enemies.FirstOrDefault(value =>
            value.Id == enemyId && value.Alive &&
            value.WorldLevel == _activeWorldLevel);
        if (enemy is null) return;
        var targetChanged = _combatEnemyId != enemy.Id ||
                            _combatTargetId is not null ||
                            _combatVillagerId is not null;
        _combatTargetId = null;
        _combatVillagerId = null;
        _combatEnemyId = enemy.Id;
        AnnounceCombatTarget(
            targetChanged,
            EnemyDisplayName(enemy.Kind).ToLowerInvariant(),
            ChatMessageStyle.Warning);
        if (Vector2.Distance(_player.Position, enemy.Position) >
            MeleeCombatService.AttackRange + .3f)
        {
            _worldActions.QueueEnemyAttack(enemy);
            return;
        }
        StartPlayerMeleeSwing(enemy.Position);
    }

    internal void AnnounceCombatTarget(
        bool targetChanged,
        string targetName,
        ChatMessageStyle style)
    {
        if (targetChanged)
            _chatUi.AddMessage(
                $"You begin attacking {targetName}.", style);
    }

    private bool HasMeleeCombatTarget =>
        _combatTargetId is not null ||
        _combatVillagerId is not null ||
        _combatEnemyId is not null;

    private void TryAutoRetaliate(EnemyState attacker)
    {
        if (!MeleeCombatService.ShouldAutoRetaliate(
                _autoRetaliateEnabled,
                _playerDefeated,
                HasMeleeCombatTarget))
            return;
        _worldActions.QueueEnemyAttack(attacker);
    }

    private void TryAutoRetaliate(VillagerState attacker)
    {
        if (!MeleeCombatService.ShouldAutoRetaliate(
                _autoRetaliateEnabled,
                _playerDefeated,
                HasMeleeCombatTarget))
            return;
        _worldActions.QueueVillagerAttack(attacker);
    }

    private void StartPlayerMeleeSwing(Vector2 target)
    {
        if (_player is null) return;
        var readyAt = PlayerMeleeReadyAt();
        if (readyAt <= _clock)
        {
            _nextMeleeAttackAt = _clock + MeleeImpactDelay();
            _swingStartedForAttackAt = _nextMeleeAttackAt;
            _player.RestartAttackAt(target);
        }
        else
        {
            _nextMeleeAttackAt = readyAt;
            _swingStartedForAttackAt = 0;
            _player.AttackAt(target);
        }
    }

    internal void CancelMeleeCombat()
    {
        if (_combatTargetId is null && _combatVillagerId is null &&
            _combatEnemyId is null)
            return;
        _combatTargetId = null;
        _combatVillagerId = null;
        _combatEnemyId = null;
        _villagerCombatRepathAt = 0;
        // The ready time is global combat state. Movement or target changes
        // cancel targeting without granting an immediate fresh attack.
        _meleeReturnToIdleAt = 0;
        if (_player?.Action == EntityAction.Attack)
            _player.Stop();
    }

    internal void UpdateMeleeCombat()
    {
        if (_combatEnemyId is { } enemyId)
        {
            UpdateEnemyCombat(enemyId);
            return;
        }
        if (_combatVillagerId is { } villagerId)
        {
            UpdateVillagerCombat(villagerId);
            return;
        }
        if (_combatTargetId is not { } targetId ||
            _activePlayer is null ||
            _player is null)
            return;
        if (FindGroundObjectLocation(targetId) is not { } location ||
            location.Object.ItemId != ItemIds.TrainingDummy)
        {
            CancelMeleeCombat();
            return;
        }
        var target = new Vector2(
            location.Object.X, location.Object.Y);
        var interactionRange =
            MeleeCombatService.InteractionRange(
                (System.Numerics.Vector2)(_player.Position - target));
        if ((_player.Position - target).Length >
            interactionRange + .22f)
        {
            CancelMeleeCombat();
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
        {
            _player.RestartAttackAt(target);
            _swingStartedForAttackAt = _nextMeleeAttackAt;
        }
        else
        {
            _player.AttackAt(target);
        }
        if (_clock < _nextMeleeAttackAt) return;
        var interaction = EntityInteractionService.TryMeleeAttack(
            _actionCooldowns,
            _activePlayer.Id,
            _clock,
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            MeleeCombatService.ExperienceForStance(
                _activePlayer.AttackExperience,
                _activePlayer.StrengthExperience,
                _activePlayer.DefenceExperience,
                _activePlayer.CombatStance),
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle(),
            _activePlayer.Inventory);
        if (!interaction.Succeeded) return;
        _nextMeleeAttackAt = PlayerMeleeReadyAt();
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();
        var roll = interaction.Attack;
        if (!roll.Hit)
        {
            ShowEntityImpact(
                GroundFeedbackKey(targetId), 0, false);
            _chatUi.AddMessage("You miss.", ChatMessageStyle.Action);
            return;
        }

        ShowEntityImpact(
            GroundFeedbackKey(targetId), roll.Damage, true);
        var health = location.Object.Health <= 0
            ? MeleeCombatService.TrainingDummyMaximumHealth
            : location.Object.Health;
        health -= roll.Damage;
        if (health <= 0)
        {
            health = MeleeCombatService.TrainingDummyMaximumHealth;
            _chatUi.AddMessage(
                "The training dummy is knocked down and reset.",
                ChatMessageStyle.Action);
        }
        location.Chunk.GroundObjects[location.Index] =
            location.Object with
            {
                Health = health,
                MaxHealth = MeleeCombatService.TrainingDummyMaximumHealth
            };
        QueueChunkSave(location.Chunk);

        var award = interaction.Experience;
        _activePlayer = _activePlayer.CombatStance switch
        {
            MeleeCombatStance.Accurate => _activePlayer with
            {
                AttackExperience = award.Experience
            },
            MeleeCombatStance.Aggressive => _activePlayer with
            {
                StrengthExperience = award.Experience
            },
            _ => _activePlayer with
            {
                DefenceExperience = award.Experience
            }
        };
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You hit for {roll.Damage}. +{award.Gained} " +
            $"{CombatStatName(_activePlayer.CombatStance)} XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your {CombatStatName(_activePlayer.CombatStance)} " +
                $"level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    private void UpdateEnemyCombat(Guid enemyId)
    {
        if (_activePlayer is null || _player is null) return;
        var index = _enemies.FindIndex(value =>
            value.Id == enemyId && value.Alive &&
            value.WorldLevel == _activeWorldLevel);
        if (index < 0)
        {
            CancelMeleeCombat();
            return;
        }
        var enemy = _enemies[index];
        var target = enemy.Position;
        if (Vector2.Distance(_player.Position, target) >
            MeleeCombatService.AttackRange + .22f)
        {
            if (MeleeCombatService.ShouldRequestMovingTargetPath(
                    _pendingPathTask is not null,
                    _clock, _enemyCombatRepathAt,
                    (System.Numerics.Vector2)_enemyCombatPathTarget,
                    (System.Numerics.Vector2)target))
            {
                _enemyCombatRepathAt = _clock +
                    MeleeCombatService.MovingTargetRepathSeconds;
                _enemyCombatPathTarget = target;
                _worldActions.QueueEnemyAttack(enemy);
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
        {
            _player.RestartAttackAt(target);
            _swingStartedForAttackAt = _nextMeleeAttackAt;
        }
        else
            _player.AttackAt(target);
        if (_clock < _nextMeleeAttackAt) return;
        var interaction = EntityInteractionService.TryMeleeAttack(
            _actionCooldowns,
            _activePlayer.Id,
            _clock,
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            MeleeCombatService.ExperienceForStance(
                _activePlayer.AttackExperience,
                _activePlayer.StrengthExperience,
                _activePlayer.DefenceExperience,
                _activePlayer.CombatStance),
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle(),
            _activePlayer.Inventory);
        if (!interaction.Succeeded) return;
        _nextMeleeAttackAt = PlayerMeleeReadyAt();
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();
        var roll = interaction.Attack;
        if (!roll.Hit)
        {
            ShowEntityImpact(EnemyFeedbackKey(enemy.Id), 0, false);
            _chatUi.AddMessage(
                $"You miss {EnemyDisplayName(enemy.Kind).ToLowerInvariant()}.",
                ChatMessageStyle.Miss);
            return;
        }
        ApplyPlayerCombatExperience(interaction.Experience);
        enemy = EnemyCombatService.ApplyHit(
            enemy, roll.Damage, "player", _clock);
        _enemies[index] = enemy;
        ShowEntityImpact(EnemyFeedbackKey(enemy.Id), roll.Damage, true);
        _chatUi.AddMessage(
            $"You hit {EnemyDisplayName(enemy.Kind).ToLowerInvariant()} " +
            $"for {roll.Damage}.",
            ChatMessageStyle.Damage);
        if (!enemy.Alive)
        {
            DropEnemyLoot(enemy);
            var split = SlimeAbilityService.Split(
                enemy, _worldSeed);
            if (split.Length > 0)
            {
                _enemies.AddRange(split);
                _slimeAttackEffects.SplitBurst(
                    enemy.Kind,
                    EnemyEffectWorld(enemy.Position),
                    HashCode.Combine(enemy.Id, split.Length));
                PlaySlimeSplitSound(enemy.Kind);
                _chatUi.AddMessage(
                    $"The large {EnemyDisplayName(enemy.Kind).ToLowerInvariant()} " +
                    "bursts into two smaller slimes!",
                    ChatMessageStyle.Warning);
            }
            _chatUi.AddMessage(
                $"The {EnemyDisplayName(enemy.Kind).ToLowerInvariant()} dissolves.",
                ChatMessageStyle.Action);
            CancelMeleeCombat();
        }
    }

    private void UpdateVillagerCombat(string villagerId)
    {
        if (_activePlayer is null || _player is null)
            return;
        var index = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (index < 0)
        {
            CancelMeleeCombat();
            return;
        }
        var villager = _villagers[index];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        if (Vector2.Distance(_player.Position, target) >
            MeleeCombatService.AttackRange + .22f)
        {
            if (MeleeCombatService.ShouldRepathMovingTarget(
                    _clock,
                    _villagerCombatRepathAt,
                    (System.Numerics.Vector2)_villagerCombatPathTarget,
                    (System.Numerics.Vector2)target))
            {
                _villagerCombatRepathAt =
                    _clock +
                    MeleeCombatService.MovingTargetRepathSeconds;
                _villagerCombatPathTarget = target;
                _worldActions.QueueVillagerAttack(villager);
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
        {
            _player.RestartAttackAt(target);
            _swingStartedForAttackAt = _nextMeleeAttackAt;
        }
        else
            _player.AttackAt(target);
        if (_clock < _nextMeleeAttackAt) return;
        var interaction = EntityInteractionService.TryMeleeAttack(
            _actionCooldowns,
            _activePlayer.Id,
            _clock,
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            MeleeCombatService.ExperienceForStance(
                _activePlayer.AttackExperience,
                _activePlayer.StrengthExperience,
                _activePlayer.DefenceExperience,
                _activePlayer.CombatStance),
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle(),
            _activePlayer.Inventory);
        if (!interaction.Succeeded) return;
        _nextMeleeAttackAt = PlayerMeleeReadyAt();
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();
        var roll = interaction.Attack;
        if (!roll.Hit)
        {
            ShowEntityImpact(
                VillagerFeedbackKey(villager.Id), 0, false);
            _chatUi.AddMessage(
                $"You miss {villager.Name}.",
                ChatMessageStyle.Miss);
            return;
        }
        ApplyPlayerCombatExperience(interaction.Experience);
        var relationshipBefore = villager.Relationships?.FirstOrDefault(value =>
            value.CharacterId == _activePlayer.Id)?.State ?? default;
        villager = VillagerSimulation.RecordAttack(
            villager,
            _activePlayer.Id,
            VillagerSimulation.PerceivedName(
                villager, _activePlayer.Id),
            roll.Damage,
            _worldGameSeconds);
        var relationshipAfter = villager.Relationships?.FirstOrDefault(value =>
            value.CharacterId == _activePlayer.Id)?.State ?? default;
        ReportPlayerRelationshipTransition(
            villager, relationshipBefore, relationshipAfter);
        ShowEntityImpact(
            VillagerFeedbackKey(villager.Id), roll.Damage, true);
        _villagers[index] = villager;
        _villagersDirty = true;
        ReactToVillagerAttack(index);
        EscalateSettlementJustice(index);
        _chatUi.AddMessage(
            $"You hit {villager.Name} for {roll.Damage}.",
            ChatMessageStyle.Damage);
        if (villager.Health <= 0)
        {
            _chatUi.AddMessage(
                $"{villager.Name} collapses.",
                ChatMessageStyle.Warning);
            CancelMeleeCombat();
        }
    }

    private double PlayerMeleeReadyAt() =>
        _activePlayer is null
            ? double.PositiveInfinity
            : _actionCooldowns.ReadyAt(
                _activePlayer.Id, EntityAction.Attack);

    private void ReactToVillagerAttack(int victimIndex)
    {
        if (_activePlayer is null || _player is null ||
            (uint)victimIndex >= (uint)_villagers.Count)
            return;
        var victim = _villagers[victimIndex];
        if (_villagerAttackReactionAt.TryGetValue(
                victim.Id, out var nextReactionAt) &&
            _clock < nextReactionAt)
            return;
        _villagerAttackReactionAt[victim.Id] = _clock + 8;

        if (victim.Health > 0)
        {
            var victimPosition = new Vector2(
                victim.PositionX, victim.PositionY);
            var away = victimPosition - _player.Position;
            if (away.LengthSquared <= .0001f)
                away = Vector2.UnitX;
            else
                away = away.Normalized();
            var fleeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed,
                    victimPosition,
                    victimPosition + away * 4,
                    victim.WorldLevel,
                    maximumRadius: 2);
            victim = VillagerSimulation.ApplyDecision(
                victim,
                new(VillagerNeed.Safe, fleeTarget),
                VillagerSimulationTier.Nearby,
                _worldGameSeconds);
            var yelled = VillagerYellService.CanYell(
                victim, _worldGameSeconds);
            if (yelled)
                victim = VillagerYellService.MarkYelled(
                    victim, _worldGameSeconds);
            _villagers[victimIndex] = victim;
            ShowVillagerCombatReaction(
                victimIndex,
                yelled
                    ? $"Help! {_activePlayer.Name} is attacking me!"
                    : "Stop! Why are you attacking me?");
        }

        var spokespersonIndex = -1;
        var spokespersonPriority = int.MinValue;
        string? spokespersonMessage = null;
        var attackedPosition = new Vector2(
            victim.PositionX, victim.PositionY);
        var attackerArmed = _activePlayer.Inventory?.Any(itemId =>
            itemId is not null &&
            ItemCatalog.Get(itemId).HasTag(ItemTag.Weapon)) == true;
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == victimIndex) continue;
            var witness = _villagers[index];
            if (witness.Health <= 0 ||
                witness.WorldLevel != victim.WorldLevel)
                continue;
            var distance = Vector2.DistanceSquared(
                new(witness.PositionX, witness.PositionY),
                attackedPosition);
            if (distance > VillagerYellService.HearingRadius *
                           VillagerYellService.HearingRadius)
                continue;
            var relationship = witness.Relationships?.FirstOrDefault(value =>
                value.CharacterId == victim.Id)?.State ?? default;
            var sameSettlement = victim.SettlementGroupId is not null &&
                victim.SettlementGroupId == witness.SettlementGroupId;
            var answersYell = VillagerYellService.ShouldAnswer(
                witness, victim, _activePlayer.Id,
                relationship, sameSettlement);
            if (distance > 10 * 10 && !answersYell) continue;
            witness = VillagerSimulation.RecordWitnessedAttack(
                witness,
                _activePlayer.Id,
                VillagerSimulation.PerceivedName(
                    witness, _activePlayer.Id),
                victim.Id,
                VillagerSimulation.PerceivedName(
                    witness, victim.Id),
                _worldGameSeconds);
            var reaction = VillagerWitnessResponseService.Decide(
                witness, victim, _activePlayer.Id, attackerArmed);
            if (answersYell &&
                reaction.Intent != VillagerWitnessIntent.Protect)
                reaction = new(
                    VillagerWitnessIntent.Protect,
                    $"I heard {victim.Name} yell. I should rush to help.",
                    Math.Max(70, reaction.Priority));
            witness = ApplyPlayerAttackWitnessResponse(
                witness, victim, reaction);
            _villagers[index] = witness;
            ObserveLog("player_attack_witness", witness.Id, new
            {
                VictimId = victim.Id,
                Intent = reaction.Intent.ToString(),
                reaction.Thought,
                Armed = attackerArmed
            });
            var priority = reaction.Priority -
                           (int)MathF.Round(MathF.Sqrt(distance));
            if (reaction.Intent == VillagerWitnessIntent.Ignore ||
                priority <= spokespersonPriority)
                continue;
            spokespersonIndex = index;
            spokespersonPriority = priority;
            spokespersonMessage = WitnessReactionLine(
                witness, victim, reaction.Intent);
        }
        if (spokespersonIndex >= 0 && spokespersonMessage is not null)
            ShowVillagerCombatReaction(
                spokespersonIndex, spokespersonMessage);
        _villagersDirty = true;
    }

    private VillagerState ApplyPlayerAttackWitnessResponse(
        VillagerState witness,
        VillagerState victim,
        VillagerWitnessDecision reaction)
    {
        var position = new Vector2(witness.PositionX, witness.PositionY);
        if (reaction.Intent == VillagerWitnessIntent.Protect)
            return witness with
            {
                ConflictTargetId = _activePlayer!.Id,
                ConflictIntent = VillagerConflictIntent.Defend,
                ConflictMotive = $"protect {victim.Name}",
                ConflictExpiresGameSeconds = _worldGameSeconds +
                    VillagerConflictService.ConflictDurationGameSeconds,
                FollowingActorId = null,
                Need = VillagerNeed.Safe,
                NextDecisionGameSeconds = _worldGameSeconds,
                LastDeliberation = new(
                    reaction.Thought, "witness_response", "protect",
                    85, 15, 70, reaction.Priority, _worldGameSeconds)
            };
        if (reaction.Intent is not
            (VillagerWitnessIntent.BackAway or
             VillagerWitnessIntent.SeekHelp))
            return witness with
            {
                LastDeliberation = new(
                    reaction.Thought, "witness_response",
                    reaction.Intent.ToString().ToLowerInvariant(),
                    55, 5, reaction.Intent == VillagerWitnessIntent.Warn
                        ? 30 : 10,
                    reaction.Priority, _worldGameSeconds)
            };

        var destination = position;
        if (reaction.Intent == VillagerWitnessIntent.SeekHelp &&
            witness.RecognizedLeaderId is { } leaderId &&
            _villagers.FirstOrDefault(value =>
                value.Id == leaderId && value.Health > 0 &&
                value.WorldLevel == witness.WorldLevel) is { } leader)
            destination = new(leader.PositionX, leader.PositionY);
        else
        {
            var away = position - _player!.Position;
            if (away.LengthSquared <= .001f) away = Vector2.UnitX;
            destination = WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed, position,
                position + away.Normalized() * 4,
                witness.WorldLevel, maximumRadius: 2);
        }
        var moving = VillagerSimulation.ApplyDecision(
            witness,
            new(VillagerNeed.Safe, destination),
            VillagerSimulationTier.Nearby,
            _worldGameSeconds);
        return moving with
        {
            LastDeliberation = new(
                reaction.Thought, "witness_response",
                reaction.Intent == VillagerWitnessIntent.SeekHelp
                    ? "seek_help" : "back_away",
                70, 10, 55, reaction.Priority, _worldGameSeconds)
        };
    }

    private static string WitnessReactionLine(
        VillagerState witness,
        VillagerState victim,
        VillagerWitnessIntent intent)
    {
        var name = VillagerSimulation.PerceivedName(witness, victim.Id);
        return intent switch
        {
            VillagerWitnessIntent.Protect =>
                $"Leave {name} alone!",
            VillagerWitnessIntent.SeekHelp =>
                $"Help! {name} is being attacked!",
            VillagerWitnessIntent.BackAway =>
                "Keep away from me!",
            _ => $"Stop attacking {name}!"
        };
    }

    private void ShowVillagerCombatReaction(
        int villagerIndex,
        string message)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count)
            return;
        var villager = _villagers[villagerIndex];
        var seconds = ConversationLineSeconds(message);
        _villagerSpeechBubbles[villager.Id] =
            new(message, _clock + seconds);
        _chatUi.AddMessage(
            $"{villager.Name}: {message}",
            ChatMessageStyle.Warning);
    }

    private void EscalateSettlementJustice(int victimIndex)
    {
        if (_activePlayer is null || _player is null ||
            _settlementGroup is not { } group ||
            (uint)victimIndex >= (uint)_villagers.Count ||
            !SettlementGroupService.IsMember(
                group, _villagers[victimIndex].Id))
            return;
        var victim = _villagers[victimIndex];
        var leader = _villagers.FirstOrDefault(value =>
                         value.Id == group.LeaderId && value.Health > 0) ??
                     _villagers.Where(value =>
                             value.Health > 0 &&
                             SettlementGroupService.IsMember(group, value.Id))
                         .OrderByDescending(value =>
                             value.Honesty + value.Boldness)
                         .FirstOrDefault();
        if (leader is null) return;
        var incidentVictims = _villagers
            .Where(value => SettlementGroupService.IsMember(group, value.Id))
            .Select(value => new
            {
                Victim = value,
                Attacks = value.Memories?.Count(memory =>
                    memory.Kind == "violence" &&
                    memory.SubjectId == _activePlayer.Id &&
                    _worldGameSeconds - memory.GameSeconds <=
                    SettlementJusticeService.IncidentWindowGameSeconds) ?? 0
            })
            .Where(value => value.Attacks > 0)
            .ToArray();
        var recentAttacks = incidentVictims.Sum(value => value.Attacks);
        victim = incidentVictims
            .OrderBy(value => value.Victim.Health)
            .ThenByDescending(value => value.Attacks)
            .Select(value => value.Victim)
            .FirstOrDefault() ?? victim;
        var attackerArmed = _activePlayer.Inventory?.Any(itemId =>
            itemId is not null &&
            ItemCatalog.Get(itemId).HasTag(ItemTag.Weapon)) == true;
        var members = _villagers.Where(value =>
            value.Health > 0 &&
            SettlementGroupService.IsMember(group, value.Id)).ToArray();
        var judgment = SettlementJusticeService.Judge(
            group, leader, victim, _activePlayer.Id,
            recentAttacks, attackerArmed, members, _worldGameSeconds);
        var previous = group.ActiveJusticeCase;
        judgment = SettlementJusticeService.PreserveEscalation(
            previous, judgment);
        var changed = previous is null ||
                      previous.AttackerId != judgment.AttackerId ||
                      previous.VictimId != judgment.VictimId ||
                      previous.Outcome != judgment.Outcome;
        var aftermath = SocialIncidentAftermathService.Begin(
            group.ActiveAftermath, group, victim,
            _activePlayer.Id, members, _worldGameSeconds);
        group = group with
        {
            ActiveJusticeCase = judgment,
            ActiveAftermath = aftermath
        };
        if (judgment.Outcome == SettlementJusticeOutcome.Exile)
        {
            group = SettlementGroupService.RemoveMember(
                group, _activePlayer.Id);
            if (previous?.Outcome != SettlementJusticeOutcome.Exile)
                group = group with { Exclusion = null };
        }
        _settlementGroup = group;
        _saves.SaveSettlementGroup(_activeWorld!.Id, group);
        ApplySettlementJusticeConsequences(group, victim, judgment, changed);
        if (changed)
        {
            var leaderIndex = VillagerIndex(leader.Id);
            if (leaderIndex >= 0)
                ShowVillagerCombatReaction(
                    leaderIndex,
                    SettlementJusticeService.LeaderLine(
                        judgment, _activePlayer.Name, victim.Name));
        }
        ObserveLog("settlement_justice", leader.Id, new
        {
            judgment.AttackerId,
            judgment.VictimId,
            Severity = judgment.Severity.ToString(),
            Outcome = judgment.Outcome.ToString(),
            judgment.RecentAttackCount,
            Changed = changed
        });
    }

    private void ApplySettlementJusticeConsequences(
        SettlementGroupState group,
        VillagerState victim,
        SettlementJusticeCase judgment,
        bool changed)
    {
        if (_activePlayer is null || _player is null) return;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var member = _villagers[index];
            if (member.Health <= 0 ||
                !SettlementGroupService.IsMember(group, member.Id))
                continue;
            if (changed)
                member = RecordSettlementJudgment(
                    member, judgment, _activePlayer.Name, victim.Name);
            if (judgment.Outcome ==
                    SettlementJusticeOutcome.CollectiveDefense &&
                SettlementJusticeService.SupportsSanction(
                    member, victim, _activePlayer.Id) &&
                member.Boldness >= .5f)
            {
                member = member with
                {
                    ConflictTargetId = _activePlayer.Id,
                    ConflictIntent = VillagerConflictIntent.Defend,
                    ConflictMotive = $"settlement judgment: {judgment.Outcome}",
                    ConflictExpiresGameSeconds = _worldGameSeconds +
                        VillagerConflictService.ConflictDurationGameSeconds,
                    FollowingActorId = null,
                    Need = VillagerNeed.Safe,
                    NextDecisionGameSeconds = _worldGameSeconds
                };
            }
            else if (judgment.Outcome is
                     SettlementJusticeOutcome.Avoidance or
                     SettlementJusticeOutcome.CollectiveDefense or
                     SettlementJusticeOutcome.Exile)
            {
                var position = new Vector2(member.PositionX, member.PositionY);
                var away = position - _player.Position;
                if (away.LengthSquared <= .001f) away = Vector2.UnitX;
                var destination = WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed, position,
                    position + away.Normalized() * 4,
                    member.WorldLevel, maximumRadius: 2);
                member = VillagerSimulation.ApplyDecision(
                    member, new(VillagerNeed.Safe, destination),
                    VillagerSimulationTier.Nearby, _worldGameSeconds);
            }
            _villagers[index] = member;
        }
        _villagersDirty = true;
    }

    private static VillagerState RecordSettlementJudgment(
        VillagerState villager,
        SettlementJusticeCase judgment,
        string attackerName,
        string victimName)
    {
        var memories = villager.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(), "settlement-justice", judgment.AttackerId,
            null, 1, judgment.FiledGameSeconds,
            judgment.Outcome == SettlementJusticeOutcome.Warning ? -15 : -40,
            $"The settlement judged {attackerName}'s attack on {victimName} as {judgment.Outcome}."));
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - VillagerSimulation.MaximumMemories);
        return villager with { Memories = memories };
    }

    private void UpdateSettlementExclusion()
    {
        if (_clock < _nextSettlementExclusionCheckAt ||
            _activePlayer is null || _player is null ||
            _settlementGroup is not { } group ||
            !SettlementJusticeService.IsExiled(group, _activePlayer.Id))
            return;
        _nextSettlementExclusionCheckAt = _clock + .5;
        var policy = SettlementExclusionPolicy.Default;
        var position = _activeWorldLevel == group.WorldLevel
            ? _player.Position
            : group.Camp + new Vector2(policy.DisengageRadius + 1, 0);
        var transition = SettlementExclusionService.Advance(
            policy, group.Exclusion, _activePlayer.Id,
            position, group.Camp, _worldGameSeconds);
        if (transition.Changed)
        {
            group = group with { Exclusion = transition.State };
            _settlementGroup = group;
            _saves.SaveSettlementGroup(_activeWorld!.Id, group);
            HandleSettlementExclusionTransition(
                group, transition.State, transition.PreviousStage);
        }
        else if (transition.State.Stage ==
                 SettlementExclusionStage.Enforcement)
            EnforceSettlementExclusion(group);
    }

    private void HandleSettlementExclusionTransition(
        SettlementGroupState group,
        SettlementExclusionState state,
        SettlementExclusionStage previous)
    {
        var leaderIndex = VillagerIndex(group.LeaderId);
        switch (state.Stage)
        {
            case SettlementExclusionStage.Grace when state.Entries > 1:
                if (leaderIndex >= 0)
                    ShowVillagerCombatReaction(
                        leaderIndex,
                        $"You were cast out. Leave within {Math.Ceiling(
                            SettlementExclusionPolicy.Default
                                .ReentryGraceGameSeconds /
                            VillagerSimulation.GameSecondsPerRealSecond):0} seconds.");
                break;
            case SettlementExclusionStage.FinalWarning:
                if (leaderIndex >= 0)
                    ShowVillagerCombatReaction(
                        leaderIndex,
                        "Final warning. Cross beyond our camp boundary now!");
                break;
            case SettlementExclusionStage.Enforcement:
                if (leaderIndex >= 0)
                    ShowVillagerCombatReaction(
                        leaderIndex,
                        "They refuse to leave. Drive them from the camp!");
                EnforceSettlementExclusion(group);
                break;
            case SettlementExclusionStage.Outside when
                previous != SettlementExclusionStage.Outside:
                ClearSettlementExclusionPursuit();
                if (leaderIndex >= 0)
                    ShowVillagerCombatReaction(
                        leaderIndex,
                        "They are beyond the boundary. Hold the camp.");
                break;
        }
        ObserveLog("settlement_exclusion", state.ActorId, new
        {
            Previous = previous.ToString(),
            Stage = state.Stage.ToString(),
            state.Entries,
            state.DeadlineGameSeconds
        });
    }

    private void EnforceSettlementExclusion(SettlementGroupState group)
    {
        if (_activePlayer is null) return;
        var victim = group.ActiveJusticeCase?.VictimId is { } victimId
            ? _villagers.FirstOrDefault(value => value.Id == victimId)
            : null;
        var responders = SettlementExclusionService.SelectResponders(
            _villagers.Where(value =>
                SettlementGroupService.IsMember(group, value.Id)));
        for (var index = 0; index < _villagers.Count; index++)
        {
            var member = _villagers[index];
            var enforcing = string.Equals(
                member.ConflictMotive,
                "enforce settlement exclusion",
                StringComparison.Ordinal);
            if (!responders.Contains(member.Id) || member.Boldness < .5f ||
                victim is not null &&
                !SettlementJusticeService.SupportsSanction(
                    member, victim, _activePlayer.Id))
            {
                if (enforcing)
                {
                    _villagers[index] = VillagerConflictService.Clear(
                        member, _worldGameSeconds);
                    _villagerWork.ReleaseActor(member.Id);
                    _villagersDirty = true;
                }
                continue;
            }
            if (member.ConflictTargetId == _activePlayer.Id &&
                member.ConflictIntent == VillagerConflictIntent.Defend &&
                member.ConflictExpiresGameSeconds > _worldGameSeconds + 60)
                continue;
            _villagers[index] = member with
            {
                ConflictTargetId = _activePlayer.Id,
                ConflictIntent = VillagerConflictIntent.Defend,
                ConflictMotive = "enforce settlement exclusion",
                ConflictExpiresGameSeconds = _worldGameSeconds +
                    VillagerConflictService.ConflictDurationGameSeconds,
                FollowingActorId = null,
                Need = VillagerNeed.Safe,
                NextDecisionGameSeconds = _worldGameSeconds
            };
            _villagersDirty = true;
        }
    }

    private void ClearSettlementExclusionPursuit()
    {
        for (var index = 0; index < _villagers.Count; index++)
        {
            var villager = _villagers[index];
            if (!string.Equals(
                    villager.ConflictMotive,
                    "enforce settlement exclusion",
                    StringComparison.Ordinal))
                continue;
            _villagers[index] = VillagerConflictService.Clear(
                villager, _worldGameSeconds);
            _villagersDirty = true;
        }
    }

    private void UpdateCombatPanelInput(Vector2 pointer, bool leftDown)
    {
        if (_gameUi.ActivePanel != GameUiPanel.Combat ||
            _activePlayer is null)
        {
            _combatLeftWasDown = leftDown;
            return;
        }
        if (leftDown && !_combatLeftWasDown)
        {
            for (var index = 0; index < MeleeStances.Length; index++)
            {
                if (!CombatStanceBounds(
                        _gameUi.Panel.Bounds, index).Contains(pointer))
                    continue;
                if (IsNetworkWorld)
                {
                    SendNetworkCombatStance(MeleeStances[index]);
                    break;
                }
                _activePlayer = _activePlayer with
                {
                    CombatStance = MeleeStances[index],
                    UpdatedUtc = DateTime.UtcNow
                };
                _saves.SavePlayer(_activePlayer);
                break;
            }
        }
        _combatLeftWasDown = leftDown;
    }

    private void RenderCombatPanel()
    {
        if (_activePlayer is null) return;
        var panel = _gameUi.Panel.Bounds;
        DrawPanelCaption("Combat", panel);
        for (var index = 0; index < MeleeStances.Length; index++)
            DrawCombatStance(
                CombatStanceBounds(panel, index),
                MeleeStances[index],
                _activePlayer.CombatStance == MeleeStances[index]);
    }

    private void RenderCombatTargetHealthBar(Vector4 scene)
    {
        var displayedEnemyId = _combatEnemyId;
        if (displayedEnemyId is null &&
            _entityFeedback.LatestImpactTargetKey is { } enemyKey &&
            enemyKey.StartsWith("enemy:", StringComparison.Ordinal) &&
            Guid.TryParseExact(
                enemyKey["enemy:".Length..], "N", out var recentEnemyId))
            displayedEnemyId = recentEnemyId;
        if (displayedEnemyId is { } enemyId)
        {
            var enemy = _enemies.FirstOrDefault(value =>
                value.Id == enemyId && value.Alive &&
                value.WorldLevel == _activeWorldLevel);
            if (enemy is null || _slimeRig is null) return;
            var pose = SlimeSpriteRig.Resolve(
                EntityAction.Idle, Vector2.UnitY, 0);
            DrawEntityFeedback(
                scene,
                EnemySpriteBounds(enemy, _slimeRig.Frame(pose)),
                enemy.Health / (float)enemy.MaximumHealth,
                EnemyFeedbackKey(enemy.Id),
                forceHealth: _combatEnemyId == enemy.Id);
            return;
        }
        var displayedVillagerId = _combatVillagerId;
        if (displayedVillagerId is null &&
            _entityFeedback.LatestImpactTargetKey is { } recentKey &&
            recentKey.StartsWith("villager:",
                StringComparison.Ordinal))
            displayedVillagerId = recentKey["villager:".Length..];
        if (displayedVillagerId is { } villagerId)
        {
            var villager = _villagers.FirstOrDefault(value =>
                value.Id == villagerId &&
                value.WorldLevel == _activeWorldLevel);
            if (villager is null ||
                !TryVillagerSpriteBounds(
                    villager, out var villagerBounds))
                return;
            DrawEntityFeedback(
                scene,
                villagerBounds,
                villager.Health /
                (float)AdventureService.BaseMaximumHealth,
                VillagerFeedbackKey(villager.Id),
                forceHealth: _combatVillagerId == villager.Id);
            return;
        }
        if (_combatTargetId is not { } targetId ||
            FindGroundObjectLocation(targetId) is not { } location ||
            !TryGroundItemVisual(
                location.Object.ItemId,
                out var frame, out _, out _, out _))
            return;
        var maximum = location.Object.MaxHealth > 0
            ? location.Object.MaxHealth
            : MeleeCombatService.TrainingDummyMaximumHealth;
        var health = location.Object.Health > 0
            ? location.Object.Health
            : maximum;
        DrawEntityFeedback(
            scene,
            SpriteBounds(frame, GroundObjectWorld(location.Object)),
            health / (float)maximum,
            GroundFeedbackKey(targetId),
            forceHealth: true);
    }

    private double MeleeImpactDelay()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Attack), out var animation))
            return .65;
        const int storedVillagerAngles = 5;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedVillagerAngles);
        var impactFrame = Math.Clamp(
            (int)MathF.Round((framesPerAngle - 1) * .62f),
            0, framesPerAngle - 1);
        return impactFrame * animation.SecondsPerFrame;
    }

    private double MeleeRecoveryDelay()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Attack), out var animation))
            return .4;
        const int storedVillagerAngles = 5;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedVillagerAngles);
        var fullAnimationSeconds =
            framesPerAngle * animation.SecondsPerFrame;
        return Math.Max(
            animation.SecondsPerFrame,
            fullAnimationSeconds - MeleeImpactDelay());
    }

    private void DrawCombatStance(
        Vector4 bounds,
        MeleeCombatStance stance,
        bool selected)
    {
        var hovered = bounds.Contains(MouseState.Position);
        var accent = stance switch
        {
            MeleeCombatStance.Accurate =>
                new Vector4(.67f, .16f, .09f, 1),
            MeleeCombatStance.Aggressive =>
                new Vector4(.72f, .36f, .10f, 1),
            _ => new Vector4(.18f, .40f, .67f, 1)
        };
        DrawUiColor(
            bounds,
            selected
                ? new(.115f, .090f, .045f, .99f)
                : hovered
                    ? new(.085f, .072f, .045f, .98f)
                    : new(.052f, .047f, .036f, .98f));
        DrawPanelOutline(
            bounds, selected ? 2 : 1,
            selected
                ? new(.58f, .43f, .17f, 1)
                : new(.27f, .22f, .13f, 1));
        if (selected)
            DrawUiColor(
                new(bounds.X + 2, bounds.Y + 3, 3, bounds.W - 6),
                accent);
        var icon = stance switch
        {
            MeleeCombatStance.Accurate => 0,
            MeleeCombatStance.Aggressive => 1,
            _ => 2
        };
        DrawUiCircle(
            bounds.X + 23, bounds.Y + bounds.W * .5f, 16,
            new(.025f, .022f, .018f, 1));
        DrawUiCircle(
            bounds.X + 23, bounds.Y + bounds.W * .5f, 14,
            new(accent.X * .35f, accent.Y * .35f, accent.Z * .35f, 1));
        DrawCombatSkillIcon(
            icon,
            new(bounds.X + 9, bounds.Y + bounds.W * .5f - 14, 28, 28));
        DrawUiText(
            stance.ToString(),
            new(bounds.X + 44, bounds.Y + 6),
            selected
                ? new FSColor(245, 225, 169, 255)
                : new FSColor(210, 199, 164, 255));
        DrawSmallCenteredUiText(
            stance switch
            {
                MeleeCombatStance.Accurate => "Trains Attack",
                MeleeCombatStance.Aggressive => "Trains Strength",
                _ => "Trains Defence"
            },
            new(bounds.X + 42, bounds.Y + 24, bounds.Z - 49, 14),
            selected
                ? new FSColor(198, 184, 143, 255)
                : new FSColor(153, 145, 119, 255));
    }

    private static Vector4 CombatStanceBounds(
        Vector4 panel, int index) =>
        new(
            panel.X + 10,
            panel.Y + 45 + index * 58,
            panel.Z - 20,
            51);

    private static string CombatStatName(MeleeCombatStance stance) =>
        stance switch
        {
            MeleeCombatStance.Accurate => "Attack",
            MeleeCombatStance.Aggressive => "Strength",
            _ => "Defence"
        };

    private void ApplyPlayerCombatExperience(
        SkillExperienceChange experience)
    {
        if (_activePlayer is null || experience.Gained <= 0) return;
        _activePlayer = _activePlayer.CombatStance switch
        {
            MeleeCombatStance.Accurate => _activePlayer with
            {
                AttackExperience = experience.Experience
            },
            MeleeCombatStance.Aggressive => _activePlayer with
            {
                StrengthExperience = experience.Experience
            },
            _ => _activePlayer with
            {
                DefenceExperience = experience.Experience
            }
        };
        AwardAdventureExperience(experience.Gained);
        _activePlayer = _activePlayer with { UpdatedUtc = DateTime.UtcNow };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{experience.Gained} " +
            $"{CombatStatName(_activePlayer.CombatStance)} XP.",
            ChatMessageStyle.Experience);
        if (experience.LevelledUp)
            _chatUi.AddMessage(
                $"Your {CombatStatName(_activePlayer.CombatStance)} " +
                $"level is now {experience.Level}.",
                ChatMessageStyle.LevelUp);
    }
}
