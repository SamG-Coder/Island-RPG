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
    private const float ResourceDecayPerGameDay = .12f;
    private const float DangerRadius = 4f;

    public static VillagerState Remember(
        VillagerState state,
        VillagerLocationType type,
        Vector2 position,
        int worldLevel,
        double gameSeconds,
        float confidence = DiscoveryConfidence)
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
        return state with { LocationMemories = memories };
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
                gameSeconds);
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
}
