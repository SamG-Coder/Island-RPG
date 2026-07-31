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
    CommitmentStatus Status = CommitmentStatus.Active);

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
        if (!(normalized.Contains("gather") ||
              normalized.Contains("collect") ||
              normalized.Contains("bring") ||
              normalized.Contains("get ")))
            return false;
        foreach (var token in normalized.Split(
                     [' ', ',', '.', '?', '!'],
                     StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, out var parsed))
            {
                quantity = Math.Clamp(parsed, 1, 100);
                break;
            }
        ItemDefinition? best = null;
        foreach (var item in ItemCatalog.All)
        {
            if (!item.Droppable) continue;
            var name = item.Name.ToLowerInvariant();
            if (!normalized.Contains(name) &&
                !normalized.Contains(
                    item.Id.Replace('_', ' '),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null ||
                item.Name.Length > best.Name.Length)
                best = item;
        }
        if (best is null)
        {
            if (normalized.Contains("wood"))
                best = ItemCatalog.Get(ItemIds.Logs);
            else
                return false;
        }
        itemId = best.Id;
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
        return state with { Promises = promises };
    }

    public static VillagerState RecordAcquiredItem(
        VillagerState state,
        string itemId,
        int quantity = 1)
    {
        if (quantity <= 0 ||
            state.Promises is not { Count: > 0 })
            return state;
        List<VillagerPromise>? updated = null;
        for (var index = 0;
             index < state.Promises.Count &&
             quantity > 0;
             index++)
        {
            var promise = state.Promises[index];
            if (promise.Status != CommitmentStatus.Active ||
                promise.Kind != VillagerPromiseKind.GatherItem ||
                !string.Equals(
                    promise.ItemId, itemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            updated ??= state.Promises.ToList();
            var applied = Math.Min(
                quantity,
                promise.TargetQuantity - promise.Progress);
            var progress = promise.Progress + applied;
            updated[index] = promise with
            {
                Progress = progress,
                Status = progress >= promise.TargetQuantity
                    ? CommitmentStatus.Fulfilled
                    : CommitmentStatus.Active
            };
            quantity -= applied;
        }
        return updated is null
            ? state
            : state with { Promises = updated };
    }

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
            : state with { Promises = updated };
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
