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
        _nextVillagerRoleAssignment = 0;
        if (_activeWorld is null) return;
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
            _villagers.AddRange(
                VillagerSimulation.CreateInitial(
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
                    setups: _activeWorld.AiNpcSetups));
            _villagersDirty = true;
        }
        _villagersNextSaveAt = _worldGameSeconds + 30;
    }

    private void UpdateVillagers(float elapsed)
    {
        if (_player is null || _activeWorld is null) return;
        UpdateConversationTurns();
        _villagerWork.Expire(_worldGameSeconds);
        if (_worldGameSeconds >= _nextVillagerRoleAssignment)
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
                    _villagers[roleIndex] = roleVillager with { WorkRole = role };
                    _villagersDirty = true;
                }
            }
            _nextVillagerRoleAssignment = _worldGameSeconds + 30 * 60;
        }
        UpdateSettlementProjectAssignments();
        TryPromptStalledProject();
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
            if (previous.Activity == VillagerActivity.Conversing &&
                ConversationHasFinished(previous))
            {
                previous = VillagerSimulation.CompleteConversation(
                    previous, _worldGameSeconds);
            }
            previous = VillagerSimulation.CompleteReflection(
                previous, _worldGameSeconds);
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
            if (!ReferenceEquals(previous, villager))
            {
                _villagers[index] = villager;
                _villagersDirty = true;
            }
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
            if (!ReferenceEquals(beforeNeedObservation, villager))
            {
                _villagers[index] = villager;
                _villagersDirty = true;
            }
            if (TryExecuteVillagerSocialGoal(
                    index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                villager.ProjectAssignment?.Requirements.Any(requirement =>
                    VillagerSettlementProjectService.NeedsItem(
                        villager, requirement.ItemId)) == true &&
                TryExecuteVillagerWorldAction(
                    index, villager, tier))
                continue;
            if (tier != VillagerSimulationTier.Distant &&
                TryExecuteVillagerCapabilityAction(
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
        if (_villagersDirty &&
            _worldGameSeconds >= _villagersNextSaveAt)
            SaveVillagers();
    }

    private void UpdateSettlementProjectAssignments()
    {
        var placedItems = _worldChunks.Values
            .Where(IsActiveSimulationChunk)
            .SelectMany(value => value.Chunk.GroundObjects)
            .Select(value => value.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = VillagerSettlementProjectService.Plan(
            _villagers, placedItems);
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
                    assignedAt);
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
                Assignments = plan.Assignments
            });
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
        var builderIndex = _villagers.FindIndex(value =>
            value.Id == assignment.BuilderId && value.Health > 0);
        if (builderIndex < 0) return;
        var builder = _villagers[builderIndex];
        if (Vector2.DistanceSquared(
                new(builder.PositionX, builder.PositionY),
                new(stalled.PositionX, stalled.PositionY)) >
            VillagerSimulation.SocialRange * VillagerSimulation.SocialRange)
            return;
        var requirement = assignment.Requirements.First();
        var itemName = ItemCatalog.Get(requirement.ItemId).Name;
        var text = $"{stalled.Name}, we still need {itemName} for " +
                   $"the {ItemCatalog.Get(assignment.ProjectItemId).Name}. " +
                   "What is stopping you?";
        ShowVillagerSpeech(
            builderIndex,
            text,
            new(stalled.PositionX, stalled.PositionY));
        var stalledIndex = _villagers.FindIndex(value =>
            value.Id == stalled.Id);
        if (stalledIndex >= 0)
        {
            var shared = VillagerSimulation.RecordSharedDialogueLine(
                _villagers[builderIndex],
                _villagers[stalledIndex],
                text,
                _worldGameSeconds);
            _villagers[builderIndex] = shared.Speaker;
            _villagers[stalledIndex] = shared.Listener;
            _villagersDirty = true;
        }
        _nextProjectAccountability[stalled.Id] = _worldGameSeconds +
            VillagerSettlementProjectService.AccountabilityDelayGameSeconds;
        ObserveLog("settlement_accountability", builder.Id, new
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
                        item.ItemId)));
            }
        }
        var beforeLocationObservation = villager;
        villager = VillagerLocationMemoryService.ObserveWorldObjects(
            villager,
            CollectionsMarshal.AsSpan(_villagerWorldObjects),
            _worldGameSeconds);
        villager = ReconcileVillagerLocationMemory(villager);
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
            var takenItemId = CropService.IsCrop(target)
                ? target.FuelItemId!
                : target.ItemId;
            var gathered = ActorActionService.Gather(
                villager.Inventory,
                takenItemId,
                CropService.IsCrop(target)
                    ? CropService.HarvestCount(villager.Inventory)
                    : 1);
            if (target.OwnerId is { Length: > 0 } owner &&
                !string.Equals(
                    owner, villager.Id,
                    StringComparison.Ordinal) ||
                !gathered.Succeeded)
            {
                ObserveLog("world_action_failed", villager.Id, new
                {
                    Action = action.Kind.ToString(),
                    target.Id,
                    target.ItemId,
                    Reason = target.OwnerId is { Length: > 0 }
                        ? "owned_by_another_actor"
                        : "inventory_full"
                });
                return false;
            }
            if (!targetGpu.Chunk.GroundObjects.Remove(target))
            {
                ObserveLog("world_action_cancelled", villager.Id, new
                {
                    Action = action.Kind.ToString(),
                    target.Id,
                    Reason = "target_changed"
                });
                return false;
            }
            var harvestedCount = gathered.Inventory.Count(value =>
                                     value == takenItemId) -
                                 villager.Inventory.Count(value =>
                                     value == takenItemId);
            var updatedVillager = villager with
            {
                Inventory = gathered.Inventory,
                FarmingExperience = CropService.IsCrop(target)
                    ? FarmingSkill.AwardExperience(
                        villager.FarmingExperience,
                        FarmingSkill.PlantingExperience *
                        harvestedCount)
                        .Experience
                    : villager.FarmingExperience,
                Need = VillagerNeed.Explore,
                Action = EntityAction.Idle,
                ActionTime = 0,
                GoalObjectId = null,
                NextDecisionGameSeconds =
                    _worldGameSeconds +
                    Math.Max(
                        VillagerSimulation.DecisionInterval(tier),
                        VillagerSimulation.GatherPauseSeconds),
                LastSimulatedGameSeconds = _worldGameSeconds
            };
            _villagers[villagerIndex] =
                VillagerCommitmentService.RecordAcquiredItem(
                    updatedVillager, takenItemId);
            ObserveLog("world_action_succeeded", villager.Id, new
            {
                Action = action.Kind.ToString(),
                target.Id,
                ItemId = takenItemId,
                PreviousOwner = target.OwnerId
            });
            QueueChunkSave(targetGpu.Chunk);
            _villagersDirty = true;
            return true;
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
        var transfer = VillagerStorageTransfer.DepositAll(
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
                    _worldGameSeconds)
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
            var acceptance =
                VillagerCommitmentService.TryAccept(
                    target,
                    _activePlayer?.Id ?? "player",
                    VillagerPromiseKind.GatherItem,
                    requestedItem,
                    requestedQuantity,
                    _worldGameSeconds);
            if (acceptance.Accepted &&
                acceptance.Promise is { } promise)
            {
                target =
                    VillagerCommitmentService.AddPromise(
                        target, promise);
                _villagers[nearestIndex] = target;
                _villagersDirty = true;
            }
            ShowVillagerSpeech(
                nearestIndex,
                acceptance.Reply,
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
            string.IsNullOrWhiteSpace(item.OwnerId) ||
            string.Equals(
                item.OwnerId, _activePlayer.Id,
                StringComparison.Ordinal))
            return;
        for (var index = 0; index < _villagers.Count; index++)
        {
            var observer = _villagers[index];
            if (observer.WorldLevel != _activeWorldLevel ||
                !string.Equals(
                    observer.Id, item.OwnerId,
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
                    item.OwnerId,
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
        if (!TryGetDropTerrain(
                (int)MathF.Floor(target.X),
                (int)MathF.Floor(target.Y),
                out var gpu,
                out var reason))
        {
            ReportBlockedAction("villager-gift-blocked", reason);
            return;
        }
        if (!PlayerInventory.TryRemove(
                _activePlayer.Inventory,
                inventorySlot,
                out var inventory))
            return;

        var itemInstanceId = Guid.NewGuid();
        gpu.Chunk.GroundObjects.Add(new(
            itemInstanceId,
            itemId,
            target.X,
            target.Y,
            OwnerId: villager.Id));
        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot == inventorySlot)
            _activeInventorySlot = -1;
        var itemName = ItemCatalog.Get(itemId).Name;
        var playerAddress = VillagerSimulation.PerceivedName(
            villager, _activePlayer.Id, "stranger");
        villager = VillagerSimulation.RecordGift(
            villager,
            _activePlayer.Id,
            playerAddress,
            itemInstanceId,
            itemId,
            _worldGameSeconds);
        var giftSpeech =
            $"{villager.Name}, this {itemName} is for you.";
        villager = VillagerSimulation.RecordDialogueTurn(
            villager,
            _activePlayer.Id,
            playerAddress,
            giftSpeech,
            _worldGameSeconds);
        villager = VillagerSimulation.RecordDialogueTurn(
            villager,
            villager.Id,
            villager.Name,
            $"Thank you, {playerAddress}.",
            _worldGameSeconds + 1);
        _villagers[villagerIndex] = villager;
        _villagersDirty = true;
        _saves.SavePlayer(_activePlayer);
        QueueChunkSave(gpu.Chunk);

        var message =
            $"{_activePlayer.Name}: {giftSpeech}";
        _chatUi.AddMessage(message, ChatMessageStyle.Player);
        ShowOverheadSpeech(
            $"{villager.Name}, this {itemName} is for you.");
        ShowVillagerSpeech(
            villagerIndex,
            $"Thank you, {playerAddress}.",
            _player.Position);
        _player.Stop();
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
            villager.ActionTime / animation.SecondsPerFrame);
        if (villager.Action == EntityAction.Die)
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
            villager.ActionTime / animation.SecondsPerFrame);
        if (villager.Action == EntityAction.Die)
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
