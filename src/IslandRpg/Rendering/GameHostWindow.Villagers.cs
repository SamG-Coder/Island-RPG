using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using FontStashSharp;
using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private string? _settlementProjectKey;
    private SettlementGroupState? _settlementGroup;
    private VillagerLeadershipResult? _settlementCouncilResult;
    private Queue<VillagerGroupConversationLine> _settlementCouncilLines = [];
    private Vector2 _settlementCouncilPoint;
    private readonly Dictionary<string, Vector2>
        _settlementCouncilPositions = [];
    private double _settlementCouncilGatherUntil;
    private double _settlementCouncilDeadline;
    private bool _settlementCouncilTimedOut;
    private double _nextSettlementCouncilLineAt;
    private VillagerGroupConversationLine? _pendingSettlementCouncilLine;
    private string? _settlementCouncilCenterSpeakerId;
    private double _settlementCouncilCenterMoveUntil;
    private bool _settlementCouncilCandidateShouldReturn;
    private double _settlementCouncilCandidateReturnAt;
    private readonly Dictionary<string, double>
        _nextProjectAccountability = [];
    private readonly HashSet<string> _completedProjectContributions = [];
    private readonly List<VillagerState> _villagers = [];
    private readonly List<VillagerWorldObject>
        _villagerWorldObjects = [];
    private readonly HashSet<Guid> _villagerReservedObjects = [];
    private readonly VillagerWorkCoordinator _villagerWork = new();
    private readonly NpcController _npcController = new();
    private double _nextVillagerRoleAssignment;
    private readonly List<SocialActorObservation>
        _socialActorObservations = [];
    private double _villagersNextSaveAt;
    private bool _villagersDirty;
    private readonly Dictionary<string, VillagerSpeechBubble>
        _villagerSpeechBubbles = [];
    private readonly Queue<string> _queuedPlayerConversationTurns = [];
    private string? _conversationFloorSpeakerId;
    private double _conversationFloorUntil;
    private sealed record VillagerSpeechBubble(
        string Text, double ExpiresAt);

    private void LoadVillagers(Vector2 spawn)
    {
        _villagers.Clear();
        _villagerSpeechBubbles.Clear();
        _observedVillagerId = null;
        _queuedPlayerConversationTurns.Clear();
        _conversationFloorSpeakerId = null;
        _conversationFloorUntil = 0;
        _villagerWork.Clear();
        _npcController.Clear();
        _settlementCouncilResult = null;
        _settlementCouncilLines.Clear();
        _settlementCouncilPositions.Clear();
        _pendingSettlementCouncilLine = null;
        _settlementCouncilCenterSpeakerId = null;
        _settlementCouncilCandidateShouldReturn = false;
        _settlementCouncilCandidateReturnAt = 0;
        _settlementCouncilDeadline = 0;
        _settlementCouncilTimedOut = false;
        _nextVillagerRoleAssignment = 0;
        _recentNpcTreeHealthId = null;
        _recentNpcMiningHealthKey = null;
        _recentNpcResourceHealthUntil = 0;
        _playerWorldHealthUntil = 0;
        if (_activeWorld is null) return;
        _settlementGroup = _saves.LoadSettlementGroup(_activeWorld.Id);
        _villagerDeaths = _saves.LoadVillagerDeaths(_activeWorld.Id);
        if (!_activeWorld.AiNpcsEnabled ||
            _activeWorld.AiNpcCount <= 0)
        {
            _villagersNextSaveAt = double.PositiveInfinity;
            return;
        }
        var saved = _saves.LoadVillagers(_activeWorld.Id);
        if (saved.Count > 0)
            _villagers.AddRange(saved.Select(value =>
                VillagerSimulation.CatchUp(
                    value,
                    _worldGameSeconds,
                    _observeMode?.HungerRateMultiplier ?? 1)));
        else
        {
            var arrivals = VillagerSimulation.CreateInitial(
                    _worldSeed,
                    spawn,
                    candidate => WorldLevelNavigation.IsWalkable(
                        _worldSeed,
                        (int)MathF.Floor(candidate.X),
                        (int)MathF.Floor(candidate.Y),
                        (int)WorldLevel.Overworld),
                    gameSeconds: _worldGameSeconds,
                    population: _activeWorld.AiNpcCount,
                    personas: _activeWorld.AiNpcPersonas,
                    setups: _activeWorld.AiNpcSetups);
            InitializeOpeningIncident(arrivals, spawn);
            ObserveLog("opening_wreck_incident", null, new
            {
                Era = "1200 AD",
                EndsAtGameSeconds = _worldGameSeconds +
                    VillagerOpeningIncidentService.IncidentRealSeconds *
                    VillagerSimulation.GameSecondsPerRealSecond,
                Injured = _openingIncidentOutcomes.Values.Where(value =>
                        value.Health < AdventureService.BaseMaximumHealth)
                    .Select(value => new { value.Id, value.Health }).ToArray(),
                Accounts = VillagerOpeningIncidentService.Accounts(
                        _openingIncidentOutcomes.Values.ToArray())
                    .Select(value => new
                    {
                        value.SpeakerId,
                        value.Purpose,
                        DeterministicMeaning = value.Text
                    }).ToArray()
            });
            _villagersDirty = true;
        }
        if (_settlementGroup is { } loadedGroup)
        {
            var livingPopulation = _villagers.Count(value => value.Health > 0);
            if (!IndependentSurvivorPolicy.CanFormSettlement(livingPopulation))
            {
                _settlementGroup = null;
                _saves.DeleteSettlementGroup(_activeWorld.Id);
                for (var index = 0; index < _villagers.Count; index++)
                    _villagers[index] = _villagers[index] with
                    {
                        SettlementGroupId = null,
                        RecognizedLeaderId = null,
                        ProjectAssignment = null,
                        WorkRole = VillagerWorkRole.Unassigned
                    };
            }
            else
            for (var index = 0; index < _villagers.Count; index++)
                if (loadedGroup.MemberIds.Contains(
                        _villagers[index].Id,
                        StringComparer.Ordinal))
                    _villagers[index] = _villagers[index] with
                    {
                        SettlementGroupId = loadedGroup.Id
                    };
        }
        _villagersNextSaveAt = _worldGameSeconds + 30;
    }

    private void UpdateVillagers(float elapsed)
    {
        if (_player is null || _activeWorld is null) return;
        AdvanceVillagerTimedActivities();
        UpdateConversationTurns();
        _villagerWork.Expire(_worldGameSeconds);
        var openingIncidentActive = UpdateOpeningIncident();
        TryCallLeadershipChallenge();
        var councilActive = !openingIncidentActive &&
            UpdateSettlementLeadership();
        var settlementOpeningActive = !openingIncidentActive &&
            !councilActive && UpdateSettlementOpening();
        if (!openingIncidentActive && !councilActive &&
            !settlementOpeningActive &&
            _worldGameSeconds >= _nextVillagerRoleAssignment)
        {
            var forecast = VillagerWorkPlanner.Forecast(_villagers);
            var roles = VillagerWorkCoordinator.AssignRoles(_villagers);
            ObserveLog("resource_plan", null, new
            {
                Forecast = forecast,
                Assignments = _villagers.Select(value => new
                {
                    value.Id,
                    Role = roles.GetValueOrDefault(
                        value.Id, VillagerWorkRole.Unassigned).ToString(),
                    Scores = new
                    {
                        Food = VillagerWorkPlanner.Suitability(
                            value, VillagerWorkRole.Food, forecast),
                        Wood = VillagerWorkPlanner.Suitability(
                            value, VillagerWorkRole.Wood, forecast),
                        Crafting = VillagerWorkPlanner.Suitability(
                            value, VillagerWorkRole.Crafting, forecast),
                        Exploration = VillagerWorkPlanner.Suitability(
                            value, VillagerWorkRole.Exploration, forecast)
                    }
                }).ToArray()
            });
            for (var roleIndex = 0; roleIndex < _villagers.Count; roleIndex++)
            {
                var roleVillager = _villagers[roleIndex];
                var role = roles.TryGetValue(roleVillager.Id, out var assigned)
                    ? assigned
                    : VillagerWorkRole.Unassigned;
                if (roleVillager.WorkRole != role)
                {
                    _npcController.Cancel(roleVillager.Id);
                    _villagerWork.ReleaseActor(roleVillager.Id);
                    _villagers[roleIndex] =
                        VillagerSimulation.CompleteAction(roleVillager) with
                        {
                            WorkRole = role,
                            NextDecisionGameSeconds = _worldGameSeconds
                        };
                    _villagersDirty = true;
                }
            }
            _nextVillagerRoleAssignment = _worldGameSeconds + 30 * 60;
        }
        if (!openingIncidentActive && !councilActive &&
            !settlementOpeningActive)
        {
            UpdateSettlementProjectAssignments();
            TryPromptStalledProject();
        }
        UpdateVillagerPromiseDeadlines();
        _villagerReservedObjects.Clear();
        foreach (var villager in _villagers)
            if (villager.GoalObjectId is { } goal)
                _villagerReservedObjects.Add(goal);
        for (var index = 0; index < _villagers.Count; index++)
        {
            var previous = _villagers[index];
            var energized = VillagerFatigueService.Advance(
                previous, _worldGameSeconds);
            if (!ReferenceEquals(previous, energized))
            {
                previous = energized;
                _villagers[index] = previous;
                _villagersDirty = true;
            }
            if (previous.Health <= 0)
            {
                _npcController.Cancel(previous.Id);
                _villagerWork.ReleaseActor(previous.Id);
                if (previous.Action != EntityAction.Die)
                {
                    _villagers[index] = previous with
                    {
                        Action = EntityAction.Die,
                        ActionTime = 0,
                        TargetX = null,
                        TargetY = null,
                        FollowingActorId = null
                    };
                    _villagersDirty = true;
                }
                else
                    _villagers[index] = previous with
                    {
                        ActionTime = previous.ActionTime + elapsed
                    };
                continue;
            }
            if (previous.ConflictIntent != VillagerConflictIntent.None &&
                previous.ConflictExpiresGameSeconds > 0 &&
                previous.ConflictExpiresGameSeconds <= _worldGameSeconds)
            {
                previous = VillagerConflictService.Expire(
                    previous, _worldGameSeconds);
                _villagers[index] = previous;
                _villagerWork.ReleaseActor(previous.Id);
                _villagersDirty = true;
            }
            if (VillagerFatigueService.ShouldRest(previous))
            {
                _npcController.Cancel(previous.Id);
                _villagerWork.ReleaseActor(previous.Id);
                previous = VillagerFatigueService.BeginRest(
                    previous, _worldGameSeconds);
                _villagers[index] = previous;
                _villagersDirty = true;
                continue;
            }
            if (previous.Activity is
                    VillagerActivity.Conversing or
                    VillagerActivity.Reflecting or
                    VillagerActivity.Following ||
                previous.ConflictIntent != VillagerConflictIntent.None)
                _villagerWork.ReleaseActor(previous.Id);
            previous = AdvanceNpcController(previous);
            previous = CompleteVillagerActionAnimation(previous);
            if (_activePlayer is not null &&
                previous.FollowingActorId == _activePlayer.Id &&
                (previous.Activity != VillagerActivity.Blocked ||
                 _worldGameSeconds >=
                 previous.NextDecisionGameSeconds) &&
                previous.Activity != VillagerActivity.Conversing &&
                previous.Activity != VillagerActivity.Reflecting)
            {
                var followerPosition = new Vector2(
                    previous.PositionX, previous.PositionY);
                var distanceSquared = Vector2.DistanceSquared(
                    followerPosition, _player.Position);
                var shouldMove =
                    distanceSquared >
                    VillagerSimulation.FollowResumeDistance *
                    VillagerSimulation.FollowResumeDistance ||
                    previous.Action == EntityAction.Move &&
                    distanceSquared >
                    VillagerSimulation.FollowStopDistance *
                    VillagerSimulation.FollowStopDistance;
                if (shouldMove)
                {
                    var desiredFollowTarget =
                        VillagerSimulation.FollowTarget(
                            followerPosition,
                            _player.Position);
                    if (VillagerSimulation.NeedsFollowRetarget(
                            previous, desiredFollowTarget))
                    {
                        var followTarget =
                            WorldLevelNavigation.ReachableWalkableTarget(
                                _worldSeed,
                                followerPosition,
                                desiredFollowTarget,
                                previous.WorldLevel,
                                maximumRadius: 3);
                        if (Vector2.DistanceSquared(
                                followerPosition,
                                followTarget) <= .01f)
                            previous =
                                VillagerSimulation.BlockMovement(
                                    previous,
                                    _worldGameSeconds);
                        else
                            previous =
                                VillagerSimulation.RetargetFollowing(
                                    previous,
                                    followTarget,
                                    _worldGameSeconds);
                    }
                }
                else
                    previous = previous with
                    {
                        Activity = VillagerActivity.Following,
                        Action = EntityAction.Idle,
                        ActionTime =
                            previous.Action == EntityAction.Idle
                                ? previous.ActionTime
                                : 0,
                        TargetX = null,
                        TargetY = null
                    };
            }
            var currentTerrain = SamplePlayerTerrain(
                previous.PositionX, previous.PositionY);
            var targetTerrain = SamplePlayerTerrain(
                previous.TargetX ?? previous.PositionX,
                previous.TargetY ?? previous.PositionY);
            var wading = currentTerrain.Biome is
                Biome.ShallowWater or
                Biome.RiverWater or
                Biome.MangroveShallows;
            var villager = VillagerSimulation.AdvanceMovement(
                previous,
                elapsed,
                ActorMovementService.TerrainSpeedMultiplier(
                    wading,
                    currentTerrain.Height,
                    targetTerrain.Height),
                candidate => WorldLevelNavigation.IsWalkable(
                    _worldSeed,
                    (int)MathF.Floor(candidate.X),
                    (int)MathF.Floor(candidate.Y),
                    previous.WorldLevel),
                _worldGameSeconds);
            if (villager.Activity == VillagerActivity.Blocked &&
                previous.Activity != VillagerActivity.Blocked)
                _villagerWork.ReleaseActor(villager.Id);
            if (villager.Action == EntityAction.Idle &&
                villager.Activity == VillagerActivity.Idle &&
                !_npcController.IsBusy(villager.Id))
                _villagerWork.ReleaseActor(villager.Id);
            var movedPosition = new Vector2(
                villager.PositionX, villager.PositionY);
            for (var otherIndex = 0;
                 otherIndex < _villagers.Count;
                 otherIndex++)
            {
                if (otherIndex == index ||
                    _villagers[otherIndex].WorldLevel !=
                    villager.WorldLevel)
                    continue;
                var otherPosition = new Vector2(
                    _villagers[otherIndex].PositionX,
                    _villagers[otherIndex].PositionY);
                if (!VillagerSimulation.FootBoxesOverlap(
                        movedPosition, otherPosition))
                    continue;
                var previousPosition = new Vector2(
                    previous.PositionX, previous.PositionY);
                var movementTarget = new Vector2(
                    previous.TargetX ?? previous.PositionX,
                    previous.TargetY ?? previous.PositionY);
                if (VillagerSimulation.TryCollisionSidestep(
                        previousPosition,
                        movedPosition,
                        movementTarget,
                        otherPosition,
                        candidate => WorldLevelNavigation.IsWalkable(
                            _worldSeed,
                            (int)MathF.Floor(candidate.X),
                            (int)MathF.Floor(candidate.Y),
                            previous.WorldLevel),
                        out var sidestep))
                    villager = villager with
                    {
                        PositionX = sidestep.X,
                        PositionY = sidestep.Y,
                        BlockedMoveAttempts = 0
                    };
                else if (VillagerSimulation.ShouldYieldThroughActor(
                             previous.BlockedMoveAttempts))
                    villager = villager with
                    {
                        BlockedMoveAttempts = 0
                    };
                else
                    villager = VillagerSimulation.BlockMovement(
                        villager with
                        {
                            PositionX = previous.PositionX,
                            PositionY = previous.PositionY
                        },
                        _worldGameSeconds);
                break;
            }
            // Compare against the persisted state, not the local state passed to
            // AdvanceMovement. Activity transitions (notably conversation
            // completion) can occur without movement returning another record.
            if (!ReferenceEquals(_villagers[index], villager))
            {
                _villagers[index] = villager;
                _villagersDirty = true;
            }
            if (_settlementCouncilResult is not null &&
                !VillagerIntentPriorityService.CanInterruptScriptedActivity(
                    villager))
                continue;
            if (openingIncidentActive &&
                !VillagerIntentPriorityService.CanInterruptScriptedActivity(
                    villager))
                continue;
            if (settlementOpeningActive &&
                !VillagerIntentPriorityService.CanInterruptScriptedActivity(
                    villager))
                continue;
            if (villager.Activity is
                VillagerActivity.Conversing or
                VillagerActivity.Reflecting)
                continue;
            if (_npcController.IsBusy(villager.Id))
                continue;
            if (villager.WorldLevel != _activeWorldLevel ||
                _worldGameSeconds < villager.NextDecisionGameSeconds)
                continue;
            var position = new Vector2(
                villager.PositionX, villager.PositionY);
            var simulationFocus = _activeWorld?.ObserveWorld == true
                ? position
                : ObservationFocusPosition();
            var tier = VillagerSimulation.Tier(
                position, simulationFocus);
            villager = VillagerSimulation.CatchUp(
                villager,
                _worldGameSeconds,
                _observeMode?.HungerRateMultiplier ?? 1);
            var beforeNeedObservation = villager;
            villager = VillagerNeedPatternMemory.ObserveHunger(
                villager,
                villager.Id,
                villager.Name,
                villager.Hunger,
                _worldGameSeconds);
            villager = ConsiderIndependentCamp(villager);
            if (!ReferenceEquals(beforeNeedObservation, villager))
            {
                _villagers[index] = villager;
                _villagersDirty = true;
            }
            if (tier != VillagerSimulationTier.Distant &&
                TryExecuteVillagerUrgentAction(index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                TryExecuteVillagerCommittedAction(index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                VillagerIntentPriorityService.ShouldProtectCommittedWork(
                    villager) &&
                TryExecuteVillagerWorldAction(
                    index, villager, tier))
                continue;
            if (TryExecuteVillagerSocialGoal(
                    index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                TryExecuteVillagerWorldAction(
                    index, villager, tier))
                continue;
            var decision = VillagerSimulation.Decide(
                villager, simulationFocus, _worldGameSeconds);
            ObserveLog("autonomous_decision", villager.Id, new
            {
                Need = decision.Need.ToString(),
                MoveTarget = decision.MoveTarget is { } moveTarget
                    ? new { X = moveTarget.X, Y = moveTarget.Y }
                    : null,
                decision.ConsumeSlot,
                decision.Speech,
                Tier = tier.ToString()
            });
            if (decision.MoveTarget is { } requestedTarget)
            {
                var safeTarget =
                    WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed,
                    position,
                    requestedTarget,
                    villager.WorldLevel,
                    maximumRadius: 2);
                decision = decision with
                {
                    MoveTarget = safeTarget
                };
                if (Vector2.DistanceSquared(
                        position, safeTarget) <= .01f &&
                    Vector2.DistanceSquared(
                        position, requestedTarget) > .01f)
                {
                    _villagers[index] =
                        VillagerSimulation.BlockMovement(
                            villager, _worldGameSeconds);
                    _villagersDirty = true;
                    continue;
                }
            }
            villager = VillagerSimulation.ApplyDecision(
                villager, decision, tier, _worldGameSeconds);
            _villagers[index] = villager;
            _villagersDirty = true;
            if (decision.Speech is { } speech &&
                tier == VillagerSimulationTier.Nearby)
                _chatUi.AddMessage(
                    $"{villager.Name}: {speech}",
                    ChatMessageStyle.Normal);
        }
        FinalizeVillagerDeaths();
        if (_villagersDirty &&
            _worldGameSeconds >= _villagersNextSaveAt)
            SaveVillagers();
    }

    private void AdvanceVillagerTimedActivities()
    {
        for (var index = 0; index < _villagers.Count; index++)
        {
            var current = _villagers[index];
            var advanced = current;
            if (advanced.Activity == VillagerActivity.Conversing &&
                ConversationHasFinished(advanced))
                advanced = VillagerSimulation.CompleteConversation(
                    advanced, _worldGameSeconds);
            advanced = VillagerSimulation.CompleteReflection(
                advanced, _worldGameSeconds);
            if (ReferenceEquals(current, advanced)) continue;
            _villagers[index] = advanced;
            _villagersDirty = true;
        }
    }

    private void FinalizeVillagerDeaths()
    {
        if (_activeWorld is null) return;
        var removed = false;
        for (var index = _villagers.Count - 1; index >= 0; index--)
        {
            var villager = _villagers[index];
            if (villager.Health > 0 ||
                villager.ActionTime < VillagerDeathAnimationSeconds(villager))
                continue;
            _npcController.Cancel(villager.Id);
            _villagerWork.ReleaseActor(villager.Id);
            _saves.AddVillagerDeath(
                _activeWorld.Id,
                new(
                    villager.PositionX,
                    villager.PositionY,
                    villager.WorldLevel,
                    villager.Gender,
                    DateTime.UtcNow,
                    villager.FacingX,
                    villager.FacingY,
                    villager.Name,
                    villager.DeathCause ?? "Cause unknown."));
            _villagerDeaths = _saves.LoadVillagerDeaths(_activeWorld.Id);
            ObserveLog("villager_died", villager.Id, new
            {
                villager.Name,
                Cause = villager.DeathCause ?? "Cause unknown."
            });
            _villagers.RemoveAt(index);
            _villagerSpeechBubbles.Remove(villager.Id);
            _nextProjectAccountability.Remove(villager.Id);
            if (_observedVillagerId == villager.Id)
                _observedVillagerId = null;
            _villagersDirty = true;
            removed = true;
        }
        if (removed)
            SaveVillagers();
    }

    private double VillagerDeathAnimationSeconds(VillagerState villager)
    {
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, EntityAction.Die), out var animation))
            return 1.4;
        const int storedVillagerAngles = 5;
        return Math.Max(1,
                   animation.Graphic.Sprite.Frames.Count /
                   storedVillagerAngles) * animation.SecondsPerFrame;
    }

    private void UpdateSettlementProjectAssignments()
    {
        var placedItems = _worldChunks.Values
            .Where(IsActiveSimulationChunk)
            .SelectMany(value => value.Chunk.GroundObjects)
            .Select(value => value.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaderId = _villagers.FirstOrDefault(value =>
            value.Health > 0 && value.RecognizedLeaderId is not null)?
            .RecognizedLeaderId;
        var plan = VillagerSettlementProjectService.Plan(
            _villagers, placedItems, leaderId, _worldGameSeconds);
        if (plan is not null && !_villagers.Any(value =>
                value.ProjectAssignment?.ProjectItemId ==
                plan.ProjectItemId))
            plan = plan with
            {
                Worksite = FindProjectWorksite(
                    plan.Worksite, plan.WorksiteLevel)
            };
        var key = plan is null
            ? null
            : $"{plan.ProjectItemId}:{plan.BuilderId}";
        if (key != _settlementProjectKey)
            _completedProjectContributions.Clear();
        for (var index = 0; index < _villagers.Count; index++)
        {
            var villager = _villagers[index];
            VillagerProjectAssignment? assignment = null;
            if (plan is not null)
            {
                var requirements = plan.Assignments.GetValueOrDefault(
                    villager.Id, [])
                    .Where(requirement =>
                        !_completedProjectContributions.Contains(
                            ProjectContributionKey(
                                plan.ProjectItemId,
                                villager.Id,
                                requirement.ItemId)))
                    .ToArray();
                var assignedAt = villager.ProjectAssignment is
                    { } existing &&
                    existing.ProjectItemId == plan.ProjectItemId &&
                    existing.BuilderId == plan.BuilderId
                        ? existing.AssignedGameSeconds
                        : _worldGameSeconds;
                assignment = new(
                    plan.ProjectItemId,
                    plan.BuilderId,
                    requirements,
                    assignedAt,
                    plan.LeaderId,
                    plan.Worksite.X,
                    plan.Worksite.Y,
                    plan.WorksiteLevel);
            }
            if (VillagerSettlementProjectService.SameAssignment(
                    villager.ProjectAssignment, assignment))
                continue;
            _villagers[index] = villager with
            {
                ProjectAssignment = assignment
            };
            _villagersDirty = true;
        }
        if (key == _settlementProjectKey) return;
        _settlementProjectKey = key;
        ObserveLog("settlement_project", null, plan is null
            ? new { Status = "complete" }
            : new
            {
                Status = "active",
                plan.ProjectItemId,
                plan.BuilderId,
                plan.LeaderId,
                Worksite = new { X = plan.Worksite.X, Y = plan.Worksite.Y },
                Assignments = plan.Assignments
            });
        if (plan is not null)
            AnnounceSettlementAssignments(plan);
    }

    private Vector2 FindProjectWorksite(Vector2 origin, int worldLevel)
    {
        for (var ring = 1; ring <= 4; ring++)
        for (var step = 0; step < 12; step++)
        {
            var angle = step / 12f * MathF.Tau;
            var candidate = origin + new Vector2(
                MathF.Cos(angle), MathF.Sin(angle)) * ring;
            if (!WorldLevelNavigation.IsWalkable(
                    _worldSeed,
                    (int)MathF.Floor(candidate.X),
                    (int)MathF.Floor(candidate.Y),
                    worldLevel) ||
                _villagers.Any(value => value.Health > 0 &&
                    value.WorldLevel == worldLevel &&
                    Vector2.DistanceSquared(
                        new(value.PositionX, value.PositionY), candidate) <
                    1.2f * 1.2f))
                continue;
            return candidate;
        }
        return origin;
    }

    private void AnnounceSettlementAssignments(
        VillagerSettlementProjectPlan plan)
    {
        var leaderIndex = _villagers.FindIndex(value =>
            value.Id == plan.LeaderId && value.Health > 0);
        if (leaderIndex < 0) return;
        var builder = _villagers.First(value => value.Id == plan.BuilderId);
        var orders = plan.Assignments
            .Where(pair => pair.Value.Count > 0)
            .Select(pair =>
            {
                var worker = _villagers.First(value => value.Id == pair.Key);
                var needs = string.Join(" and ", pair.Value.Select(value =>
                    $"{value.Quantity} {ItemCatalog.Get(value.ItemId).Name}"));
                return $"{worker.Name}, bring {needs}";
            });
        var projectName = ItemCatalog.Get(plan.ProjectItemId).Name;
        var text = $"We are building the {projectName}. " +
                   $"{builder.Name}, remain at the worksite. " +
                   string.Join(". ", orders) + ".";
        ShowVillagerSpeech(
            leaderIndex, text, plan.Worksite);
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (_villagers[index].Health <= 0) continue;
            _villagers[index] = VillagerSimulation.RecordDialogueTurn(
                _villagers[index],
                _villagers[leaderIndex].Id,
                _villagers[leaderIndex].Name,
                text,
                _worldGameSeconds);
        }
        ObserveLog("settlement_orders", plan.LeaderId, new
        {
            plan.ProjectItemId,
            plan.BuilderId,
            Orders = plan.Assignments
        });
        _villagersDirty = true;
    }

    private bool UpdateSettlementLeadership()
    {
        var living = _villagers.Where(value =>
                value.Health > 0 && !value.IndependentByChoice &&
                (_settlementGroup is null ||
                 _settlementGroup.MemberIds.Contains(
                     value.Id, StringComparer.Ordinal)))
            .ToArray();
        if (!IndependentSurvivorPolicy.CanFormSettlement(living.Length))
            return false;
        var recognized = living.Select(value => value.RecognizedLeaderId)
            .FirstOrDefault(id => id is not null &&
                living.Any(candidate => candidate.Id == id));
        if (recognized is not null && living.All(value =>
                value.RecognizedLeaderId == recognized))
            return false;
        if (_settlementCouncilResult is null)
        {
            var result = VillagerLeadershipService.HoldCouncil(living);
            if (result is null) return false;
            _settlementCouncilResult = result;
            _settlementCouncilLines = new(
                VillagerGroupConversationService.OpeningCouncil(
                    living, result));
            _settlementCouncilPoint = new(
                living.Average(value => value.PositionX),
                living.Average(value => value.PositionY));
            _settlementCouncilPositions.Clear();
            _settlementCouncilGatherUntil = _worldGameSeconds +
                12 * VillagerSimulation.GameSecondsPerRealSecond;
            _settlementCouncilDeadline = _clock + 90;
            _settlementCouncilTimedOut = false;
            _nextSettlementCouncilLineAt = double.PositiveInfinity;
            for (var index = 0; index < _villagers.Count; index++)
            {
                var villager = _villagers[index];
                if (villager.Health <= 0 ||
                    !living.Any(value => value.Id == villager.Id))
                    continue;
                var offset = VillagerGroupConversationService.CircleOffset(
                    villager.Id, index, _villagers.Count);
                var desired = _settlementCouncilPoint +
                    new Vector2(offset.X, offset.Y);
                var target = WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed,
                    new(villager.PositionX, villager.PositionY),
                    desired,
                    villager.WorldLevel,
                    maximumRadius: 3);
                _settlementCouncilPositions[villager.Id] = target;
                _villagers[index] = VillagerSimulation.ApplyDecision(
                    villager,
                    new(VillagerNeed.Social, target),
                    VillagerSimulationTier.Nearby,
                    _worldGameSeconds);
            }
            ObserveLog("settlement_council_gathering", null, new
            {
                MeetingPoint = new
                {
                    X = _settlementCouncilPoint.X,
                    Y = _settlementCouncilPoint.Y
                },
                Participants = living.Select(value => value.Id).ToArray()
            });
            _villagersDirty = true;
            return true;
        }
        var gathered = living.All(value =>
            _settlementCouncilPositions.TryGetValue(value.Id, out var place) &&
            Vector2.DistanceSquared(
                new(value.PositionX, value.PositionY), place) <= .75f * .75f);
        if (!gathered &&
            _worldGameSeconds < _settlementCouncilGatherUntil)
            return true;
        if (double.IsPositiveInfinity(_nextSettlementCouncilLineAt))
            _nextSettlementCouncilLineAt = _clock;
        if (!_settlementCouncilTimedOut &&
            _clock >= _settlementCouncilDeadline)
        {
            var remainingTurns = _settlementCouncilLines.Count +
                                 (_pendingSettlementCouncilLine is null
                                     ? 0
                                     : 1);
            _settlementCouncilTimedOut = true;
            _settlementCouncilLines.Clear();
            _pendingSettlementCouncilLine = null;
            _settlementCouncilCandidateShouldReturn = false;
            _nextSettlementCouncilLineAt = _clock;
            ObserveLog("settlement_council_timeout", null, new
            {
                DurationSeconds = 90,
                RemainingTurns = remainingTurns
            });
        }
        if (_settlementCouncilCandidateShouldReturn &&
            _clock >= _settlementCouncilCandidateReturnAt &&
            _settlementCouncilCenterSpeakerId is { } returningCandidate)
        {
            ReturnCouncilCandidateToCircle(returningCandidate);
            _settlementCouncilCandidateShouldReturn = false;
        }
        if (_clock < _nextSettlementCouncilLineAt ||
            ConversationFloorBusy || _npcAiDialogueTask is not null)
            return true;
        VillagerGroupConversationLine? line = null;
        if (_pendingSettlementCouncilLine is { } pendingLine)
        {
            var pendingSpeaker = _villagers.First(value =>
                value.Id == pendingLine.SpeakerId);
            if (Vector2.DistanceSquared(
                    new(pendingSpeaker.PositionX, pendingSpeaker.PositionY),
                    _settlementCouncilPoint) > .8f * .8f &&
                _worldGameSeconds < _settlementCouncilCenterMoveUntil)
                return true;
            line = pendingLine;
            _pendingSettlementCouncilLine = null;
        }
        else if (_settlementCouncilLines.TryDequeue(out var queuedLine))
        {
            if (_settlementCouncilCenterSpeakerId is { } previousCandidate &&
                queuedLine.Purpose != "proposal")
                ReturnCouncilCandidateToCircle(previousCandidate);
            if (queuedLine.Purpose == "proposal")
            {
                if (_settlementCouncilCenterSpeakerId is { } centerSpeaker)
                    ReturnCouncilCandidateToCircle(centerSpeaker);
                var candidateIndex = _villagers.FindIndex(value =>
                    value.Id == queuedLine.SpeakerId);
                if (candidateIndex >= 0)
                {
                    _villagers[candidateIndex] = VillagerSimulation.ApplyDecision(
                        _villagers[candidateIndex],
                        new(VillagerNeed.Social, _settlementCouncilPoint),
                        VillagerSimulationTier.Nearby,
                        _worldGameSeconds);
                    _settlementCouncilCenterSpeakerId = queuedLine.SpeakerId;
                    _pendingSettlementCouncilLine = queuedLine;
                    _settlementCouncilCenterMoveUntil = _worldGameSeconds +
                        8 * VillagerSimulation.GameSecondsPerRealSecond;
                    ObserveLog("settlement_candidate_steps_forward",
                        queuedLine.SpeakerId, new
                        {
                            X = _settlementCouncilPoint.X,
                            Y = _settlementCouncilPoint.Y
                        });
                    return true;
                }
            }
            line = queuedLine;
        }
        if (line is not null)
        {
            var speakerIndex = _villagers.FindIndex(value =>
                value.Id == line.SpeakerId);
            if (speakerIndex >= 0)
            {
                var seconds = ConversationLineSeconds(line.Text);
                if (line.UseAi)
                {
                    var leaderId = _settlementCouncilResult!.LeaderId;
                    var councilLeader = _villagers.First(value =>
                        value.Id == leaderId);
                    SpeakVillagerDialogue(
                        _villagers[speakerIndex],
                        councilLeader.Id,
                        "the gathered survivors",
                        VillagerSocialIntent.AskSurvival,
                        line.Text,
                        allowNpcReply: false);
                    _npcAiDialogueGroupListenerIds = _villagers
                        .Where(IsPresentAtCouncil)
                        .Select(value => value.Id)
                        .ToArray();
                    _npcAiDialogueGroupPurpose = line.Purpose;
                }
                else
                {
                    ShowVillagerSpeech(
                        speakerIndex, line.Text, _settlementCouncilPoint);
                    RecordCouncilLineForGroup(
                        speakerIndex, line.Text, seconds, line.Purpose);
                }
                ObserveLog("settlement_council_turn", line.SpeakerId, new
                {
                    line.Purpose,
                    line.Text,
                    RemainingTurns = _settlementCouncilLines.Count
                });
                _nextSettlementCouncilLineAt = _clock + seconds;
                if (line.Purpose == "proposal" && !line.UseAi)
                {
                    _settlementCouncilCandidateShouldReturn = true;
                    _settlementCouncilCandidateReturnAt = _clock + seconds;
                }
                _villagersDirty = true;
            }
            return true;
        }
        var resultToApply = _settlementCouncilResult;
        if (resultToApply is null) return false;
        var updated = VillagerLeadershipService.ApplyCouncil(
            _villagers, resultToApply, _worldGameSeconds);
        for (var index = 0; index < _villagers.Count; index++)
            _villagers[index] = updated[index];
        var leader = _villagers.First(value =>
            value.Id == resultToApply.LeaderId);
        var previousGroup = _settlementGroup;
        var departures = IndependentSurvivorPolicy.LeadershipDepartures(
            living, resultToApply);
        foreach (var departureId in departures)
            ApplyLeadershipDeparture(departureId, leader.Id);
        var livingIds = _villagers
            .Where(value => value.Health > 0 &&
                            !value.IndependentByChoice)
            .Select(value => value.Id)
            .ToArray();
        if (IndependentSurvivorPolicy.CanFormSettlement(livingIds.Length))
        {
            _settlementGroup = previousGroup is null
                ? SettlementGroupService.Form(
                    _activeWorld!.Id,
                    leader.Id,
                    livingIds,
                    _settlementCouncilPoint,
                    leader.WorldLevel,
                    _worldGameSeconds)
                : previousGroup with
                {
                    LeaderId = leader.Id,
                    MemberIds = livingIds
                };
            for (var index = 0; index < _villagers.Count; index++)
                _villagers[index] = _villagers[index] with
                {
                    SettlementGroupId = livingIds.Contains(
                        _villagers[index].Id, StringComparer.Ordinal)
                        ? _settlementGroup.Id
                        : null
                };
            _saves.SaveSettlementGroup(_activeWorld!.Id, _settlementGroup);
        }
        else
            DissolveSettlementAfterSchism(
                previousGroup,
                departures.FirstOrDefault() ?? leader.Id);
        ObserveLog("settlement_council", leader.Id, new
        {
            resultToApply.Contested,
            TimedOut = _settlementCouncilTimedOut,
            Votes = resultToApply.Votes,
            Worksite = new { X = leader.PositionX, Y = leader.PositionY },
            GroupId = _settlementGroup?.Id,
            Camp = _settlementGroup is null
                ? null
                : new { X = _settlementGroup.CampX, Y = _settlementGroup.CampY },
            Departures = departures
        });
        _settlementCouncilResult = null;
        _settlementCouncilLines.Clear();
        _settlementCouncilPositions.Clear();
        _pendingSettlementCouncilLine = null;
        _settlementCouncilCenterSpeakerId = null;
        _settlementCouncilCandidateShouldReturn = false;
        _settlementCouncilCandidateReturnAt = 0;
        _settlementCouncilDeadline = 0;
        _settlementCouncilTimedOut = false;
        _villagersDirty = true;
        return false;
    }

    private void RecordCouncilLineForGroup(
        int speakerIndex, string text, double seconds, string purpose)
    {
        for (var index = 0; index < _villagers.Count; index++)
        {
            var listener = _villagers[index];
            if (!IsPresentAtCouncil(listener)) continue;
            listener = VillagerSimulation.RecordDialogueTurn(
                listener,
                _villagers[speakerIndex].Id,
                _villagers[speakerIndex].Name,
                text,
                _worldGameSeconds);
            if (purpose == "introduction" && index != speakerIndex)
                listener = VillagerSimulation.RecordIntroductionResponse(
                    listener,
                    _villagers[speakerIndex].Id,
                    _villagers[speakerIndex].Name,
                    _worldGameSeconds);
            if (index != speakerIndex)
                listener = VillagerSimulation.BeginConversation(
                    listener, _villagers[speakerIndex].Id,
                    _worldGameSeconds, seconds);
            _villagers[index] = listener;
        }
    }

    private bool IsPresentAtCouncil(VillagerState villager) =>
        villager.Health > 0 &&
        villager.WorldLevel == _activeWorldLevel &&
        Vector2.DistanceSquared(
            new(villager.PositionX, villager.PositionY),
            _settlementCouncilPoint) <= 8 * 8;

    private void ReturnCouncilCandidateToCircle(string candidateId)
    {
        var index = _villagers.FindIndex(value => value.Id == candidateId);
        if (index < 0 ||
            !_settlementCouncilPositions.TryGetValue(
                candidateId, out var position))
            return;
        _villagers[index] = VillagerSimulation.ApplyDecision(
            _villagers[index],
            new(VillagerNeed.Social, position),
            VillagerSimulationTier.Nearby,
            _worldGameSeconds);
        _settlementCouncilCenterSpeakerId = null;
    }

    private void TryCallLeadershipChallenge()
    {
        if (_settlementCouncilResult is not null) return;
        var challenger = VillagerLeadershipService.SelectChallenger(
            _villagers, _worldGameSeconds);
        if (challenger is null) return;
        var challengerIndex = _villagers.FindIndex(value =>
            value.Id == challenger.Id);
        ShowVillagerSpeech(
            challengerIndex,
            "This plan is failing. I call for us to gather and choose our direction again.",
            new(challenger.PositionX, challenger.PositionY));
        for (var index = 0; index < _villagers.Count; index++)
            if (_villagers[index].Health > 0)
                _villagers[index] = _villagers[index] with
                {
                    RecognizedLeaderId = null,
                    NextLeadershipChallengeGameSeconds =
                        _worldGameSeconds +
                        VillagerLeadershipService
                            .MinimumLeadershipTenureGameSeconds
                };
        ObserveLog("leadership_challenge", challenger.Id, new
        {
            PreviousLeaderId = challenger.RecognizedLeaderId,
            Reason = "stalled_project"
        });
        _villagersDirty = true;
    }

    private static string ProjectContributionKey(
        string projectItemId, string contributorId, string itemId) =>
        $"{projectItemId}:{contributorId}:{itemId}";

    private void TryPromptStalledProject()
    {
        var stalled = _villagers.FirstOrDefault(value =>
            value.ProjectAssignment?.BuilderId != value.Id &&
            VillagerSettlementProjectService.IsStalled(
                value, _worldGameSeconds));
        if (stalled is null ||
            stalled.ProjectAssignment is not { } assignment ||
            _nextProjectAccountability.GetValueOrDefault(stalled.Id) >
            _worldGameSeconds)
            return;
        var leaderIndex = _villagers.FindIndex(value =>
            value.Id == assignment.LeaderId && value.Health > 0);
        if (leaderIndex < 0)
            leaderIndex = _villagers.FindIndex(value =>
                value.Id == assignment.BuilderId && value.Health > 0);
        if (leaderIndex < 0) return;
        var leader = _villagers[leaderIndex];
        if (Vector2.DistanceSquared(
                new(leader.PositionX, leader.PositionY),
                new(stalled.PositionX, stalled.PositionY)) >
            VillagerSimulation.SocialRange * VillagerSimulation.SocialRange)
            return;
        var requirement = assignment.Requirements.First();
        var itemName = ItemCatalog.Get(requirement.ItemId).Name;
        var text = $"{stalled.Name}, we still need {itemName} for " +
                   $"the {ItemCatalog.Get(assignment.ProjectItemId).Name}. " +
                   "What is stopping you?";
        ShowVillagerSpeech(
            leaderIndex,
            text,
            new(stalled.PositionX, stalled.PositionY));
        var stalledIndex = _villagers.FindIndex(value =>
            value.Id == stalled.Id);
        if (stalledIndex >= 0)
        {
            var shared = VillagerSimulation.RecordSharedDialogueLine(
                _villagers[leaderIndex],
                _villagers[stalledIndex],
                text,
                _worldGameSeconds);
            _villagers[leaderIndex] = shared.Speaker;
            _villagers[stalledIndex] = shared.Listener;
            _villagersDirty = true;
            var consequence = VillagerLeadershipService.ApplyMissedAssignment(
                _villagers[leaderIndex],
                _villagers[stalledIndex],
                assignment.ProjectItemId,
                _worldGameSeconds);
            _villagers[leaderIndex] = consequence.Leader;
            _villagers[stalledIndex] = consequence.Worker;
        }
        _nextProjectAccountability[stalled.Id] = _worldGameSeconds +
            VillagerSettlementProjectService.AccountabilityDelayGameSeconds;
        ObserveLog("settlement_accountability", leader.Id, new
        {
            ContributorId = stalled.Id,
            assignment.ProjectItemId,
            requirement.ItemId
        });
    }

    private void UpdateVillagerPromiseDeadlines()
    {
        for (var promisorIndex = 0;
             promisorIndex < _villagers.Count;
             promisorIndex++)
        {
            var promisor = _villagers[promisorIndex];
            var expiredPromisees = promisor.Promises?
                .Where(value =>
                    value.Status == CommitmentStatus.Active &&
                    value.DeadlineGameSeconds <= _worldGameSeconds)
                .Select(value => value.PromiseeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            foreach (var promiseeId in expiredPromisees)
            {
                var promiseeIndex = _villagers.FindIndex(value =>
                    value.Id == promiseeId);
                if (promiseeIndex < 0) continue;
                var result = VillagerCommitmentService.UpdateDeadlines(
                    _villagers[promisorIndex],
                    _villagers[promiseeIndex],
                    _worldGameSeconds);
                _villagers[promisorIndex] = result.Promisor;
                _villagers[promiseeIndex] = result.Promisee;
                _villagersDirty = true;
            }
            _villagers[promisorIndex] =
                VillagerCommitmentService.UpdateDeadlines(
                    _villagers[promisorIndex], _worldGameSeconds);
        }
    }

    private bool TryExecuteVillagerSocialGoal(
        int villagerIndex,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Health <= 0) return false;
        if (_player is null || _activePlayer is null ||
            tier == VillagerSimulationTier.Distant)
            return false;
        if (ConversationFloorBusy)
            return false;
        _socialActorObservations.Clear();
        if (!IsObserveWorld)
            _socialActorObservations.Add(new(
                _activePlayer.Id,
                VillagerSimulation.PerceivedName(
                    villager, _activePlayer.Id),
                _player.Position,
                _activeWorldLevel,
                _activePlayer.Hunger,
                VillagerSimulation.CountFood(
                    _activePlayer.Inventory ?? []),
                VillagerCapabilityMemory.VisibleTools(
                    _activePlayer.Inventory)));
        foreach (var actor in _villagers)
            _socialActorObservations.Add(new(
                actor.Id,
                VillagerSimulation.PerceivedName(
                    villager, actor.Id),
                new(actor.PositionX, actor.PositionY),
                actor.WorldLevel,
                actor.Hunger,
                VillagerSimulation.CountFood(
                    actor.Inventory),
                VillagerCapabilityMemory.VisibleTools(
                    actor.Inventory)));
        var beforeSocialObservation = villager;
        foreach (var actor in _socialActorObservations)
        {
            if (actor.Id == villager.Id ||
                actor.WorldLevel != villager.WorldLevel)
                continue;
            var before = VillagerCapabilityMemory.KnownTools(
                villager, actor.Id).Count;
            var distance = Vector2.Distance(
                new(villager.PositionX, villager.PositionY),
                actor.Position);
            if (distance <= VillagerSimulation.SocialRange)
                villager = VillagerNeedPatternMemory.ObserveHunger(
                    villager,
                    actor.Id,
                    actor.Name,
                    actor.Hunger,
                    _worldGameSeconds);
            villager = VillagerCapabilityMemory.Observe(
                villager,
                actor.Id,
                actor.Name,
                actor.VisibleToolIds,
                distance,
                _worldGameSeconds);
            var knownTools = VillagerCapabilityMemory.KnownTools(
                villager, actor.Id);
            if (knownTools.Count > before)
                ObserveLog("capability_observed", villager.Id, new
                {
                    SubjectId = actor.Id,
                    ToolIds = knownTools
                });
        }
        if (!ReferenceEquals(beforeSocialObservation, villager))
        {
            _villagers[villagerIndex] = villager;
            _villagersDirty = true;
        }
        var goal = VillagerSimulation.SelectSocialGoal(
            villager,
            CollectionsMarshal.AsSpan(
                _socialActorObservations),
            _worldGameSeconds);
        ObserveLog("social_decision", villager.Id, new
        {
            Intent = goal.Intent.ToString(),
            goal.OtherActorId,
            Target = goal.Target is { } socialTarget
                ? new { X = socialTarget.X, Y = socialTarget.Y }
                : null,
            goal.Speech,
            Candidates = _socialActorObservations.Select(value => new
            {
                value.Id,
                value.Name,
                Position = new
                {
                    X = value.Position.X,
                    Y = value.Position.Y
                },
                value.Hunger,
                value.FoodCount,
                value.VisibleToolIds
            }).ToArray()
        });
        if (goal.Intent == VillagerSocialIntent.None)
            return false;
        if (goal.Target is { } target)
        {
            var safeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed,
                new(villager.PositionX, villager.PositionY),
                target,
                    villager.WorldLevel,
                    maximumRadius: 2);
            if (Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    safeTarget) <= .01f)
            {
                ObserveLog("social_action_failed", villager.Id, new
                {
                    Intent = goal.Intent.ToString(),
                    Reason = "no_reachable_approach"
                });
                _villagers[villagerIndex] =
                    VillagerSimulation.BlockMovement(
                        villager, _worldGameSeconds);
                _villagersDirty = true;
                return false;
            }
            _villagers[villagerIndex] =
                VillagerSimulation.ApplyDecision(
                    villager,
                    new(VillagerNeed.Social, safeTarget),
                    tier,
                    _worldGameSeconds);
            _villagersDirty = true;
            return true;
        }

        VillagerRequestApproval? requestApproval = null;
        VillagerRefusalPlan? refusalPlan = null;
        if (goal.Speech is { } speech &&
            goal.OtherActorId is { } conversationPartnerId)
        {
            var partner = _socialActorObservations
                .First(value =>
                    value.Id == conversationPartnerId);
            var partnerIsPlayer = _activePlayer is not null &&
                                  partner.Id == _activePlayer.Id;
            var statedPartnerName = goal.Intent ==
                    VillagerSocialIntent.Introduce &&
                !partnerIsPlayer
                ? _villagers.First(value =>
                    value.Id == partner.Id).Name
                : partner.Name;
            if (goal.Intent == VillagerSocialIntent.RequestFood &&
                !partnerIsPlayer)
            {
                var owner = _villagers.First(value =>
                    value.Id == partner.Id);
                requestApproval =
                    VillagerRequestApprovalService.EvaluateFoodRequest(
                        villager, owner, _worldGameSeconds);
                if (!requestApproval.Value.Approved)
                    refusalPlan =
                        VillagerRequestApprovalService.PlanAfterRefusal(
                            villager, owner);
                ObserveLog("approval_request", villager.Id, new
                {
                    OwnerId = owner.Id,
                    ItemId = ItemIds.CookedMinnows,
                    Quantity = 1
                });
                ObserveLog("approval_response", owner.Id, new
                {
                    RequesterId = villager.Id,
                    requestApproval.Value.Approved,
                    requestApproval.Value.Score,
                    requestApproval.Value.Reason,
                    requestApproval.Value.Reply,
                    RefusalStrategy = refusalPlan?.Strategy.ToString()
                });
            }
            SpeakVillagerDialogue(
                villager,
                partner.Id,
                partner.Name,
                goal.Intent,
                speech,
                replyFallback: requestApproval?.Reply);
            villager = _villagers[villagerIndex];
            villager = goal.Intent == VillagerSocialIntent.Introduce
                ? VillagerSimulation.RecordIntroductionResponse(
                    villager,
                    partner.Id,
                    partnerIsPlayer ? null : statedPartnerName,
                    _worldGameSeconds)
                : VillagerSimulation.RecordConversation(
                    villager,
                    partner.Id,
                    statedPartnerName,
                    goal.Intent,
                    _worldGameSeconds);
            villager = villager with
            {
                Need = VillagerNeed.Idle
            };
            _villagers[villagerIndex] = villager;
            var otherVillagerIndex = _villagers.FindIndex(value =>
                value.Id == partner.Id);
            if (otherVillagerIndex >= 0)
            {
                var listener = _villagers[otherVillagerIndex];
                listener =
                    VillagerSimulation.RecordConversation(
                        listener,
                        villager.Id,
                        villager.Name,
                        goal.Intent,
                        _worldGameSeconds) with
                    {
                        Need = VillagerNeed.Idle
                    };
                _villagers[otherVillagerIndex] = listener;
                HoldVillagerConversation(
                    otherVillagerIndex,
                    new(villager.PositionX, villager.PositionY),
                    ConversationLineSeconds(speech));
            }
            _villagersDirty = true;
        }
        if (goal.OtherActorId is null ||
            goal.OtherActorId == _activePlayer?.Id)
        {
            var updatedVillager = villager with
            {
                Need = VillagerNeed.Idle,
                Action = EntityAction.Idle,
                ActionTime = 0,
                NextDecisionGameSeconds =
                    _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds
            };
            _villagers[villagerIndex] = updatedVillager;
            _villagersDirty = true;
            return true;
        }

        var otherIndex = _villagers.FindIndex(value =>
            value.Id == goal.OtherActorId);
        if (otherIndex < 0) return true;
        if (goal.Intent is not
            (VillagerSocialIntent.RequestFood or
             VillagerSocialIntent.OfferFood))
            return true;
        var donorIndex =
            goal.Intent == VillagerSocialIntent.OfferFood
                ? villagerIndex
                : otherIndex;
        var receiverIndex =
            donorIndex == villagerIndex
                ? otherIndex
                : villagerIndex;
        var donor = _villagers[donorIndex];
        var receiver = _villagers[receiverIndex];
        var forcedTaking = requestApproval is
            { Approved: false } && refusalPlan is
            { Strategy: VillagerRefusalStrategy.TakeByForce };
        if (requestApproval is { Approved: false } &&
            refusalPlan is { } rejectedPlan)
        {
            var refusal = VillagerRequestApprovalService.ApplyRefusal(
                receiver, donor, rejectedPlan, _worldGameSeconds);
            receiver = refusal.Requester;
            donor = refusal.Owner;
            _villagers[receiverIndex] = receiver;
            _villagers[donorIndex] = donor;
            ObserveLog("refusal_branch", receiver.Id, new
            {
                OwnerId = donor.Id,
                Strategy = rejectedPlan.Strategy.ToString(),
                rejectedPlan.Thought,
                rejectedPlan.Action,
                rejectedPlan.TradeItemId
            });
            _villagersDirty = true;
            if (!forcedTaking) return true;
        }
        if (!forcedTaking &&
            VillagerSimulation.CountFood(donor.Inventory) <= 1)
            return true;
        var foodSlot = Array.FindIndex(
            donor.Inventory,
            item => item is not null &&
                    SurvivalService.TryFoodEffect(
                        item, out _));
        if (foodSlot < 0 ||
            !PlayerInventory.TryAdd(
                receiver.Inventory,
                donor.Inventory[foodSlot]!,
                out var receiverInventory) ||
            !PlayerInventory.TryRemove(
                donor.Inventory,
                foodSlot,
                out var donorInventory))
            return true;
        string? completedTradeItem = null;
        if (requestApproval is { Approved: true, Reason: "trade_offer" } &&
            receiver.LastDeliberation is
                { Action: "seek_trade", ItemId: { Length: > 0 } tradeItem })
        {
            var tradeSlot = Array.FindIndex(
                receiverInventory,
                item => string.Equals(
                    item, tradeItem,
                    StringComparison.OrdinalIgnoreCase));
            if (tradeSlot >= 0 &&
                PlayerInventory.TryAdd(
                    donorInventory, tradeItem,
                    out var tradedDonorInventory) &&
                PlayerInventory.TryRemove(
                    receiverInventory, tradeSlot,
                    out var tradedReceiverInventory))
            {
                donorInventory = tradedDonorInventory;
                receiverInventory = tradedReceiverInventory;
                completedTradeItem = tradeItem;
            }
        }
        _villagers[donorIndex] = donor with
        {
            Inventory = donorInventory,
            Need = VillagerNeed.Idle,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                    VillagerSimulation.SocialCooldownRealSeconds *
                    VillagerSimulation.GameSecondsPerRealSecond,
            LastDeliberation = completedTradeItem is null
                ? receiver.LastDeliberation
                : null,
            LastSimulatedGameSeconds =
                _worldGameSeconds
        };
        _villagers[receiverIndex] = receiver with
        {
            Inventory = receiverInventory,
            Need = VillagerNeed.Food,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                VillagerSimulation.SocialCooldownRealSeconds *
                VillagerSimulation.GameSecondsPerRealSecond,
            LastSimulatedGameSeconds =
                _worldGameSeconds
        };
        _villagersDirty = true;
        if (forcedTaking)
        {
            ObserveLog("drastic_action", receiver.Id, new
            {
                Action = "take_food_by_force",
                OwnerId = donor.Id,
                ItemId = donor.Inventory[foodSlot]
            });
            StartVillagerConflict(
                receiverIndex, donorIndex, "food taken by force", false);
        }
        if (completedTradeItem is not null)
            ObserveLog("trade_completed", receiver.Id, new
            {
                PartnerId = donor.Id,
                OfferedItemId = completedTradeItem,
                ReceivedItemId = donor.Inventory[foodSlot]
            });
        return true;
    }

    private bool TryExecuteVillagerWorldAction(
        int villagerIndex,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Health <= 0) return false;
        _villagerWorldObjects.Clear();
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var item in gpu.Chunk.GroundObjects)
            {
                if (CropService.IsCrop(item) &&
                    !CropService.IsReady(item, _worldGameSeconds))
                    continue;
                if (_villagerReservedObjects.Contains(item.Id) &&
                    item.Id != villager.GoalObjectId)
                    continue;
                _villagerWorldObjects.Add(new(
                    item.Id,
                    CropService.IsCrop(item)
                        ? item.FuelItemId!
                        : item.ItemId,
                    new(item.X, item.Y),
                    item.OwnerId,
                    StorageContainerService.IsStorage(
                        item.ItemId),
                    item.GroupOwnerId));
            }
        }
        var beforeLocationObservation = villager;
        villager = VillagerLocationMemoryService.ObserveWorldObjects(
            villager,
            CollectionsMarshal.AsSpan(_villagerWorldObjects),
            _worldGameSeconds);
        villager = ReconcileVillagerLocationMemory(villager);
        villager = ExchangeSettlementKnowledge(villager);
        _villagers[villagerIndex] = villager;
        if (!ReferenceEquals(beforeLocationObservation, villager))
            _villagersDirty = true;
        var action = VillagerSimulation.SelectWorldAction(
            villager,
            CollectionsMarshal.AsSpan(_villagerWorldObjects),
            _worldGameSeconds);
        ObserveLog("world_decision", villager.Id, new
        {
            Kind = action.Kind.ToString(),
            action.ObjectId,
            Target = action.Target is { } worldTarget
                ? new { X = worldTarget.X, Y = worldTarget.Y }
                : null,
            VisibleObjects = _villagerWorldObjects.Count
        });
        if (action.Kind == VillagerWorldActionKind.None)
            return false;
        if (action.ObjectId is { } reservedId)
            _villagerReservedObjects.Add(reservedId);
        if (action.Kind is
            VillagerWorldActionKind.ApproachItem or
            VillagerWorldActionKind.ApproachStorage)
        {
            var safeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                _worldSeed,
                new(villager.PositionX, villager.PositionY),
                action.Target ?? new(
                    villager.PositionX, villager.PositionY),
                villager.WorldLevel,
                maximumRadius: 2);
            if (Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    safeTarget) <= .01f)
            {
                ObserveLog("world_action_failed", villager.Id, new
                {
                    Action = action.Kind.ToString(),
                    action.ObjectId,
                    Reason = "no_reachable_approach"
                });
                if (action.RememberedLocation is { } remembered)
                    villager = VillagerLocationMemoryService.MarkUnreachable(
                        villager,
                        remembered.Type,
                        new(remembered.PositionX, remembered.PositionY),
                        remembered.WorldLevel,
                        _worldGameSeconds);
                _villagers[villagerIndex] =
                    VillagerSimulation.BlockMovement(
                        villager, _worldGameSeconds, action.ObjectId);
                _villagersDirty = true;
                return true;
            }
            var decision = new VillagerDecision(
                action.Kind ==
                VillagerWorldActionKind.ApproachItem
                    ? VillagerNeed.Food
                    : VillagerNeed.Safe,
                safeTarget);
            _villagers[villagerIndex] =
                VillagerSimulation.ApplyDecision(
                    villager,
                    decision,
                    tier,
                    _worldGameSeconds) with
                {
                    GoalObjectId = action.ObjectId
                };
            _villagersDirty = true;
            return true;
        }

        var targetGpu = _worldChunks.Values.FirstOrDefault(gpu =>
            IsActiveSimulationChunk(gpu) &&
            gpu.Chunk.GroundObjects.Any(item =>
                item.Id == action.ObjectId));
        var target = targetGpu?.Chunk.GroundObjects.FirstOrDefault(
            item => item.Id == action.ObjectId);
        if (targetGpu is null || target is null)
        {
            ObserveLog("world_action_cancelled", villager.Id, new
            {
                Action = action.Kind.ToString(),
                action.ObjectId,
                Reason = "target_disappeared"
            });
            return false;
        }
        if (action.Kind == VillagerWorldActionKind.TakeItem)
        {
            var reservationKey = ResourceReservationKey(
                "ground", target.Id.ToString());
            if (!_villagerWork.TryReserve(
                    reservationKey, villager.Id, _worldGameSeconds))
                return false;
            var intent = new NpcBrainIntent(
                "take_item", EntityAction.Gather,
                new(target.X, target.Y), target.Id.ToString());
            return BeginNpcControlledAction(
                villagerIndex,
                villager,
                intent,
                () => CompleteVillagerGroundPickup(
                    villager.Id, targetGpu, target.Id,
                    reservationKey, tier, intent),
                VillagerSimulation.GatherPauseSeconds,
                reservationKey,
                () => targetGpu.Chunk.GroundObjects.Any(value =>
                    value.Id == target.Id));
        }

        if (action.Kind !=
                VillagerWorldActionKind.DepositItems ||
            !string.Equals(
                target.OwnerId, villager.Id,
                StringComparison.Ordinal))
        {
            ObserveLog("world_action_failed", villager.Id, new
            {
                Action = action.Kind.ToString(),
                target.Id,
                Reason = "storage_not_owned"
            });
            return false;
        }
        var container = StorageContainerService.Open(target);
        var transfer = EntityInteractionService.DepositAll(
            container,
            villager.Inventory,
            villager.Id,
            itemId => VillagerStorageTransfer.IsWorkItemForRole(
                villager.WorkRole, itemId));
        if (transfer.ItemsMoved == 0)
        {
            ObserveLog("world_action_failed", villager.Id, new
            {
                Action = action.Kind.ToString(),
                target.Id,
                Reason = "nothing_depositable"
            });
            return false;
        }
        var savedStorage = StorageContainerService.Save(
            target, container);
        var targetIndex =
            targetGpu.Chunk.GroundObjects.IndexOf(target);
        targetGpu.Chunk.GroundObjects[targetIndex] = savedStorage;
        _villagers[villagerIndex] = villager with
        {
            Inventory = transfer.Inventory,
            Action = EntityAction.Work,
            ActionTime = 0,
            GoalObjectId = null,
            NextDecisionGameSeconds =
                _worldGameSeconds +
                VillagerSimulation.DecisionInterval(tier),
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = action.Kind.ToString(),
            target.Id,
            target.ItemId,
            ItemsMoved = transfer.ItemsMoved
        });
        QueueChunkSave(targetGpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private NpcActionResult CompleteVillagerGroundPickup(
        string actorId,
        GpuWorldChunk targetGpu,
        Guid targetId,
        string reservationKey,
        VillagerSimulationTier tier,
        NpcBrainIntent intent)
    {
        var actorIndex = VillagerIndex(actorId);
        var target = targetGpu.Chunk.GroundObjects.FirstOrDefault(value =>
            value.Id == targetId);
        if (actorIndex < 0 || target is null)
        {
            _villagerWork.ReleaseTarget(reservationKey, actorId);
            return new(intent, false, "target_unavailable");
        }
        var villager = _villagers[actorIndex];
        var crop = CropService.IsCrop(target);
        var gathered = crop
            ? EntityInteractionService.Harvest(
                villager.Inventory, target, _worldGameSeconds)
            : EntityInteractionService.Pickup(
                villager.Inventory, target.ItemId);
        var takenItemId = gathered.ItemId ?? target.ItemId;
        var ownedByAnother = !SettlementGroupService.CanAccess(
            villager, target.OwnerId, target.GroupOwnerId);
        if (ownedByAnother || !gathered.Succeeded)
        {
            _villagerWork.ReleaseTarget(reservationKey, actorId);
            ObserveLog("world_action_failed", actorId, new
            {
                Action = VillagerWorldActionKind.TakeItem.ToString(),
                target.Id,
                target.ItemId,
                Reason = ownedByAnother
                    ? "owned_by_another_actor"
                    : "inventory_full"
            });
            return new(intent, false,
                ownedByAnother ? "owned_by_another_actor" : "inventory_full");
        }
        if (!targetGpu.Chunk.GroundObjects.Remove(target))
        {
            _villagerWork.ReleaseTarget(reservationKey, actorId);
            return new(intent, false, "target_changed");
        }
        var harvestedCount = gathered.Quantity;
        var updated = villager with
        {
            Inventory = gathered.Inventory,
            FarmingExperience = crop
                ? FarmingSkill.AwardExperience(
                    villager.FarmingExperience,
                    FarmingSkill.PlantingExperience * harvestedCount)
                    .Experience
                : villager.FarmingExperience,
            Need = VillagerNeed.Explore,
            GoalObjectId = null,
            NextDecisionGameSeconds = _worldGameSeconds + Math.Max(
                VillagerSimulation.DecisionInterval(tier),
                VillagerSimulation.GatherPauseSeconds),
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        _villagers[actorIndex] =
            VillagerCommitmentService.RecordAcquiredItem(
                updated, takenItemId);
        _villagerWork.ReleaseTarget(reservationKey, actorId);
        ObserveLog("world_action_succeeded", actorId, new
        {
            Action = VillagerWorldActionKind.TakeItem.ToString(),
            target.Id,
            ItemId = takenItemId,
            PreviousOwner = target.OwnerId
                ?? target.GroupOwnerId
        });
        QueueChunkSave(targetGpu.Chunk);
        _villagersDirty = true;
        return new(intent, true);
    }

    private VillagerState ExchangeSettlementKnowledge(
        VillagerState villager)
    {
        if (_settlementGroup is not { } group ||
            villager.SettlementGroupId != group.Id ||
            villager.WorldLevel != group.WorldLevel ||
            Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY),
                group.Camp) > group.CacheRadius * group.CacheRadius)
            return villager;
        var updatedGroup = SettlementGroupService.ReportDiscoveries(
            group, villager);
        if (!ReferenceEquals(updatedGroup, group))
        {
            _settlementGroup = updatedGroup;
            if (_activeWorld is not null)
                _saves.SaveSettlementGroup(
                    _activeWorld.Id, updatedGroup);
            ObserveLog("settlement_knowledge_report", villager.Id, new
            {
                GroupId = group.Id,
                SharedLocations = updatedGroup.SharedLocations?.Count ?? 0
            });
            group = updatedGroup;
        }
        return SettlementGroupService.LearnReports(
            villager, group, _worldGameSeconds);
    }

    private VillagerState ConsiderIndependentCamp(VillagerState villager)
    {
        var result = IndependentSurvivorPolicy.ConsiderPersonalCamp(
            villager,
            _villagers.Count(value => value.Health > 0),
            _worldGameSeconds);
        if (ReferenceEquals(result, villager)) return villager;
        ObserveLog("independent_camp_selected", villager.Id, new
        {
            X = result.PersonalCampX,
            Y = result.PersonalCampY,
            result.PersonalCampWorldLevel
        });
        return result;
    }

    private VillagerState ReconcileVillagerLocationMemory(
        VillagerState villager)
    {
        if (villager.LocationMemories is not { Count: > 0 })
            return villager;
        var current = new Vector2(villager.PositionX, villager.PositionY);
        var updated = villager;
        foreach (var memory in villager.LocationMemories.ToArray())
        {
            if (memory.WorldLevel != villager.WorldLevel ||
                memory.Type == VillagerLocationType.Danger ||
                Vector2.DistanceSquared(
                    current,
                    new(memory.PositionX, memory.PositionY)) >
                VillagerLocationMemoryService.MatchRadius *
                VillagerLocationMemoryService.MatchRadius)
                continue;
            var rememberedPosition = new Vector2(
                memory.PositionX, memory.PositionY);
            var valid = memory.Type switch
            {
                VillagerLocationType.FoodSource =>
                    _villagerWorldObjects.Any(item =>
                        VillagerLocationMemoryService.LocationTypeForItem(
                            item.ItemId) == VillagerLocationType.FoodSource &&
                        Vector2.DistanceSquared(
                            item.Position, rememberedPosition) <=
                        VillagerLocationMemoryService.MatchRadius *
                        VillagerLocationMemoryService.MatchRadius),
                VillagerLocationType.Storage =>
                    _villagerWorldObjects.Any(item =>
                        item.IsStorage &&
                        Vector2.DistanceSquared(
                            item.Position, rememberedPosition) <=
                        VillagerLocationMemoryService.MatchRadius *
                        VillagerLocationMemoryService.MatchRadius),
                VillagerLocationType.WoodSource =>
                    _worldChunks.Values.Any(gpu =>
                        IsActiveSimulationChunk(gpu) &&
                        gpu.Chunk.TreeInstances.Any(tree =>
                            tree.State == TreeLifecycleState.Standing &&
                            Vector2.DistanceSquared(
                                new(tree.X + .5f, tree.Y + .5f),
                                rememberedPosition) <=
                            VillagerLocationMemoryService.MatchRadius *
                            VillagerLocationMemoryService.MatchRadius)),
                VillagerLocationType.FishingSpot =>
                    _worldChunks.Values.Any(gpu =>
                        IsActiveSimulationChunk(gpu) &&
                        gpu.Chunk.Fish.Any(fish =>
                        {
                            var profile = FishingSkill.Profile(fish.Species);
                            var remaining = gpu.Chunk.FishRemaining.TryGetValue(
                                fish.StableKey, out var count)
                                ? count
                                : profile.SchoolSize;
                            return remaining > 0 &&
                                   Vector2.DistanceSquared(
                                       new(fish.X, fish.Y),
                                       rememberedPosition) <=
                                   VillagerLocationMemoryService.MatchRadius *
                                   VillagerLocationMemoryService.MatchRadius;
                        })),
                _ => false
            };
            updated = valid
                ? VillagerLocationMemoryService.Remember(
                    updated,
                    memory.Type,
                    rememberedPosition,
                    memory.WorldLevel,
                    _worldGameSeconds,
                    clearFailedLocation: false)
                : VillagerLocationMemoryService.ObserveEmpty(
                    updated,
                    memory.Type,
                    rememberedPosition,
                    memory.WorldLevel,
                    _worldGameSeconds);
        }
        return updated;
    }

    private void SaveVillagers()
    {
        if (!_villagersDirty || _activeWorld is null) return;
        _saves.SaveVillagers(_activeWorld.Id, _villagers);
        _villagersDirty = false;
        _villagersNextSaveAt = _worldGameSeconds + 30;
    }

    private void ShowVillagerSpeech(
        int villagerIndex,
        string message,
        Vector2 listenerPosition)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count ||
            string.IsNullOrWhiteSpace(message))
            return;
        var villager = _villagers[villagerIndex];
        var seconds = ConversationLineSeconds(message);
        HoldVillagerConversation(
            villagerIndex, listenerPosition, seconds);
        villager = _villagers[villagerIndex];
        TakeConversationFloor(villager.Id, seconds);
        _villagerSpeechBubbles[villager.Id] =
            new(message, _clock + seconds);
        _chatUi.AddMessage(
            $"{villager.Name}: {message}",
            ChatMessageStyle.Npc);
    }

    private bool ConversationFloorBusy =>
        _clock < _conversationFloorUntil;

    private static double ConversationLineSeconds(string message) =>
        Math.Clamp(
            1.5 + message.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length * .16,
            2,
            5);

    private bool ConversationHasFinished(VillagerState villager) =>
        _worldGameSeconds >= villager.ActivityUntilGameSeconds ||
        (_npcAiDialogueTask is null && !ConversationFloorBusy);

    private void TakeConversationFloor(
        string speakerId,
        double seconds)
    {
        _conversationFloorSpeakerId = speakerId;
        _conversationFloorUntil = double.IsPositiveInfinity(seconds)
            ? double.PositiveInfinity
            : _clock + seconds;
    }

    private bool TryQueuePlayerConversationTurn(string message)
    {
        if (_player is null || _activePlayer is null ||
            !_villagers.Any(villager =>
                villager.WorldLevel == _activeWorldLevel &&
                Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    _player.Position) < 10 * 10))
            return false;
        if (_queuedPlayerConversationTurns.Count < 8)
            _queuedPlayerConversationTurns.Enqueue(message);
        ShowOverheadSpeech(message);
        if (!ConversationFloorBusy)
            StartNextPlayerConversationTurn();
        return true;
    }

    private void UpdateConversationTurns()
    {
        if (ConversationFloorBusy) return;
        _conversationFloorSpeakerId = null;
        StartNextPlayerConversationTurn();
    }

    private void StartNextPlayerConversationTurn()
    {
        if (_activePlayer is null ||
            _queuedPlayerConversationTurns.Count == 0)
            return;
        var message = _queuedPlayerConversationTurns.Dequeue();
        TryHandleVillagerChat(message);
    }

    private void HoldVillagerConversation(
        int villagerIndex,
        Vector2 listenerPosition,
        double seconds,
        string? partnerId = null)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count)
            return;
        var villager = _villagers[villagerIndex];
        var position = new Vector2(
            villager.PositionX, villager.PositionY);
        var direction = listenerPosition - position;
        if (direction.LengthSquared > .0001f)
            direction = direction.Normalized();
        villager = VillagerSimulation.BeginConversation(
            villager with
            {
                FacingX = direction.X,
                FacingY = direction.Y
            },
            partnerId,
            _worldGameSeconds,
            seconds);
        _villagers[villagerIndex] = villager;
        _villagersDirty = true;
    }

    private void RenderVillagerOverheadSpeech(Vector4 scene)
    {
        if (_chatFont is null || _fontRenderer is null ||
            _villagerSpeechBubbles.Count == 0)
            return;
        foreach (var villager in _villagers)
        {
            if (!_villagerSpeechBubbles.TryGetValue(
                    villager.Id, out var bubble) ||
                _clock >= bubble.ExpiresAt ||
                villager.WorldLevel != _activeWorldLevel ||
                !_entityAnimations.TryGetValue(
                    (villager.Gender, villager.Action),
                    out var animation))
                continue;
            var directional = VillagerDirectionRig.Resolve(
                new(villager.FacingX, villager.FacingY),
                animation.Graphic.Sprite.Frames.Count,
                5,
                (int)(villager.ActionTime /
                      animation.SecondsPerFrame));
            var terrain = SamplePlayerTerrain(
                villager.PositionX, villager.PositionY);
            var projected = IsometricTerrainProjection.Project(
                villager.PositionX,
                villager.PositionY,
                terrain.Height);
            var sprite = SpriteBounds(
                animation.Graphic.Sprite.Frames[directional.Index],
                projected,
                directional.Mirror);
            DrawVillagerSpeechBubble(
                scene, sprite, bubble.Text);
        }
    }

    private VillagerState CompleteVillagerActionAnimation(
        VillagerState villager)
    {
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, villager.Action), out var animation) ||
            !EntityActionLifecycle.HasCompletedAnimation(
                villager.Action,
                villager.ActionTime,
                EntityActionLifecycle.FramesPerDirection(
                    animation.Textures.Length),
                animation.SecondsPerFrame /
                VillagerFatigueService.WorkEffectiveness(
                    villager.Energy)))
            return villager;
        return VillagerSimulation.CompleteAction(villager);
    }

    private VillagerState AdvanceNpcController(VillagerState villager)
    {
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, villager.Action), out var animation))
            return villager;
        var wasBusy = _npcController.IsBusy(villager.Id);
        _npcController.Advance(
            villager.Id,
            villager.Action,
            villager.ActionTime,
            EntityActionLifecycle.FramesPerDirection(
                animation.Textures.Length) * animation.SecondsPerFrame /
            VillagerFatigueService.WorkEffectiveness(villager.Energy));
        while (_npcController.TryDequeueResult(
                   out var actorId, out var result))
        {
            ObserveLog(
                result.Succeeded
                    ? "npc_action_impact"
                    : "npc_action_failed",
                actorId,
                new
                {
                    Action = result.Intent.Name,
                    Target = result.Intent.TargetKey,
                    result.Reason
                });
        }
        var refreshedIndex = _villagers.FindIndex(value =>
            value.Id == villager.Id);
        var refreshed = refreshedIndex >= 0
            ? _villagers[refreshedIndex]
            : villager;
        return wasBusy && !_npcController.IsBusy(villager.Id)
            ? VillagerSimulation.CompleteAction(refreshed)
            : refreshed;
    }

    private void DrawVillagerSpeechBubble(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) sprite,
        string fullText)
    {
        const float horizontalPadding = 9;
        const float verticalPadding = 6;
        var font = _chatFont!;
        var renderer = _fontRenderer!;
        var scale = scene.Z / ReferenceWidth;
        var centerX = scene.X +
                      (sprite.Left + sprite.Right) * .5f * scale;
        const float maximumTextWidth = 260;
        var lines = new List<string>(3);
        var current = "";
        foreach (var word in fullText.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0
                ? word
                : current + " " + word;
            if (current.Length > 0 &&
                font.MeasureString(candidate).X >
                maximumTextWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
                current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) return;
        var lineHeight = MathF.Ceiling(
            font.MeasureString("Ag").Y);
        var textWidth = lines.Max(line =>
            font.MeasureString(line).X);
        var width = textWidth + horizontalPadding * 2;
        var height =
            lineHeight * lines.Count + verticalPadding * 2;
        var x = Math.Clamp(
            centerX - width * .5f,
            scene.X + 4,
            scene.X + scene.Z - width - 4);
        var y = Math.Max(
            scene.Y + 4,
            scene.Y + sprite.Top * scale - height - 12);
        var bounds = new Vector4(
            MathF.Round(x), MathF.Round(y),
            MathF.Ceiling(width), MathF.Ceiling(height));
        DrawRoundedUiColor(bounds, 6, new(.68f, .68f, .66f, .9f));
        DrawRoundedUiColor(
            new(bounds.X + 1, bounds.Y + 1,
                bounds.Z - 2, bounds.W - 2),
            5, new(.98f, .98f, .97f, .98f));
        var tailCenter = Math.Clamp(
            centerX,
            bounds.X + 10,
            bounds.X + bounds.Z - 10);
        DrawUiColor(
            new(
                MathF.Round(tailCenter - 3),
                bounds.Y + bounds.W - 1,
                6,
                6),
            new(.98f, .98f, .97f, .98f));
        _uiColorBatch.Flush();
        for (var index = 0; index < lines.Count; index++)
            font.DrawText(
                renderer,
                lines[index],
                new(
                    bounds.X + horizontalPadding,
                    bounds.Y + verticalPadding +
                    index * lineHeight),
                new FSColor(20, 20, 18, 255));
    }

    private bool TryHandleVillagerChat(string message)
    {
        if (_player is null) return false;
        var nearestIndex = -1;
        var nearestDistance = 10f * 10f;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var villager = _villagers[index];
            if (villager.WorldLevel != _activeWorldLevel) continue;
            var distance = Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY),
                _player.Position);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestIndex = index;
        }
        if (nearestIndex < 0) return false;
        var target = _villagers[nearestIndex];
        var text = message.Trim();
        var lower = text.ToLowerInvariant();
        if (_activePlayer is not null &&
            (lower.Contains("follow me") ||
             lower.Contains("come with me")))
        {
            target = target with
            {
                FollowingActorId = _activePlayer.Id,
                NextDecisionGameSeconds = _worldGameSeconds
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "All right, I'll stay with you.",
                _player.Position);
            return true;
        }
        if (lower.Contains("come here") ||
            lower.Contains("come back"))
        {
            target = target with
            {
                FollowingActorId = _activePlayer?.Id,
                NextDecisionGameSeconds = _worldGameSeconds
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "I'm coming.",
                _player.Position);
            return true;
        }
        if (lower.Contains("go away") ||
            lower.Contains("leave me alone") ||
            lower.Contains("get away from me"))
        {
            var hostile = lower.Contains("fuck") ||
                          lower.Contains("bitch") ||
                          lower.Contains("ugly") ||
                          lower.Contains("idiot") ||
                          lower.Contains("stupid");
            var dismissalReply = hostile
                ? "Fine. I'll leave, but don't speak to me like that."
                : "All right. I'll give you some space.";
            target = VillagerSimulation.ApplyDismissal(
                target,
                _activePlayer?.Id ?? "player",
                _activePlayer is null
                    ? "the stranger"
                    : VillagerSimulation.PerceivedName(
                        target, _activePlayer.Id),
                text,
                dismissalReply,
                hostile ? -35 : -8,
                _worldGameSeconds);
            _villagers[nearestIndex] = target;
            _villagersDirty = true;
            ShowVillagerSpeech(
                nearestIndex,
                dismissalReply,
                _player.Position);
            return true;
        }
        if (lower is "wait" or "wait here" or "stay here" ||
            lower.Contains("stop following"))
        {
            target = target with
            {
                FollowingActorId = null,
                Action = EntityAction.Idle,
                ActionTime = 0,
                TargetX = null,
                TargetY = null
            };
            _villagers[nearestIndex] = target;
            ShowVillagerSpeech(
                nearestIndex,
                "I'll wait here.",
                _player.Position);
            HoldVillagerConversation(
                nearestIndex, _player.Position, 10);
            return true;
        }
        if (TryBeginNpcAiSpeech(nearestIndex, message))
            return true;
        if (VillagerCommitmentService.TryParseGatherRequest(
                text, out var requestedItem,
                out var requestedQuantity))
        {
            ShowVillagerSpeech(
                nearestIndex,
                $"I understand you are proposing {requestedQuantity} " +
                $"{ItemCatalog.Get(requestedItem).Name}, but I cannot " +
                "decide without thinking it through.",
                _player.Position);
            return true;
        }
        var response = FallbackNpcReply(target, message);
        ShowVillagerSpeech(
            nearestIndex,
            response,
            _player.Position);
        return true;
    }

    private void NotifyVillagersOfTaking(
        WorldGroundObject item)
    {
        if (_player is null ||
            _activePlayer is null ||
            string.IsNullOrWhiteSpace(item.OwnerId) &&
            string.IsNullOrWhiteSpace(item.GroupOwnerId) ||
            string.Equals(
                item.OwnerId, _activePlayer.Id,
                StringComparison.Ordinal))
            return;
        var ownershipId = item.OwnerId ?? item.GroupOwnerId!;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var observer = _villagers[index];
            if (observer.WorldLevel != _activeWorldLevel ||
                item.OwnerId is not null && !string.Equals(
                    observer.Id, item.OwnerId, StringComparison.Ordinal) ||
                item.GroupOwnerId is not null && !string.Equals(
                    observer.SettlementGroupId,
                    item.GroupOwnerId,
                    StringComparison.Ordinal) ||
                Vector2.DistanceSquared(
                    new(observer.PositionX, observer.PositionY),
                    _player.Position) > 12 * 12)
                continue;
            observer =
                VillagerSimulation.ObserveUnauthorizedTaking(
                    observer,
                    item.Id,
                    item.ItemId,
                    ownershipId,
                    _activePlayer.Id,
                    _worldGameSeconds,
                    confidence: 1,
                    itemValue: ItemValue(item.ItemId),
                    out var reaction);
            _villagers[index] = observer;
            _villagersDirty = true;
            if (reaction == OwnershipReaction.None)
                continue;
            _chatUi.AddMessage(
                $"{observer.Name}: " +
                VillagerSimulation.ReactionSpeech(
                    observer.Name,
                    ItemCatalog.Get(item.ItemId).Name,
                    reaction),
                ChatMessageStyle.Warning);
        }
    }

    internal void GiveItemToVillager(
        string villagerId,
        int inventorySlot,
        string itemId)
    {
        if (_player is null || _activePlayer is null ||
            !InventoryContainsAt(inventorySlot, itemId))
            return;
        var villagerIndex = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (villagerIndex < 0) return;
        var villager = _villagers[villagerIndex];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        if (Vector2.Distance(_player.Position, target) >
            VillagerSimulation.InteractionRange + .3f)
        {
            _worldActions.QueueVillagerGift(
                villager, inventorySlot, itemId);
            return;
        }
        var itemName = ItemCatalog.Get(itemId).Name;
        var giftSpeech =
            $"{villager.Name}, this {itemName} is for you.";
        var decisionPrompt =
            $"I offer you this {itemName} as a gift. Do you accept it?";
        if (_pendingVillagerGift is not null ||
            !TryBeginNpcAiSpeech(villagerIndex, decisionPrompt))
        {
            ReportBlockedAction(
                "villager-gift-blocked",
                "The survivor cannot consider the offer while the AI model is unavailable or busy.");
            return;
        }
        _pendingVillagerGift = new(
            villager.Id, _activePlayer.Id, inventorySlot, itemId);
        _chatUi.AddMessage(
            $"{_activePlayer.Name}: {giftSpeech}",
            ChatMessageStyle.Player);
        ShowOverheadSpeech(giftSpeech);
        _player.Stop();
    }

    private void ResolveVillagerGiftOffer(
        int villagerIndex,
        PendingVillagerGift gift,
        NpcAiInterpretation interpretation,
        string fallback)
    {
        if (_activePlayer is null || _player is null ||
            _activePlayer.Id != gift.PlayerId ||
            (uint)villagerIndex >= (uint)_villagers.Count)
            return;
        if (!string.Equals(
                interpretation.Decision, "accept",
                StringComparison.OrdinalIgnoreCase))
        {
            ApplyNpcAiInterpretation(
                villagerIndex, interpretation,
                "No, I won't take it.");
            return;
        }

        var villager = _villagers[villagerIndex];
        var reply = string.IsNullOrWhiteSpace(interpretation.Reply)
            ? fallback
            : interpretation.Reply;
        var intent = new NpcBrainIntent(
            "receive_gift", EntityAction.Gather,
            new(villager.PositionX, villager.PositionY), gift.ItemId);
        if (BeginNpcControlledAction(
                villagerIndex, villager, intent,
                () => CompleteAcceptedVillagerGift(gift, reply),
                VillagerSimulation.GatherPauseSeconds))
            return;
        ShowVillagerSpeech(
            villagerIndex,
            "I cannot take that just now.",
            _player.Position);
    }

    private NpcActionResult CompleteAcceptedVillagerGift(
        PendingVillagerGift gift,
        string reply)
    {
        var intent = new NpcBrainIntent(
            "receive_gift", EntityAction.Gather, null, gift.ItemId);
        if (_activePlayer is null || _player is null ||
            _activePlayer.Id != gift.PlayerId ||
            !InventoryContainsAt(gift.PlayerInventorySlot, gift.ItemId))
            return new(intent, false, "offer_unavailable");
        var villagerIndex = VillagerIndex(gift.VillagerId);
        if (villagerIndex < 0) return new(intent, false, "actor_unavailable");
        var villager = _villagers[villagerIndex];
        if (villager.Health <= 0 ||
            Vector2.DistanceSquared(
                _player.Position,
                new(villager.PositionX, villager.PositionY)) >
            MathF.Pow(VillagerSimulation.InteractionRange + .3f, 2) ||
            !VillagerGiftTransferService.TryTransfer(
                _activePlayer.Inventory,
                gift.PlayerInventorySlot,
                gift.ItemId,
                villager.Inventory,
                out var playerInventory,
                out var receiverInventory))
            return new(intent, false, "transfer_failed");

        var itemInstanceId = Guid.NewGuid();
        var giverName = VillagerSimulation.PerceivedName(
            villager, _activePlayer.Id, "stranger");
        villager = VillagerSimulation.RecordGift(
            villager with { Inventory = receiverInventory },
            _activePlayer.Id, giverName, itemInstanceId,
            gift.ItemId, _worldGameSeconds);
        villager = VillagerSimulation.RecordDialogueTurn(
            villager, villager.Id, villager.Name,
            reply, _worldGameSeconds);
        _villagers[villagerIndex] = villager;
        _activePlayer = _activePlayer with
        {
            Inventory = playerInventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == gift.PlayerInventorySlot)
            _activeInventorySlot = -1;
        _villagersDirty = true;
        _saves.SavePlayer(_activePlayer);
        ShowVillagerSpeech(villagerIndex, reply, _player.Position);
        return new(intent, true);
    }

    private static int ItemValue(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.HasTag(ItemTag.MetalToolSprite)) return 30;
        if (item.HasTag(ItemTag.Tool)) return 15;
        if (item.HasTag(ItemTag.PlaceableObject)) return 20;
        if (item.HasTag(ItemTag.CookedFood)) return 5;
        return 2;
    }

    private ActorVisual? GetVillagerVisual(VillagerState villager)
    {
        const int storedVillagerAngles = 5;
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, villager.Action),
                out var animation))
            return null;
        var graphic = animation.Graphic;
        var rawFrame = (int)(
            VillagerVisualAnimationTime(villager) /
            animation.SecondsPerFrame);
        if (villager.Action is EntityAction.Die or EntityAction.Hurt)
        {
            var framesPerAngle = Math.Max(
                1, graphic.Sprite.Frames.Count /
                   storedVillagerAngles);
            rawFrame = Math.Min(
                rawFrame, framesPerAngle - 1);
        }
        var directional = VillagerDirectionRig.Resolve(
            new Vector2(
                villager.FacingX,
                villager.FacingY),
            graphic.Sprite.Frames.Count,
            storedVillagerAngles,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            villager.PositionX, villager.PositionY);
        var world = IsometricTerrainProjection.Project(
            villager.PositionX,
            villager.PositionY,
            terrain.Height);
        return new ActorVisual(
            graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index],
            world,
            directional.Mirror,
            terrain.Biome is
                Biome.ShallowWater or
                Biome.RiverWater or
                Biome.MangroveShallows,
            villager.TeamColor);
    }

    private bool TryVillagerSpriteBounds(
        VillagerState villager,
        out (float Left, float Top, float Right, float Bottom) bounds)
    {
        const int storedVillagerAngles = 5;
        if (!_entityAnimations.TryGetValue(
                (villager.Gender, villager.Action),
                out var animation))
        {
            bounds = default;
            return false;
        }
        var rawFrame = (int)(
            VillagerVisualAnimationTime(villager) /
            animation.SecondsPerFrame);
        if (villager.Action is EntityAction.Die or EntityAction.Hurt)
        {
            var framesPerAngle = Math.Max(
                1, animation.Graphic.Sprite.Frames.Count /
                   storedVillagerAngles);
            rawFrame = Math.Min(
                rawFrame, framesPerAngle - 1);
        }
        var directional = VillagerDirectionRig.Resolve(
            new(villager.FacingX, villager.FacingY),
            animation.Graphic.Sprite.Frames.Count,
            storedVillagerAngles,
            rawFrame);
        var terrain = SamplePlayerTerrain(
            villager.PositionX, villager.PositionY);
        bounds = SpriteBounds(
            animation.Graphic.Sprite.Frames[directional.Index],
            IsometricTerrainProjection.Project(
                villager.PositionX,
                villager.PositionY,
                terrain.Height),
            directional.Mirror);
        return true;
    }

    private double VillagerVisualAnimationTime(VillagerState villager)
    {
        if (villager.Action != EntityAction.Idle)
            return villager.ActionTime;
        uint hash = 2166136261;
        foreach (var character in villager.Id)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return _clock + (hash % 10_000) / 997.0;
    }

    private bool TryGetVillagerUnderMouse(
        Vector2 mouse,
        out VillagerState villager)
    {
        for (var index = _villagers.Count - 1; index >= 0; index--)
        {
            var candidate = _villagers[index];
            if (candidate.WorldLevel != _activeWorldLevel ||
                candidate.Health <= 0 ||
                !TryVillagerSpriteBounds(
                    candidate, out var bounds))
                continue;
            if (mouse.X < bounds.Left || mouse.X > bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y > bounds.Bottom)
                continue;
            villager = candidate;
            return true;
        }
        villager = null!;
        return false;
    }
}
