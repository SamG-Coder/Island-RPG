namespace IslandRpg.Gameplay;

/// <summary>
/// Converts nearby carried tools into durable, actor-specific knowledge.
/// </summary>
internal static class VillagerCapabilityMemory
{
    public const string ObservedToolKind = "observed-tool";

    public static IReadOnlyList<string> VisibleTools(string?[]? inventory) =>
        (inventory ?? [])
        .Where(itemId => itemId is not null)
        .Select(itemId => itemId!)
        .Where(itemId =>
            ItemCatalog.TryGet(itemId, out var item) &&
            item.HasTag(ItemTag.Tool))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static VillagerState Observe(
        VillagerState observer,
        string subjectId,
        string subjectName,
        IReadOnlyList<string>? visibleToolIds,
        float distance,
        double gameSeconds)
    {
        if (subjectId == observer.Id ||
            distance > VillagerSimulation.SocialRange ||
            visibleToolIds is not { Count: > 0 })
            return observer;

        var memories = observer.Memories?.ToList() ?? [];
        var changed = false;
        foreach (var toolId in visibleToolIds
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ItemCatalog.TryGet(toolId, out var tool) ||
                !tool.HasTag(ItemTag.Tool))
                continue;
            var index = memories.FindIndex(memory =>
                memory.Kind == ObservedToolKind &&
                memory.SubjectId == subjectId &&
                string.Equals(
                    memory.ItemId, toolId,
                    StringComparison.OrdinalIgnoreCase));
            var summary = $"{subjectName} was carrying {tool.Name}.";
            if (index >= 0)
            {
                var existing = memories[index];
                memories[index] = existing with
                {
                    Confidence = Math.Min(1, existing.Confidence + .08f),
                    GameSeconds = gameSeconds,
                    Summary = summary
                };
            }
            else
                memories.Add(new(
                    Guid.NewGuid(),
                    ObservedToolKind,
                    subjectId,
                    null,
                    .9f,
                    gameSeconds,
                    Summary: summary,
                    ItemId: toolId));
            changed = true;
        }
        if (!changed) return observer;
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - VillagerSimulation.MaximumMemories);
        return observer with { Memories = memories };
    }

    public static IReadOnlyList<string> KnownTools(
        VillagerState observer, string subjectId) =>
        observer.Memories?
            .Where(memory =>
                memory.Kind == ObservedToolKind &&
                memory.SubjectId == subjectId &&
                memory.Confidence >= .35f &&
                memory.ItemId is not null)
            .OrderByDescending(memory => memory.GameSeconds)
            .Select(memory => memory.ItemId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}
