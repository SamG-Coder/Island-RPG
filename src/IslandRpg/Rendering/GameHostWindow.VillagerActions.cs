using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool BeginNpcControlledAction(
        int index,
        VillagerState villager,
        NpcBrainIntent intent,
        Func<NpcActionResult> interaction,
        double recoveryGameSeconds,
        string? reservationKey = null,
        Func<bool>? targetAvailable = null)
    {
        if (!_npcController.TryBegin(
                villager.Id,
                intent,
                interaction,
                reservationKey is null
                    ? null
                    : () => _villagerWork.ReleaseTarget(
                        reservationKey, villager.Id),
                targetAvailable))
            return false;
        _villagers[index] = villager with
        {
            Action = intent.Action,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerFatigueService.AdjustedWorkDuration(
                    recoveryGameSeconds, villager.Energy),
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        _villagersDirty = true;
        return true;
    }

    private int VillagerIndex(string actorId) =>
        _villagers.FindIndex(value => value.Id == actorId);

    private bool TryExecuteVillagerCapabilityAction(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Health <= 0) return false;
        if (VillagerFatigueService.ShouldRest(villager)) return false;
        if (TryExecuteVillagerUrgentAction(index, villager, tier) ||
            TryVillagerSettlementContribution(index, villager, tier) ||
            TryVillagerReachProjectWorksite(index, villager, tier) ||
            TryVillagerPlaceCompletedProject(index, villager) ||
            TryVillagerWithdrawWorkItem(index, villager) ||
            TryVillagerRoleAction(index, villager, tier) ||
            TryVillagerCookStew(index, villager) ||
            TryVillagerCook(index, villager) ||
            TryVillagerWithdrawFood(index, villager) ||
            TryVillagerCraft(index, villager) ||
            TryVillagerGatherTreeSticks(index, villager, tier) ||
            TryVillagerForage(index, villager, tier) ||
            TryVillagerFish(index, villager, tier) ||
            TryVillagerCutTree(index, villager, tier) ||
            TryVillagerMine(index, villager, tier) ||
            TryVillagerPlaceObject(index, villager) ||
            TryVillagerPlaceOrTendCampfire(index, villager) ||
            TryVillagerTakeCampfireFuel(index, villager) ||
            TryVillagerDropRequestedItem(index, villager) ||
            TryVillagerFulfilGift(index, villager, tier))
            return true;
        return false;
    }

    private bool TryExecuteVillagerUrgentAction(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier) =>
        villager.Health > 0 &&
        (TryVillagerResolveNpcConflict(index, villager, tier) ||
         TryVillagerDefendSelf(index, villager, tier) ||
         TryVillagerMeetUrgentFoodNeed(index, villager, tier));

    private bool TryVillagerMeetUrgentFoodNeed(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (!VillagerIntentPriorityService.NeedsUrgentFood(villager))
            return false;

        return TryVillagerEat(index, villager, tier) ||
               TryVillagerWithdrawFood(index, villager) ||
               TryVillagerForage(index, villager, tier) ||
               TryVillagerFish(index, villager, tier) ||
               TryExploreForUrgentFood(index, villager, tier);
    }

    private bool TryExploreForUrgentFood(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        var preferred = VillagerSettlementProjectService
            .ContinuingExplorationTarget(villager, _worldGameSeconds);
        var target = WorldLevelNavigation.ReachableExplorationTarget(
            _worldSeed,
            new(villager.PositionX, villager.PositionY),
            preferred,
            villager.WorldLevel);
        MoveVillagerForCapability(
            index, villager, tier, target, VillagerNeed.Food);
        return true;
    }

    private bool TryExecuteVillagerCommittedAction(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (!VillagerIntentPriorityService.HasCommittedWork(villager) ||
            !VillagerIntentPriorityService.ShouldProtectCommittedWork(
                villager))
            return false;
        if (TryExecuteVillagerPlanDirective(index, villager, tier))
            return true;
        if (VillagerPromisePlanService.HasActiveWork(villager))
        {
            var collectionItem = VillagerPromisePlanService
                .CurrentCollectionItem(villager);
            return TryVillagerPromiseRendezvous(index, villager, tier) ||
                   // Deliver possessed promised items before looking for more
                   // resources. Otherwise any available forage target can
                   // indefinitely starve the hand-off.
                   TryVillagerFulfilGift(index, villager, tier) ||
                   collectionItem is not null &&
                   TryCollectPromisedItem(
                       index, villager, tier, collectionItem) ||
                   TryExploreForPromise(index, villager, tier);
        }
        return TryVillagerPromiseRendezvous(index, villager, tier) ||
               TryVillagerSettlementContribution(index, villager, tier) ||
               TryVillagerReachProjectWorksite(index, villager, tier) ||
               TryVillagerPlaceCompletedProject(index, villager) ||
               TryVillagerWithdrawWorkItem(index, villager) ||
               TryVillagerCraft(index, villager) ||
               TryVillagerGatherTreeSticks(index, villager, tier) ||
               TryVillagerForage(index, villager, tier) ||
               TryVillagerCutTree(index, villager, tier) ||
               TryVillagerMine(index, villager, tier) ||
               TryVillagerPlaceObject(index, villager) ||
               TryVillagerPlaceOrTendCampfire(index, villager) ||
               TryVillagerProjectExplore(index, villager, tier) ||
               TryExecuteVillagerWorldAction(index, villager, tier);
    }

    private bool TryCollectPromisedItem(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier,
        string itemId)
    {
        // A matching loose item is always cheaper than producing another.
        if (TryExecuteVillagerWorldAction(
                index, villager, tier, itemId))
            return true;
        var route = VillagerCollectionRouteService.For(itemId);
        if (!VillagerCollectionRouteService.HasRequiredTool(
                route, villager.Inventory))
        {
            // Reuse the autonomous dependency planner. It stages foundation
            // materials and tools; a normal pickup supplies any missing loose
            // prerequisite without pretending it fulfils the promise.
            return TryVillagerCraft(index, villager) ||
                   TryExecuteVillagerWorldAction(index, villager, tier);
        }
        return route switch
        {
            VillagerCollectionRoute.TreeLogs =>
                TryVillagerCutTree(index, villager, tier),
            VillagerCollectionRoute.TreeSticks =>
                TryVillagerGatherTreeSticks(index, villager, tier),
            VillagerCollectionRoute.Forage =>
                TryVillagerForage(index, villager, tier),
            VillagerCollectionRoute.Fish =>
                TryVillagerFish(index, villager, tier),
            VillagerCollectionRoute.Mine =>
                TryVillagerMine(index, villager, tier),
            _ => false
        };
    }

    private bool TryExecuteVillagerPlanDirective(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        var step = VillagerPromisePlanService.CurrentDirective(villager);
        if (step is null) return false;
        if (step.ExecuteAfterGameSeconds > _worldGameSeconds)
        {
            _villagers[index] = villager with
            {
                Activity = VillagerActivity.Idle,
                Action = EntityAction.Idle,
                NextDecisionGameSeconds = Math.Min(
                    step.ExecuteAfterGameSeconds,
                    _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds)
            };
            return true;
        }

        bool started;
        switch (step.Action)
        {
            case VillagerPromisePlanAction.FollowActor:
                _villagers[index] = CompletePlanStep(
                    villager with
                    {
                        FollowingActorId = step.TargetActorId,
                        NextDecisionGameSeconds = _worldGameSeconds
                    }, step);
                _villagersDirty = true;
                return true;
            case VillagerPromisePlanAction.WaitUntil:
                _villagers[index] = CompletePlanStep(
                    villager with
                    {
                        FollowingActorId = null,
                        Activity = VillagerActivity.Idle,
                        Action = EntityAction.Idle
                    }, step);
                _villagersDirty = true;
                return true;
            case VillagerPromisePlanAction.MoveTo:
            case VillagerPromisePlanAction.Rendezvous:
                return TryExecutePlanMovement(index, villager, tier, step);
            case VillagerPromisePlanAction.ExploreArea:
                MoveVillagerForCapability(
                    index, villager, tier,
                    VillagerSettlementProjectService.ExplorationTarget(
                        villager, _worldGameSeconds),
                    VillagerNeed.Explore);
                CompleteStartedPlanStep(index, step);
                return true;
            case VillagerPromisePlanAction.FleeFromTarget:
                var origin = _player?.Position ??
                    new Vector2(villager.PositionX - 1, villager.PositionY);
                var away = new Vector2(villager.PositionX, villager.PositionY) - origin;
                if (away.LengthSquared < .01f) away = Vector2.UnitX;
                away = away.Normalized();
                MoveVillagerForCapability(
                    index, villager, tier,
                    new(villager.PositionX + away.X * 8,
                        villager.PositionY + away.Y * 8),
                    VillagerNeed.Safe);
                CompleteStartedPlanStep(index, step);
                return true;
            case VillagerPromisePlanAction.Rest:
                _villagers[index] = CompletePlanStep(
                    VillagerFatigueService.BeginRest(
                        villager, _worldGameSeconds), step);
                _villagersDirty = true;
                return true;
            case VillagerPromisePlanAction.Eat:
                started = TryVillagerEat(index, villager, tier);
                break;
            case VillagerPromisePlanAction.SeekFood:
                if (TryVillagerEat(index, villager, tier))
                {
                    CompleteStartedPlanStep(index, step);
                    return true;
                }
                started = TryVillagerForage(index, villager, tier) ||
                          TryVillagerFish(index, villager, tier) ||
                          TryVillagerWithdrawFood(index, villager);
                if (!started)
                {
                    MoveVillagerForCapability(
                        index, villager, tier,
                        VillagerSettlementProjectService.ExplorationTarget(
                            villager, _worldGameSeconds),
                        VillagerNeed.Food);
                    return true;
                }
                return true;
            case VillagerPromisePlanAction.CraftItem:
                started = TryVillagerCraft(index, villager);
                break;
            case VillagerPromisePlanAction.BuildObject:
                started = TryVillagerPlaceObject(index, villager) ||
                          TryVillagerPlaceOrTendCampfire(index, villager);
                break;
            case VillagerPromisePlanAction.DepositItem:
                started = TryExecuteVillagerWorldAction(index, villager, tier);
                break;
            case VillagerPromisePlanAction.WithdrawItem:
                started = TryVillagerWithdrawWorkItem(index, villager) ||
                          TryVillagerWithdrawFood(index, villager);
                break;
            case VillagerPromisePlanAction.CutTree:
                started = TryVillagerCutTree(index, villager, tier);
                break;
            case VillagerPromisePlanAction.Mine:
            case VillagerPromisePlanAction.Dig:
                started = TryVillagerMine(index, villager, tier);
                break;
            case VillagerPromisePlanAction.Fish:
                started = TryVillagerFish(index, villager, tier);
                break;
            case VillagerPromisePlanAction.Cook:
                started = TryVillagerCook(index, villager) ||
                          TryVillagerCookStew(index, villager);
                break;
            case VillagerPromisePlanAction.AttackTarget:
                started = TryVillagerDefendSelf(index, villager, tier);
                break;
            case VillagerPromisePlanAction.Collect:
                started = TryExecuteVillagerWorldAction(
                              index, villager, tier, step.ItemId) ||
                          TryVillagerGatherTreeSticks(index, villager, tier) ||
                          TryVillagerForage(index, villager, tier) ||
                          TryVillagerCutTree(index, villager, tier) ||
                          TryVillagerMine(index, villager, tier) ||
                          TryVillagerFish(index, villager, tier);
                break;
            case VillagerPromisePlanAction.Deliver:
                started = TryVillagerDropRequestedItem(index, villager) ||
                          TryVillagerFulfilGift(index, villager, tier);
                break;
            case VillagerPromisePlanAction.InteractWithTarget:
                started = TryExecuteVillagerWorldAction(index, villager, tier);
                break;
            case VillagerPromisePlanAction.TalkToActor:
                _villagers[index] = CompletePlanStep(villager, step);
                _villagersDirty = true;
                return true;
            default:
                return false;
        }
        if (!started)
        {
            _villagers[index] = VillagerPromisePlanService
                .FailOrRetryDirective(villager, step) with
            {
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds
            };
            _villagersDirty = true;
            return true;
        }
        if (step.Action == VillagerPromisePlanAction.Collect)
            return true;
        CompleteStartedPlanStep(index, step);
        return true;
    }

    private bool TryExecutePlanMovement(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier,
        VillagerPromisePlanStep step)
    {
        if (step.TargetX is not { } x || step.TargetY is not { } y)
        {
            _villagers[index] = VillagerPromisePlanService
                .FailOrRetryDirective(villager, step);
            _villagersDirty = true;
            return true;
        }
        var target = new Vector2(x, y);
        if (Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY), target) <=
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
        {
            _villagers[index] = CompletePlanStep(villager, step);
            _villagersDirty = true;
            return true;
        }
        MoveVillagerForCapability(index, villager, tier, target,
            VillagerNeed.Explore);
        return true;
    }

    private void CompleteStartedPlanStep(
        int index,
        VillagerPromisePlanStep step)
    {
        _villagers[index] = CompletePlanStep(_villagers[index], step);
        _villagersDirty = true;
    }

    private static VillagerState CompletePlanStep(
        VillagerState villager,
        VillagerPromisePlanStep step) =>
        VillagerPromisePlanService.CompleteDirective(villager, step);

    private bool TryExploreForPromise(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (!VillagerPromisePlanService.PlansFor(villager).Any(step =>
                step.Action == VillagerPromisePlanAction.Collect))
            return false;
        var preferred = VillagerSettlementProjectService
            .ContinuingExplorationTarget(villager, _worldGameSeconds);
        var target = WorldLevelNavigation.ReachableExplorationTarget(
            _worldSeed,
            new(villager.PositionX, villager.PositionY),
            preferred,
            villager.WorldLevel);
        MoveVillagerForCapability(
            index,
            villager,
            tier,
            target,
            VillagerNeed.Explore);
        return true;
    }

    private bool TryVillagerPromiseRendezvous(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        var step = VillagerPromisePlanService.DueRendezvous(
            villager, _worldGameSeconds);
        if (step?.TargetX is not { } x ||
            step.TargetY is not { } y ||
            step.WorldLevel is { } level && level != villager.WorldLevel)
            return false;
        var target = new Vector2(x, y);
        if (Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY), target) <=
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
        {
            _villagers[index] =
                VillagerPromisePlanService.RecordRendezvousReached(
                    villager, step.PromiseId) with
            {
                Activity = VillagerActivity.Idle,
                Action = EntityAction.Idle,
                ActionTime = 0,
                TargetX = null,
                TargetY = null,
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds
            };
            _villagersDirty = true;
            ObserveLog("promise_rendezvous_reached", villager.Id, new
            {
                step.PromiseId,
                step.ItemId,
                step.RemainingQuantity
            });
            return true;
        }
        MoveVillagerForCapability(
            index, villager, tier, target, VillagerNeed.Safe);
        return true;
    }

    private bool TryVillagerRoleAction(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        return villager.WorkRole switch
        {
            VillagerWorkRole.Food =>
                TryVillagerCookStew(index, villager) ||
                TryVillagerCook(index, villager) ||
                TryVillagerWithdrawFood(index, villager) ||
                TryVillagerFish(index, villager, tier) ||
                TryVillagerPlantCrop(index, villager) ||
                TryVillagerForage(index, villager, tier),
            VillagerWorkRole.Wood =>
                TryVillagerCutTree(index, villager, tier) ||
                TryVillagerGatherTreeSticks(index, villager, tier) ||
                TryVillagerProjectExplore(index, villager, tier),
            VillagerWorkRole.Crafting =>
                TryVillagerCraft(index, villager) ||
                TryVillagerPlaceObject(index, villager) ||
                TryVillagerPlaceOrTendCampfire(index, villager),
            VillagerWorkRole.Exploration =>
                TryVillagerMine(index, villager, tier),
            _ => false
        };
    }

    private bool TryVillagerPlantCrop(int index, VillagerState villager)
    {
        var seedSlot = Array.FindIndex(villager.Inventory, itemId =>
            itemId is not null &&
            CropService.TryHarvestItem(itemId, out _));
        if (seedSlot < 0 || villager.Inventory[seedSlot] is not { } seedItemId)
            return false;
        var originX = (int)MathF.Floor(villager.PositionX);
        var originY = (int)MathF.Floor(villager.PositionY);
        foreach (var offset in new (int X, int Y)[]
                 { (0, 1), (1, 0), (0, -1), (-1, 0) })
        {
            var x = originX + offset.X;
            var y = originY + offset.Y;
            if (!TryGetDropTerrain(x, y, out var gpu, out _) ||
                gpu.Chunk.GroundObjects.Any(value =>
                    (int)MathF.Floor(value.X) == x &&
                    (int)MathF.Floor(value.Y) == y))
                continue;
            var planted = EntityInteractionService.Plant(
                villager.Inventory, seedSlot,
                x + .5f, y + .5f,
                _worldGameSeconds, villager.Id);
            if (!planted.Succeeded || planted.Object is null) return false;
            gpu.Chunk.GroundObjects.Add(planted.Object);
            _villagers[index] = villager with
            {
                Inventory = planted.Inventory,
                FarmingExperience = FarmingSkill.AwardExperience(
                    villager.FarmingExperience,
                    FarmingSkill.PlantingExperience).Experience,
                Action = EntityAction.Idle,
                ActionTime = 0,
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds
            };
            QueueChunkSave(gpu.Chunk);
            ObserveLog("world_action_succeeded", villager.Id, new
            {
                Action = "plant_crop",
                ItemId = seedItemId,
                X = x,
                Y = y
            });
            _villagersDirty = true;
            return true;
        }
        return false;
    }

    private bool TryVillagerSettlementContribution(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.ProjectAssignment is not { } assignment ||
            villager.Id == assignment.BuilderId)
            return false;
        var builderIndex = _villagers.FindIndex(value =>
            value.Id == assignment.BuilderId && value.Health > 0);
        if (builderIndex < 0) return false;
        var builder = _villagers[builderIndex];
        var slot = VillagerSettlementProjectService.ContributionSlot(
            villager, builder);
        if (slot < 0) return false;
        if (_settlementGroup is { } group &&
            villager.SettlementGroupId == group.Id)
            return TryVillagerDepositProjectCache(
                index, villager, tier, assignment, slot, group);
        var contributorPosition = new Vector2(
            villager.PositionX, villager.PositionY);
        var worksite = new Vector2(
            assignment.WorksiteX, assignment.WorksiteY);
        var worksiteRendezvous =
            VillagerSettlementProjectService.RendezvousPoint(
            worksite, villager.Id, isBuilder: false);
        var builderPosition = new Vector2(builder.PositionX, builder.PositionY);
        var builderRendezvous =
            VillagerSettlementProjectService.RendezvousPoint(
                builderPosition, villager.Id, isBuilder: false);
        if (Vector2.DistanceSquared(builderPosition, worksite) >
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
        {
            if (Vector2.DistanceSquared(
                    contributorPosition, worksiteRendezvous) >
                VillagerSimulation.InteractionRange *
                VillagerSimulation.InteractionRange)
                MoveVillagerForCapability(
                    index, villager, tier, worksiteRendezvous,
                    VillagerNeed.Safe);
            else
            {
                _villagers[index] = villager with
                {
                    Action = EntityAction.Idle,
                    ActionTime = 0,
                    NextDecisionGameSeconds = _worldGameSeconds +
                        VillagerSimulation.NearbyDecisionSeconds
                };
                _villagersDirty = true;
            }
            return true;
        }
        if (Vector2.DistanceSquared(
                contributorPosition, builderPosition) >
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
        {
            MoveVillagerForCapability(
                index, villager, tier, builderRendezvous,
                VillagerNeed.Safe);
            return true;
        }
        if (!EntityInteractionService.TryTransfer(
                villager.Inventory,
                builder.Inventory,
                slot,
                out var contributorInventory,
                out var builderInventory,
                out var itemId))
            return false;
        _villagers[index] = villager with
        {
            Inventory = contributorInventory,
            Action = EntityAction.Idle,
            ActionTime = 0
        };
        _villagers[builderIndex] = builder with
        {
            Inventory = builderInventory,
            Action = EntityAction.Idle,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds
        };
        _villagersDirty = true;
        _completedProjectContributions.Add(ProjectContributionKey(
            assignment.ProjectItemId, villager.Id, itemId!));
        ObserveLog("settlement_contribution_delivered", villager.Id, new
        {
            BuilderId = builder.Id,
            assignment.ProjectItemId,
            ItemId = itemId
        });
        return true;
    }

    private bool TryVillagerDepositProjectCache(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier,
        VillagerProjectAssignment assignment,
        int slot,
        SettlementGroupState group)
    {
        var cachePoint = VillagerSettlementProjectService.RendezvousPoint(
            group.Camp, villager.Id, isBuilder: false);
        var position = new Vector2(villager.PositionX, villager.PositionY);
        if (Vector2.DistanceSquared(position, cachePoint) >
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
        {
            MoveVillagerForCapability(
                index, villager, tier, cachePoint, VillagerNeed.Safe);
            return true;
        }
        if (!TryFindGroundObjectDrop(
                group.Camp, out var gpu, out var dropPosition, out _))
            return false;
        var dropped = EntityInteractionService.Drop(
            villager.Inventory,
            slot,
            dropPosition.X,
            dropPosition.Y,
            villager.Id);
        if (!dropped.Succeeded || dropped.Object is null) return false;
        var cached = SettlementGroupService.ClaimForGroup(
            dropped.Object, group);
        gpu.Chunk.GroundObjects.Add(cached);
        _villagers[index] = villager with
        {
            Inventory = dropped.Inventory,
            Action = EntityAction.Idle,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds
        };
        _completedProjectContributions.Add(ProjectContributionKey(
            assignment.ProjectItemId, villager.Id, cached.ItemId));
        QueueChunkSave(gpu.Chunk);
        ObserveLog("settlement_cache_deposit", villager.Id, new
        {
            GroupId = group.Id,
            assignment.ProjectItemId,
            ItemId = cached.ItemId,
            cached.Id,
            Position = new { X = cached.X, Y = cached.Y }
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerReachProjectWorksite(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.ProjectAssignment is not { } assignment ||
            villager.Id != assignment.BuilderId ||
            villager.WorldLevel != assignment.WorksiteLevel)
            return false;
        var worksite = new Vector2(assignment.WorksiteX, assignment.WorksiteY);
        if (Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY), worksite) <=
            VillagerSimulation.InteractionRange *
            VillagerSimulation.InteractionRange)
            return false;
        MoveVillagerForCapability(
            index, villager, tier, worksite, VillagerNeed.Safe);
        return true;
    }

    private bool TryVillagerPlaceCompletedProject(
        int index, VillagerState villager) =>
        VillagerSettlementProjectService.CarriesCompletedProject(villager) &&
        TryVillagerPlaceObject(index, villager);

    private bool TryVillagerProjectExplore(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.ProjectAssignment?.Requirements.Any(requirement =>
                VillagerSettlementProjectService.NeedsItem(
                    villager, requirement.ItemId)) != true)
            return false;
        MoveVillagerForCapability(
            index,
            villager,
            tier,
            VillagerSettlementProjectService.ExplorationTarget(
                villager, _worldGameSeconds),
            VillagerNeed.Explore);
        return true;
    }

    private bool TryVillagerGatherTreeSticks(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        var requested = RequestedVillagerAction(
            villager, "gather_sticks");
        var needsSticks =
            VillagerWorkSupplyPlanner.NeedsSticks(villager) ||
            VillagerPromisePlanService.NeedsItem(
                villager, ItemIds.Sticks);
        if ((!requested && !needsSticks) ||
            PlayerInventory.IsFull(villager.Inventory))
            return false;
        var best = FindNearestVillagerTree(
            villager, requireSticks: true, out var bestDistance);
        if (best is null) return false;
        villager = VillagerLocationMemoryService.Remember(
            villager,
            VillagerLocationType.WoodSource,
            new(best.Value.Tree.X + .5f, best.Value.Tree.Y + .5f),
            villager.WorldLevel,
            _worldGameSeconds);
        _villagers[index] = villager;
        _villagersDirty = true;
        var reservationKey = TreeReservationKey(best.Value.Tree.Id);
        if (!_villagerWork.TryReserve(
                reservationKey, villager.Id, _worldGameSeconds))
            return false;
        var target = new Vector2(
            best.Value.Tree.X + .5f, best.Value.Tree.Y + .5f);
        if (bestDistance > 1.6f * 1.6f)
        {
            MoveVillagerForCapability(
                index, villager, tier, target, VillagerNeed.Explore);
            return true;
        }
        var actionGpu = best.Value.Gpu;
        var treeId = best.Value.Tree.Id;
        return BeginNpcControlledAction(
            index,
            villager,
            new("gather_sticks", EntityAction.Gather, target,
                treeId.ToString()),
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var treeIndex = actionGpu.Chunk.TreeInstances.FindIndex(
                    tree => tree.Id == treeId &&
                            tree.State == TreeLifecycleState.Standing &&
                            tree.SticksRemaining > 0);
                if (actorIndex < 0 || treeIndex < 0)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(new("gather_sticks", EntityAction.Gather,
                        target, treeId.ToString()), false,
                        "target_unavailable");
                }
                var actor = _villagers[actorIndex];
                var gathered = EntityInteractionService.Gather(
                    actor.Inventory, ItemIds.Sticks, 1);
                if (!gathered.Succeeded)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(new("gather_sticks", EntityAction.Gather,
                        target, treeId.ToString()), false,
                        "inventory_full");
                }
                var tree = actionGpu.Chunk.TreeInstances[treeIndex];
                actionGpu.Chunk.TreeInstances[treeIndex] = tree with
                {
                    SticksRemaining = tree.SticksRemaining - 1
                };
                _villagers[actorIndex] =
                    VillagerCommitmentService.RecordAcquiredItem(
                        actor with { Inventory = gathered.Inventory },
                        ItemIds.Sticks);
                QueueChunkSave(actionGpu.Chunk);
                _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                _villagersDirty = true;
                return new(new("gather_sticks", EntityAction.Gather,
                    target, treeId.ToString()), true);
            },
            VillagerSimulation.GatherPauseSeconds,
            reservationKey);
    }

    private bool TryVillagerEat(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Hunger > 62) return false;
        var slot = VillagerFoodService.FindMealSlot(villager.Inventory);
        if (slot < 0) return false;
        _villagers[index] = VillagerSimulation.ApplyDecision(
            villager,
            new(VillagerNeed.Food, null, slot),
            tier,
            _worldGameSeconds);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "eat",
            Slot = slot
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerForage(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        var requested = RequestedVillagerAction(villager,
            "gather", "gather_berries", "gather_fibre", "seek_food");
        var promisedBerries =
            VillagerPromisePlanService.NeedsItem(
                villager, ItemIds.WildBerries) ||
            VillagerPromisePlanService.NeedsItem(
                villager, ItemIds.TropicalBerries);
        var wantsFood = promisedBerries ||
                        (villager.Hunger <= 82 ||
                         RequestedVillagerAction(
                             villager, "gather_berries", "seek_food")) &&
                        VillagerSimulation.CountFood(
                            villager.Inventory) == 0;
        var wantsFibre =
            RequestedVillagerAction(villager, "gather_fibre") ||
            VillagerWorkSupplyPlanner.NeedsFibre(villager) ||
            VillagerPromisePlanService.NeedsItem(
                villager, ItemIds.PlantFibres);
        if (!requested && !wantsFood && !wantsFibre) return false;

        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, WorldVegetation Value, string Key)? best = null;
        var bestDistance = VillagerSimulation.ResourceSearchRadius *
                           VillagerSimulation.ResourceSearchRadius;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var cached in gpu.VegetationRenderItems)
            {
                if (cached.VegetationIndex < 0) continue;
                var vegetation = gpu.Chunk.Vegetation[
                    cached.VegetationIndex];
                var eligible = wantsFood &&
                               vegetation.Kind == WorldVegetationKind.BerryBush ||
                               wantsFibre &&
                               vegetation.Kind is WorldVegetationKind.Shrub or
                                   WorldVegetationKind.FloweringShrub;
                if (!eligible ||
                    !VegetationReady(gpu.Chunk, cached.StableKey) ||
                    !_villagerWork.IsAvailable(
                        ResourceReservationKey("vegetation", cached.StableKey),
                        villager.Id, _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(vegetation.X, vegetation.Y));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, vegetation, cached.StableKey);
            }
        }
        if (best is null) return false;
        var reservationKey = ResourceReservationKey(
            "vegetation", best.Value.Key);
        if (!_villagerWork.TryReserve(
                reservationKey, villager.Id, _worldGameSeconds))
            return false;
        var target = new Vector2(best.Value.Value.X, best.Value.Value.Y);
        if (bestDistance > VillagerSimulation.InteractionRange *
                           VillagerSimulation.InteractionRange)
        {
            MoveVillagerForCapability(
                index, villager, tier, target,
                best.Value.Value.Kind == WorldVegetationKind.BerryBush
                    ? VillagerNeed.Food
                    : VillagerNeed.Explore);
            return true;
        }

        var berries = best.Value.Value.Kind == WorldVegetationKind.BerryBush;
        var itemId = berries
            ? best.Value.Value.GraphicName.Equals(
                "FORAGM_NN", StringComparison.OrdinalIgnoreCase)
                ? ItemIds.TropicalBerries
                : ItemIds.WildBerries
            : ItemIds.PlantFibres;
        var amount = berries ? 2 : 1;
        var actionName = berries ? "gather_berries" : "gather_fibre";
        var actionGpu = best.Value.Gpu;
        var vegetationKey = best.Value.Key;
        var intent = new NpcBrainIntent(
            actionName, EntityAction.Gather, target, vegetationKey);
        return BeginNpcControlledAction(
            index,
            villager,
            intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                if (actorIndex < 0 ||
                    !VegetationReady(actionGpu.Chunk, vegetationKey))
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "target_unavailable");
                }
                var actor = _villagers[actorIndex];
                var gathered = EntityInteractionService.Gather(
                    actor.Inventory, itemId, amount);
                if (!gathered.Succeeded)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "inventory_full");
                }
                var farming = berries
                    ? FarmingSkill.AwardExperience(
                        actor.FarmingExperience, 18 * amount).Experience
                    : actor.FarmingExperience;
                _villagers[actorIndex] =
                    VillagerCommitmentService.RecordAcquiredItem(
                        actor with
                        {
                            Inventory = gathered.Inventory,
                            FarmingExperience = farming,
                            Need = berries
                                ? VillagerNeed.Food
                                : VillagerNeed.Explore
                        },
                        itemId,
                        amount);
                SetVegetationCooldown(
                    actionGpu.Chunk, vegetationKey,
                    berries ? 12 * 60 : 5 * 60);
                QueueChunkSave(actionGpu.Chunk);
                _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                _villagersDirty = true;
                return new(intent, true);
            },
            VillagerSimulation.GatherPauseSeconds,
            reservationKey);
    }

    private bool TryVillagerCraft(int index, VillagerState villager)
    {
        var level = CraftingSkill.LevelForExperience(
            villager.CraftingExperience);
        var nearbyObjects = NearbyGroundObjects(villager, 12).ToArray();
        var hasCampfire = nearbyObjects
            .Any(value => CampfireService.IsCampfire(value.Object));
        var nearbyStations = nearbyObjects
            .Select(value => value.Object.ItemId)
            .Where(CraftingStationService.IsStation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var desired in VillagerCraftPlanner.PriorityFor(villager))
        {
            if (!VillagerCraftPlanner.Needs(
                    desired, villager) ||
                VillagerCraftPlanner.ConsumesAssignedContribution(
                    desired, villager) ||
                villager.ProjectAssignment is { } project &&
                ItemCatalog.Get(desired).HasTag(ItemTag.PlaceableObject) &&
                project.ProjectItemId != desired ||
                desired == ItemIds.Campfire && hasCampfire ||
                ItemCatalog.Get(desired).HasTag(ItemTag.PlaceableObject) &&
                nearbyObjects.Any(value =>
                    value.Object.ItemId == desired) ||
                desired == ItemIds.StorageChest &&
                NearbyGroundObjects(villager, 12).Any(value =>
                    StorageContainerService.IsStorage(value.Object.ItemId) &&
                    value.Object.OwnerId == villager.Id))
                continue;
            var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
                value.ResultItemId == desired);
            if (recipe is null) continue;
            var stationAvailable =
                recipe.RequiredStationItemId is null ||
                nearbyStations.Contains(recipe.RequiredStationItemId);
            var crafted = EntityInteractionService.Craft(
                villager.Inventory, recipe, level, stationAvailable);
            EntityCachedCraftResult cachedCraft = default;
            if (!crafted.Succeeded &&
                _settlementGroup is { } group &&
                villager.SettlementGroupId == group.Id &&
                villager.ProjectAssignment?.BuilderId == villager.Id &&
                Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY),
                    group.Camp) <=
                (group.CacheRadius + 2) * (group.CacheRadius + 2))
            {
                var cacheItems = _worldChunks.Values
                    .Where(IsActiveSimulationChunk)
                    .SelectMany(value => value.Chunk.GroundObjects)
                    .Where(value =>
                        SettlementGroupService.IsInCache(group, value))
                    .ToArray();
                cachedCraft = EntityInteractionService.CraftWithGroundCache(
                    villager.Inventory,
                    cacheItems,
                    recipe,
                    level,
                    stationAvailable);
                if (cachedCraft.Succeeded)
                    crafted = new(
                        true,
                        cachedCraft.Inventory,
                        cachedCraft.ItemId);
            }
            if (!crafted.Succeeded) continue;
            if (cachedCraft.Succeeded && _settlementGroup is { } cacheGroup)
                ApplySettlementCacheCraft(cacheGroup, cachedCraft);
            var experience = CraftingSkill.AwardExperience(
                villager.CraftingExperience, recipe,
                crafted.Inventory);
            _villagers[index] = villager with
            {
                Inventory = crafted.Inventory,
                CraftingExperience = experience.Experience,
                Action = EntityAction.Work,
                ActionTime = 0,
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerFatigueService.AdjustedWorkDuration(
                        VillagerSimulation.NearbyDecisionSeconds,
                        villager.Energy),
                LastSimulatedGameSeconds = _worldGameSeconds
            };
            ObserveLog("world_action_succeeded", villager.Id, new
            {
                Action = "craft",
                Recipe = recipe.Id,
                recipe.ResultItemId
            });
            _villagersDirty = true;
            return true;
        }
        return false;
    }

    private void ApplySettlementCacheCraft(
        SettlementGroupState group,
        in EntityCachedCraftResult craft)
    {
        var consumed = craft.ConsumedCacheObjectIds.ToHashSet();
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            var removed = gpu.Chunk.GroundObjects.RemoveAll(value =>
                consumed.Contains(value.Id));
            if (removed > 0) QueueChunkSave(gpu.Chunk);
        }
        foreach (var itemId in craft.ReturnedCacheItemIds)
        {
            if (!TryFindGroundObjectDrop(
                    group.Camp, out var gpu, out var position, out _))
                continue;
            gpu.Chunk.GroundObjects.Add(new(
                Guid.NewGuid(),
                itemId,
                position.X,
                position.Y,
                GroupOwnerId: group.Id));
            QueueChunkSave(gpu.Chunk);
        }
        ObserveLog("settlement_cache_consumed", group.LeaderId, new
        {
            GroupId = group.Id,
            craft.ItemId,
            Consumed = craft.ConsumedCacheObjectIds.Count,
            Returned = craft.ReturnedCacheItemIds
        });
    }

    private bool TryVillagerFish(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        var net = PlayerInventory.BestFishingNet(villager.Inventory);
        if ((!RequestedVillagerAction(villager, "fish") &&
             villager.Hunger > 70) ||
            net is null ||
            PlayerInventory.IsFull(villager.Inventory))
            return false;
        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, WorldFish Fish)? best = null;
        var bestDistance = 24f * 24f;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var fish in gpu.Chunk.Fish)
            {
                var profile = FishingSkill.Profile(fish.Species);
                var remaining = gpu.Chunk.FishRemaining.TryGetValue(
                    fish.StableKey, out var count)
                    ? count
                    : profile.SchoolSize;
                if (remaining <= 0 ||
                    !VillagerLocationMemoryService.CanVisit(
                        villager,
                        new(fish.X, fish.Y),
                        _worldGameSeconds) ||
                    !FishingSkill.CanCatch(
                        fish.Species,
                        FishingSkill.LevelForExperience(
                            villager.FishingExperience),
                        net.FishingPower) ||
                    !_villagerWork.IsAvailable(
                        ResourceReservationKey("fish", fish.StableKey),
                        villager.Id, _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(fish.X, fish.Y));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, fish);
            }
        }
        if (best is null) return false;
        villager = VillagerLocationMemoryService.Remember(
            villager,
            VillagerLocationType.FishingSpot,
            new(best.Value.Fish.X, best.Value.Fish.Y),
            villager.WorldLevel,
            _worldGameSeconds);
        _villagers[index] = villager;
        _villagersDirty = true;
        var reservationKey = ResourceReservationKey(
            "fish", best.Value.Fish.StableKey);
        if (!_villagerWork.TryReserve(
                reservationKey, villager.Id, _worldGameSeconds))
            return false;
        var fishTarget = new Vector2(best.Value.Fish.X, best.Value.Fish.Y);
        if (bestDistance > 3.2f * 3.2f)
        {
            MoveVillagerForCapability(
                index, villager, tier, fishTarget, VillagerNeed.Food);
            return true;
        }
        var actionGpu = best.Value.Gpu;
        var fishKey = best.Value.Fish.StableKey;
        var species = best.Value.Fish.Species;
        var intent = new NpcBrainIntent(
            "fish", EntityAction.Fish, fishTarget, fishKey);
        return BeginNpcControlledAction(
            index,
            villager,
            intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var profile = FishingSkill.Profile(species);
                var remaining = actionGpu.Chunk.FishRemaining.TryGetValue(
                    fishKey, out var current)
                    ? current
                    : profile.SchoolSize;
                if (actorIndex < 0 || remaining <= 0)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "target_unavailable");
                }
                var actor = _villagers[actorIndex];
                var caught = EntityInteractionService.CatchFish(
                    actor.Inventory, actor.FishingExperience, species);
                if (!caught.Succeeded)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "inventory_full");
                }
                actionGpu.Chunk.FishRemaining[fishKey] = remaining - 1;
                _villagers[actorIndex] =
                    VillagerCommitmentService.RecordAcquiredItem(
                        actor with
                        {
                            Inventory = caught.Inventory,
                            FishingExperience = caught.Experience.Experience
                        },
                        profile.ItemId);
                QueueChunkSave(actionGpu.Chunk);
                _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                _villagersDirty = true;
                return new(intent, true);
            },
            VillagerSimulation.GatherPauseSeconds,
            reservationKey);
    }

    private bool TryVillagerCutTree(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (EntityInteractionService.TryAutoSharpenStoneTool(
                villager.Inventory,
                ItemIds.BluntStoneAxe,
                out var automaticallySharpenedInventory))
        {
            villager = villager with
            {
                Inventory = automaticallySharpenedInventory
            };
            _villagers[index] = villager;
            _villagersDirty = true;
        }
        var axe = PlayerInventory.BestAxe(villager.Inventory);
        if (axe is null || PlayerInventory.IsFull(villager.Inventory))
            return false;
        var needsLogs = villager.Goals?.Any(goal =>
            goal.Status == CommitmentStatus.Active &&
            goal.ItemId == ItemIds.Logs) == true ||
            VillagerPromisePlanService.NeedsItem(
                villager, ItemIds.Logs);
        if (!needsLogs &&
            !VillagerWorkCapability.CanPerform(
                villager, VillagerWorkRole.Wood) &&
            !RequestedVillagerAction(villager, "cut_tree", "gather"))
            return false;
        var best = FindNearestVillagerTree(
            villager, requireSticks: false, out var bestDistance);
        if (best is null) return false;
        villager = VillagerLocationMemoryService.Remember(
            villager,
            VillagerLocationType.WoodSource,
            new(best.Value.Tree.X + .5f, best.Value.Tree.Y + .5f),
            villager.WorldLevel,
            _worldGameSeconds);
        _villagers[index] = villager;
        _villagersDirty = true;
        var reservationKey = TreeReservationKey(best.Value.Tree.Id);
        if (!_villagerWork.TryReserve(
                reservationKey, villager.Id, _worldGameSeconds))
            return false;
        var target = new Vector2(
            best.Value.Tree.X + .5f, best.Value.Tree.Y + .5f);
        if (bestDistance > 1.6f * 1.6f)
        {
            MoveVillagerForCapability(
                index, villager, tier, target, VillagerNeed.Safe);
            return true;
        }
        var actionGpu = best.Value.Gpu;
        var treeId = best.Value.Tree.Id;
        var intent = new NpcBrainIntent(
            "cut_tree", EntityAction.Work, target, treeId.ToString());
        return BeginNpcControlledAction(
            index, villager, intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var treeIndex = actionGpu.Chunk.TreeInstances.FindIndex(value =>
                    value.Id == treeId &&
                    value.State == TreeLifecycleState.Standing);
                if (actorIndex < 0 || treeIndex < 0)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "target_unavailable");
                }
                var actor = _villagers[actorIndex];
                var tree = actionGpu.Chunk.TreeInstances[treeIndex];
                if (axe.Id == ItemIds.StoneAxe &&
                    EntityInteractionService.TryBluntStoneTool(
                        actor.Inventory,
                        axe.Id,
                        Random.Shared.NextSingle(),
                        out var bluntedInventory))
                {
                    if (EntityInteractionService.TryAutoSharpenStoneTool(
                            bluntedInventory,
                            ItemIds.BluntStoneAxe,
                            out var resharpenedInventory))
                    {
                        actor = actor with
                        {
                            Inventory = resharpenedInventory
                        };
                        _villagers[actorIndex] = actor;
                        _villagersDirty = true;
                    }
                    else
                    {
                        _villagers[actorIndex] = actor with
                        {
                            Inventory = bluntedInventory
                        };
                        _villagerWork.ReleaseTarget(
                            reservationKey, actor.Id);
                        _villagersDirty = true;
                        return new(intent, false, "tool_blunted");
                    }
                }
                var strike = EntityInteractionService.StrikeResource(new(
                    EntityResourceAction.Woodcut,
                    actor.WoodcuttingExperience,
                    tree.Health,
                    tree.MaxHealth,
                    axe.WoodcuttingPower,
                    Random.Shared.NextSingle(),
                    Random.Shared.NextSingle()));
                if (!strike.Hit)
                {
                    _villagers[actorIndex] = LogVillagerResourceStrike(
                        actor, "woodcutting", treeId.ToString(),
                        TreeDisplayName(tree.TreeType), strike);
                    _villagersDirty = true;
                    return new(intent, true);
                }
                var felled = strike.Depleted;
                actionGpu.Chunk.TreeInstances[treeIndex] = tree with
                {
                    Health = strike.Health,
                    State = felled
                        ? TreeLifecycleState.Stump
                        : TreeLifecycleState.Standing
                };
                var inventory = actor.Inventory;
                if (felled)
                    inventory = EntityInteractionService.Gather(
                        inventory,
                        ItemIds.Logs,
                        WoodcuttingSkill.FellingLogCount(
                            tree.MaxHealth)).Inventory;
                var updated = actor with
                {
                    Inventory = inventory,
                    WoodcuttingExperience = strike.Experience.Experience
                };
                updated = LogVillagerResourceStrike(
                    updated, "woodcutting", treeId.ToString(),
                    TreeDisplayName(tree.TreeType), strike);
                _villagers[actorIndex] = felled
                    ? VillagerCommitmentService.RecordAcquiredItem(
                        updated, ItemIds.Logs)
                    : updated;
                QueueChunkSave(actionGpu.Chunk);
                if (felled)
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                _villagersDirty = true;
                return new(intent, true);
            },
            VillagerSimulation.NearbyDecisionSeconds,
            reservationKey,
            () => actionGpu.Chunk.TreeInstances.Any(value =>
                value.Id == treeId &&
                value.State == TreeLifecycleState.Standing));
    }

    private (GpuWorldChunk Gpu, WorldTreeInstance Tree)?
        FindNearestVillagerTree(
            VillagerState villager,
            bool requireSticks,
            out float bestDistance)
    {
        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, IslandTree Source)? best = null;
        bestDistance = 24f * 24f;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var source in gpu.Chunk.Trees)
            {
                var instance = TreeInteractionAvailability.InstanceAt(
                    gpu.Chunk.TreeInstances, source.X, source.Y);
                if (instance is { State: not TreeLifecycleState.Standing } ||
                    requireSticks &&
                    !TreeInteractionAvailability.CanGatherSticks(
                        gpu.Chunk.TreeInstances, source.X, source.Y) ||
                    instance is not null &&
                    !_villagerWork.IsAvailable(
                        TreeReservationKey(instance.Id),
                        villager.Id, _worldGameSeconds) ||
                    !VillagerLocationMemoryService.CanVisit(
                        villager,
                        new(source.X + .5f, source.Y + .5f),
                        _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(source.X + .5f, source.Y + .5f));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, source);
            }
        }
        if (best is null) return null;
        var tree = EnsureTreeInstance(
            best.Value.Gpu,
            best.Value.Source,
            initializeSticks: requireSticks,
            out _);
        if (requireSticks && tree.SticksRemaining <= 0 ||
            !_villagerWork.IsAvailable(
                TreeReservationKey(tree.Id),
                villager.Id, _worldGameSeconds))
            return null;
        return (best.Value.Gpu, tree);
    }

    private bool TryVillagerMine(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (!RequestedVillagerAction(villager, "mine") &&
            !VillagerWorkCapability.CanPerform(
                villager, VillagerWorkRole.Exploration))
            return false;
        var pickaxe = PlayerInventory.BestPickaxe(villager.Inventory);
        if (pickaxe is null || PlayerInventory.IsFull(villager.Inventory))
            return false;
        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, WorldVegetation Value,
            WorldVegetationRenderItem Cached,
            MiningNodeDefinition Definition)? best = null;
        var bestDistance = 24f * 24f;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var cached in gpu.VegetationRenderItems)
            {
                if (cached.VegetationIndex < 0) continue;
                var value = gpu.Chunk.Vegetation[cached.VegetationIndex];
                if (!MiningNodeCatalog.TryGet(value, out var definition))
                    continue;
                if (!_villagerWork.IsAvailable(
                        ResourceReservationKey("mining", cached.StableKey),
                        villager.Id, _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(value.X, value.Y));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, value, cached, definition);
            }
        }
        if (best is null) return false;
        var reservationKey = ResourceReservationKey(
            "mining", best.Value.Cached.StableKey);
        if (!_villagerWork.TryReserve(
                reservationKey, villager.Id, _worldGameSeconds))
            return false;
        var target = new Vector2(best.Value.Value.X, best.Value.Value.Y);
        if (bestDistance > 1.5f * 1.5f)
        {
            MoveVillagerForCapability(
                index, villager, tier, target, VillagerNeed.Explore);
            return true;
        }
        var actionGpu = best.Value.Gpu;
        var nodeKey = best.Value.Cached.StableKey;
        var nodeDefinition = best.Value.Definition;
        var intent = new NpcBrainIntent(
            "mine", EntityAction.Mine, target, nodeKey);
        return BeginNpcControlledAction(
            index, villager, intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                var nodeExists = actionGpu.VegetationRenderItems.Any(value =>
                    value.StableKey == nodeKey && value.VegetationIndex >= 0);
                if (actorIndex < 0 || !nodeExists)
                {
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                    return new(intent, false, "target_unavailable");
                }
                var actor = _villagers[actorIndex];
                var state = actionGpu.Chunk.MiningStates.FirstOrDefault(value =>
                    value.StableKey == nodeKey);
                var strike = EntityInteractionService.StrikeResource(new(
                    EntityResourceAction.Mine,
                    actor.MiningExperience,
                    state?.Health ?? nodeDefinition.MaximumHealth,
                    nodeDefinition.MaximumHealth,
                    pickaxe.MiningPower,
                    Random.Shared.NextSingle(),
                    Random.Shared.NextSingle(),
                    nodeDefinition.CompletionExperience));
                if (!strike.Hit)
                {
                    _villagers[actorIndex] = LogVillagerResourceStrike(
                        actor, "mining", nodeKey,
                        nodeDefinition.DisplayName, strike);
                    _villagersDirty = true;
                    return new(intent, true);
                }
                actionGpu.Chunk.MiningStates.RemoveAll(value =>
                    value.StableKey == nodeKey);
                actionGpu.Chunk.MiningStates.Add(new(
                    nodeKey, strike.Health, nodeDefinition.MaximumHealth));
                var inventory = actor.Inventory;
                if (strike.Depleted && nodeDefinition.RewardItemId is { } reward)
                    inventory = EntityInteractionService.Gather(
                        inventory, reward, 1).Inventory;
                var updated = actor with
                {
                    Inventory = inventory,
                    MiningExperience = strike.Experience.Experience
                };
                updated = LogVillagerResourceStrike(
                    updated, "mining", nodeKey,
                    nodeDefinition.DisplayName, strike);
                _villagers[actorIndex] =
                    strike.Depleted &&
                    nodeDefinition.RewardItemId is { } acquiredReward
                        ? VillagerCommitmentService.RecordAcquiredItem(
                            updated, acquiredReward)
                        : updated;
                QueueChunkSave(actionGpu.Chunk);
                if (strike.Depleted)
                    _villagerWork.ReleaseTarget(reservationKey, villager.Id);
                _villagersDirty = true;
                return new(intent, true);
            },
            VillagerSimulation.NearbyDecisionSeconds,
            reservationKey);
    }

    private VillagerState LogVillagerResourceStrike(
        VillagerState villager,
        string skill,
        string targetId,
        string target,
        ResourceStrikeResult strike)
    {
        ShowNpcResourceFeedback(skill, targetId, strike);
        villager = VillagerActionMemoryService.RecordResourceStrike(
            villager, skill, targetId, target, strike,
            _worldGameSeconds);
        var skillName = char.ToUpperInvariant(skill[0]) + skill[1..];
        _chatUi.AddMessage(
            strike.Hit
                ? $"{villager.Name} hits the {target.ToLowerInvariant()} for " +
                  $"{strike.Damage} damage ({strike.Health} health); " +
                  $"{villager.Name} gains {strike.Experience.Gained} " +
                  $"{skillName} XP."
                : $"{villager.Name} misses the {target.ToLowerInvariant()} " +
                  $"({skillName} level {strike.Experience.Level}).",
            strike.Hit ? ChatMessageStyle.Experience : ChatMessageStyle.Miss);
        ObserveLog("resource_strike", villager.Id, new
        {
            ActorId = villager.Id,
            ActorName = villager.Name,
            Skill = skill,
            Target = target,
            strike.Hit,
            strike.Damage,
            RemainingHealth = strike.Health,
            ExperienceGained = strike.Experience.Gained,
            SkillExperience = strike.Experience.Experience,
            SkillLevel = strike.Experience.Level,
            strike.Depleted
        });
        return villager;
    }

    private void ShowNpcResourceFeedback(
        string skill, string targetId, ResourceStrikeResult strike)
    {
        if (skill.Equals("woodcutting", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(targetId, out var treeId))
            ShowEntityImpact(
                TreeFeedbackKey(treeId),
                strike.Hit ? strike.Damage : 0,
                strike.Hit);
        else if (skill.Equals("mining", StringComparison.OrdinalIgnoreCase))
            ShowEntityImpact(
                MiningFeedbackKey(targetId),
                strike.Hit ? strike.Damage : 0,
                strike.Hit);
    }

    private bool TryVillagerPlaceOrTendCampfire(
        int index, VillagerState villager)
    {
        var nearby = NearbyGroundObjects(villager, 8)
            .FirstOrDefault(value =>
                CampfireService.IsCampfire(value.Object) &&
                (value.Object.OwnerId is null ||
                 value.Object.OwnerId == villager.Id));
        if (nearby.Object is null) return false;
        var fire = nearby.Object;
        var fireState = CampfireService.State(fire, _worldGameSeconds);
        var updatedFire = fire;
        var inventoryState = villager.Inventory;
        if (fireState == CampfireState.Empty)
        {
            var logSlot = Array.FindIndex(
                inventoryState, value => value == ItemIds.Logs);
            if (logSlot < 0) return false;
            var fueled = EntityInteractionService.AddCampfireFuel(
                inventoryState, logSlot, fire, _worldGameSeconds);
            if (!fueled.Succeeded || fueled.Object is null) return false;
            updatedFire = fueled.Object;
            inventoryState = fueled.Inventory;
        }
        else if (fireState == CampfireState.Fueled &&
                 CampfireService.CanLight(
                     fire, inventoryState, _worldGameSeconds))
            updatedFire = EntityInteractionService.LightCampfire(
                fire, inventoryState,
                _worldGameSeconds,
                FiremakingSkill.LevelForExperience(
                    villager.FiremakingExperience));
        else return false;
        ReplaceGroundObject(nearby.Gpu, fire, updatedFire);
        _villagers[index] = villager with
        {
            Inventory = inventoryState,
            Action = EntityAction.Work,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(nearby.Gpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerPlaceObject(int index, VillagerState villager)
    {
        var slot = Array.FindIndex(
            villager.Inventory,
            value => value is not null &&
                     PlaceableObjectCatalog.IsPlaceable(value));
        if (slot < 0 || villager.Inventory[slot] is not { } itemId)
            return false;
        if (itemId == ItemIds.TrainingDummy) return false;
        if (IndependentSurvivorPolicy.PersonalCamp(villager) is
                { } personalCamp &&
            Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY), personalCamp) >
            2.5f * 2.5f)
        {
            MoveVillagerForCapability(
                index,
                villager,
                VillagerSimulationTier.Nearby,
                personalCamp,
                VillagerNeed.Safe);
            return true;
        }
        var placementOrigin = villager.ProjectAssignment is { } assignment &&
                              assignment.BuilderId == villager.Id &&
                              assignment.ProjectItemId == itemId
            ? new Vector2(assignment.WorksiteX, assignment.WorksiteY)
            : new Vector2(villager.PositionX, villager.PositionY);
        if (!TryFindGroundObjectDrop(
                placementOrigin, out var gpu, out var position, out _))
            return false;
        var placed = EntityInteractionService.Place(
            villager.Inventory, slot,
            position.X, position.Y, villager.Id);
        if (!placed.Succeeded || placed.Object is null) return false;
        gpu.Chunk.GroundObjects.Add(placed.Object);
        _villagers[index] = villager with
        {
            Inventory = placed.Inventory,
            Action = EntityAction.Work,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(gpu.Chunk);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "build",
            ItemId = itemId,
            OwnerId = villager.Id
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerWithdrawFood(int index, VillagerState villager)
    {
        if (villager.Hunger > 55 ||
            VillagerSimulation.CountFood(villager.Inventory) > 0)
            return false;
        var storage = NearbyGroundObjects(villager, 3)
            .FirstOrDefault(value =>
                StorageContainerService.IsStorage(value.Object.ItemId) &&
                value.Object.OwnerId == villager.Id);
        if (storage.Object is null) return false;
        var container = StorageContainerService.Open(storage.Object);
        if (!EntityInteractionService.TryWithdrawFirst(
                container,
                villager.Inventory,
                itemId => SurvivalService.TryFoodEffect(itemId, out _),
                out var inventory,
                out _))
            return false;
        var updated = StorageContainerService.Save(
            storage.Object, container);
        ReplaceGroundObject(storage.Gpu, storage.Object, updated);
        _villagers[index] = villager with
        {
            Inventory = inventory,
            Action = EntityAction.Gather,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(storage.Gpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerWithdrawWorkItem(
        int index,
        VillagerState villager)
    {
        if (villager.WorkRole == VillagerWorkRole.Unassigned ||
            VillagerStorageTransfer.HasWorkItem(
                villager.WorkRole, villager.Inventory) ||
            PlayerInventory.IsFull(villager.Inventory))
            return false;
        var storage = NearbyGroundObjects(villager, 3)
            .FirstOrDefault(value =>
                StorageContainerService.IsStorage(value.Object.ItemId) &&
                value.Object.OwnerId == villager.Id);
        if (storage.Object is null) return false;
        var container = StorageContainerService.Open(storage.Object);
        if (!EntityInteractionService.TryWithdrawFirst(
                container,
                villager.Inventory,
                itemId => VillagerStorageTransfer.IsWorkItemForRole(
                    villager.WorkRole, itemId),
                out var inventory,
                out _))
            return false;
        var updated = StorageContainerService.Save(
            storage.Object, container);
        ReplaceGroundObject(storage.Gpu, storage.Object, updated);
        _villagers[index] = villager with
        {
            Inventory = inventory,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(storage.Gpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerDefendSelf(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (IsObserveWorld ||
            _activePlayer is null || _player is null ||
            villager.Boldness < .58f ||
            villager.Memories?.Any(memory =>
                memory.Kind == "violence" &&
                memory.SubjectId == _activePlayer.Id &&
                _worldGameSeconds - memory.GameSeconds < 15 * 60) != true)
            return false;
        var position = new Vector2(villager.PositionX, villager.PositionY);
        var distance = Vector2.DistanceSquared(position, _player.Position);
        if (distance > MeleeCombatService.AttackRange *
                       MeleeCombatService.AttackRange)
        {
            MoveVillagerForCapability(
                index, villager, tier, _player.Position, VillagerNeed.Safe);
            return true;
        }
        var targetId = _activePlayer.Id;
        var intent = new NpcBrainIntent(
            "defend_self", EntityAction.Attack,
            _player.Position, targetId);
        return BeginNpcControlledAction(
            index, villager, intent,
            () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                if (actorIndex < 0 || _activePlayer?.Id != targetId ||
                    _player is null || _activePlayer.Health <= 0)
                    return new(intent, false, "target_unavailable");
                var actor = _villagers[actorIndex];
                var interaction = EntityInteractionService.TryMeleeAttack(
                    _actionCooldowns,
                    actor.Id,
                    _clock,
                    actor.AttackExperience,
                    actor.StrengthExperience,
                    actor.AttackExperience,
                    DeterministicRoll(actor.Id, "combat-hit"),
                    DeterministicRoll(actor.Id, "combat-damage"),
                    actor.Inventory);
                if (!interaction.Succeeded)
                    return new(intent, false, interaction.Failure);
                if (interaction.Attack.Hit)
                    ApplyPlayerDamage(
                        interaction.Attack.Damage, actor.Name);
                else
                    ShowEntityImpact(
                        PlayerFeedbackKey(targetId), 0, false);
                TryAutoRetaliate(actor);
                _villagers[actorIndex] = actor with
                {
                    AttackExperience = interaction.Experience.Experience
                };
                _villagersDirty = true;
                return new(intent, true);
            },
            MeleeCombatService.AttackIntervalSeconds *
            VillagerSimulation.GameSecondsPerRealSecond,
            targetAvailable: () =>
            {
                var actorIndex = VillagerIndex(villager.Id);
                return _activePlayer?.Id == targetId &&
                       _activePlayer.Health > 0 && _player is not null &&
                       actorIndex >= 0 &&
                       Vector2.DistanceSquared(
                           new(_villagers[actorIndex].PositionX,
                               _villagers[actorIndex].PositionY),
                           _player.Position) <=
                       MeleeCombatService.AttackRange *
                       MeleeCombatService.AttackRange;
            });
    }

    private bool TryVillagerCook(int index, VillagerState villager)
    {
        var rawSlot = Array.FindIndex(
            villager.Inventory,
            value => value is not null &&
                     CookingSkill.CanCook(
                         value,
                         CookingSkill.LevelForExperience(
                             villager.CookingExperience)));
        if (rawSlot < 0) return false;
        var fire = NearbyGroundObjects(villager, 3)
            .FirstOrDefault(value =>
                CampfireService.State(
                    value.Object, _worldGameSeconds) == CampfireState.Lit);
        if (fire.Object is null) return false;
        var level = CookingSkill.LevelForExperience(
            villager.CookingExperience);
        var cooked = EntityInteractionService.Cook(
            villager.Inventory,
            rawSlot,
            level,
            DeterministicRoll(villager.Id, "cook"));
        if (!cooked.Succeeded) return false;
        var profile = CookingSkill.TryProfile(
            villager.Inventory[rawSlot]!, out var cookingProfile)
            ? cookingProfile
            : null;
        var xp = CookingSkill.AwardExperience(
            villager.CookingExperience,
            cooked.ItemId == profile?.BurntItemId
                ? 0
                : profile?.Experience ?? 0);
        _villagers[index] = villager with
        {
            Inventory = cooked.Inventory,
            CookingExperience = xp.Experience,
            Action = EntityAction.Work,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.GatherPauseSeconds
        };
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerCookStew(int index, VillagerState villager)
    {
        var level = CookingSkill.LevelForExperience(
            villager.CookingExperience);
        if (level < StewCookingService.RequiredLevel ||
            !StewCookingService.HasIngredients(villager.Inventory))
            return false;
        var pot = NearbyGroundObjects(villager, 3)
            .FirstOrDefault(value =>
                value.Object.ItemId == ItemIds.CookingPot &&
                HasNearbyLitCampfire(value.Object));
        if (pot.Object is null) return false;
        var cooked = EntityInteractionService.CookStew(
            villager.Inventory, level);
        if (!cooked.Succeeded) return false;
        var xp = CookingSkill.AwardExperience(
            villager.CookingExperience,
            StewCookingService.Experience);
        _villagers[index] = villager with
        {
            Inventory = cooked.Inventory,
            CookingExperience = xp.Experience,
            Action = EntityAction.Work,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.GatherPauseSeconds
        };
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "cook_stew",
            ItemId = ItemIds.FishBerryStew
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerTakeCampfireFuel(
        int index, VillagerState villager)
    {
        if (!RequestedVillagerAction(villager, "withdraw") ||
            PlayerInventory.IsFull(villager.Inventory))
            return false;
        var fire = NearbyGroundObjects(villager, 3)
            .FirstOrDefault(value =>
                (value.Object.OwnerId is null ||
                 value.Object.OwnerId == villager.Id) &&
                CampfireService.CanRemoveFuel(
                    value.Object, _worldGameSeconds));
        if (fire.Object is null) return false;
        var taken = EntityInteractionService.TakeCampfireFuel(
            villager.Inventory, fire.Object, _worldGameSeconds);
        if (!taken.Succeeded || taken.Object is null) return false;
        ReplaceGroundObject(
            fire.Gpu, fire.Object,
            taken.Object);
        _villagers[index] = villager with
        {
            Inventory = taken.Inventory,
            Action = EntityAction.Gather,
            LastDeliberation = villager.LastDeliberation is { } trace
                ? trace with { Action = "none", ItemId = "" }
                : null,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(fire.Gpu.Chunk);
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerDropRequestedItem(
        int index, VillagerState villager)
    {
        if (!RequestedVillagerAction(villager, "drop")) return false;
        var requestedItem = villager.LastDeliberation?.ItemId;
        var slot = string.IsNullOrWhiteSpace(requestedItem)
            ? Array.FindLastIndex(villager.Inventory, item => item is not null)
            : Array.FindIndex(villager.Inventory, item =>
                string.Equals(item, requestedItem,
                    StringComparison.OrdinalIgnoreCase));
        if (slot < 0 || villager.Inventory[slot] is not { } itemId ||
            !PlayerInventory.CanDrop(itemId) ||
            !TryGroundItemVisual(itemId, out _, out _, out _, out _) ||
            !TryFindGroundObjectDrop(
                new(villager.PositionX, villager.PositionY),
                out var gpu, out var position, out _))
            return false;
        var dropped = EntityInteractionService.Drop(
            villager.Inventory, slot,
            position.X, position.Y, villager.Id);
        if (!dropped.Succeeded || dropped.Object is null) return false;
        gpu.Chunk.GroundObjects.Add(dropped.Object);
        _villagers[index] = villager with
        {
            Inventory = dropped.Inventory,
            Action = EntityAction.Gather,
            LastDeliberation = villager.LastDeliberation is { } trace
                ? trace with { Action = "none", ItemId = "" }
                : null,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds
        };
        QueueChunkSave(gpu.Chunk);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "drop",
            ItemId = itemId,
            OwnerId = villager.Id
        });
        _villagersDirty = true;
        return true;
    }

    private bool RequestedVillagerAction(
        VillagerState villager, params string[] actions)
    {
        var deliberation = villager.LastDeliberation;
        if (deliberation is null ||
            deliberation.Decision is "refuse" or "clarify" ||
            string.IsNullOrWhiteSpace(deliberation.Action) ||
            _worldGameSeconds - deliberation.GameSeconds > 15 * 60)
            return false;
        return actions.Contains(
            deliberation.Action,
            StringComparer.OrdinalIgnoreCase);
    }

    private bool TryVillagerFulfilGift(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        var promise = villager.Promises?.FirstOrDefault(value =>
            value.Status == CommitmentStatus.Active &&
            value.Kind == VillagerPromiseKind.GiveItem &&
            value.ItemId is not null);
        if (promise is null ||
            !VillagerCommitmentService.HasDeliverableItem(
                villager, promise))
            return false;
        if (_activePlayer is not null && _player is not null &&
            promise.PromiseeId == _activePlayer.Id)
        {
            var playerDistance = Vector2.DistanceSquared(
                new(villager.PositionX, villager.PositionY),
                _player.Position);
            if (playerDistance > VillagerSimulation.InteractionRange *
                                 VillagerSimulation.InteractionRange)
            {
                MoveVillagerForCapability(
                    index, villager, tier, _player.Position,
                    VillagerNeed.Social);
                return true;
            }
            if (!VillagerCommitmentService.TryCompleteDeliveryToInventory(
                    villager,
                    _activePlayer.Id,
                    ActivePlayerInventory(),
                    promise.Id,
                    _worldGameSeconds,
                    out var deliveredVillager,
                    out var playerInventory))
                return false;
            _villagers[index] = deliveredVillager;
            _activePlayer = _activePlayer with
            {
                Inventory = playerInventory.ItemIds(),
                InventoryQuantities = playerInventory.Quantities(),
                UpdatedUtc = DateTime.UtcNow
            };
            _saves.SavePlayer(_activePlayer);
            ObserveLog("favor_completed", villager.Id, new
            {
                PromiseId = promise.Id,
                PromiseeId = _activePlayer.Id,
                promise.ItemId
            });
            _villagersDirty = true;
            return true;
        }
        var receiverIndex = _villagers.FindIndex(value =>
            value.Id == promise.PromiseeId && value.Health > 0);
        if (receiverIndex < 0) return false;
        var receiver = _villagers[receiverIndex];
        var distance = Vector2.DistanceSquared(
            new(villager.PositionX, villager.PositionY),
            new(receiver.PositionX, receiver.PositionY));
        if (distance > VillagerSimulation.InteractionRange *
                       VillagerSimulation.InteractionRange)
        {
            MoveVillagerForCapability(
                index,
                villager,
                tier,
                new(receiver.PositionX, receiver.PositionY),
                VillagerNeed.Social);
            return true;
        }
        var priorProgress = promise.Progress;
        var deliveredPromisor = villager;
        var deliveredReceiver = receiver;
        (deliveredPromisor, deliveredReceiver) =
            VillagerCommitmentService.CompleteDelivery(
                deliveredPromisor,
                deliveredReceiver,
                promise.Id,
                _worldGameSeconds);
        var deliveredPromise = deliveredPromisor.Promises?
            .FirstOrDefault(value => value.Id == promise.Id);
        if (deliveredPromise is null ||
            deliveredPromise.Progress == priorProgress)
            return false;
        _villagers[index] = deliveredPromisor;
        _villagers[receiverIndex] = deliveredReceiver;
        ObserveLog("favor_completed", villager.Id, new
        {
            PromiseId = promise.Id,
            PromiseeId = receiver.Id,
            promise.ItemId
        });
        _villagersDirty = true;
        return true;
    }

    private void MoveVillagerForCapability(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier,
        Vector2 target,
        VillagerNeed need)
    {
        var position = new Vector2(villager.PositionX, villager.PositionY);
        var safe = WorldLevelNavigation.ReachableWalkableTarget(
            _worldSeed,
            position,
            target,
            villager.WorldLevel,
            maximumRadius: 3);
        _villagers[index] = VillagerSimulation.ApplyDecision(
            villager,
            new(need, safe),
            tier,
            _worldGameSeconds);
        _villagersDirty = true;
    }

    private IEnumerable<(GpuWorldChunk Gpu, WorldGroundObject Object)>
        NearbyGroundObjects(VillagerState villager, float radius)
    {
        var position = new Vector2(villager.PositionX, villager.PositionY);
        var radiusSquared = radius * radius;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var value in gpu.Chunk.GroundObjects)
                if (Vector2.DistanceSquared(
                        position, new(value.X, value.Y)) <= radiusSquared)
                    yield return (gpu, value);
        }
    }

    private static void ReplaceGroundObject(
        GpuWorldChunk gpu,
        WorldGroundObject previous,
        WorldGroundObject updated)
    {
        var index = gpu.Chunk.GroundObjects.IndexOf(previous);
        if (index >= 0) gpu.Chunk.GroundObjects[index] = updated;
    }

    private static string TreeReservationKey(Guid treeId) =>
        $"tree:{treeId:N}";

    private static string ResourceReservationKey(
        string kind, string stableKey) =>
        $"{kind}:{stableKey}";

    private float DeterministicRoll(string actorId, string purpose)
    {
        var hash = HashCode.Combine(
            actorId,
            purpose,
            (long)Math.Floor(_worldGameSeconds));
        return (uint)hash / (float)uint.MaxValue;
    }
}
