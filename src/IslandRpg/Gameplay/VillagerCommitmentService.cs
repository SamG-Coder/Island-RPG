using System.Security.Cryptography;
using System.Text;

namespace IslandRpg.Gameplay;

internal enum VillagerGoalKind : byte
{
    StockpileFood,
    StockpileWood,
    EstablishStorage,
    ImproveSkill,
    Explore,
    HelpPerson
}

internal enum CommitmentStatus : byte
{
    Active,
    Fulfilled,
    Broken,
    Cancelled
}

internal enum VillagerPromiseKind : byte
{
    GatherItem,
    GiveItem,
    HelpBuild
}

internal sealed record VillagerLongTermGoal(
    Guid Id,
    VillagerGoalKind Kind,
    string? ItemId,
    int TargetQuantity,
    int Progress,
    double CreatedGameSeconds,
    CommitmentStatus Status = CommitmentStatus.Active,
    string? PartnerId = null);

internal sealed record VillagerPromise(
    Guid Id,
    VillagerPromiseKind Kind,
    string PromisorId,
    string PromiseeId,
    string? ItemId,
    int TargetQuantity,
    int Progress,
    double CreatedGameSeconds,
    double DeadlineGameSeconds,
    CommitmentStatus Status = CommitmentStatus.Active,
    float? RendezvousX = null,
    float? RendezvousY = null,
    int? RendezvousWorldLevel = null,
    double? RendezvousGameSeconds = null);

internal readonly record struct PromiseAcceptance(
    bool Accepted,
    string Reply,
    VillagerPromise? Promise = null);

internal static class VillagerCommitmentService
{
    public const int MaximumGoals = 8;
    public const int MaximumPromises = 8;
    public const int MaximumActivePromises = 3;
    public const double DefaultPromiseDuration = 12 * 60 * 60;

    public static IReadOnlyList<VillagerLongTermGoal> InitialGoals(
        string villagerId,
        double gameSeconds) =>
    [
        new(
            StableId(villagerId, "goal-food", gameSeconds),
            VillagerGoalKind.StockpileFood,
            ItemId: null,
            TargetQuantity: 3,
            Progress: 0,
            CreatedGameSeconds: gameSeconds),
        new(
            StableId(villagerId, "goal-wood", gameSeconds),
            VillagerGoalKind.StockpileWood,
            ItemIds.Logs,
            TargetQuantity: 4,
            Progress: 0,
            CreatedGameSeconds: gameSeconds)
    ];

    public static PromiseAcceptance TryAccept(
        VillagerState promisor,
        string promiseeId,
        VillagerPromiseKind kind,
        string? itemId,
        int quantity,
        double gameSeconds)
    {
        quantity = Math.Clamp(quantity, 1, 100);
        var active = promisor.Promises?.Count(value =>
            value.Status == CommitmentStatus.Active) ?? 0;
        if (active >= MaximumActivePromises)
            return new(false,
                "I already have too much that I've promised to do.");
        var relationship =
            promisor.Relationships?.FirstOrDefault(value =>
                value.CharacterId == promiseeId);
        if (relationship?.State.Trust < -35)
            return new(false,
                "I don't trust you enough to promise that.");
        if (kind is
                VillagerPromiseKind.GatherItem or
                VillagerPromiseKind.GiveItem &&
            (itemId is null ||
             !ItemCatalog.TryGet(itemId, out var item) ||
             !item.Droppable))
            return new(false, "I can't promise that item.");
        var promise = new VillagerPromise(
            StableId(
                promisor.Id,
                $"{promiseeId}:{kind}:{itemId}:{quantity}",
                gameSeconds),
            kind,
            promisor.Id,
            promiseeId,
            itemId,
            quantity,
            Progress: 0,
            CreatedGameSeconds: gameSeconds,
            DeadlineGameSeconds:
                gameSeconds + DefaultPromiseDuration);
        return new(
            true,
            kind switch
            {
                VillagerPromiseKind.GatherItem =>
                    $"All right. I'll gather {quantity} " +
                    $"{ItemCatalog.Get(itemId!).Name}.",
                VillagerPromiseKind.GiveItem =>
                    $"All right. I'll bring you {quantity} " +
                    $"{ItemCatalog.Get(itemId!).Name}.",
                _ => "All right. I'll help with that."
            },
            promise);
    }

    public static bool TryParseGatherRequest(
        string text,
        out string itemId,
        out int quantity)
    {
        itemId = "";
        quantity = 1;
        var normalized = text.Trim().ToLowerInvariant();
        var request = RequestedActionClause(normalized);
        if (!ContainsCollectionRequest(request))
            return false;
        foreach (var token in request.Split(
                     [' ', ',', '.', '?', '!'],
                     StringSplitOptions.RemoveEmptyEntries))
            if (TryParseQuantity(token, out var parsed))
            {
                quantity = Math.Clamp(parsed, 1, 100);
                break;
            }
        if (!ItemLanguageService.TryResolveMention(request, out var item))
            return false;
        itemId = item.Id;
        return true;
    }

    private static bool ContainsCollectionRequest(string text) =>
        new[]
        {
            "gather", "collect", "bring", "fetch", "find", "harvest",
            "pick up", "look for", "search for", "get"
        }.Any(verb => text.Contains(verb, StringComparison.Ordinal));

    private static bool TryParseQuantity(string token, out int quantity)
    {
        if (int.TryParse(token, out quantity)) return true;
        quantity = token switch
        {
            "a" or "an" or "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "dozen" => 12,
            _ => 0
        };
        return quantity > 0;
    }

    private static string RequestedActionClause(string text)
    {
        var start = -1;
        foreach (var marker in new[]
                 {
                     "would you ", "could you ", "can you ",
                     "will you ", "please "
                 })
        {
            var candidate = text.LastIndexOf(
                marker, StringComparison.Ordinal);
            if (candidate > start) start = candidate + marker.Length;
        }
        var request = start >= 0 ? text[start..] : text;
        var end = request.Length;
        foreach (var marker in new[]
                 {
                     ". i will ", ". i'll ", "; i will ",
                     ", i will ", " while i will ", " while i'll ",
                     " while i ", " and i will ", " and i'll "
                 })
        {
            var candidate = request.IndexOf(
                marker, StringComparison.Ordinal);
            if (candidate >= 0) end = Math.Min(end, candidate);
        }
        return request[..end];
    }

    public static bool TryResolveAiItemProposal(
        string proposalText,
        string action,
        string modelItem,
        int modelQuantity,
        out VillagerPromiseKind kind,
        out string itemId,
        out int quantity)
    {
        kind = action.Equals("give", StringComparison.OrdinalIgnoreCase)
            ? VillagerPromiseKind.GiveItem
            : VillagerPromiseKind.GatherItem;
        itemId = "";
        quantity = Math.Clamp(modelQuantity, 1, 100);
        if (action is not ("gather" or "give" or "gather_sticks" or
            "gather_berries" or "gather_fibre" or "meet"))
            return false;
        if (!TryParseGatherRequest(
                proposalText, out var requestedItemId,
                out var requestedQuantity))
            return false;
        // Smaller local models sometimes select `meet` when a request contains
        // both collection and rendezvous instructions. The player's explicit
        // collection clause remains the authoritative executable commitment.
        if (action == "meet")
        {
            itemId = requestedItemId;
            quantity = requestedQuantity;
            return true;
        }
        var impliedItem = action switch
        {
            "gather_sticks" => ItemIds.Sticks,
            "gather_berries" => ItemIds.WildBerries,
            "gather_fibre" => ItemIds.PlantFibres,
            _ => null
        };
        if (impliedItem is not null)
            itemId = impliedItem;
        else if (ItemCatalog.TryGet(modelItem, out var exact))
            itemId = exact.Id;
        else if (ItemLanguageService.TryResolveMention(modelItem, out var item))
            itemId = item.Id;
        if (itemId.Length == 0 ||
            !VillagerSettlementProjectService.MatchesRequirement(
                requestedItemId, itemId))
            return false;
        quantity = requestedQuantity;
        return true;
    }

    public static VillagerState AddPromise(
        VillagerState state,
        VillagerPromise promise)
    {
        var promises = state.Promises?.ToList() ?? [];
        promises.RemoveAll(value =>
            value.Status != CommitmentStatus.Active &&
            promises.Count >= MaximumPromises);
        if (promises.Count >= MaximumPromises)
            promises.RemoveAt(0);
        promises.Add(promise);
        return VillagerPromisePlanService.CompileActionPlan(
            state with { Promises = promises });
    }

    public static VillagerState RecordAcquiredItem(
        VillagerState state,
        string itemId,
        int quantity = 1)
    {
        if (quantity <= 0) return state;
        var item = ItemCatalog.Get(itemId);
        List<VillagerLongTermGoal>? updatedGoals = null;
        if (state.Goals is { Count: > 0 })
            for (var index = 0; index < state.Goals.Count; index++)
            {
                var goal = state.Goals[index];
                if (goal.Status != CommitmentStatus.Active ||
                    goal.Progress >= goal.TargetQuantity ||
                    !MatchesGoal(goal, item))
                    continue;
                updatedGoals ??= state.Goals.ToList();
                var progress = Math.Min(
                    goal.TargetQuantity,
                    goal.Progress + quantity);
                updatedGoals[index] = goal with
                {
                    Progress = progress,
                    Status = progress >= goal.TargetQuantity
                        ? CommitmentStatus.Fulfilled
                        : CommitmentStatus.Active
                };
            }

        List<VillagerPromise>? updatedPromises = null;
        var remainingPromiseQuantity = quantity;
        if (state.Promises is not { Count: > 0 })
            return VillagerPromisePlanService.RecordDirectiveAcquisition(
                updatedGoals is null
                ? state
                : state with { Goals = updatedGoals },
                itemId,
                quantity);
        for (var index = 0;
             index < state.Promises.Count &&
             remainingPromiseQuantity > 0;
             index++)
        {
            var promise = state.Promises[index];
            if (promise.Status != CommitmentStatus.Active ||
                promise.Kind != VillagerPromiseKind.GatherItem ||
                promise.ItemId is not { } promisedItem ||
                !VillagerSettlementProjectService.MatchesRequirement(
                    itemId, promisedItem))
                continue;
            updatedPromises ??= state.Promises.ToList();
            var applied = Math.Min(
                remainingPromiseQuantity,
                promise.TargetQuantity - promise.Progress);
            var progress = promise.Progress + applied;
            updatedPromises[index] = promise with
            {
                Progress = progress,
                Status = progress >= promise.TargetQuantity &&
                         promise.RendezvousGameSeconds is null
                    ? CommitmentStatus.Fulfilled
                    : CommitmentStatus.Active
            };
            remainingPromiseQuantity -= applied;
        }
        return VillagerPromisePlanService.RecordDirectiveAcquisition(
            VillagerPromisePlanService.CompileActionPlan(state with
        {
            Goals = updatedGoals ?? state.Goals,
            Promises = updatedPromises ?? state.Promises
        }), itemId, quantity);
    }

    private static bool MatchesGoal(
        VillagerLongTermGoal goal, ItemDefinition item) =>
        goal.Kind switch
        {
            VillagerGoalKind.StockpileFood =>
                SurvivalService.TryFoodEffect(item.Id, out _),
            VillagerGoalKind.StockpileWood => item.HasTag(ItemTag.Log),
            _ => string.Equals(
                goal.ItemId, item.Id,
                StringComparison.OrdinalIgnoreCase)
        };

    public static VillagerState UpdateDeadlines(
        VillagerState state,
        double gameSeconds)
    {
        if (state.Promises is not { Count: > 0 })
            return state;
        List<VillagerPromise>? updated = null;
        for (var index = 0;
             index < state.Promises.Count;
             index++)
        {
            var promise = state.Promises[index];
            if (promise.Status != CommitmentStatus.Active ||
                promise.DeadlineGameSeconds > gameSeconds)
                continue;
            updated ??= state.Promises.ToList();
            updated[index] = promise with
            {
                Status = CommitmentStatus.Broken
            };
        }
        return updated is null
            ? state
            : VillagerPromisePlanService.CompileActionPlan(
                state with { Promises = updated });
    }

    public static (VillagerState Promisor, VillagerState Promisee)
        UpdateDeadlines(
            VillagerState promisor,
            VillagerState promisee,
            double gameSeconds)
    {
        var promises = promisor.Promises?.ToList() ?? [];
        var broken = promises
            .Select((promise, index) => (promise, index))
            .Where(value =>
                value.promise.Status == CommitmentStatus.Active &&
                value.promise.PromiseeId == promisee.Id &&
                value.promise.DeadlineGameSeconds <= gameSeconds)
            .ToArray();
        if (broken.Length == 0) return (promisor, promisee);
        foreach (var entry in broken)
        {
            promises[entry.index] = entry.promise with
            {
                Status = CommitmentStatus.Broken
            };
            promisee = AddRelationshipOutcome(
                promisee, promisor.Id, CommitmentStatus.Broken);
            promisee = AddMemory(
                promisee,
                "promise-broken",
                promisor.Id,
                gameSeconds,
                $"{promisor.Name} broke a promise.",
                -20);
        }
        promisor = AddMemory(
            VillagerPromisePlanService.CompileActionPlan(
                promisor with { Promises = promises }),
            "promise-broken",
            promisee.Id,
            gameSeconds,
            $"Failed to keep a promise to {promisee.Name}.",
            -12);
        return (promisor, promisee);
    }

    public static RelationshipState ApplyOutcome(
        in RelationshipState relationship,
        CommitmentStatus outcome) =>
        outcome switch
        {
            CommitmentStatus.Fulfilled =>
                (relationship with
                {
                    Trust = relationship.Trust + 8,
                    Respect = relationship.Respect + 4,
                    Gratitude = relationship.Gratitude + 5,
                    Resentment = relationship.Resentment - 2
                }).Clamp(),
            CommitmentStatus.Broken =>
                (relationship with
                {
                    Trust = relationship.Trust - 12,
                    Respect = relationship.Respect - 5,
                    Resentment = relationship.Resentment + 6
                }).Clamp(),
            _ => relationship
        };

    public static (VillagerState Promisor, VillagerState Promisee)
        CompleteDelivery(
            VillagerState promisor,
            VillagerState promisee,
            Guid promiseId,
            double gameSeconds)
    {
        var promises = promisor.Promises?.ToList() ?? [];
        var index = promises.FindIndex(value =>
            value.Id == promiseId &&
            value.Status == CommitmentStatus.Active &&
            value.Kind == VillagerPromiseKind.GiveItem &&
            value.PromiseeId == promisee.Id);
        if (index < 0) return (promisor, promisee);
        var promise = promises[index];
        if (promise.ItemId is null) return (promisor, promisee);
        var inventorySlot = Array.FindIndex(
            promisor.Inventory,
            item => string.Equals(
                item, promise.ItemId, StringComparison.OrdinalIgnoreCase));
        if (!EntityInteractionService.TryTransfer(
                promisor.Inventory,
                promisee.Inventory,
                inventorySlot,
                out var sourceInventory,
                out var destinationInventory,
                out var transferredItem) ||
            !string.Equals(
                transferredItem,
                promise.ItemId,
                StringComparison.OrdinalIgnoreCase))
            return (promisor, promisee);
        promisor = promisor with { Inventory = sourceInventory };
        promisee = promisee with { Inventory = destinationInventory };
        var progress = Math.Min(
            promise.TargetQuantity, promise.Progress + 1);
        var fulfilled = progress >= promise.TargetQuantity;
        promises[index] = promise with
        {
            Progress = progress,
            Status = fulfilled
                ? CommitmentStatus.Fulfilled
                : CommitmentStatus.Active
        };
        promisor = AddMemory(
            VillagerPromisePlanService.CompileActionPlan(
                promisor with { Promises = promises }),
            "favor-delivered",
            promisee.Id,
            gameSeconds,
            $"Delivered {ItemCatalog.Get(promise.ItemId).Name} to {promisee.Name}.",
            fulfilled ? 12 : 5);
        if (!fulfilled) return (promisor, promisee);
        promisee = AddRelationshipOutcome(
            promisee, promisor.Id, CommitmentStatus.Fulfilled);
        promisee = AddMemory(
            promisee,
            "favor-completed",
            promisor.Id,
            gameSeconds,
            $"{promisor.Name} kept a promise to bring {ItemCatalog.Get(promise.ItemId).Name}.",
            20);
        return (promisor, promisee);
    }

    public static bool TryCompleteDeliveryToInventory(
        VillagerState promisor,
        string promiseeId,
        InventoryContainer promiseeInventory,
        Guid promiseId,
        double gameSeconds,
        out VillagerState updatedPromisor,
        out InventoryContainer updatedPromiseeInventory)
    {
        updatedPromisor = promisor;
        updatedPromiseeInventory = promiseeInventory.Clone();
        var promises = promisor.Promises?.ToList() ?? [];
        var promiseIndex = promises.FindIndex(value =>
            value.Id == promiseId &&
            value.Status == CommitmentStatus.Active &&
            value.Kind == VillagerPromiseKind.GiveItem &&
            value.PromiseeId == promiseeId &&
            value.ItemId is not null);
        if (promiseIndex < 0) return false;
        var promise = promises[promiseIndex];
        var sourceSlot = Array.FindIndex(promisor.Inventory, value =>
            value is not null &&
            VillagerSettlementProjectService.MatchesRequirement(
                value, promise.ItemId!));
        if (sourceSlot < 0 ||
            promisor.Inventory[sourceSlot] is not { } transferredItem ||
            !updatedPromiseeInventory.TryAdd(transferredItem) ||
            !PlayerInventory.TryRemove(
                promisor.Inventory, sourceSlot, out var sourceInventory))
            return false;
        var progress = Math.Min(
            promise.TargetQuantity, promise.Progress + 1);
        promises[promiseIndex] = promise with
        {
            Progress = progress,
            Status = progress >= promise.TargetQuantity
                ? CommitmentStatus.Fulfilled
                : CommitmentStatus.Active
        };
        updatedPromisor = AddMemory(
            VillagerPromisePlanService.CompileActionPlan(promisor with
            {
                Inventory = sourceInventory,
                Promises = promises
            }),
            "favor-delivered",
            promiseeId,
            gameSeconds,
            $"Delivered {ItemCatalog.Get(transferredItem).Name} as promised.",
            progress >= promise.TargetQuantity ? 12 : 5);
        return true;
    }

    public static bool TryCompleteDeliveryToInventory(
        VillagerState promisor,
        string promiseeId,
        string?[] promiseeInventory,
        Guid promiseId,
        double gameSeconds,
        out VillagerState updatedPromisor,
        out string?[] updatedPromiseeInventory)
    {
        updatedPromisor = promisor;
        updatedPromiseeInventory = promiseeInventory;
        var promises = promisor.Promises?.ToList() ?? [];
        var promiseIndex = promises.FindIndex(value =>
            value.Id == promiseId &&
            value.Status == CommitmentStatus.Active &&
            value.Kind == VillagerPromiseKind.GiveItem &&
            value.PromiseeId == promiseeId &&
            value.ItemId is not null);
        if (promiseIndex < 0) return false;
        var promise = promises[promiseIndex];
        var sourceSlot = Array.FindIndex(promisor.Inventory, value =>
            value is not null &&
            VillagerSettlementProjectService.MatchesRequirement(
                value, promise.ItemId!));
        if (!EntityInteractionService.TryTransfer(
                promisor.Inventory,
                promiseeInventory,
                sourceSlot,
                out var sourceInventory,
                out var destinationInventory,
                out var transferredItem) ||
            transferredItem is null ||
            !VillagerSettlementProjectService.MatchesRequirement(
                transferredItem, promise.ItemId!))
            return false;
        var progress = Math.Min(
            promise.TargetQuantity, promise.Progress + 1);
        promises[promiseIndex] = promise with
        {
            Progress = progress,
            Status = progress >= promise.TargetQuantity
                ? CommitmentStatus.Fulfilled
                : CommitmentStatus.Active
        };
        updatedPromisor = AddMemory(
            VillagerPromisePlanService.CompileActionPlan(promisor with
            {
                Inventory = sourceInventory,
                Promises = promises
            }),
            "favor-delivered",
            promiseeId,
            gameSeconds,
            $"Delivered {ItemCatalog.Get(transferredItem).Name} as promised.",
            progress >= promise.TargetQuantity ? 12 : 5);
        updatedPromiseeInventory = destinationInventory;
        return true;
    }

    private static VillagerState AddRelationshipOutcome(
        VillagerState state,
        string actorId,
        CommitmentStatus outcome)
    {
        var relationships = state.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == actorId);
        var existing = index >= 0
            ? relationships[index]
            : new VillagerRelationship(actorId, default);
        var updated = existing with
        {
            State = ApplyOutcome(existing.State, outcome)
        };
        if (index >= 0) relationships[index] = updated;
        else relationships.Add(updated);
        return state with { Relationships = relationships };
    }

    private static VillagerState AddMemory(
        VillagerState state,
        string kind,
        string subjectId,
        double gameSeconds,
        string summary,
        int sentiment)
    {
        var memories = state.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(), kind, subjectId, null, 1,
            gameSeconds, sentiment, summary));
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - VillagerSimulation.MaximumMemories);
        return state with { Memories = memories };
    }

    private static Guid StableId(
        string ownerId,
        string purpose,
        double gameSeconds)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ownerId}|{purpose}|{gameSeconds:R}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
