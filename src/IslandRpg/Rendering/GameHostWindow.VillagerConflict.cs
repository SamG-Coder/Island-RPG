using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void StartVillagerConflict(
        int aggressorIndex,
        int victimIndex,
        string motive,
        bool wasAttacked)
    {
        if ((uint)aggressorIndex >= (uint)_villagers.Count ||
            (uint)victimIndex >= (uint)_villagers.Count ||
            aggressorIndex == victimIndex)
            return;
        var aggressor = _villagers[aggressorIndex];
        var victim = _villagers[victimIndex];
        if (aggressor.Health <= 0 || victim.Health <= 0) return;
        var allies = CountConflictAllies(victim, aggressor.Id);
        var decision = VillagerConflictService.DecideResponse(
            victim, aggressor, wasAttacked, allies);
        victim = VillagerConflictService.ApplyDecision(
            victim, aggressor, decision, motive, _worldGameSeconds);
        _villagers[victimIndex] = victim;
        _villagersDirty = true;
        ObserveLog("conflict_decision", victim.Id, new
        {
            AggressorId = aggressor.Id,
            Motive = motive,
            Intent = decision.Intent.ToString(),
            decision.Thought,
            decision.Risk
        });
    }

    private bool TryVillagerResolveNpcConflict(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Health <= 0) return false;
        if (villager.ConflictIntent == VillagerConflictIntent.None)
            return false;
        var targetIndex = _villagers.FindIndex(value =>
            value.Id == villager.ConflictTargetId);
        if (targetIndex < 0 || targetIndex == index ||
            villager.ConflictExpiresGameSeconds <= _worldGameSeconds)
        {
            EndVillagerConflict(index, villager, "expired");
            return true;
        }
        var target = _villagers[targetIndex];
        if (target.Health <= 0 || target.WorldLevel != villager.WorldLevel)
        {
            EndVillagerConflict(index, villager, "target_unavailable");
            return true;
        }
        var position = new Vector2(villager.PositionX, villager.PositionY);
        var targetPosition = new Vector2(target.PositionX, target.PositionY);
        if (villager.ConflictIntent is VillagerConflictIntent.Surrender or
            VillagerConflictIntent.Warn)
        {
            EndVillagerConflict(index, villager,
                villager.ConflictIntent.ToString().ToLowerInvariant());
            return true;
        }
        if (villager.ConflictIntent is VillagerConflictIntent.Flee or
            VillagerConflictIntent.CallForHelp)
        {
            if (villager.ConflictIntent == VillagerConflictIntent.CallForHelp)
                RequestConflictHelp(index, targetIndex);
            var away = position - targetPosition;
            if (away.LengthSquared <= .001f) away = Vector2.UnitX;
            MoveVillagerForCapability(
                index, villager, tier,
                position + away.Normalized() * 5,
                VillagerNeed.Safe);
            return true;
        }
        if (villager.ConflictIntent is not
            (VillagerConflictIntent.Defend or
             VillagerConflictIntent.Retaliate))
        {
            EndVillagerConflict(index, villager, "deescalated");
            return true;
        }
        if (Vector2.DistanceSquared(position, targetPosition) >
            MeleeCombatService.AttackRange * MeleeCombatService.AttackRange)
        {
            MoveVillagerForCapability(
                index, villager, tier, targetPosition, VillagerNeed.Safe);
            return true;
        }
        var targetId = target.Id;
        var intent = new NpcBrainIntent(
            "conflict_attack", EntityAction.Attack,
            targetPosition, targetId);
        return BeginNpcControlledAction(
            index, villager, intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var currentTargetIndex = VillagerIndex(targetId);
                if (actorIndex < 0 || currentTargetIndex < 0)
                    return new(intent, false, "target_unavailable");
                var actor = _villagers[actorIndex];
                var currentTarget = _villagers[currentTargetIndex];
                if (actor.Health <= 0 || currentTarget.Health <= 0)
                    return new(intent, false, "target_unavailable");
                var interaction = EntityInteractionService.TryMeleeAttack(
                    _actionCooldowns,
                    actor.Id,
                    _clock,
                    actor.AttackExperience,
                    actor.StrengthExperience,
                    actor.AttackExperience,
                    DeterministicRoll(actor.Id, $"npc-hit:{targetId}"),
                    DeterministicRoll(actor.Id, $"npc-damage:{targetId}"),
                    actor.Inventory);
                if (!interaction.Succeeded)
                    return new(intent, false, interaction.Failure);
                var roll = interaction.Attack;
                _villagers[actorIndex] = actor with
                {
                    AttackExperience = interaction.Experience.Experience
                };
                if (roll.Hit)
                {
                    currentTarget = VillagerSimulation.RecordAttack(
                        currentTarget, actor.Id, actor.Name,
                        roll.Damage, _worldGameSeconds);
                    _villagers[currentTargetIndex] = currentTarget;
                    ObserveNpcConflictWitnesses(
                        actorIndex, currentTargetIndex);
                    if (currentTarget.Health > 0)
                        StartVillagerConflict(
                            actorIndex, currentTargetIndex,
                            actor.ConflictMotive ?? "attack", true);
                }
                ShowEntityImpact(
                    VillagerFeedbackKey(currentTarget.Id),
                    roll.Hit ? roll.Damage : 0,
                    roll.Hit);
                ObserveLog("conflict_attack", actor.Id, new
                {
                    TargetId = currentTarget.Id,
                    roll.Hit,
                    Damage = roll.Hit ? roll.Damage : 0,
                    Intent = actor.ConflictIntent.ToString()
                });
                _villagersDirty = true;
                return new(intent, true);
            },
            MeleeCombatService.AttackIntervalSeconds *
            VillagerSimulation.GameSecondsPerRealSecond,
            targetAvailable: () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var currentTargetIndex = VillagerIndex(targetId);
                return actorIndex >= 0 && currentTargetIndex >= 0 &&
                       _villagers[actorIndex].Health > 0 &&
                       _villagers[currentTargetIndex].Health > 0 &&
                       Vector2.DistanceSquared(
                           new(_villagers[actorIndex].PositionX,
                               _villagers[actorIndex].PositionY),
                           new(_villagers[currentTargetIndex].PositionX,
                               _villagers[currentTargetIndex].PositionY)) <=
                       MeleeCombatService.AttackRange *
                       MeleeCombatService.AttackRange;
            });
    }

    private void RequestConflictHelp(int callerIndex, int aggressorIndex)
    {
        var caller = _villagers[callerIndex];
        var aggressor = _villagers[aggressorIndex];
        if (!VillagerYellService.CanYell(caller, _worldGameSeconds)) return;
        var helpers = new List<(int Index, float Distance)>();
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == callerIndex || index == aggressorIndex) continue;
            var candidate = _villagers[index];
            var relationship = candidate.Relationships?.FirstOrDefault(value =>
                value.CharacterId == caller.Id)?.State ?? default;
            var distance = Vector2.DistanceSquared(
                new(candidate.PositionX, candidate.PositionY),
                new(caller.PositionX, caller.PositionY));
            var sameSettlement = caller.SettlementGroupId is not null &&
                caller.SettlementGroupId == candidate.SettlementGroupId;
            if (!VillagerYellService.ShouldAnswer(
                    candidate, caller, aggressor.Id,
                    relationship, sameSettlement))
                continue;
            helpers.Add((index, distance));
        }
        caller = VillagerYellService.MarkYelled(caller, _worldGameSeconds);
        _villagers[callerIndex] = caller;
        ShowVillagerCombatReaction(
            callerIndex, $"Help! {aggressor.Name} is attacking me!");
        var responderIds = new List<string>();
        foreach (var (helperIndex, _) in helpers
                     .OrderBy(value => value.Distance))
        {
            var helper = _villagers[helperIndex];
            if (helper.ConflictTargetId == aggressor.Id) continue;
            _villagers[helperIndex] = VillagerConflictService.ApplyDecision(
                helper, aggressor,
                new(VillagerConflictIntent.Defend,
                    $"I heard {caller.Name} yell. I should rush to help.",
                    70, true),
                $"answer {caller.Name}'s yell", _worldGameSeconds);
            responderIds.Add(helper.Id);
        }
        ObserveLog("call_for_help", caller.Id, new
        {
            ResponderIds = responderIds,
            AggressorId = aggressor.Id
        });
        _villagersDirty = true;
    }

    private void ObserveNpcConflictWitnesses(int aggressorIndex, int victimIndex)
    {
        var aggressor = _villagers[aggressorIndex];
        var victim = _villagers[victimIndex];
        var victimPosition = new Vector2(victim.PositionX, victim.PositionY);
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == aggressorIndex || index == victimIndex) continue;
            var witness = _villagers[index];
            if (witness.Health <= 0 ||
                witness.WorldLevel != victim.WorldLevel ||
                Vector2.DistanceSquared(
                    new(witness.PositionX, witness.PositionY),
                    victimPosition) > 10 * 10)
                continue;
            witness = VillagerSimulation.RecordWitnessedAttack(
                witness, aggressor.Id, aggressor.Name,
                victim.Id, victim.Name, _worldGameSeconds);
            var victimRelationship = witness.Relationships?.FirstOrDefault(value =>
                value.CharacterId == victim.Id)?.State ?? default;
            if (VillagerRelationshipClassifier.WillDefend(
                    victimRelationship,
                    victim.Id == witness.RecognizedLeaderId) &&
                witness.Boldness >= .55f)
                witness = VillagerConflictService.ApplyDecision(
                    witness, aggressor,
                    new(VillagerConflictIntent.Defend,
                        $"I should defend {victim.Name}.", 70, true),
                    $"defend {victim.Name}", _worldGameSeconds);
            _villagers[index] = witness;
        }
    }

    private int CountConflictAllies(VillagerState victim, string aggressorId) =>
        _villagers.Count(candidate =>
            candidate.Id != victim.Id && candidate.Id != aggressorId &&
            candidate.Health > 0 &&
            candidate.WorldLevel == victim.WorldLevel &&
            Vector2.DistanceSquared(
                new(candidate.PositionX, candidate.PositionY),
                new(victim.PositionX, victim.PositionY)) <= 10 * 10 &&
            VillagerRelationshipClassifier.WillDefend(
                candidate.Relationships?.FirstOrDefault(value =>
                    value.CharacterId == victim.Id)?.State ?? default,
                victim.Id == candidate.RecognizedLeaderId));

    private void EndVillagerConflict(
        int index, VillagerState villager, string reason)
    {
        _villagers[index] = VillagerConflictService.Clear(
            villager, _worldGameSeconds);
        _villagersDirty = true;
        ObserveLog("conflict_deescalated", villager.Id, new { Reason = reason });
    }
}
