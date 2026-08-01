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
        var roll = MeleeCombatService.Roll(
            villager.AttackExperience,
            villager.StrengthExperience,
            DeterministicRoll(villager.Id, $"npc-hit:{target.Id}"),
            DeterministicRoll(villager.Id, $"npc-damage:{target.Id}"),
            villager.Inventory);
        var attackXp = SkillService.AwardExperience(
            villager.AttackExperience, roll.Experience);
        _villagers[index] = villager with
        {
            AttackExperience = attackXp.Experience,
            Action = EntityAction.Attack,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                MeleeCombatService.AttackIntervalSeconds *
                VillagerSimulation.GameSecondsPerRealSecond
        };
        if (roll.Hit)
        {
            target = VillagerSimulation.RecordAttack(
                target, villager.Id, villager.Name,
                roll.Damage, _worldGameSeconds);
            _villagers[targetIndex] = target;
            ObserveNpcConflictWitnesses(index, targetIndex);
            if (target.Health > 0)
                StartVillagerConflict(
                    index, targetIndex,
                    villager.ConflictMotive ?? "attack", true);
        }
        ObserveLog("conflict_attack", villager.Id, new
        {
            TargetId = target.Id,
            roll.Hit,
            Damage = roll.Hit ? roll.Damage : 0,
            Intent = villager.ConflictIntent.ToString()
        });
        _villagersDirty = true;
        return true;
    }

    private void RequestConflictHelp(int callerIndex, int aggressorIndex)
    {
        var caller = _villagers[callerIndex];
        var aggressor = _villagers[aggressorIndex];
        var helperIndex = -1;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == callerIndex || index == aggressorIndex) continue;
            var candidate = _villagers[index];
            var trust = candidate.Relationships?.FirstOrDefault(value =>
                value.CharacterId == caller.Id)?.State.Trust ?? 0;
            var distance = Vector2.DistanceSquared(
                new(candidate.PositionX, candidate.PositionY),
                new(caller.PositionX, caller.PositionY));
            if (candidate.Health <= 20 || candidate.Boldness < .55f ||
                candidate.WorldLevel != caller.WorldLevel || trust <= 0 ||
                distance > 10 * 10 || distance >= bestDistance)
                continue;
            helperIndex = index;
            bestDistance = distance;
        }
        if (helperIndex < 0) return;
        var helper = _villagers[helperIndex];
        if (helper.ConflictTargetId == aggressor.Id) return;
        _villagers[helperIndex] = VillagerConflictService.ApplyDecision(
            helper, aggressor,
            new(VillagerConflictIntent.Defend,
                $"{caller.Name} needs help. I should defend them.",
                70, true),
            $"help {caller.Name}", _worldGameSeconds);
        ObserveLog("call_for_help", caller.Id, new
        {
            HelperId = helper.Id,
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
            var victimTrust = witness.Relationships?.FirstOrDefault(value =>
                value.CharacterId == victim.Id)?.State.Trust ?? 0;
            if (victimTrust > 5 && witness.Boldness >= .55f)
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
            (candidate.Relationships?.FirstOrDefault(value =>
                value.CharacterId == victim.Id)?.State.Trust ?? 0) > 0);

    private void EndVillagerConflict(
        int index, VillagerState villager, string reason)
    {
        _villagers[index] = VillagerConflictService.Clear(
            villager, _worldGameSeconds);
        _villagersDirty = true;
        ObserveLog("conflict_deescalated", villager.Id, new { Reason = reason });
    }
}
