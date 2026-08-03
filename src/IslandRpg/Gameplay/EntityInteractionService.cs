using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum EntityResourceAction : byte
{
    Woodcut,
    Mine
}

internal readonly record struct EntityResourceInteraction(
    EntityResourceAction Action,
    int Experience,
    int TargetHealth,
    int TargetMaximumHealth,
    int ToolPower,
    float AccuracyRoll,
    float DamageRoll,
    int CompletionExperience = 0);

internal readonly record struct EntityMeleeInteractionResult(
    bool Succeeded,
    MeleeAttackRoll Attack,
    SkillExperienceChange Experience,
    string? Failure = null);

internal readonly record struct EntitySurvivalInteractionResult(
    bool Succeeded,
    string?[] Inventory,
    SurvivalUpdate Survival,
    string? ItemId = null,
    string? Failure = null);

internal readonly record struct EntityWorldObjectInteractionResult(
    bool Succeeded,
    string?[] Inventory,
    WorldGroundObject? Object = null,
    string? ItemId = null,
    int Quantity = 0,
    string? Failure = null);

internal readonly record struct EntityFishingInteractionResult(
    bool Succeeded,
    string?[] Inventory,
    SkillExperienceChange Experience,
    string? ItemId = null,
    string? Failure = null);

internal readonly record struct EntityCachedCraftResult(
    bool Succeeded,
    string?[] Inventory,
    IReadOnlyList<Guid> ConsumedCacheObjectIds,
    IReadOnlyList<string> ReturnedCacheItemIds,
    string? ItemId = null,
    string? Failure = null);

/// <summary>
/// Actor-neutral boundary for interactions that change inventories, skills,
/// health, or world resources. Player and NPC controllers decide when an
/// interaction reaches its impact frame; this service decides its outcome.
/// Presentation, quests, memories, and persistence remain caller-owned.
/// </summary>
internal static class EntityInteractionService
{
    public static ResourceStrikeResult StrikeResource(
        in EntityResourceInteraction interaction) =>
        interaction.Action switch
        {
            EntityResourceAction.Woodcut => ResourceStrikeService.Woodcut(
                interaction.Experience,
                interaction.TargetHealth,
                interaction.TargetMaximumHealth,
                interaction.ToolPower,
                interaction.AccuracyRoll,
                interaction.DamageRoll),
            EntityResourceAction.Mine => ResourceStrikeService.Mine(
                interaction.Experience,
                interaction.TargetHealth,
                interaction.ToolPower,
                interaction.CompletionExperience,
                interaction.AccuracyRoll,
                interaction.DamageRoll),
            _ => throw new ArgumentOutOfRangeException(nameof(interaction))
        };

    public static EntityMeleeInteractionResult MeleeAttack(
        int attackExperience,
        int strengthExperience,
        int progressionExperience,
        float hitRoll,
        float damageRoll,
        string?[]? inventory = null)
    {
        var attack = MeleeCombatService.Roll(
            attackExperience,
            strengthExperience,
            hitRoll,
            damageRoll,
            inventory);
        return new(
            true,
            attack,
            SkillService.AwardExperience(
                progressionExperience, attack.Experience));
    }

    public static EntityMeleeInteractionResult TryMeleeAttack(
        EntityActionCooldowns cooldowns,
        string attackerId,
        double clock,
        int attackExperience,
        int strengthExperience,
        int progressionExperience,
        float hitRoll,
        float damageRoll,
        string?[]? inventory = null)
    {
        if (!cooldowns.TryCommit(
                attackerId,
                EntityAction.Attack,
                clock,
                MeleeCombatService.AttackIntervalSeconds))
            return new(
                false,
                default,
                new(
                    progressionExperience,
                    0,
                    SkillService.LevelForExperience(progressionExperience),
                    SkillService.LevelForExperience(progressionExperience)),
                "attack_cooldown");
        return MeleeAttack(
            attackExperience,
            strengthExperience,
            progressionExperience,
            hitRoll,
            damageRoll,
            inventory);
    }

    public static ActorInventoryResult Gather(
        string?[]? inventory, string itemId, int quantity) =>
        ActorActionService.Gather(inventory, itemId, quantity);

    public static ActorInventoryResult Craft(
        string?[]? inventory,
        CraftingRecipe recipe,
        int craftingLevel,
        bool stationAvailable = false) =>
        ActorActionService.Craft(
            inventory, recipe, craftingLevel, stationAvailable);

    public static EntityCachedCraftResult CraftWithGroundCache(
        string?[]? inventory,
        IReadOnlyList<WorldGroundObject> cacheItems,
        CraftingRecipe recipe,
        int craftingLevel,
        bool stationAvailable = false)
    {
        var actorInventory = PlayerInventory.Normalize(inventory);
        var combined = (string?[])actorInventory.Clone();
        var supplied = new Dictionary<int, WorldGroundObject>();
        foreach (var ingredient in recipe.Ingredients)
        {
            var needed = Math.Max(
                0,
                ingredient.Count -
                combined.Count(ingredient.Accepts));
            foreach (var item in cacheItems.Where(value =>
                         ingredient.Accepts(value.ItemId)))
            {
                if (needed <= 0 || supplied.Values.Any(value =>
                        value.Id == item.Id))
                    break;
                var slot = Array.FindIndex(combined, value => value is null);
                if (slot < 0) break;
                combined[slot] = item.ItemId;
                supplied.Add(slot, item);
                needed--;
            }
        }
        var crafted = ActorActionService.Craft(
            combined, recipe, craftingLevel, stationAvailable);
        if (!crafted.Succeeded)
            return new(
                false,
                actorInventory,
                [],
                [],
                Failure: crafted.Failure);

        var updatedActor = (string?[])crafted.Inventory.Clone();
        var returned = new List<string>();
        var produced = new List<string>();
        foreach (var (slot, source) in supplied)
        {
            if (updatedActor[slot] is { } remaining)
            {
                if (string.Equals(
                        remaining,
                        recipe.ResultItemId,
                        StringComparison.OrdinalIgnoreCase))
                    produced.Add(remaining);
                else
                    returned.Add(remaining);
            }
            updatedActor[slot] = null;
        }
        foreach (var itemId in produced)
            if (!PlayerInventory.TryAdd(
                    updatedActor, itemId, out updatedActor))
                return new(
                    false,
                    actorInventory,
                    [],
                    [],
                    Failure: "inventory_full");
        return new(
            true,
            updatedActor,
            supplied.Values.Select(value => value.Id).ToArray(),
            returned,
            recipe.ResultItemId);
    }

    public static ActorInventoryResult Cook(
        string?[]? inventory,
        int slot,
        int cookingLevel,
        float roll) =>
        ActorActionService.Cook(inventory, slot, cookingLevel, roll);

    public static CookingResult ResolveCooking(
        string rawItemId,
        int cookingLevel,
        float roll) =>
        CookingSkill.Roll(rawItemId, cookingLevel, roll);

    public static ActorInventoryResult CookStew(
        string?[]? inventory, int cookingLevel) =>
        ActorActionService.CookStew(inventory, cookingLevel);

    public static bool TryTransfer(
        string?[]? source,
        string?[]? destination,
        int sourceSlot,
        out string?[] updatedSource,
        out string?[] updatedDestination,
        out string? itemId) =>
        ActorActionService.TryTransfer(
            source, destination, sourceSlot,
            out updatedSource, out updatedDestination, out itemId);

    public static EntitySurvivalInteractionResult Eat(
        string?[]? inventory,
        int slot,
        float hunger,
        float wellFedSeconds,
        int health,
        int maximumHealth)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if ((uint)slot >= (uint)unchanged.Length ||
            unchanged[slot] is not { } itemId ||
            !SurvivalService.TryFoodEffect(itemId, out var effect))
            return new(
                false, unchanged,
                new(hunger, wellFedSeconds, health),
                Failure: "not_edible");
        if (!PlayerInventory.TryRemove(unchanged, slot, out var updated))
            return new(
                false, unchanged,
                new(hunger, wellFedSeconds, health),
                Failure: "item_unavailable");
        return new(
            true,
            updated,
            SurvivalService.Eat(
                effect, hunger, wellFedSeconds, health, maximumHealth),
            itemId);
    }

    public static EntityWorldObjectInteractionResult Plant(
        string?[]? inventory,
        int seedSlot,
        float x,
        float y,
        double gameSeconds,
        string? ownerId = null)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if ((uint)seedSlot >= (uint)unchanged.Length ||
            unchanged[seedSlot] is not { } seedItemId ||
            !CropService.TryHarvestItem(seedItemId, out _))
            return new(false, unchanged, Failure: "not_seed");
        if (!PlayerInventory.TryRemove(
                unchanged, seedSlot, out var updated))
            return new(false, unchanged, Failure: "item_unavailable");
        return new(
            true,
            updated,
            CropService.Plant(
                seedItemId, x, y, gameSeconds, ownerId),
            seedItemId,
            1);
    }

    public static EntityWorldObjectInteractionResult Harvest(
        string?[]? inventory,
        WorldGroundObject crop,
        double gameSeconds)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if (!CropService.IsCrop(crop))
            return new(false, unchanged, Failure: "not_crop");
        if (!CropService.IsReady(crop, gameSeconds))
            return new(false, unchanged, Failure: "not_ready");
        var itemId = crop.FuelItemId!;
        var gathered = Gather(
            unchanged, itemId, CropService.HarvestCount(unchanged));
        var quantity = PlayerInventory.AddedCount(
            unchanged, gathered.Inventory, itemId);
        return new(
            gathered.Succeeded,
            gathered.Inventory,
            ItemId: itemId,
            Quantity: quantity,
            Failure: gathered.Failure);
    }

    public static EntityWorldObjectInteractionResult Pickup(
        string?[]? inventory, string itemId)
    {
        var gathered = Gather(inventory, itemId, 1);
        return new(
            gathered.Succeeded,
            gathered.Inventory,
            ItemId: itemId,
            Quantity: gathered.Succeeded ? 1 : 0,
            Failure: gathered.Failure);
    }

    public static EntityFishingInteractionResult CatchFish(
        string?[]? inventory,
        int fishingExperience,
        WorldFishSpecies species)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        var profile = FishingSkill.Profile(species);
        var gathered = Gather(unchanged, profile.ItemId, 1);
        if (!gathered.Succeeded)
            return new(
                false, unchanged,
                SkillService.AwardExperience(fishingExperience, 0),
                Failure: gathered.Failure ?? "inventory_full");
        return new(
            true,
            gathered.Inventory,
            FishingSkill.AwardExperience(fishingExperience, species),
            profile.ItemId);
    }

    public static EntityWorldObjectInteractionResult Place(
        string?[]? inventory,
        int slot,
        float x,
        float y,
        string? ownerId = null)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if ((uint)slot >= (uint)unchanged.Length ||
            unchanged[slot] is not { } itemId ||
            !PlaceableObjectCatalog.IsPlaceable(itemId))
            return new(false, unchanged, Failure: "not_placeable");
        if (!PlayerInventory.TryRemove(unchanged, slot, out var updated))
            return new(false, unchanged, Failure: "item_unavailable");
        return new(
            true,
            updated,
            new(Guid.NewGuid(), itemId, x, y, OwnerId: ownerId),
            itemId,
            1);
    }

    public static EntityWorldObjectInteractionResult Drop(
        string?[]? inventory,
        int slot,
        float x,
        float y,
        string? ownerId = null)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if ((uint)slot >= (uint)unchanged.Length ||
            unchanged[slot] is not { } itemId ||
            !PlayerInventory.CanDrop(itemId))
            return new(false, unchanged, Failure: "not_droppable");
        if (!PlayerInventory.TryRemove(unchanged, slot, out var updated))
            return new(false, unchanged, Failure: "item_unavailable");
        return new(
            true,
            updated,
            new(Guid.NewGuid(), itemId, x, y, OwnerId: ownerId),
            itemId,
            1);
    }

    public static EntityWorldObjectInteractionResult AddCampfireFuel(
        string?[]? inventory,
        int slot,
        WorldGroundObject campfire,
        double gameSeconds)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if ((uint)slot >= (uint)unchanged.Length ||
            unchanged[slot] is not { } itemId ||
            !CampfireService.CanAddFuel(campfire, itemId, gameSeconds))
            return new(false, unchanged, Failure: "invalid_fuel");
        if (!PlayerInventory.TryRemove(unchanged, slot, out var updated))
            return new(false, unchanged, Failure: "item_unavailable");
        return new(
            true,
            updated,
            CampfireService.AddFuel(campfire, itemId, gameSeconds),
            itemId,
            1);
    }

    public static EntityWorldObjectInteractionResult TakeCampfireFuel(
        string?[]? inventory,
        WorldGroundObject campfire,
        double gameSeconds)
    {
        var unchanged = PlayerInventory.Normalize(inventory);
        if (!CampfireService.CanRemoveFuel(campfire, gameSeconds) ||
            campfire.FuelItemId is not { } itemId)
            return new(false, unchanged, Failure: "no_removable_fuel");
        var gathered = Gather(unchanged, itemId, 1);
        if (!gathered.Succeeded)
            return new(
                false, unchanged,
                Failure: gathered.Failure ?? "inventory_full");
        return new(
            true,
            gathered.Inventory,
            CampfireService.RemoveFuel(campfire, gameSeconds),
            itemId,
            1);
    }

    public static WorldGroundObject LightCampfire(
        WorldGroundObject campfire,
        string?[]? inventory,
        double gameSeconds,
        int firemakingLevel)
    {
        if (!CampfireService.CanLight(
                campfire, inventory ?? [], gameSeconds))
            return campfire;
        return CampfireService.Light(
            campfire, gameSeconds, firemakingLevel);
    }

    public static VillagerStorageTransferResult DepositAll(
        ItemContainerState container,
        string?[] inventory,
        string ownerId,
        Func<string, bool>? retain = null) =>
        VillagerStorageTransfer.DepositAll(
            container, inventory, ownerId, retain);

    public static bool TryWithdrawFirst(
        ItemContainerState container,
        string?[] inventory,
        Func<string, bool> accepts,
        out string?[] updatedInventory,
        out string? itemId) =>
        VillagerStorageTransfer.TryWithdrawFirst(
            container, inventory, accepts,
            out updatedInventory, out itemId);

    public static bool TryBluntStoneTool(
        string?[]? inventory,
        string toolItemId,
        float roll,
        out string?[] updatedInventory) =>
        PlayerInventory.TryBluntStoneTool(
            inventory, toolItemId, roll, out updatedInventory);

    public static bool TryAutoSharpenStoneTool(
        string?[]? inventory,
        string bluntToolItemId,
        out string?[] updatedInventory)
    {
        var normalized = PlayerInventory.Normalize(inventory);
        var rocksSlot = Array.FindIndex(
            normalized, item => item == ItemIds.SmallRocks);
        var toolSlot = Array.FindIndex(
            normalized, item => item == bluntToolItemId);
        return PlayerInventory.TrySharpenStoneTool(
            normalized, rocksSlot, toolSlot, out updatedInventory);
    }
}
