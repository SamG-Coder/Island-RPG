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
            "Gather enough shoreline material to make tools and a fire ring.",
            "Search the dark shoreline carefully for stone, sticks and fibre.",
            "I gathered the first materials I need to survive.",
            50,
            [
                new("rocks", "Gather small rocks", QuestEventType.GatherItem, ItemIds.SmallRocks, 3),
                new("large-rocks", "Gather large rocks", QuestEventType.GatherItem, ItemIds.LargeRock, 3),
                new("sticks", "Gather sticks", QuestEventType.GatherItem, ItemIds.Sticks, 2),
                new("fibres", "Gather plant fibres", QuestEventType.GatherItem, ItemIds.PlantFibres, 2)
            ]),
        new(
            "tools-of-survival",
            "Tools of Survival",
            "CRAFTING",
            "Shape the tools needed to cut fuel and prepare food.",
            "Knapp two sharp stones, then bind a knife and an axe.",
            "I shaped crude materials into dependable tools.",
            200,
            [
                new("sharp-rock", "Craft sharpened rocks", QuestEventType.CraftItem, ItemIds.SharpenedRock, 2),
                new("knife", "Craft a stone knife", QuestEventType.CraftItem, ItemIds.StoneKnife),
                new("axe", "Craft a stone axe", QuestEventType.CraftItem, ItemIds.StoneAxe)
            ],
            "washed-ashore"),
        new(
            "first-light",
            "First Light",
            "FIREMAKING",
            "Build and light a campfire while the shore is still dark.",
            "Cut a log with the axe, place a stone fire ring, add fuel and light it.",
            "I made fire and secured warmth against the island night.",
            150,
            [
                new("campfire", "Craft a campfire", QuestEventType.CraftItem, ItemIds.Campfire),
                new("light", "Place, fuel and light the campfire", QuestEventType.LightCampfire)
            ],
            "tools-of-survival"),
        new(
            "island-provision",
            "Island Provision",
            "FOOD",
            "Catch and cook food from the island.",
            "Make a fishing net, catch a fish and cook it over a lit campfire.",
            "I proved that the island can provide more than scraps.",
            250,
            [
                new("net", "Craft a primitive fishing net", QuestEventType.CraftItem, ItemIds.PrimitiveFishingNet),
                new("fish", "Catch a fish", QuestEventType.CatchFish),
                new("cook", "Cook food", QuestEventType.CookFood)
            ],
            "first-light"),
        new(
            "a-place-for-everything",
            "A Place for Everything",
            "BUILDING",
            "Establish a small working camp.",
            "Prepare a hammer and timber, then place a workbench and storage chest.",
            "My camp now has a place for work and supplies.",
            300,
            [
                new("hammer", "Craft a stone hammer", QuestEventType.CraftItem, ItemIds.StoneHammer),
                new("planks", "Carve planks", QuestEventType.CraftItem, ItemIds.Plank, 10),
                new("workbench", "Build a workbench", QuestEventType.BuildObject, ItemIds.Workbench),
                new("storage", "Build a storage chest", QuestEventType.BuildObject, ItemIds.StorageChest)
            ],
            "island-provision"),
        new(
            "beneath-the-surface",
            "Beneath the Surface",
            "EXPLORATION",
            "Open a route underground and recover ore.",
            "Prepare digging and mining tools, secure a rope and descend below the island.",
            "I descended beneath the island and returned with ore.",
            500,
            [
                new("rope", "Craft a rope", QuestEventType.CraftItem, ItemIds.Rope),
                new("shovel", "Craft a stone shovel", QuestEventType.CraftItem, ItemIds.StoneShovel),
                new("pickaxe", "Craft a stone pickaxe", QuestEventType.CraftItem, ItemIds.StonePickaxe),
                new("enter", "Dig and enter a cave", QuestEventType.EnterCave),
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

    public static (QuestDefinition Definition, QuestProgress Progress)?
        ActiveQuest(IReadOnlyList<QuestProgress>? progress)
    {
        var normalized = Normalize(progress);
        for (var index = 0; index < Definitions.Count; index++)
            if (normalized[index].Status == QuestStatus.InProgress)
                return (Definitions[index], normalized[index]);
        return null;
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
