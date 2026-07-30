namespace IslandRpg.Gameplay;

internal enum QuestStatus
{
    Locked,
    NotStarted,
    InProgress,
    Complete
}

internal enum QuestEventType
{
    GatherItem,
    CraftItem,
    LightCampfire,
    CatchFish,
    CookFood,
    BuildObject,
    EnterCave,
    MineOre
}

internal sealed record QuestEvent(
    QuestEventType Type,
    string? TargetId = null,
    int Amount = 1);

internal sealed record QuestObjective(
    string Id,
    string Description,
    QuestEventType EventType,
    string? TargetId = null,
    int Required = 1);

internal sealed record QuestDefinition(
    string Id,
    string Title,
    string Category,
    string Summary,
    string StartText,
    string CompletionText,
    int AdventureExperience,
    IReadOnlyList<QuestObjective> Objectives,
    string? PrerequisiteQuestId = null);

internal sealed record QuestProgress(
    string QuestId,
    QuestStatus Status,
    IReadOnlyDictionary<string, int>? ObjectiveCounts = null,
    DateTime? CompletedUtc = null);

internal sealed record QuestUpdateResult(
    IReadOnlyList<QuestProgress> Progress,
    int AdventureExperience,
    QuestDefinition? CompletedQuest = null);

internal static class QuestService
{
    public static readonly IReadOnlyList<QuestDefinition> Definitions =
    [
        new(
            "washed-ashore",
            "Washed Ashore",
            "SURVIVAL",
            "Learn to gather the island's basic natural materials.",
            "Search the shoreline and nearby trees for useful materials.",
            "I gathered the first materials I need to survive.",
            50,
            [
                new("rocks", "Pick up small rocks", QuestEventType.GatherItem, ItemIds.SmallRocks),
                new("sticks", "Gather a stick", QuestEventType.GatherItem, ItemIds.Sticks),
                new("fibres", "Gather plant fibres", QuestEventType.GatherItem, ItemIds.PlantFibres)
            ]),
        new(
            "first-light",
            "First Light",
            "FIREMAKING",
            "Build and light a campfire before darkness falls.",
            "Craft a campfire, place it, then gather what is needed to light it.",
            "I made fire and secured warmth against the island night.",
            150,
            [
                new("campfire", "Craft a campfire", QuestEventType.CraftItem, ItemIds.Campfire),
                new("light", "Light the campfire", QuestEventType.LightCampfire)
            ],
            "washed-ashore"),
        new(
            "tools-of-survival",
            "Tools of Survival",
            "CRAFTING",
            "Make the first tools needed to work the island.",
            "Use gathered stone, sticks and fibres to craft basic tools.",
            "I shaped crude materials into dependable tools.",
            200,
            [
                new("knife", "Craft a stone knife", QuestEventType.CraftItem, ItemIds.StoneKnife),
                new("axe", "Craft a stone axe", QuestEventType.CraftItem, ItemIds.StoneAxe)
            ],
            "first-light"),
        new(
            "island-provision",
            "Island Provision",
            "FOOD",
            "Catch and cook food from the island.",
            "Make a fishing net, catch a fish and cook it over a lit campfire.",
            "I proved that the island can provide more than scraps.",
            250,
            [
                new("fish", "Catch a fish", QuestEventType.CatchFish),
                new("cook", "Cook food", QuestEventType.CookFood)
            ],
            "tools-of-survival"),
        new(
            "a-place-for-everything",
            "A Place for Everything",
            "BUILDING",
            "Establish a small working camp.",
            "Craft and place a workbench and a storage chest.",
            "My camp now has a place for work and supplies.",
            300,
            [
                new("workbench", "Build a workbench", QuestEventType.BuildObject, ItemIds.Workbench),
                new("storage", "Build a storage chest", QuestEventType.BuildObject, ItemIds.StorageChest)
            ],
            "island-provision"),
        new(
            "beneath-the-surface",
            "Beneath the Surface",
            "EXPLORATION",
            "Open a route underground and recover ore.",
            "Dig a cave entrance, secure a rope and descend below the island.",
            "I descended beneath the island and returned with ore.",
            500,
            [
                new("enter", "Enter a cave", QuestEventType.EnterCave),
                new("ore", "Mine ore underground", QuestEventType.MineOre)
            ],
            "a-place-for-everything")
    ];

    public static IReadOnlyList<QuestProgress> Normalize(
        IReadOnlyList<QuestProgress>? progress)
    {
        var existing = (progress ?? [])
            .ToDictionary(value => value.QuestId, StringComparer.OrdinalIgnoreCase);
        var result = new List<QuestProgress>(Definitions.Count);
        foreach (var definition in Definitions)
        {
            if (existing.TryGetValue(definition.Id, out var saved))
            {
                result.Add(saved);
                continue;
            }
            var unlocked = definition.PrerequisiteQuestId is null ||
                           result.Any(value =>
                               value.QuestId == definition.PrerequisiteQuestId &&
                               value.Status == QuestStatus.Complete);
            result.Add(new(
                definition.Id,
                unlocked ? QuestStatus.InProgress : QuestStatus.Locked));
        }
        return UnlockAvailable(result);
    }

    public static QuestUpdateResult Apply(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        QuestEvent questEvent)
    {
        var normalized = Normalize(progress).ToArray();
        QuestDefinition? completed = null;
        for (var index = 0; index < normalized.Length; index++)
        {
            var state = normalized[index];
            if (state.Status != QuestStatus.InProgress) continue;
            var definition = Definitions.First(value => value.Id == state.QuestId);
            var matching = definition.Objectives.Where(objective =>
                objective.EventType == questEvent.Type &&
                (objective.TargetId is null ||
                 objective.TargetId.Equals(
                     questEvent.TargetId,
                     StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length == 0) continue;
            var counts = new Dictionary<string, int>(
                state.ObjectiveCounts ??
                new Dictionary<string, int>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var objective in matching)
                counts[objective.Id] = Math.Min(
                    objective.Required,
                    counts.GetValueOrDefault(objective.Id) +
                    Math.Max(0, questEvent.Amount));
            var complete = definition.Objectives.All(objective =>
                counts.GetValueOrDefault(objective.Id) >= objective.Required);
            normalized[index] = state with
            {
                Status = complete
                    ? QuestStatus.Complete
                    : QuestStatus.InProgress,
                ObjectiveCounts = counts,
                CompletedUtc = complete ? DateTime.UtcNow : null
            };
            if (!complete) continue;
            completed = definition;
            adventureExperience = Math.Min(
                AdventureService.ExperienceForLevel(
                    AdventureService.MaximumLevel),
                Math.Max(0, adventureExperience) +
                definition.AdventureExperience);
        }
        return new(
            UnlockAvailable(normalized),
            adventureExperience,
            completed);
    }

    public static QuestUpdateResult Complete(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        string questId)
    {
        var normalized = Normalize(progress).ToArray();
        var index = Array.FindIndex(
            normalized,
            value => value.QuestId.Equals(
                questId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return new(normalized, adventureExperience);
        var definition = Definitions[index];
        if (normalized[index].Status == QuestStatus.Complete)
            return new(normalized, adventureExperience);
        var counts = definition.Objectives.ToDictionary(
            objective => objective.Id,
            objective => objective.Required,
            StringComparer.OrdinalIgnoreCase);
        normalized[index] = normalized[index] with
        {
            Status = QuestStatus.Complete,
            ObjectiveCounts = counts,
            CompletedUtc = DateTime.UtcNow
        };
        adventureExperience = Math.Min(
            AdventureService.ExperienceForLevel(AdventureService.MaximumLevel),
            Math.Max(0, adventureExperience) + definition.AdventureExperience);
        return new(
            UnlockAvailable(normalized),
            adventureExperience,
            definition);
    }

    private static IReadOnlyList<QuestProgress> UnlockAvailable(
        IReadOnlyList<QuestProgress> progress)
    {
        var result = progress.ToArray();
        for (var index = 0; index < Definitions.Count; index++)
        {
            if (result[index].Status != QuestStatus.Locked) continue;
            var prerequisite = Definitions[index].PrerequisiteQuestId;
            if (prerequisite is not null &&
                result.Any(value =>
                    value.QuestId == prerequisite &&
                    value.Status == QuestStatus.Complete))
                result[index] = result[index] with
                {
                    Status = QuestStatus.InProgress
                };
        }
        return result;
    }
}
