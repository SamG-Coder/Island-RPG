namespace IslandRpg.Gameplay;

internal enum VillagerPromisePlanAction : byte
{
    Collect,
    Deliver,
    Rendezvous,
    MoveTo,
    InteractWithTarget,
    CraftItem,
    BuildObject,
    DepositItem,
    WithdrawItem,
    FollowActor,
    ExploreArea,
    WaitUntil,
    TalkToActor,
    AttackTarget,
    FleeFromTarget,
    Rest,
    Eat,
    CutTree,
    Mine,
    Fish,
    Cook,
    Dig
}

internal sealed record VillagerPromisePlanStep(
    Guid PromiseId,
    VillagerPromisePlanAction Action,
    string? ItemId,
    int RemainingQuantity,
    double ExecuteAfterGameSeconds = 0,
    float? TargetX = null,
    float? TargetY = null,
    int? WorldLevel = null,
    string? TargetActorId = null,
    string? TargetKey = null,
    int Attempts = 0,
    int MaximumAttempts = 4);

/// <summary>
/// Projects social promises into concrete simulation work. Dialogue may add
/// scheduling details, but autonomous code owns execution and progress.
/// </summary>
internal static class VillagerPromisePlanService
{
    public static IReadOnlyList<VillagerPromisePlanStep> PlansFor(
        VillagerState villager)
    {
        if (villager.Health <= 0 || villager.Promises is not { Count: > 0 })
            return [];
        return villager.ActionPlan ?? BuildPlans(villager);
    }

    /// <summary>
    /// Compiles social commitments into a persistent high-level queue. Ollama
    /// chooses the commitment; the simulation controller remains responsible
    /// for finding valid targets and completing physical interactions.
    /// </summary>
    public static VillagerState CompileActionPlan(VillagerState villager) =>
        villager with
        {
            ActionPlan = BuildPlans(villager)
                .Concat(villager.ActionPlan?.Where(step =>
                    step.PromiseId == Guid.Empty) ?? [])
                .Take(16)
                .ToArray()
        };

    public static VillagerState CompileAiDirective(
        VillagerState villager,
        string action,
        string itemId,
        int quantity,
        string? targetActorId,
        float? targetX,
        float? targetY,
        int worldLevel,
        double gameSeconds,
        int delayMinutes)
    {
        if (!TryMapAction(action, out var opcode)) return villager;
        var existing = villager.ActionPlan?
            .Where(step => step.PromiseId != Guid.Empty)
            .ToList() ?? [];
        existing.Add(new(
            Guid.Empty,
            opcode,
            string.IsNullOrWhiteSpace(itemId) ? null : itemId,
            Math.Max(1, quantity),
            delayMinutes > 0
                ? gameSeconds + delayMinutes * 60d
                : gameSeconds,
            targetX,
            targetY,
            worldLevel,
            targetActorId));
        return villager with { ActionPlan = existing };
    }

    public static VillagerPromisePlanStep? CurrentDirective(
        VillagerState villager) =>
        villager.ActionPlan?.FirstOrDefault(step =>
            step.PromiseId == Guid.Empty);

    public static VillagerState CompleteDirective(
        VillagerState villager,
        VillagerPromisePlanStep directive)
    {
        var plan = villager.ActionPlan?.ToList() ?? [];
        var index = plan.FindIndex(step => ReferenceEquals(step, directive) ||
            step == directive);
        if (index >= 0) plan.RemoveAt(index);
        return villager with { ActionPlan = plan };
    }

    public static VillagerState FailOrRetryDirective(
        VillagerState villager,
        VillagerPromisePlanStep directive)
    {
        var plan = villager.ActionPlan?.ToList() ?? [];
        var index = plan.FindIndex(step => step == directive);
        if (index < 0) return villager;
        if (directive.Attempts + 1 >= directive.MaximumAttempts)
            plan.RemoveAt(index);
        else
            plan[index] = directive with { Attempts = directive.Attempts + 1 };
        return villager with { ActionPlan = plan };
    }

    public static VillagerState RecordDirectiveAcquisition(
        VillagerState villager, string itemId, int quantity = 1)
    {
        if (quantity <= 0 || villager.ActionPlan is not { Count: > 0 })
            return villager;
        var plan = villager.ActionPlan.ToList();
        var remaining = quantity;
        for (var index = 0; index < plan.Count && remaining > 0;)
        {
            var step = plan[index];
            if (step.PromiseId != Guid.Empty ||
                step.Action != VillagerPromisePlanAction.Collect ||
                step.ItemId is not { } plannedItem ||
                !VillagerSettlementProjectService.MatchesRequirement(
                    itemId, plannedItem))
            {
                index++;
                continue;
            }
            var applied = Math.Min(remaining, step.RemainingQuantity);
            remaining -= applied;
            var needed = step.RemainingQuantity - applied;
            if (needed <= 0) plan.RemoveAt(index);
            else plan[index++] = step with { RemainingQuantity = needed };
        }
        return villager with { ActionPlan = plan };
    }

    private static bool TryMapAction(
        string action,
        out VillagerPromisePlanAction opcode)
    {
        opcode = action switch
        {
            "follow" or "come" => VillagerPromisePlanAction.FollowActor,
            "wait" or "stop_following" => VillagerPromisePlanAction.WaitUntil,
            "go_away" or "flee" => VillagerPromisePlanAction.FleeFromTarget,
            "explore" => VillagerPromisePlanAction.ExploreArea,
            "seek_shelter" => VillagerPromisePlanAction.MoveTo,
            "rest" => VillagerPromisePlanAction.Rest,
            "seek_food" or "take_food" => VillagerPromisePlanAction.Eat,
            "craft" => VillagerPromisePlanAction.CraftItem,
            "build" or "help_build" or "light_fire" => VillagerPromisePlanAction.BuildObject,
            "cut_tree" => VillagerPromisePlanAction.CutTree,
            "mine" => VillagerPromisePlanAction.Mine,
            "fish" => VillagerPromisePlanAction.Fish,
            "cook" => VillagerPromisePlanAction.Cook,
            "dig" => VillagerPromisePlanAction.Dig,
            "withdraw" => VillagerPromisePlanAction.WithdrawItem,
            "drop" => VillagerPromisePlanAction.DepositItem,
            "give" => VillagerPromisePlanAction.Deliver,
            "attack" or "retaliate" or "defend" => VillagerPromisePlanAction.AttackTarget,
            "enter_cave" or "board_boat" => VillagerPromisePlanAction.InteractWithTarget,
            "meet" => VillagerPromisePlanAction.Rendezvous,
            "warn" or "surrender" or "call_help" or "forgive" or
                "deescalate" or "threaten" or "seek_trade" =>
                VillagerPromisePlanAction.TalkToActor,
            "gather" or "gather_sticks" or "gather_berries" or
                "gather_fibre" => VillagerPromisePlanAction.Collect,
            _ => default
        };
        return action is
            "follow" or "come" or "wait" or "stop_following" or
            "go_away" or "flee" or "explore" or "seek_shelter" or
            "rest" or "seek_food" or "take_food" or "craft" or
            "build" or "help_build" or "light_fire" or "cut_tree" or
            "mine" or "fish" or "cook" or "dig" or "withdraw" or
            "drop" or "give" or "attack" or "retaliate" or "defend" or
            "enter_cave" or "board_boat" or "meet" or "gather" or
            "gather_sticks" or "gather_berries" or "gather_fibre" or
            "warn" or "surrender" or "call_help" or "forgive" or
            "deescalate" or "threaten" or "seek_trade";
    }

    private static IReadOnlyList<VillagerPromisePlanStep> BuildPlans(
        VillagerState villager)
    {
        var result = new List<VillagerPromisePlanStep>();
        foreach (var promise in villager.Promises ?? [])
        {
            if (promise.Status != CommitmentStatus.Active) continue;
            var remaining = Math.Max(0,
                promise.TargetQuantity - promise.Progress);
            var carried = promise.ItemId is null
                ? 0
                : villager.Inventory.Count(value =>
                    value is not null &&
                    VillagerSettlementProjectService.MatchesRequirement(
                        value, promise.ItemId));
            var acquisitionRemaining = promise.Kind ==
                                       VillagerPromiseKind.GiveItem
                ? Math.Max(0, remaining - carried)
                : remaining;
            if (acquisitionRemaining > 0 && promise.ItemId is not null)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Collect,
                    promise.ItemId, acquisitionRemaining));
            if (promise.RendezvousGameSeconds is { } rendezvousAt &&
                promise.RendezvousX is { } x &&
                promise.RendezvousY is { } y)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Rendezvous,
                    promise.ItemId, remaining, rendezvousAt,
                    x, y, promise.RendezvousWorldLevel));
            else if (promise.Kind == VillagerPromiseKind.GiveItem &&
                     remaining > 0 && carried > 0)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Deliver,
                    promise.ItemId, remaining));
        }
        return result;
    }

    public static bool HasActiveWork(VillagerState villager) =>
        villager.Health > 0 && villager.Promises?.Any(promise =>
            promise.Status == CommitmentStatus.Active &&
            (promise.Progress < promise.TargetQuantity ||
             promise.RendezvousGameSeconds is not null ||
             promise.Kind == VillagerPromiseKind.GiveItem)) == true;

    public static bool HasQueuedDirective(VillagerState villager) =>
        villager.Health > 0 && CurrentDirective(villager) is not null;

    public static bool NeedsItem(VillagerState villager, string itemId) =>
        villager.Promises?.Any(promise =>
            promise.Status == CommitmentStatus.Active &&
            promise.Progress < promise.TargetQuantity &&
            promise.ItemId is { } promised &&
            VillagerSettlementProjectService.MatchesRequirement(
                itemId, promised)) == true ||
        villager.ActionPlan?.Any(step =>
            step.PromiseId == Guid.Empty &&
            step.Action == VillagerPromisePlanAction.Collect &&
            step.RemainingQuantity > 0 &&
            step.ItemId is { } planned &&
            VillagerSettlementProjectService.MatchesRequirement(
                itemId, planned)) == true;

    public static VillagerState ScheduleRendezvous(
        VillagerState villager,
        string promiseeId,
        float x,
        float y,
        int worldLevel,
        double rendezvousGameSeconds)
    {
        var promises = villager.Promises?.ToList() ?? [];
        var index = promises.FindLastIndex(value =>
            value.Status == CommitmentStatus.Active &&
            value.PromiseeId == promiseeId);
        if (index < 0) return villager;
        promises[index] = promises[index] with
        {
            RendezvousX = x,
            RendezvousY = y,
            RendezvousWorldLevel = worldLevel,
            RendezvousGameSeconds = rendezvousGameSeconds
        };
        return CompileActionPlan(villager with { Promises = promises });
    }

    public static VillagerPromisePlanStep? DueRendezvous(
        VillagerState villager,
        double gameSeconds) =>
        PlansFor(villager).FirstOrDefault(step =>
            step.Action == VillagerPromisePlanAction.Rendezvous &&
            step.ExecuteAfterGameSeconds <= gameSeconds);

    public static string? CurrentPlanDescription(
        VillagerState villager,
        double gameSeconds)
    {
        var plans = PlansFor(villager);
        var due = plans.FirstOrDefault(step =>
            step.Action == VillagerPromisePlanAction.Rendezvous &&
            step.ExecuteAfterGameSeconds <= gameSeconds);
        if (due is not null)
            return due.RemainingQuantity > 0
                ? $"Returning to the agreed meeting place; " +
                  $"still missing {due.RemainingQuantity}."
                : "Returning to the agreed meeting place.";
        var collect = plans.FirstOrDefault(step =>
            step.Action == VillagerPromisePlanAction.Collect);
        if (collect?.ItemId is { } itemId)
            return $"Collecting {collect.RemainingQuantity} " +
                   $"{ItemCatalog.Get(itemId).Name} for a promise.";
        return plans.Any(step =>
            step.Action == VillagerPromisePlanAction.Deliver)
            ? "Delivering the items promised."
            : null;
    }

    public static VillagerState RecordRendezvousReached(
        VillagerState villager,
        Guid promiseId)
    {
        var promises = villager.Promises?.ToList() ?? [];
        var index = promises.FindIndex(value => value.Id == promiseId);
        if (index < 0) return villager;
        var promise = promises[index];
        promises[index] = promise with
        {
            RendezvousX = null,
            RendezvousY = null,
            RendezvousWorldLevel = null,
            RendezvousGameSeconds = null,
            Status = promise.Progress >= promise.TargetQuantity
                ? CommitmentStatus.Fulfilled
                : CommitmentStatus.Active
        };
        return CompileActionPlan(villager with { Promises = promises });
    }
}
