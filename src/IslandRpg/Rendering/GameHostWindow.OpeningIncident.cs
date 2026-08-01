using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private enum OpeningPlaybackPhase : byte { Approaching, Performing }

    private sealed record OpeningPlayback(
        Guid EventId,
        string Kind,
        string ActorId,
        string OtherId,
        string Fallback,
        Vector2 Target,
        Guid? CargoId)
    {
        public OpeningPlaybackPhase Phase { get; set; }
        public double PhaseStartedAt { get; set; }
        public Guid? VisibleCargoId { get; set; } = CargoId;
    }

    private readonly Queue<OpeningPlayback> _openingIncidentQueue = [];
    private readonly Dictionary<string, VillagerState>
        _openingIncidentOutcomes = [];
    private OpeningPlayback? _openingIncidentCurrent;
    private double _openingIncidentDeadline;
    private double _nextOpeningShoreDamageAt;

    private void InitializeOpeningIncident(
        IReadOnlyList<VillagerState> arrivals, Vector2 center)
    {
        _openingIncidentQueue.Clear();
        _openingIncidentOutcomes.Clear();
        _openingIncidentCurrent = null;
        var outcomes = VillagerOpeningIncidentService.Apply(
            arrivals, _worldSeed, _worldGameSeconds);
        foreach (var outcome in outcomes)
            _openingIncidentOutcomes[outcome.Id] = outcome;

        for (var index = 0; index < arrivals.Count; index++)
        {
            var arrival = arrivals[index];
            var radial = VillagerGroupConversationService.CircleOffset(
                arrival.Id, index, arrivals.Count);
            var desired = center + new Vector2(radial.X, radial.Y) * 2.2f;
            var scattered = WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed, center, desired, arrival.WorldLevel,
                maximumRadius: 4);
            var outcome = _openingIncidentOutcomes[arrival.Id];
            var injured = outcome.Health < arrival.Health;
            _villagers.Add(arrival with
            {
                PositionX = scattered.X,
                PositionY = scattered.Y,
                Health = outcome.Health,
                Action = injured ? EntityAction.Hurt : EntityAction.Idle,
                Activity = injured
                    ? VillagerActivity.Resting
                    : VillagerActivity.Idle,
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerOpeningIncidentService.IncidentRealSeconds *
                    VillagerSimulation.GameSecondsPerRealSecond
            });
        }

        foreach (var group in outcomes.SelectMany(value =>
                     value.Memories ?? [])
                 .Where(value => value.Kind.StartsWith(
                     "wreck_", StringComparison.Ordinal))
                 .GroupBy(value => value.EventId)
                 .OrderBy(value => IncidentPlaybackPriority(
                     value.First().Kind))
                 .ThenBy(value => value.Key))
        {
            var memories = group.ToArray();
            var actorMemory = SelectActorMemory(memories);
            var actor = outcomes.First(value =>
                value.Memories?.Contains(actorMemory) == true);
            var other = outcomes.FirstOrDefault(value =>
                value.Id == actorMemory.SubjectId);
            if (other is null || other.Id == actor.Id) continue;
            var target = InteractionPoint(actor, other, center);
            var cargoId = actorMemory.Kind == "wreck_dispute"
                ? SpawnOpeningCargo(target)
                : null;
            _openingIncidentQueue.Enqueue(new(
                actorMemory.EventId, actorMemory.Kind,
                actor.Id, other.Id,
                actorMemory.Summary ?? "There was confusion by the wreck.",
                target, cargoId));
        }
        foreach (var playback in _openingIncidentQueue)
            if (playback.Kind == "wreck_rescue")
                StageOpeningRescuer(
                    playback.ActorId, playback.OtherId);
        _openingIncidentDeadline = _clock +
            VillagerOpeningIncidentService.IncidentRealSeconds;
        _nextOpeningShoreDamageAt = _clock + 2;
    }

    private bool UpdateOpeningIncident()
    {
        if (_openingIncidentCurrent is null &&
            !_openingIncidentQueue.TryDequeue(out _openingIncidentCurrent))
            return false;
        if (_clock >= _openingIncidentDeadline)
        {
            FinishOpeningIncident(false, "opening_timeout");
            _openingIncidentQueue.Clear();
            ReleaseOpeningIncidentActors();
            return false;
        }

        var playback = _openingIncidentCurrent!;
        ApplyOpeningShoreDamage();
        var actorIndex = _villagers.FindIndex(value =>
            value.Id == playback.ActorId);
        var otherIndex = _villagers.FindIndex(value =>
            value.Id == playback.OtherId);
        if (actorIndex < 0 || otherIndex < 0 ||
            _villagers[actorIndex].Health <= 0 ||
            _villagers[otherIndex].Health <= 0)
        {
            FinishOpeningIncident(false, "participant_unavailable");
            return _openingIncidentQueue.Count > 0;
        }

        if (playback.Phase == OpeningPlaybackPhase.Approaching)
        {
            DirectOpeningParticipants(playback, actorIndex, otherIndex);
            if (!OpeningParticipantsArrived(
                    playback, actorIndex, otherIndex))
                return true;
            BeginOpeningPerformance(playback, actorIndex, otherIndex);
            return true;
        }

        if (_clock - playback.PhaseStartedAt < 3.5 ||
            _npcAiDialogueTask is not null)
        {
            if (playback.Kind is "wreck_rescue" or "wreck_abandonment")
                SetOpeningPose(otherIndex, EntityAction.Hurt);
            return true;
        }
        FinishOpeningIncident(true, "performed");
        return _openingIncidentQueue.Count > 0;
    }

    private void DirectOpeningParticipants(
        OpeningPlayback playback, int actorIndex, int otherIndex)
    {
        if (playback.PhaseStartedAt == 0)
            playback.PhaseStartedAt = _clock;
        if (playback.Kind == "wreck_dispute" &&
            playback.VisibleCargoId is null)
            playback.VisibleCargoId = SpawnOpeningCargo(playback.Target);
        if (playback.Kind == "wreck_abandonment")
        {
            var victim = _villagers[otherIndex];
            var away = new Vector2(
                _villagers[actorIndex].PositionX - victim.PositionX,
                _villagers[actorIndex].PositionY - victim.PositionY);
            away = away.LengthSquared > .01f
                ? Vector2.Normalize(away)
                : Vector2.UnitX;
            MoveOpeningActor(actorIndex,
                new Vector2(victim.PositionX, victim.PositionY) + away * 7);
            SetOpeningPose(otherIndex, EntityAction.Hurt);
            return;
        }
        if (playback.Kind == "wreck_rescue")
        {
            var injured = _villagers[otherIndex];
            SetOpeningPose(otherIndex, EntityAction.Hurt);
            MoveOpeningActor(actorIndex,
                new Vector2(injured.PositionX + .7f, injured.PositionY));
            return;
        }
        MoveOpeningActor(actorIndex, playback.Target + new Vector2(.7f, 0));
        MoveOpeningActor(otherIndex, playback.Target - new Vector2(.7f, 0));
    }

    private bool OpeningParticipantsArrived(
        OpeningPlayback playback, int actorIndex, int otherIndex)
    {
        var actor = new Vector2(
            _villagers[actorIndex].PositionX,
            _villagers[actorIndex].PositionY);
        var other = new Vector2(
            _villagers[otherIndex].PositionX,
            _villagers[otherIndex].PositionY);
        return playback.Kind == "wreck_abandonment"
            ? Vector2.DistanceSquared(actor, other) >= 25
            : playback.Kind == "wreck_rescue"
                ? Vector2.DistanceSquared(actor, other) <= 2.25f
            : Vector2.DistanceSquared(actor, playback.Target) <= 2.25f &&
              Vector2.DistanceSquared(other, playback.Target) <= 3.24f;
    }

    private void BeginOpeningPerformance(
        OpeningPlayback playback, int actorIndex, int otherIndex)
    {
        playback.Phase = OpeningPlaybackPhase.Performing;
        playback.PhaseStartedAt = _clock;
        SpeakVillagerDialogue(
            _villagers[actorIndex], _villagers[otherIndex].Id,
            _villagers[otherIndex].Name,
            VillagerSocialIntent.AskSurvival,
            playback.Fallback,
            allowNpcReply: false);
        SetOpeningPose(actorIndex, playback.Kind switch
        {
            "wreck_dispute" or "wreck_rescue" => EntityAction.Gather,
            _ => EntityAction.Idle
        });
        SetOpeningPose(otherIndex, playback.Kind switch
        {
            "wreck_rescue" or "wreck_abandonment" => EntityAction.Hurt,
            "wreck_dispute" => EntityAction.Gather,
            _ => EntityAction.Idle
        });
        ObserveLog("opening_incident_performed", playback.ActorId, new
        {
            playback.EventId,
            playback.Kind,
            playback.OtherId,
            CargoId = playback.VisibleCargoId
        });
    }

    private void FinishOpeningIncident(bool succeeded, string reason)
    {
        var playback = _openingIncidentCurrent;
        if (playback is null) return;
        if (succeeded) CommitOpeningOutcome(playback.EventId);
        ObserveLog("opening_incident_result", playback.ActorId, new
        {
            playback.EventId,
            playback.Kind,
            Succeeded = succeeded,
            Reason = reason
        });
        ReleaseOpeningActor(playback.ActorId);
        ReleaseOpeningActor(playback.OtherId);
        _openingIncidentCurrent = null;
    }

    private void CommitOpeningOutcome(Guid eventId)
    {
        for (var index = 0; index < _villagers.Count; index++)
        {
            var current = _villagers[index];
            if (!_openingIncidentOutcomes.TryGetValue(
                    current.Id, out var outcome))
                continue;
            var eventMemories = (outcome.Memories ?? [])
                .Where(value => value.EventId == eventId).ToArray();
            if (eventMemories.Length == 0) continue;
            var memories = (current.Memories ?? []).ToList();
            memories.AddRange(eventMemories.Where(memory =>
                memories.All(value => value.EventId != memory.EventId)));
            var relatedIds = eventMemories.Select(value => value.SubjectId)
                .ToHashSet(StringComparer.Ordinal);
            var relationships = (current.Relationships ?? []).ToList();
            foreach (var finalRelationship in outcome.Relationships ?? [])
            {
                if (!relatedIds.Contains(finalRelationship.CharacterId))
                    continue;
                var relationIndex = relationships.FindIndex(value =>
                    value.CharacterId == finalRelationship.CharacterId);
                if (relationIndex >= 0)
                    relationships[relationIndex] = finalRelationship;
                else relationships.Add(finalRelationship);
            }
            _villagers[index] = current with
            {
                Memories = memories,
                Relationships = relationships
            };
        }
        _villagersDirty = true;
    }

    private void MoveOpeningActor(int index, Vector2 desired)
    {
        var villager = _villagers[index];
        if (villager.Action == EntityAction.Move &&
            villager.TargetX is not null) return;
        var target = WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed, new(villager.PositionX, villager.PositionY),
            desired, villager.WorldLevel, maximumRadius: 3);
        _villagers[index] = VillagerSimulation.ApplyDecision(
            villager, new(VillagerNeed.Safe, target),
            VillagerSimulationTier.Nearby, _worldGameSeconds) with
        {
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerOpeningIncidentService.IncidentRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond
        };
    }

    private void SetOpeningPose(int index, EntityAction action)
    {
        var villager = _villagers[index];
        if (villager.Action == action) return;
        _villagers[index] = villager with
        {
            Action = action,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerOpeningIncidentService.IncidentRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond
        };
    }

    private void ReleaseOpeningIncidentActors()
    {
        for (var index = 0; index < _villagers.Count; index++)
            ReleaseOpeningActor(_villagers[index].Id);
    }

    private void ReleaseOpeningActor(string actorId)
    {
        var index = _villagers.FindIndex(value => value.Id == actorId);
        if (index < 0) return;
        _villagers[index] = VillagerSimulation.CompleteAction(
            _villagers[index]) with
        {
            NextDecisionGameSeconds = _worldGameSeconds
        };
    }

    private Vector2 InteractionPoint(
        VillagerState first, VillagerState second, Vector2 fallback)
    {
        var desired = (new Vector2(first.PositionX, first.PositionY) +
                       new Vector2(second.PositionX, second.PositionY)) * .5f;
        return WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed, fallback, desired, first.WorldLevel,
            maximumRadius: 3);
    }

    private Guid? SpawnOpeningCargo(Vector2 target)
    {
        if (!TryGetDropTerrain(
                (int)MathF.Floor(target.X),
                (int)MathF.Floor(target.Y),
                out var gpu, out _))
            return null;
        var id = Guid.NewGuid();
        gpu.Chunk.GroundObjects.Add(new(
            id, ItemIds.StorageBarrel, target.X, target.Y));
        QueueChunkSave(gpu.Chunk);
        return id;
    }

    private static VillagerMemory SelectActorMemory(
        IReadOnlyList<VillagerMemory> memories) =>
        memories.FirstOrDefault(value =>
            value.Summary?.StartsWith("I ", StringComparison.Ordinal) == true)
        ?? memories[0];

    private void ApplyOpeningShoreDamage()
    {
        if (_clock < _nextOpeningShoreDamageAt) return;
        _nextOpeningShoreDamageAt = _clock + 2;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var villager = _villagers[index];
            if (villager.Health <= 0 ||
                villager.Action != EntityAction.Hurt)
                continue;
            var biome = SamplePlayerTerrain(
                villager.PositionX, villager.PositionY).Biome;
            var exposed = VillagerOpeningIncidentService.ApplyShoreExposure(
                villager, biome);
            if (ReferenceEquals(exposed, villager)) continue;
            var health = exposed.Health;
            _villagers[index] = exposed;
            ObserveLog("opening_shore_injury", villager.Id, new
            {
                Health = health,
                Biome = biome.ToString()
            });
            _villagersDirty = true;
        }
    }

    private static int IncidentPlaybackPriority(string kind) =>
        kind switch
        {
            "wreck_rescue" => 0,
            "wreck_abandonment" => 1,
            "wreck_dispute" => 2,
            _ => 3
        };

    private void StageOpeningRescuer(string helperId, string injuredId)
    {
        var helperIndex = _villagers.FindIndex(value => value.Id == helperId);
        var injuredIndex = _villagers.FindIndex(value => value.Id == injuredId);
        if (helperIndex < 0 || injuredIndex < 0) return;
        var helper = _villagers[helperIndex];
        var injured = _villagers[injuredIndex];
        var injuredPosition = new Vector2(
            injured.PositionX, injured.PositionY);
        var direction = new Vector2(
            helper.PositionX - injured.PositionX,
            helper.PositionY - injured.PositionY);
        direction = direction.LengthSquared > .01f
            ? Vector2.Normalize(direction)
            : Vector2.UnitX;
        var staged = WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed,
            injuredPosition,
            injuredPosition + direction * 3.5f,
            injured.WorldLevel,
            maximumRadius: 3);
        _villagers[helperIndex] = helper with
        {
            PositionX = staged.X,
            PositionY = staged.Y,
            Action = EntityAction.Idle,
            Activity = VillagerActivity.Idle,
            TargetX = null,
            TargetY = null
        };
    }
}
