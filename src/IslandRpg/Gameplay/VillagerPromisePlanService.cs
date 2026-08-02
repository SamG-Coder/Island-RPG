namespace IslandRpg.Gameplay;

internal enum VillagerPromisePlanAction : byte
{
    Collect,
    Deliver,
    Rendezvous
}

internal sealed record VillagerPromisePlanStep(
    Guid PromiseId,
    VillagerPromisePlanAction Action,
    string? ItemId,
    int RemainingQuantity,
    double ExecuteAfterGameSeconds = 0,
    float? TargetX = null,
    float? TargetY = null,
    int? WorldLevel = null);

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
        var result = new List<VillagerPromisePlanStep>();
        foreach (var promise in villager.Promises)
        {
            if (promise.Status != CommitmentStatus.Active) continue;
            var remaining = Math.Max(0,
                promise.TargetQuantity - promise.Progress);
            if (remaining > 0 && promise.ItemId is not null)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Collect,
                    promise.ItemId, remaining));
            if (promise.RendezvousGameSeconds is { } rendezvousAt &&
                promise.RendezvousX is { } x &&
                promise.RendezvousY is { } y)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Rendezvous,
                    promise.ItemId, remaining, rendezvousAt,
                    x, y, promise.RendezvousWorldLevel));
            else if (promise.Kind == VillagerPromiseKind.GiveItem &&
                     remaining == 0)
                result.Add(new(
                    promise.Id, VillagerPromisePlanAction.Deliver,
                    promise.ItemId, 0));
        }
        return result;
    }

    public static bool HasActiveWork(VillagerState villager) =>
        villager.Health > 0 && villager.Promises?.Any(promise =>
            promise.Status == CommitmentStatus.Active &&
            (promise.Progress < promise.TargetQuantity ||
             promise.RendezvousGameSeconds is not null ||
             promise.Kind == VillagerPromiseKind.GiveItem)) == true;

    public static bool NeedsItem(VillagerState villager, string itemId) =>
        villager.Promises?.Any(promise =>
            promise.Status == CommitmentStatus.Active &&
            promise.Progress < promise.TargetQuantity &&
            promise.ItemId is { } promised &&
            VillagerSettlementProjectService.MatchesRequirement(
                itemId, promised)) == true;

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
        return villager with { Promises = promises };
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
        return villager with { Promises = promises };
    }
}
