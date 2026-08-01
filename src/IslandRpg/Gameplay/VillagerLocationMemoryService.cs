using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum VillagerLocationType : byte
{
    FoodSource,
    WoodSource,
    FishingSpot,
    Storage,
    Danger
}

internal sealed record VillagerLocationMemory(
    float PositionX,
    float PositionY,
    int WorldLevel,
    VillagerLocationType Type,
    float Confidence,
    double LastObservedGameSeconds);

internal static class VillagerLocationMemoryService
{
    public const int MaximumMemories = 32;
    public const float DiscoveryConfidence = .85f;
    public const float EmptyConfidencePenalty = .5f;
    public const float MinimumUsefulConfidence = .2f;
    public const float MatchRadius = 2f;
    public const int MaximumFailedLocations = 16;
    public const double FailedLocationRetryGameSeconds = 15 * 60;
    private const float ResourceDecayPerGameDay = .12f;
    private const float DangerRadius = 4f;

    public static VillagerState Remember(
        VillagerState state,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel,
        double gameSeconds,
        float confidence = DiscoveryConfidence,
        bool clearFailedLocation = true)
    {
        if (state.Health <= 0) return state;
        var memories = state.LocationMemories?.ToList() ?? [];
        var index = memories.FindIndex(memory =>
            memory.Type == type &&
            memory.WorldLevel == worldLevel &&
            Vector2.DistanceSquared(
                new(memory.PositionX, memory.PositionY), position) <=
            MatchRadius * MatchRadius);
        var refreshed = new VillagerLocationMemory(
            position.X,
            position.Y,
            worldLevel,
            type,
            Math.Clamp(confidence, 0, 1),
            gameSeconds);
        if (index >= 0)
            memories[index] = refreshed with
            {
                Confidence = Math.Max(
                    refreshed.Confidence,
                    ConfidenceAt(memories[index], gameSeconds))
            };
        else
            memories.Add(refreshed);
        if (memories.Count > MaximumMemories)
            memories = memories
                .OrderByDescending(memory =>
                    RetentionScore(memory, gameSeconds))
                .Take(MaximumMemories)
                .ToList();
        return state with
        {
            LocationMemories = memories,
            FailedLocations = clearFailedLocation
                ? RemoveMatchingFailure(
                    state.FailedLocations, type, position, worldLevel,
                    gameSeconds)
                : state.FailedLocations
        };
    }

    public static VillagerState ObserveWorldObjects(
        VillagerState state,
        ReadOnlySpan<VillagerWorldObject> objects,
        double gameSeconds)
    {
        var observed = state;
        for (var index = 0; index < objects.Length; index++)
        {
            ref readonly var item = ref objects[index];
            if (Vector2.DistanceSquared(
                    new(state.PositionX, state.PositionY),
                    item.Position) >
                VillagerSimulation.ResourceSearchRadius *
                VillagerSimulation.ResourceSearchRadius)
                continue;
            var type = item.IsStorage
                ? VillagerLocationType.Storage
                : LocationTypeForItem(item.ItemId);
            if (type is null) continue;
            observed = Remember(
                observed,
                type.Value,
                item.Position,
                state.WorldLevel,
                gameSeconds,
                clearFailedLocation: false);
        }
        return observed;
    }

    public static VillagerLocationMemory? SelectUsefulLocation(
        VillagerState state,
        double gameSeconds,
        bool storageOnly = false)
    {
        if (state.Health <= 0 || state.LocationMemories is not { Count: > 0 })
            return null;
        var position = new Vector2(state.PositionX, state.PositionY);
        VillagerLocationMemory? best = null;
        var bestScore = float.MinValue;
        foreach (var memory in state.LocationMemories)
        {
            if (memory.Type == VillagerLocationType.Danger ||
                storageOnly && memory.Type != VillagerLocationType.Storage ||
                memory.WorldLevel != state.WorldLevel)
                continue;
            if (IsTemporarilyFailed(state, memory, gameSeconds))
                continue;
            var confidence = ConfidenceAt(memory, gameSeconds);
            if (confidence < MinimumUsefulConfidence) continue;
            var target = new Vector2(memory.PositionX, memory.PositionY);
            if (!CanVisit(state, target, gameSeconds)) continue;
            var score = confidence * 100 +
                        NeedBonus(state, memory.Type) -
                        Vector2.Distance(position, target);
            if (score <= bestScore) continue;
            bestScore = score;
            best = memory;
        }
        return best;
    }

    public static bool CanVisit(
        VillagerState state,
        Vector2 position,
        double gameSeconds)
    {
        if (IsUrgent(state)) return true;
        return state.LocationMemories?.Any(memory =>
            memory.Type == VillagerLocationType.Danger &&
            memory.WorldLevel == state.WorldLevel &&
            ConfidenceAt(memory, gameSeconds) >= MinimumUsefulConfidence &&
            Vector2.DistanceSquared(
                new(memory.PositionX, memory.PositionY), position) <=
            DangerRadius * DangerRadius) != true;
    }

    public static VillagerState ObserveEmpty(
        VillagerState state,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel,
        double gameSeconds)
    {
        if (state.LocationMemories is not { Count: > 0 }) return state;
        var memories = state.LocationMemories.ToList();
        var index = memories.FindIndex(memory =>
            memory.Type == type &&
            memory.WorldLevel == worldLevel &&
            Vector2.DistanceSquared(
                new(memory.PositionX, memory.PositionY), position) <=
            MatchRadius * MatchRadius);
        if (index < 0) return state;
        var memory = memories[index];
        memories[index] = memory with
        {
            Confidence = Math.Max(
                0,
                ConfidenceAt(memory, gameSeconds) -
                EmptyConfidencePenalty),
            LastObservedGameSeconds = gameSeconds
        };
        return state with { LocationMemories = memories };
    }

    public static VillagerState MarkUnreachable(
        VillagerState state,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel,
        double gameSeconds)
    {
        if (state.Health <= 0) return state;
        state = ObserveEmpty(
            state, type, position, worldLevel, gameSeconds);
        var failures = (state.FailedLocations ?? [])
            .Where(value => value.RetryAfterGameSeconds > gameSeconds)
            .ToList();
        var index = failures.FindIndex(value =>
            Matches(value, type, position, worldLevel));
        var failureCount = index < 0 ? 1 : failures[index].Failures + 1;
        var retryAfter = gameSeconds + FailedLocationRetryGameSeconds *
            Math.Min(4, failureCount);
        var failed = new VillagerFailedLocation(
            position.X, position.Y, worldLevel, type,
            retryAfter, failureCount);
        if (index < 0) failures.Add(failed);
        else failures[index] = failed;
        if (failures.Count > MaximumFailedLocations)
            failures = failures
                .OrderByDescending(value => value.RetryAfterGameSeconds)
                .Take(MaximumFailedLocations)
                .ToList();
        return state with { FailedLocations = failures };
    }

    public static bool IsTemporarilyFailed(
        VillagerState state,
        VillagerLocationMemory memory,
        double gameSeconds) =>
        state.FailedLocations?.Any(value =>
            value.RetryAfterGameSeconds > gameSeconds &&
            Matches(
                value,
                memory.Type,
                new(memory.PositionX, memory.PositionY),
                memory.WorldLevel)) == true;

    public static float ConfidenceAt(
        VillagerLocationMemory memory,
        double gameSeconds)
    {
        var elapsedDays = Math.Max(
            0,
            gameSeconds - memory.LastObservedGameSeconds) /
            (24 * 60 * 60);
        var decay = memory.Type == VillagerLocationType.Danger
            ? ResourceDecayPerGameDay * .25f
            : ResourceDecayPerGameDay;
        return Math.Clamp(
            memory.Confidence - (float)elapsedDays * decay,
            0,
            1);
    }

    public static VillagerLocationType? LocationTypeForItem(string itemId)
    {
        if (!ItemCatalog.TryGet(itemId, out var item)) return null;
        if (SurvivalService.TryFoodEffect(itemId, out _) ||
            item.HasTag(ItemTag.Berry) || item.HasTag(ItemTag.Fish))
            return VillagerLocationType.FoodSource;
        if (item.HasTag(ItemTag.Log) ||
            item.HasTag(ItemTag.WoodcuttingMaterial))
            return VillagerLocationType.WoodSource;
        return null;
    }

    private static bool IsUrgent(VillagerState state) =>
        state.Hunger <= 35 ||
        state.Health <= 20 ||
        state.Need == VillagerNeed.Safe ||
        state.ConflictIntent != VillagerConflictIntent.None;

    private static float NeedBonus(
        VillagerState state,
        VillagerLocationType type) => type switch
    {
        VillagerLocationType.FoodSource when
            state.Hunger <= 60 || state.WorkRole == VillagerWorkRole.Food => 35,
        VillagerLocationType.FishingSpot when
            state.Hunger <= 60 || state.WorkRole == VillagerWorkRole.Food => 30,
        VillagerLocationType.WoodSource when
            state.WorkRole == VillagerWorkRole.Wood => 30,
        VillagerLocationType.Storage when
            PlayerInventory.Count(state.Inventory) >=
            VillagerSimulation.StorageDepositThreshold => 30,
        _ => 0
    };

    private static float RetentionScore(
        VillagerLocationMemory memory,
        double gameSeconds) =>
        ConfidenceAt(memory, gameSeconds) * 1000 +
        (float)Math.Min(gameSeconds, memory.LastObservedGameSeconds) /
        (24 * 60 * 60);

    private static IReadOnlyList<VillagerFailedLocation>? RemoveMatchingFailure(
        IReadOnlyList<VillagerFailedLocation>? failures,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel,
        double gameSeconds)
    {
        if (failures is not { Count: > 0 }) return failures;
        var remaining = failures.Where(value =>
                value.RetryAfterGameSeconds > gameSeconds &&
                !Matches(value, type, position, worldLevel))
            .ToArray();
        return remaining.Length == 0 ? null : remaining;
    }

    private static bool Matches(
        VillagerFailedLocation failure,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel) =>
        failure.Type == type &&
        failure.WorldLevel == worldLevel &&
        Vector2.DistanceSquared(
            new(failure.PositionX, failure.PositionY), position) <=
        MatchRadius * MatchRadius;
}
