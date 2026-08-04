using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void UpdateSocialIncidentAftermath()
    {
        if (_settlementGroup is not { ActiveAftermath: { } aftermath } group ||
            _activeWorld is null || _player is null)
            return;
        if (SocialIncidentAftermathService.Finished(
                aftermath, _worldGameSeconds))
        {
            _settlementGroup = group with { ActiveAftermath = null };
            _saves.SaveSettlementGroup(
                _activeWorld.Id, _settlementGroup);
            return;
        }
        if (_worldGameSeconds < aftermath.ReadyGameSeconds) return;

        foreach (var assignment in aftermath.Assignments)
        {
            if (assignment.Completed) continue;
            var actorIndex = VillagerIndex(assignment.ActorId);
            if (actorIndex < 0) continue;
            var actor = _villagers[actorIndex];
            if (actor.Health <= 0 ||
                actor.ConflictIntent != VillagerConflictIntent.None ||
                _npcController.IsBusy(actor.Id))
                continue;
            var targetIndex = VillagerIndex(assignment.TargetId);
            var targetsPlayer = _activePlayer?.Id == assignment.TargetId;
            var targetPosition = targetsPlayer
                ? _player.Position
                : targetIndex >= 0
                    ? new Vector2(
                        _villagers[targetIndex].PositionX,
                        _villagers[targetIndex].PositionY)
                    : new Vector2(float.NaN);
            if (!float.IsFinite(targetPosition.X))
            {
                CompleteAftermathWithoutOutcome(group, aftermath, actor.Id);
                return;
            }
            var distanceSquared = Vector2.DistanceSquared(
                new(actor.PositionX, actor.PositionY), targetPosition);
            if (targetsPlayer && distanceSquared > 12 * 12)
            {
                CompleteAftermathWithoutOutcome(group, aftermath, actor.Id);
                return;
            }
            if (distanceSquared > 1.75f * 1.75f)
            {
                MoveVillagerForCapability(
                    actorIndex, actor, VillagerSimulationTier.Nearby,
                    targetPosition, VillagerNeed.Social);
                continue;
            }
            if (ConversationFloorBusy) return;
            var victimIndex = VillagerIndex(aftermath.VictimId);
            var aggressorName = _activePlayer?.Id == aftermath.AggressorId
                ? _activePlayer.Name
                : _villagers.FirstOrDefault(value =>
                    value.Id == aftermath.AggressorId)?.Name ?? "the attacker";
            var victimName = victimIndex >= 0
                ? _villagers[victimIndex].Name : "the victim";
            var targetName = targetsPlayer
                ? aggressorName
                : _villagers[targetIndex].Name;
            var speech = SocialIncidentAftermathService.Speech(
                assignment, aggressorName, victimName, targetName);
            ShowVillagerSpeech(actorIndex, speech, targetPosition);
            actor = _villagers[actorIndex];
            actor = VillagerSimulation.RecordDialogueTurn(
                actor, actor.Id, actor.Name, speech, _worldGameSeconds);
            actor = SocialIncidentAftermathService.RecordCompletedInteraction(
                actor, aftermath, assignment,
                aggressorName, victimName, targetName,
                _worldGameSeconds);
            _villagers[actorIndex] = actor;
            if (targetIndex >= 0)
            {
                var target = _villagers[targetIndex];
                target = VillagerSimulation.RecordDialogueTurn(
                    target, actor.Id, actor.Name,
                    speech, _worldGameSeconds);
                target = SocialIncidentAftermathService.RecordReceivedSupport(
                    target, aftermath, assignment,
                    actor.Name, _worldGameSeconds);
                if (assignment.Role ==
                    SocialAftermathRole.ShareAccount)
                    target = SocialIncidentAftermathService.RecordHeardAccount(
                        target, actor, aftermath,
                        aggressorName, victimName,
                        _worldGameSeconds);
                _villagers[targetIndex] = target;
            }
            aftermath = SocialIncidentAftermathService.Complete(
                aftermath, actor.Id);
            _settlementGroup = group = group with
            {
                ActiveAftermath = aftermath
            };
            _saves.SaveSettlementGroup(_activeWorld.Id, group);
            _villagersDirty = true;
            ObserveLog("social_incident_aftermath", actor.Id, new
            {
                Role = assignment.Role.ToString(),
                assignment.TargetId,
                aftermath.IncidentId
            });
            return;
        }
    }

    private void CompleteAftermathWithoutOutcome(
        SettlementGroupState group,
        SocialIncidentAftermathState aftermath,
        string actorId)
    {
        aftermath = SocialIncidentAftermathService.Complete(
            aftermath, actorId);
        _settlementGroup = group with { ActiveAftermath = aftermath };
        _saves.SaveSettlementGroup(
            _activeWorld!.Id, _settlementGroup);
    }
}
