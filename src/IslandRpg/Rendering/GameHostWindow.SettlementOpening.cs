using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool UpdateSettlementOpening()
    {
        if (_settlementGroup is not { } group ||
            !SettlementOpeningService.IsOpeningActive(group))
            return false;
        if (group.OpeningStage == SettlementOpeningStage.Reconnaissance)
        {
            if (group.ScoutAssignments is not { Count: > 0 })
            {
                group = SettlementOpeningService.AssignScouts(
                    group,
                    _villagers,
                    target => WorldLevelNavigation.ReachableWalkableTarget(
                        _worldSeed,
                        group.Camp,
                        target,
                        group.WorldLevel,
                        maximumRadius: 8));
                SaveSettlementOpening(group);
                SpeakSettlementLeader(
                    "Before we choose our camp, scout different ground. Find water, food, wood, stone, danger, and somewhere defensible.");
                ObserveLog("settlement_reconnaissance_requested", group.LeaderId,
                    new { Assignments = group.ScoutAssignments });
            }
            UpdateSettlementScouts(group);
            return true;
        }
        if (group.OpeningStage == SettlementOpeningStage.ComparingCamps)
        {
            if (group.CampResponses is null)
            {
                SaveSettlementOpening(group with { CampResponses = [] });
                SpeakSettlementLeader(CompareCampReports(group));
                ObserveLog("settlement_camps_compared", group.LeaderId, new
                {
                    Candidates = group.ScoutReports
                });
                return true;
            }
            var decided = SettlementOpeningService.DecideCamp(
                group, _villagers);
            var selected = SettlementOpeningService.BestCamp(group);
            SaveSettlementOpening(decided);
            ApplySettlementMembership(decided);
            if (selected is not null)
                SpeakSettlementLeader(
                    $"We will make camp at {DirectionFrom(group.Camp, decided.Camp)}. Bring what you carry; our first store will be a shared cache on the ground.");
            foreach (var response in decided.CampResponses ?? [])
                SpeakSettlementMember(response.VillagerId, response.Reason);
            ObserveLog("settlement_camp_decided", decided.LeaderId, new
            {
                Camp = new { X = decided.CampX, Y = decided.CampY },
                decided.CampResponses,
                Members = decided.MemberIds
            });
            return true;
        }
        if (group.OpeningStage == SettlementOpeningStage.MovingToCamp)
        {
            var present = true;
            foreach (var memberId in group.MemberIds)
            {
                var index = _villagers.FindIndex(value =>
                    value.Id == memberId && value.Health > 0);
                if (index < 0) continue;
                var villager = _villagers[index];
                var offset = VillagerGroupConversationService.CircleOffset(
                    villager.Id, index, Math.Max(1, group.MemberIds.Count));
                var desired = group.Camp + new Vector2(offset.X, offset.Y) * .45f;
                if (Vector2.DistanceSquared(
                        new(villager.PositionX, villager.PositionY), desired) <=
                    SettlementOpeningService.ArrivalRadius *
                    SettlementOpeningService.ArrivalRadius)
                    continue;
                present = false;
                if (VillagerIntentPriorityService.HasUrgentOverride(villager))
                    continue;
                MoveSettlementVillager(index, villager, desired,
                    VillagerNeed.Safe);
            }
            if (!present) return true;
            group = SettlementOpeningService.CompleteMove(group);
            SaveSettlementOpening(group);
            SpeakSettlementLeader(
                "This is our camp. Put project supplies in the shared ground cache here before we attempt permanent storage.");
            ObserveLog("settlement_cache_established", group.LeaderId, new
            {
                GroupId = group.Id,
                Camp = new { X = group.CampX, Y = group.CampY }
            });
            return false;
        }
        return false;
    }

    private void UpdateSettlementScouts(SettlementGroupState group)
    {
        foreach (var assignment in group.ScoutAssignments ?? [])
        {
            var index = _villagers.FindIndex(value =>
                value.Id == assignment.ScoutId && value.Health > 0);
            if (index < 0)
            {
                if (!assignment.Reported)
                    SaveSettlementOpening(
                        SettlementOpeningService.MarkReported(
                            group, assignment.ScoutId));
                return;
            }
            var scout = _villagers[index];
            if (VillagerIntentPriorityService.HasUrgentOverride(scout))
                continue;
            var position = new Vector2(scout.PositionX, scout.PositionY);
            var target = new Vector2(assignment.TargetX, assignment.TargetY);
            if (!assignment.Reached)
            {
                if (Vector2.DistanceSquared(position, target) >
                    SettlementOpeningService.ArrivalRadius *
                    SettlementOpeningService.ArrivalRadius)
                {
                    MoveSettlementVillager(
                        index, scout, target, VillagerNeed.Explore);
                    continue;
                }
                var report = AssessSettlementScout(scout, target);
                group = SettlementOpeningService.RecordReport(group, report);
                SaveSettlementOpening(group);
                ObserveLog("settlement_scout_sector_reached", scout.Id, report);
            }
            if (Vector2.DistanceSquared(position, group.Camp) >
                SettlementOpeningService.ArrivalRadius *
                SettlementOpeningService.ArrivalRadius)
            {
                MoveSettlementVillager(
                    index, scout, group.Camp, VillagerNeed.Social);
                continue;
            }
            var completed = (group.ScoutAssignments ?? []).First(value =>
                value.ScoutId == scout.Id);
            if (completed.Reported) continue;
            var scoutReport = group.ScoutReports!.First(value =>
                value.ScoutId == scout.Id);
            SpeakSettlementMember(scout.Id, ScoutReportText(scoutReport));
            group = SettlementGroupService.ReportDiscoveries(group, scout);
            group = SettlementOpeningService.MarkReported(group, scout.Id);
            SaveSettlementOpening(group);
            ObserveLog("settlement_scout_reported", scout.Id, scoutReport);
            return;
        }
    }

    private SettlementScoutReport AssessSettlementScout(
        VillagerState scout, Vector2 position)
    {
        const float radius = 9;
        var memories = scout.LocationMemories ?? [];
        bool HasMemory(VillagerLocationType type) => memories.Any(value =>
            value.Type == type && value.WorldLevel == scout.WorldLevel &&
            Vector2.DistanceSquared(
                new(value.PositionX, value.PositionY), position) <=
            radius * radius);
        var nearbyItems = _worldChunks.Values
            .Where(IsActiveSimulationChunk)
            .SelectMany(value => value.Chunk.GroundObjects)
            .Where(value => Vector2.DistanceSquared(
                new(value.X, value.Y), position) <= radius * radius)
            .ToArray();
        var food = HasMemory(VillagerLocationType.FoodSource) ||
                   nearbyItems.Any(value =>
                       SurvivalService.TryFoodEffect(value.ItemId, out _));
        var wood = HasMemory(VillagerLocationType.WoodSource) ||
                   nearbyItems.Any(value => ItemCatalog.TryGet(
                       value.ItemId, out var item) &&
                       (item.HasTag(ItemTag.Log) ||
                        item.HasTag(ItemTag.WoodcuttingMaterial)));
        var stone = nearbyItems.Any(value =>
            value.ItemId is ItemIds.LargeRock or ItemIds.MediumRock or
                ItemIds.SmallRocks || ItemCatalog.TryGet(
                value.ItemId, out var item) &&
                item.HasTag(ItemTag.MiningMaterial));
        var water = false;
        for (var y = -6; y <= 6 && !water; y += 3)
        for (var x = -6; x <= 6 && !water; x += 3)
            water = InfiniteWorldGenerator.BiomeAt(
                _worldSeed,
                (int)MathF.Floor(position.X) + x,
                (int)MathF.Floor(position.Y) + y) is
                Biome.ShallowWater or Biome.RiverWater or
                Biome.MangroveShallows;
        var danger = HasMemory(VillagerLocationType.Danger);
        var centerHeight = InfiniteWorldGenerator.SampleRenderedHeight(
            _worldSeed, position.X, position.Y);
        var surroundingHeight = 0f;
        for (var index = 0; index < 8; index++)
        {
            var angle = index / 8f * MathF.Tau;
            surroundingHeight += InfiniteWorldGenerator.SampleRenderedHeight(
                _worldSeed,
                position.X + MathF.Cos(angle) * 5,
                position.Y + MathF.Sin(angle) * 5);
        }
        var defensible = centerHeight >= surroundingHeight / 8f + .15f;
        var score = 20 + (water ? 22 : -18) + (food ? 15 : -8) +
                    (wood ? 13 : -6) + (stone ? 10 : -4) +
                    (defensible ? 12 : 0) - (danger ? 35 : 0);
        return new(
            scout.Id, position.X, position.Y,
            water, food, wood, stone, danger, defensible,
            score, _worldGameSeconds);
    }

    private void MoveSettlementVillager(
        int index, VillagerState villager, Vector2 desired, VillagerNeed need)
    {
        var position = new Vector2(villager.PositionX, villager.PositionY);
        var target = WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed, position, desired, villager.WorldLevel,
            maximumRadius: 4);
        if (villager.TargetX is { } x && villager.TargetY is { } y &&
            Vector2.DistanceSquared(new(x, y), target) < .5f * .5f)
            return;
        _npcController.Cancel(villager.Id);
        _villagerWork.ReleaseActor(villager.Id);
        _villagers[index] = VillagerSimulation.ApplyDecision(
            villager,
            new(need, target),
            VillagerSimulationTier.Nearby,
            _worldGameSeconds);
        _villagersDirty = true;
    }

    private void SaveSettlementOpening(SettlementGroupState group)
    {
        _settlementGroup = group;
        if (_activeWorld is not null)
            _saves.SaveSettlementGroup(_activeWorld.Id, group);
        _villagersDirty = true;
    }

    private void ApplySettlementMembership(SettlementGroupState group)
    {
        var departingIds = (group.CampResponses ?? [])
            .Where(value =>
                value.Response == SettlementCampResponseKind.Leave)
            .Select(value => value.VillagerId)
            .ToHashSet(StringComparer.Ordinal);
        if (!IndependentSurvivorPolicy.CanFormSettlement(
                group.MemberIds.Count))
        {
            DissolveSettlementAfterSchism(
                group,
                departingIds.FirstOrDefault() ?? group.LeaderId);
            return;
        }
        for (var index = 0; index < _villagers.Count; index++)
        {
            var member = group.MemberIds.Contains(
                _villagers[index].Id, StringComparer.Ordinal);
            _villagers[index] = _villagers[index] with
            {
                SettlementGroupId = member ? group.Id : null,
                IndependentByChoice = departingIds.Contains(
                    _villagers[index].Id) ||
                    _villagers[index].IndependentByChoice,
                WorkRole = member
                    ? _villagers[index].WorkRole
                    : VillagerWorkRole.Unassigned,
                ProjectAssignment = member
                    ? _villagers[index].ProjectAssignment
                    : null
            };
        }
    }

    private void SpeakSettlementLeader(string text) =>
        SpeakSettlementMember(_settlementGroup?.LeaderId, text);

    private void SpeakSettlementMember(string? villagerId, string text)
    {
        if (villagerId is null) return;
        var index = _villagers.FindIndex(value => value.Id == villagerId);
        if (index >= 0) ShowVillagerSpeech(
            index, text,
            new(_villagers[index].PositionX, _villagers[index].PositionY));
    }

    private static string ScoutReportText(SettlementScoutReport report)
    {
        return $"My sector: water {Found(report.Water)}, " +
               $"food {Found(report.Food)}, wood {Found(report.Wood)}, " +
               $"stone {Found(report.Stone)}, " +
               $"defensible ground {Found(report.DefensibleGround)}, " +
               $"danger {Found(report.Danger)}.";
    }

    private static string CompareCampReports(SettlementGroupState group)
    {
        var reports = group.ScoutReports ?? [];
        var best = SettlementOpeningService.BestCamp(group);
        return best is null
            ? "The scouts found no viable alternative. We remain here."
            : $"We have {reports.Count} possible camp sites. " +
              $"The strongest has score {best.CampScore:0}: " +
              $"water {Found(best.Water)}, food {Found(best.Food)}, " +
              $"wood {Found(best.Wood)}, stone {Found(best.Stone)}, " +
              $"defensible ground {Found(best.DefensibleGround)}, " +
              $"danger {Found(best.Danger)}.";
    }

    private static string Found(bool value) => value ? "found" : "not found";

    private static string DirectionFrom(Vector2 origin, Vector2 target)
    {
        var delta = target - origin;
        if (MathF.Abs(delta.X) > MathF.Abs(delta.Y))
            return delta.X >= 0 ? "the eastern ground" : "the western ground";
        return delta.Y >= 0 ? "the southern ground" : "the northern ground";
    }

    private void ApplyLeadershipDeparture(
        string departureId, string leaderId)
    {
        var departureIndex = _villagers.FindIndex(value =>
            value.Id == departureId);
        var leaderIndex = _villagers.FindIndex(value =>
            value.Id == leaderId);
        if (departureIndex < 0 || leaderIndex < 0) return;
        var departing = VillagerSimulation.ApplyDismissal(
            _villagers[departureIndex],
            leaderId,
            _villagers[leaderIndex].Name,
            "The council chose another leader.",
            "I will leave and survive by my own judgment.",
            -30,
            _worldGameSeconds) with
        {
            IndependentByChoice = true,
            SettlementGroupId = null,
            RecognizedLeaderId = null,
            ProjectAssignment = null,
            WorkRole = VillagerWorkRole.Unassigned,
            PersonalCampX = null,
            PersonalCampY = null,
            PersonalCampWorldLevel = null
        };
        var leader = VillagerSimulation.ApplyDismissal(
            _villagers[leaderIndex],
            departing.Id,
            departing.Name,
            "I reject the council and will leave.",
            "You are abandoning us when the work is barely begun.",
            -24,
            _worldGameSeconds);
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == departureIndex || index == leaderIndex ||
                _villagers[index].Health <= 0 ||
                _villagers[index].IndependentByChoice)
                continue;
            _villagers[index] = VillagerSimulation.ApplyDismissal(
                _villagers[index],
                departing.Id,
                departing.Name,
                "I reject the council and will leave.",
                "You are taking your labour away while all of us are at risk.",
                -12,
                _worldGameSeconds);
        }
        var conflict = VillagerConflictService.DecideResponse(
            departing, leader, wasAttacked: false);
        departing = VillagerConflictService.ApplyDecision(
            departing,
            leader,
            conflict,
            "Dispute over leadership and the division of shared supplies.",
            _worldGameSeconds);
        var away = new Vector2(
            departing.PositionX - _settlementCouncilPoint.X,
            departing.PositionY - _settlementCouncilPoint.Y);
        away = away.LengthSquared > .01f
            ? Vector2.Normalize(away)
            : Vector2.UnitX;
        var target = WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed,
            new(departing.PositionX, departing.PositionY),
            new Vector2(departing.PositionX, departing.PositionY) + away * 10,
            departing.WorldLevel,
            maximumRadius: 4);
        departing = VillagerSimulation.ApplyDecision(
            departing,
            new(VillagerNeed.Safe, target),
            VillagerSimulationTier.Nearby,
            _worldGameSeconds) with
        {
            IndependentByChoice = true,
            SettlementGroupId = null,
            RecognizedLeaderId = null,
            ProjectAssignment = null,
            WorkRole = VillagerWorkRole.Unassigned
        };
        _villagers[departureIndex] = departing;
        _villagers[leaderIndex] = leader;
        SpeakVillagerDialogue(
            departing,
            leader.Id,
            "the gathered survivors",
            VillagerSocialIntent.AskSurvival,
            "The council chose another leader. I reject the result and will leave to survive alone. The shared supplies must be divided fairly.",
            allowNpcReply: false);
        ObserveLog("leadership_departure", departing.Id, new
        {
            LeaderId = leader.Id,
            ConflictIntent = conflict.Intent.ToString()
        });
        _villagersDirty = true;
    }

    private void DissolveSettlementAfterSchism(
        SettlementGroupState? previousGroup,
        string catalystId)
    {
        var formerMemberIds = previousGroup?.MemberIds.ToHashSet(
            StringComparer.Ordinal);
        var claimants = _villagers.Where(value => value.Health > 0 &&
                (formerMemberIds is null ||
                 formerMemberIds.Contains(value.Id)))
            .Select(value => value.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previousGroup is not null && claimants.Length > 0)
        {
            foreach (var gpu in _worldChunks.Values)
            {
                var changed = false;
                for (var index = 0;
                     index < gpu.Chunk.GroundObjects.Count;
                     index++)
                {
                    var item = gpu.Chunk.GroundObjects[index];
                    if (item.GroupOwnerId != previousGroup.Id) continue;
                    var claimant = claimants[
                        item.Id.ToByteArray()[0] % claimants.Length];
                    gpu.Chunk.GroundObjects[index] = item with
                    {
                        OwnerId = claimant,
                        GroupOwnerId = null
                    };
                    claims[claimant] = claims.GetValueOrDefault(claimant) + 1;
                    changed = true;
                }
                if (changed) QueueChunkSave(gpu.Chunk);
            }
        }
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (_villagers[index].Health <= 0) continue;
            _villagers[index] = _villagers[index] with
            {
                IndependentByChoice = true,
                SettlementGroupId = null,
                RecognizedLeaderId = null,
                ProjectAssignment = null,
                WorkRole = VillagerWorkRole.Unassigned,
                NextDecisionGameSeconds = _worldGameSeconds
            };
        }
        _settlementGroup = null;
        _settlementProjectKey = null;
        _completedProjectContributions.Clear();
        if (_activeWorld is not null)
            _saves.DeleteSettlementGroup(_activeWorld.Id);
        ObserveLog("settlement_dissolved", catalystId, new
        {
            Reason = "leadership_schism",
            Claims = claims
        });
        _villagersDirty = true;
    }
}
