using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private static readonly string[] VillagerCraftPriority =
    [
        ItemIds.MediumRock,
        ItemIds.SharpenedRock,
        ItemIds.StoneKnife,
        ItemIds.StoneAxe,
        ItemIds.PrimitiveFishingNet,
        ItemIds.SmallRocks,
        ItemIds.Campfire,
        ItemIds.StonePickaxe,
        ItemIds.StoneShovel,
        ItemIds.Rope,
        ItemIds.StoneHammer,
        ItemIds.Workbench,
        ItemIds.StorageChest
    ];

    private bool TryExecuteVillagerCapabilityAction(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (TryVillagerDefendSelf(index, villager, tier) ||
            TryVillagerEat(index, villager, tier) ||
            TryVillagerRoleAction(index, villager, tier) ||
            TryVillagerCookStew(index, villager) ||
            TryVillagerCook(index, villager) ||
            TryVillagerWithdrawFood(index, villager) ||
            TryVillagerGatherTreeSticks(index, villager, tier) ||
            TryVillagerForage(index, villager, tier) ||
            TryVillagerFish(index, villager, tier) ||
            TryVillagerCutTree(index, villager, tier) ||
            TryVillagerMine(index, villager, tier) ||
            TryVillagerCraft(index, villager) ||
            TryVillagerPlaceObject(index, villager) ||
            TryVillagerPlaceOrTendCampfire(index, villager) ||
            TryVillagerTakeCampfireFuel(index, villager) ||
            TryVillagerDropRequestedItem(index, villager) ||
            TryVillagerFulfilGift(index, villager))
            return true;
        return false;
    }

    private bool TryVillagerRoleAction(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.LastDeliberation is { Action: not "none" } trace &&
            trace.Decision is not ("refuse" or "clarify") &&
            _worldGameSeconds - trace.GameSeconds <= 15 * 60)
            return false;
        return villager.WorkRole switch
        {
            VillagerWorkRole.Food =>
                TryVillagerCookStew(index, villager) ||
                TryVillagerCook(index, villager) ||
                TryVillagerWithdrawFood(index, villager) ||
                TryVillagerFish(index, villager, tier) ||
                TryVillagerForage(index, villager, tier),
            VillagerWorkRole.Wood =>
                TryVillagerCutTree(index, villager, tier) ||
                TryVillagerGatherTreeSticks(index, villager, tier),
            VillagerWorkRole.Crafting =>
                TryVillagerCraft(index, villager) ||
                TryVillagerPlaceObject(index, villager) ||
                TryVillagerPlaceOrTendCampfire(index, villager),
            VillagerWorkRole.Exploration =>
                TryVillagerMine(index, villager, tier) ||
                TryVillagerForage(index, villager, tier),
            _ => false
        };
    }

    private bool TryVillagerGatherTreeSticks(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        var requested = RequestedVillagerAction(
            villager, "gather_sticks");
        var needsSticks = VillagerCraftPriority.Any(itemId =>
            CraftingSkill.Recipes.FirstOrDefault(recipe =>
                recipe.ResultItemId == itemId) is { } recipe &&
            recipe.Ingredients.Any(ingredient =>
                ingredient.ItemId == ItemIds.Sticks) &&
            !villager.Inventory.Contains(itemId));
        if ((!requested && !needsSticks) ||
            PlayerInventory.IsFull(villager.Inventory))
            return false;
        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, WorldTreeInstance Tree)? best = null;
        var bestDistance = 24f * 24f;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var tree in gpu.Chunk.TreeInstances)
            {
                if (tree.State != TreeLifecycleState.Standing ||
                    tree.SticksRemaining <= 0 ||
                    !_villagerWork.IsAvailable(
                        TreeReservationKey(tree.Id),
                        villager.Id, _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(tree.X + .5f, tree.Y + .5f));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, tree);
            }
        }
        if (best is null) return false;
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
        var treeIndex = best.Value.Gpu.Chunk.TreeInstances.FindIndex(
            tree => tree.Id == best.Value.Tree.Id);
        if (treeIndex < 0) return false;
        var gathered = ActorActionService.Gather(
            villager.Inventory, ItemIds.Sticks, 1);
        if (!gathered.Succeeded) return false;
        best.Value.Gpu.Chunk.TreeInstances[treeIndex] =
            best.Value.Tree with
            {
                SticksRemaining = best.Value.Tree.SticksRemaining - 1
            };
        _villagers[index] = villager with
        {
            Inventory = gathered.Inventory,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.GatherPauseSeconds,
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        QueueChunkSave(best.Value.Gpu.Chunk);
        _villagerWork.ReleaseTarget(reservationKey, villager.Id);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "gather_sticks",
            ItemId = ItemIds.Sticks,
            Quantity = 1
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerEat(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (villager.Hunger > 62) return false;
        var slot = Array.FindIndex(
            villager.Inventory,
            item => item is not null &&
                    SurvivalService.TryFoodEffect(item, out _));
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
        var wantsFood = (villager.Hunger <= 82 ||
                         RequestedVillagerAction(villager, "gather_berries", "seek_food")) &&
                        VillagerSimulation.CountFood(
                            villager.Inventory) == 0;
        var wantsFibre = RequestedVillagerAction(villager, "gather_fibre") ||
            !villager.Inventory.Any(item =>
            item is not null && ItemCatalog.Get(item).HasTag(ItemTag.Knife)) ||
            PlayerInventory.BestFishingNet(villager.Inventory) is null;
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
        var gathered = ActorActionService.Gather(
            villager.Inventory, itemId, amount);
        if (!gathered.Succeeded) return false;
        var farming = berries
            ? FarmingSkill.AwardExperience(
                villager.FarmingExperience, 18 * amount).Experience
            : villager.FarmingExperience;
        _villagers[index] = villager with
        {
            Inventory = gathered.Inventory,
            FarmingExperience = farming,
            Need = berries ? VillagerNeed.Food : VillagerNeed.Explore,
            Action = EntityAction.Gather,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.GatherPauseSeconds,
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        SetVegetationCooldown(
            best.Value.Gpu.Chunk,
            best.Value.Key,
            berries ? 12 * 60 : 5 * 60);
        QueueChunkSave(best.Value.Gpu.Chunk);
        _villagerWork.ReleaseTarget(reservationKey, villager.Id);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = berries ? "gather_berries" : "gather_fibre",
            ItemId = itemId,
            Quantity = amount
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerCraft(int index, VillagerState villager)
    {
        var level = CraftingSkill.LevelForExperience(
            villager.CraftingExperience);
        var hasCampfire = NearbyGroundObjects(villager, 12)
            .Any(value => CampfireService.IsCampfire(value.Object));
        var hasWorkbench = NearbyGroundObjects(villager, 12)
            .Any(value => value.Object.ItemId == ItemIds.Workbench);
        foreach (var desired in VillagerCraftPriority)
        {
            if (villager.Inventory.Contains(desired) ||
                desired == ItemIds.Campfire && hasCampfire ||
                desired == ItemIds.Workbench && hasWorkbench ||
                desired == ItemIds.StorageChest &&
                NearbyGroundObjects(villager, 12).Any(value =>
                    StorageContainerService.IsStorage(value.Object.ItemId) &&
                    value.Object.OwnerId == villager.Id))
                continue;
            var recipe = CraftingSkill.Recipes.FirstOrDefault(value =>
                value.ResultItemId == desired);
            if (recipe is null) continue;
            var stationAvailable = recipe.RequiredStationItemId switch
            {
                null => true,
                ItemIds.Workbench => hasWorkbench,
                ItemIds.Campfire => hasCampfire,
                _ => false
            };
            var crafted = ActorActionService.Craft(
                villager.Inventory, recipe, level, stationAvailable);
            if (!crafted.Succeeded) continue;
            var experience = CraftingSkill.AwardExperience(
                villager.CraftingExperience, recipe);
            _villagers[index] = villager with
            {
                Inventory = crafted.Inventory,
                CraftingExperience = experience.Experience,
                Action = EntityAction.Work,
                ActionTime = 0,
                NextDecisionGameSeconds = _worldGameSeconds +
                    VillagerSimulation.NearbyDecisionSeconds,
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

    private bool TryVillagerFish(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if ((!RequestedVillagerAction(villager, "fish") &&
             villager.Hunger > 70) ||
            PlayerInventory.BestFishingNet(villager.Inventory) is null ||
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
                    !FishingSkill.CanCatch(
                        fish.Species,
                        FishingSkill.LevelForExperience(
                            villager.FishingExperience)) ||
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
        var catchProfile = FishingSkill.Profile(best.Value.Fish.Species);
        var gathered = ActorActionService.Gather(
            villager.Inventory, catchProfile.ItemId, 1);
        if (!gathered.Succeeded) return false;
        var award = FishingSkill.AwardExperience(
            villager.FishingExperience, best.Value.Fish.Species);
        var remainingFish = best.Value.Gpu.Chunk.FishRemaining.TryGetValue(
            best.Value.Fish.StableKey, out var current)
            ? current
            : catchProfile.SchoolSize;
        best.Value.Gpu.Chunk.FishRemaining[
            best.Value.Fish.StableKey] = remainingFish - 1;
        _villagers[index] = villager with
        {
            Inventory = gathered.Inventory,
            FishingExperience = award.Experience,
            Action = EntityAction.Fish,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.GatherPauseSeconds,
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        QueueChunkSave(best.Value.Gpu.Chunk);
        _villagerWork.ReleaseTarget(reservationKey, villager.Id);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "fish",
            catchProfile.ItemId,
            Species = best.Value.Fish.Species.ToString()
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerCutTree(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        var axe = PlayerInventory.BestAxe(villager.Inventory);
        if (axe is null || PlayerInventory.IsFull(villager.Inventory))
            return false;
        var needsLogs = villager.Goals?.Any(goal =>
            goal.Status == CommitmentStatus.Active &&
            goal.ItemId == ItemIds.Logs) == true;
        if (!needsLogs &&
            villager.WorkRole != VillagerWorkRole.Wood &&
            !RequestedVillagerAction(villager, "cut_tree", "gather"))
            return false;
        var position = new Vector2(villager.PositionX, villager.PositionY);
        (GpuWorldChunk Gpu, WorldTreeInstance Tree)? best = null;
        var bestDistance = 24f * 24f;
        foreach (var gpu in _worldChunks.Values)
        {
            if (!IsActiveSimulationChunk(gpu)) continue;
            foreach (var tree in gpu.Chunk.TreeInstances)
            {
                if (tree.State != TreeLifecycleState.Standing ||
                    !_villagerWork.IsAvailable(
                        TreeReservationKey(tree.Id),
                        villager.Id, _worldGameSeconds))
                    continue;
                var distance = Vector2.DistanceSquared(
                    position, new(tree.X + .5f, tree.Y + .5f));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (gpu, tree);
            }
        }
        if (best is null) return false;
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
        var treeIndex = best.Value.Gpu.Chunk.TreeInstances.FindIndex(
            value => value.Id == best.Value.Tree.Id);
        if (treeIndex < 0) return false;
        var damage = Math.Max(1, axe.WoodcuttingPower);
        var health = Math.Max(0, best.Value.Tree.Health - damage);
        var felled = health == 0;
        best.Value.Gpu.Chunk.TreeInstances[treeIndex] =
            best.Value.Tree with
            {
                Health = health,
                State = felled
                    ? TreeLifecycleState.Stump
                    : TreeLifecycleState.Standing
            };
        var inventory = villager.Inventory;
        if (felled)
            inventory = ActorActionService.Gather(
                inventory, ItemIds.Logs, 1).Inventory;
        var xp = SkillService.AwardExperience(
            villager.WoodcuttingExperience,
            damage + (felled ? Math.Max(10, best.Value.Tree.MaxHealth / 5) : 0));
        _villagers[index] = villager with
        {
            Inventory = inventory,
            WoodcuttingExperience = xp.Experience,
            Action = EntityAction.Work,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds,
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        QueueChunkSave(best.Value.Gpu.Chunk);
        if (felled)
            _villagerWork.ReleaseTarget(reservationKey, villager.Id);
        ObserveLog("world_action_succeeded", villager.Id, new
        {
            Action = "cut_tree",
            Damage = damage,
            RemainingHealth = health,
            Felled = felled
        });
        _villagersDirty = true;
        return true;
    }

    private bool TryVillagerMine(
        int index, VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (!RequestedVillagerAction(villager, "mine") &&
            villager.WorkRole != VillagerWorkRole.Exploration)
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
        var state = best.Value.Gpu.Chunk.MiningStates.FirstOrDefault(value =>
            value.StableKey == best.Value.Cached.StableKey);
        var health = state?.Health ?? best.Value.Definition.MaximumHealth;
        var damage = Math.Max(1, pickaxe.MiningPower);
        health = Math.Max(0, health - damage);
        best.Value.Gpu.Chunk.MiningStates.RemoveAll(value =>
            value.StableKey == best.Value.Cached.StableKey);
        best.Value.Gpu.Chunk.MiningStates.Add(new(
            best.Value.Cached.StableKey,
            health,
            best.Value.Definition.MaximumHealth));
        var inventory = villager.Inventory;
        if (health == 0 && best.Value.Definition.RewardItemId is { } reward)
            inventory = ActorActionService.Gather(
                inventory, reward, 1).Inventory;
        var xp = SkillService.AwardExperience(
            villager.MiningExperience,
            damage + (health == 0
                ? best.Value.Definition.CompletionExperience
                : 0));
        _villagers[index] = villager with
        {
            Inventory = inventory,
            MiningExperience = xp.Experience,
            Action = EntityAction.Mine,
            ActionTime = 0,
            NextDecisionGameSeconds = _worldGameSeconds +
                VillagerSimulation.NearbyDecisionSeconds,
            LastSimulatedGameSeconds = _worldGameSeconds
        };
        QueueChunkSave(best.Value.Gpu.Chunk);
        if (health == 0)
            _villagerWork.ReleaseTarget(reservationKey, villager.Id);
        _villagersDirty = true;
        return true;
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
            updatedFire = CampfireService.AddFuel(
                fire, ItemIds.Logs, _worldGameSeconds);
            PlayerInventory.TryRemove(
                inventoryState, logSlot, out inventoryState);
        }
        else if (fireState == CampfireState.Fueled &&
                 CampfireService.CanLight(
                     fire, inventoryState, _worldGameSeconds))
            updatedFire = CampfireService.Light(
                fire,
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
        if (!TryGetDropTerrain(
                (int)MathF.Floor(villager.PositionX),
                (int)MathF.Floor(villager.PositionY),
                out var gpu, out _))
            return false;
        var offset = new Vector2(
            .75f + DeterministicRoll(villager.Id, itemId),
            .4f);
        var position = new Vector2(
            villager.PositionX, villager.PositionY) + offset;
        if (!PlayerInventory.TryRemove(
                villager.Inventory, slot, out var inventory))
            return false;
        var placed = new WorldGroundObject(
            Guid.NewGuid(), itemId,
            position.X, position.Y,
            OwnerId: villager.Id);
        gpu.Chunk.GroundObjects.Add(placed);
        _villagers[index] = villager with
        {
            Inventory = inventory,
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
        var foodIndex = container.Items.ToList().FindIndex(value =>
            value is not null && SurvivalService.TryFoodEffect(value, out _));
        if (foodIndex < 0 ||
            !container.TryTake(foodIndex, 1, out var itemId) ||
            !PlayerInventory.TryAdd(
                villager.Inventory, itemId!, out var inventory))
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

    private bool TryVillagerDefendSelf(
        int index,
        VillagerState villager,
        VillagerSimulationTier tier)
    {
        if (_observeMode is not null ||
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
        var roll = MeleeCombatService.Roll(
            villager.AttackExperience,
            villager.StrengthExperience,
            DeterministicRoll(villager.Id, "combat-hit"),
            DeterministicRoll(villager.Id, "combat-damage"));
        if (roll.Hit)
            ApplyPlayerDamage(roll.Damage, villager.Name);
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
        _villagersDirty = true;
        return true;
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
        var cooked = ActorActionService.Cook(
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
        var cooked = ActorActionService.CookStew(
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
        if (fire.Object?.FuelItemId is not { } fuelItemId ||
            !PlayerInventory.TryAdd(
                villager.Inventory, fuelItemId, out var inventory))
            return false;
        ReplaceGroundObject(
            fire.Gpu, fire.Object,
            CampfireService.RemoveFuel(
                fire.Object, _worldGameSeconds));
        _villagers[index] = villager with
        {
            Inventory = inventory,
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
                out var gpu, out var position, out _) ||
            !PlayerInventory.TryRemove(
                villager.Inventory, slot, out var inventory))
            return false;
        gpu.Chunk.GroundObjects.Add(new(
            Guid.NewGuid(), itemId, position.X, position.Y,
            OwnerId: villager.Id));
        _villagers[index] = villager with
        {
            Inventory = inventory,
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

    private bool TryVillagerFulfilGift(int index, VillagerState villager)
    {
        var promise = villager.Promises?.FirstOrDefault(value =>
            value.Status == CommitmentStatus.Active &&
            value.Kind == VillagerPromiseKind.GiveItem &&
            value.ItemId is not null);
        if (promise is null) return false;
        var receiverIndex = _villagers.FindIndex(value =>
            value.Id == promise.PromiseeId && value.Health > 0);
        if (receiverIndex < 0) return false;
        var receiver = _villagers[receiverIndex];
        var distance = Vector2.DistanceSquared(
            new(villager.PositionX, villager.PositionY),
            new(receiver.PositionX, receiver.PositionY));
        if (distance > VillagerSimulation.InteractionRange *
                       VillagerSimulation.InteractionRange)
            return false;
        var slot = Array.FindIndex(
            villager.Inventory, value => value == promise.ItemId);
        if (!ActorActionService.TryTransfer(
                villager.Inventory,
                receiver.Inventory,
                slot,
                out var source,
                out var destination,
                out var itemId))
            return false;
        _villagers[index] = villager with { Inventory = source };
        _villagers[receiverIndex] = VillagerSimulation.RecordGift(
            receiver,
            villager.Id,
            villager.Name,
            Guid.NewGuid(),
            itemId!,
            _worldGameSeconds) with { Inventory = destination };
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
