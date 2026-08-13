using System.Collections;
using System.Collections.Immutable;

namespace IslandRpg.Gameplay;

public enum QuestStatus : byte
{
    Locked,
    NotStarted,
    InProgress,
    Complete
}

public enum QuestEventType : byte
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

/// <summary>
/// A typed fact emitted only after its owning gameplay transaction commits.
/// Amount is a positive authoritative delta, never a client-provided total.
/// </summary>
public sealed record QuestEvent(
    QuestEventType Type,
    string? TargetId = null,
    int Amount = 1);

public sealed record QuestObjective(
    string Id,
    string Description,
    QuestEventType EventType,
    string? TargetId = null,
    int Required = 1);

public sealed record QuestDefinition(
    string Id,
    string Title,
    string Category,
    string Summary,
    string StartText,
    string CompletionText,
    int AdventureExperience,
    IReadOnlyList<QuestObjective> Objectives,
    string? PrerequisiteQuestId = null);

public readonly record struct QuestObjectiveCount(
    string ObjectiveId,
    int Count);

/// <summary>
/// Sparse immutable quest counters stored in definition order. The public
/// dictionary surface preserves existing journal and save call sites while
/// canonical authority state remains compact and cannot be mutated.
/// </summary>
public sealed class QuestObjectiveCounts :
    IReadOnlyDictionary<string, int>,
    IEquatable<QuestObjectiveCounts>
{
    private readonly ImmutableArray<QuestObjectiveCount> _entries;

    internal QuestObjectiveCounts(
        ImmutableArray<QuestObjectiveCount> entries) => _entries = entries;

    public static QuestObjectiveCounts Empty { get; } = new([]);

    public ImmutableArray<QuestObjectiveCount> Entries => _entries;

    public int Count => _entries.Length;

    public IEnumerable<string> Keys =>
        _entries.Select(static value => value.ObjectiveId);

    public IEnumerable<int> Values =>
        _entries.Select(static value => value.Count);

    public int this[string key] => TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException(
            $"Quest objective '{key}' has no recorded progress.");

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out int value)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var entry in _entries)
        {
            if (!string.Equals(
                    entry.ObjectiveId, key, StringComparison.Ordinal))
                continue;
            value = entry.Count;
            return true;
        }
        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
    {
        foreach (var entry in _entries)
            yield return new(entry.ObjectiveId, entry.Count);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(QuestObjectiveCounts? other) =>
        other is not null && _entries.SequenceEqual(other._entries);

    public override bool Equals(object? obj) =>
        obj is QuestObjectiveCounts other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries)
        {
            hash.Add(entry.ObjectiveId, StringComparer.Ordinal);
            hash.Add(entry.Count);
        }
        return hash.ToHashCode();
    }

    internal static QuestObjectiveCounts Normalize(
        QuestDefinition definition,
        IReadOnlyDictionary<string, int>? values)
    {
        if (values is null || values.Count == 0) return Empty;
        if (values.Count > definition.Objectives.Count)
            throw Invalid(
                $"Quest '{definition.Id}' has too many objective counters.");

        var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw Invalid(
                    $"Quest '{definition.Id}' contains an empty objective ID.");
            var objective = definition.Objectives.FirstOrDefault(value =>
                string.Equals(
                    value.Id, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (objective is null)
                throw Invalid(
                    $"Quest '{definition.Id}' contains unknown objective '{pair.Key}'.");
            if (!canonical.TryAdd(objective.Id, pair.Value))
                throw Invalid(
                    $"Quest '{definition.Id}' contains duplicate objective '{objective.Id}'.");
            if (pair.Value < 0 || pair.Value > objective.Required)
                throw Invalid(
                    $"Quest objective '{objective.Id}' has an invalid count.");
        }

        var entries = ImmutableArray.CreateBuilder<QuestObjectiveCount>(
            canonical.Count);
        foreach (var objective in definition.Objectives)
            if (canonical.TryGetValue(objective.Id, out var count) && count > 0)
                entries.Add(new(objective.Id, count));
        return entries.Count == 0 ? Empty : new(entries.MoveToImmutable());
    }

    internal static QuestObjectiveCounts Required(
        QuestDefinition definition)
    {
        var entries = ImmutableArray.CreateBuilder<QuestObjectiveCount>(
            definition.Objectives.Count);
        foreach (var objective in definition.Objectives)
            entries.Add(new(objective.Id, objective.Required));
        return new(entries.MoveToImmutable());
    }

    private static InvalidDataException Invalid(string detail) => new(detail);
}

/// <summary>
/// Durable quest state. CompletionTick is an authoritative simulation tick;
/// -1 is the sole canonical value for an incomplete quest.
/// </summary>
public sealed record QuestProgress(
    string QuestId,
    QuestStatus Status,
    IReadOnlyDictionary<string, int>? ObjectiveCounts = null,
    long CompletionTick = -1)
{
    public const long IncompleteTick = -1;
}

public sealed record QuestUpdateResult(
    ImmutableArray<QuestProgress> Progress,
    int AdventureExperience,
    QuestDefinition? CompletedQuest = null,
    bool Changed = false,
    int AdventureExperienceGained = 0)
{
    public bool Completed => CompletedQuest is not null;
}

/// <summary>
/// Dependency-free deterministic quest authority. It consumes typed committed
/// gameplay events and never reads wall-clock time or infers transactions from
/// mutable inventory state.
/// </summary>
public static class QuestService
{
    public const int MaximumEventAmount = 1_000_000;
    public const int MaximumIdentifierLength = 128;

    public static IReadOnlyList<QuestDefinition> Definitions { get; } =
        ImmutableArray.Create<QuestDefinition>(
    [
        new(
            "washed-ashore",
            "Washed Ashore",
            "SURVIVAL",
            "Gather naturally occurring shoreline materials for primitive tools.",
            "Search the dark shoreline for large rocks, sticks and plant fibre.",
            "I gathered the first materials I need to survive.",
            50,
            [
                new("large-rocks", "Gather large rocks", QuestEventType.GatherItem, ItemIds.LargeRock, 5),
                new("sticks", "Gather sticks", QuestEventType.GatherItem, ItemIds.Sticks, 2),
                new("fibres", "Gather plant fibres", QuestEventType.GatherItem, ItemIds.PlantFibres, 2)
            ]),
        new(
            "tools-of-survival",
            "Tools of Survival",
            "CRAFTING",
            "Shape the tools needed to cut fuel and prepare food.",
            "Break down large stone, knapp two sharp edges, then bind a knife and axe.",
            "I shaped crude materials into dependable tools.",
            200,
            [
                new("medium-rocks", "Make medium rocks", QuestEventType.CraftItem, ItemIds.MediumRock, 8),
                new("sharp-rock", "Craft sharpened rocks", QuestEventType.CraftItem, ItemIds.SharpenedRock, 2),
                new("knife", "Craft a stone knife", QuestEventType.CraftItem, ItemIds.StoneKnife),
                new("axe", "Craft a stone axe", QuestEventType.CraftItem, ItemIds.StoneAxe),
                new("small-rocks", "Make small rocks", QuestEventType.CraftItem, ItemIds.SmallRocks, 4)
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
    ]);

    private static readonly IReadOnlyDictionary<string, int> DefinitionIndices =
        CreateDefinitionIndices();

    static QuestService() => ValidateDefinitions();

    /// <summary>
    /// Converts persisted or external state into definition-order canonical
    /// state. Unknown IDs, invalid counters, impossible dependency completion,
    /// and ambiguous duplicates are rejected rather than silently trusted.
    /// </summary>
    public static ImmutableArray<QuestProgress> Normalize(
        IReadOnlyList<QuestProgress>? progress)
    {
        var existing = new Dictionary<string, QuestProgress>(
            StringComparer.OrdinalIgnoreCase);
        if (progress is not null)
        {
            if (progress.Count > Definitions.Count)
                throw Invalid("Quest progress exceeds the definition count.");
            foreach (var state in progress)
            {
                if (state is null ||
                    !DefinitionIndices.TryGetValue(state.QuestId, out var index))
                    throw Invalid("Quest progress contains an unknown quest ID.");
                var canonicalId = Definitions[index].Id;
                if (!existing.TryAdd(canonicalId, state))
                    throw Invalid(
                        $"Quest progress contains duplicate quest '{canonicalId}'.");
                if (!Enum.IsDefined(state.Status))
                    throw Invalid(
                        $"Quest '{canonicalId}' has an invalid status.");
            }
        }

        var result = ImmutableArray.CreateBuilder<QuestProgress>(
            Definitions.Count);
        foreach (var definition in Definitions)
        {
            existing.TryGetValue(definition.Id, out var saved);
            var counts = QuestObjectiveCounts.Normalize(
                definition, saved?.ObjectiveCounts);
            var unlocked = definition.PrerequisiteQuestId is null ||
                           result.Any(value =>
                               value.QuestId == definition.PrerequisiteQuestId &&
                               value.Status == QuestStatus.Complete);
            var savedStatus = saved?.Status ?? QuestStatus.NotStarted;
            QuestStatus status;
            long completionTick;
            if (savedStatus == QuestStatus.Complete)
            {
                if (!unlocked)
                    throw Invalid(
                        $"Quest '{definition.Id}' completed before its prerequisite.");
                if (!ObjectivesComplete(definition, counts))
                    throw Invalid(
                        $"Completed quest '{definition.Id}' has incomplete objectives.");
                status = QuestStatus.Complete;
                // Zero is the deterministic migration value for a legacy
                // completed record that predates simulation ticks.
                completionTick = Math.Max(0, saved?.CompletionTick ?? 0);
            }
            else if (unlocked)
            {
                if (ObjectivesComplete(definition, counts))
                    throw Invalid(
                        $"Quest '{definition.Id}' has completed counters but no completion state.");
                status = QuestStatus.InProgress;
                completionTick = QuestProgress.IncompleteTick;
            }
            else
            {
                if (counts.Count != 0)
                    throw Invalid(
                        $"Locked quest '{definition.Id}' contains objective progress.");
                status = QuestStatus.Locked;
                completionTick = QuestProgress.IncompleteTick;
            }
            result.Add(new(
                definition.Id, status, counts, completionTick));
        }
        return result.MoveToImmutable();
    }

    /// <summary>
    /// Rejects any noncanonical representation, including mutable dictionaries,
    /// non-definition ordering, legacy statuses, and noncanonical tick sentinels.
    /// </summary>
    public static void Validate(IReadOnlyList<QuestProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var canonical = Normalize(progress);
        if (progress.Count != canonical.Length)
            throw Invalid("Canonical quest progress must contain every quest.");
        for (var index = 0; index < canonical.Length; index++)
        {
            var provided = progress[index];
            var expected = canonical[index];
            if (!string.Equals(
                    provided.QuestId, expected.QuestId,
                    StringComparison.Ordinal) ||
                provided.Status != expected.Status ||
                provided.CompletionTick != expected.CompletionTick ||
                provided.ObjectiveCounts is not QuestObjectiveCounts counts ||
                !counts.Equals(expected.ObjectiveCounts as QuestObjectiveCounts))
                throw Invalid(
                    $"Quest '{expected.QuestId}' is not canonical.");
        }
    }

    public static bool TryValidate(
        IReadOnlyList<QuestProgress>? progress,
        out string? detail)
    {
        try
        {
            if (progress is null)
                throw Invalid("Quest progress is required.");
            Validate(progress);
            detail = null;
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or
                                      ArgumentException)
        {
            detail = error.Message;
            return false;
        }
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

    /// <summary>
    /// Derives bounded item-progress facts from an already trusted inventory.
    /// Multiplayer authority may use this only after a committed server-owned
    /// inventory transition; clients never supply these totals.
    /// </summary>
    public static ImmutableArray<QuestEvent> InventoryProgressEvents(
        IReadOnlyList<QuestProgress>? progress,
        string?[]? inventory,
        IReadOnlyList<int>? quantities = null)
    {
        if (ActiveQuest(progress) is not { } active) return [];
        var result = ImmutableArray.CreateBuilder<QuestEvent>();
        foreach (var objective in active.Definition.Objectives)
        {
            if (objective.TargetId is null ||
                objective.EventType is not (
                    QuestEventType.GatherItem or QuestEventType.CraftItem))
                continue;
            var held = quantities is null
                ? PlayerInventory.Count(inventory, objective.TargetId)
                : PlayerInventory.Count(
                    inventory, quantities, objective.TargetId);
            var recorded = active.Progress.ObjectiveCounts?
                .GetValueOrDefault(objective.Id) ?? 0;
            var missing = Math.Min(objective.Required, held) - recorded;
            if (missing > 0)
                result.Add(new(
                    objective.EventType, objective.TargetId, missing));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Solo-compatible adapter. It uses a deterministic logical completion
    /// ordinal, never wall-clock time. Authority should call the tick overload.
    /// </summary>
    public static QuestUpdateResult Apply(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        QuestEvent questEvent) => Apply(
            progress,
            adventureExperience,
            questEvent,
            NextSoloCompletionTick(progress));

    public static QuestUpdateResult Apply(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        QuestEvent questEvent,
        long completionTick)
    {
        ArgumentNullException.ThrowIfNull(questEvent);
        ValidateAdventureExperience(adventureExperience);
        ValidateEvent(questEvent);
        if (completionTick < 0)
            throw new ArgumentOutOfRangeException(nameof(completionTick));

        var normalized = Normalize(progress);
        var activeIndex = -1;
        for (var index = 0; index < normalized.Length; index++)
            if (normalized[index].Status == QuestStatus.InProgress)
            {
                activeIndex = index;
                break;
            }
        if (activeIndex < 0)
            return new(normalized, adventureExperience);

        var state = normalized[activeIndex];
        var definition = Definitions[activeIndex];
        var matching = definition.Objectives.Where(objective =>
            objective.EventType == questEvent.Type &&
            (objective.TargetId is null ||
             string.Equals(
                 objective.TargetId,
                 questEvent.TargetId,
                 StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matching.Length == 0)
            return new(normalized, adventureExperience);

        var values = state.ObjectiveCounts!.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        var changed = false;
        foreach (var objective in matching)
        {
            var current = values.GetValueOrDefault(objective.Id);
            var remaining = objective.Required - current;
            if (remaining <= 0) continue;
            values[objective.Id] = current + Math.Min(
                remaining, questEvent.Amount);
            changed = true;
        }
        if (!changed)
            return new(normalized, adventureExperience);

        var counters = QuestObjectiveCounts.Normalize(definition, values);
        var complete = ObjectivesComplete(definition, counters);
        var updated = state with
        {
            Status = complete
                ? QuestStatus.Complete
                : QuestStatus.InProgress,
            ObjectiveCounts = counters,
            CompletionTick = complete
                ? completionTick
                : QuestProgress.IncompleteTick
        };
        normalized = normalized.SetItem(activeIndex, updated);

        QuestDefinition? completedQuest = null;
        var gained = 0;
        if (complete)
        {
            completedQuest = definition;
            var maximum = AdventureService.ExperienceForLevel(
                AdventureService.MaximumLevel);
            var nextExperience = Math.Min(
                maximum,
                checked((long)adventureExperience +
                        definition.AdventureExperience));
            gained = checked((int)nextExperience - adventureExperience);
            adventureExperience = checked((int)nextExperience);
            normalized = UnlockAvailable(normalized);
        }
        return new(
            normalized,
            adventureExperience,
            completedQuest,
            Changed: true,
            AdventureExperienceGained: gained);
    }

    /// <summary>
    /// Solo/debug adapter using the same deterministic logical tick policy as
    /// the three-argument Apply overload.
    /// </summary>
    public static QuestUpdateResult Complete(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        string questId) => Complete(
            progress,
            adventureExperience,
            questId,
            NextSoloCompletionTick(progress));

    public static QuestUpdateResult Complete(
        IReadOnlyList<QuestProgress>? progress,
        int adventureExperience,
        string questId,
        long completionTick)
    {
        ValidateAdventureExperience(adventureExperience);
        if (completionTick < 0)
            throw new ArgumentOutOfRangeException(nameof(completionTick));
        var normalized = Normalize(progress);
        if (string.IsNullOrWhiteSpace(questId) ||
            !DefinitionIndices.TryGetValue(questId, out var index) ||
            normalized[index].Status != QuestStatus.InProgress)
            return new(normalized, adventureExperience);

        var definition = Definitions[index];
        normalized = normalized.SetItem(index, normalized[index] with
        {
            Status = QuestStatus.Complete,
            ObjectiveCounts = QuestObjectiveCounts.Required(definition),
            CompletionTick = completionTick
        });
        var maximum = AdventureService.ExperienceForLevel(
            AdventureService.MaximumLevel);
        var nextExperience = Math.Min(
            maximum,
            checked((long)adventureExperience +
                    definition.AdventureExperience));
        var gained = checked((int)nextExperience - adventureExperience);
        adventureExperience = checked((int)nextExperience);
        return new(
            UnlockAvailable(normalized),
            adventureExperience,
            definition,
            Changed: true,
            AdventureExperienceGained: gained);
    }

    private static ImmutableArray<QuestProgress> UnlockAvailable(
        ImmutableArray<QuestProgress> progress)
    {
        var result = progress.ToBuilder();
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
        return result.MoveToImmutable();
    }

    private static bool ObjectivesComplete(
        QuestDefinition definition,
        IReadOnlyDictionary<string, int> counts) =>
        definition.Objectives.All(objective =>
            counts.GetValueOrDefault(objective.Id) >= objective.Required);

    private static long NextSoloCompletionTick(
        IReadOnlyList<QuestProgress>? progress)
    {
        var normalized = Normalize(progress);
        var latest = normalized
            .Where(static value => value.Status == QuestStatus.Complete)
            .Select(static value => value.CompletionTick)
            .DefaultIfEmpty(0)
            .Max();
        return checked(latest + 1);
    }

    private static void ValidateAdventureExperience(int value)
    {
        var maximum = AdventureService.ExperienceForLevel(
            AdventureService.MaximumLevel);
        if (value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(
                nameof(value), "Adventure experience is outside its bounds.");
    }

    private static void ValidateEvent(QuestEvent value)
    {
        if (!Enum.IsDefined(value.Type) ||
            value.Amount is <= 0 or > MaximumEventAmount)
            throw new ArgumentException(
                "The quest event type or amount is invalid.", nameof(value));
        if (value.TargetId is null) return;
        if (!ValidIdentifier(value.TargetId, allowUnderscore: true))
            throw new ArgumentException(
                "The quest event target ID is invalid.", nameof(value));
    }

    private static IReadOnlyDictionary<string, int> CreateDefinitionIndices()
    {
        var result = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Definitions.Count; index++)
            if (!result.TryAdd(Definitions[index].Id, index))
                throw new InvalidOperationException(
                    "Quest definitions contain a duplicate ID.");
        return result;
    }

    private static void ValidateDefinitions()
    {
        if (Definitions.Count == 0)
            throw new InvalidOperationException(
                "At least one quest definition is required.");
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            if (!ValidIdentifier(definition.Id) ||
                string.IsNullOrWhiteSpace(definition.Title) ||
                string.IsNullOrWhiteSpace(definition.Category) ||
                string.IsNullOrWhiteSpace(definition.Summary) ||
                string.IsNullOrWhiteSpace(definition.StartText) ||
                string.IsNullOrWhiteSpace(definition.CompletionText) ||
                definition.AdventureExperience <= 0 ||
                definition.Objectives.Count == 0 ||
                definition.Objectives.Count > 32 ||
                (definition.PrerequisiteQuestId is not null &&
                 !known.Contains(definition.PrerequisiteQuestId)))
                throw new InvalidOperationException(
                    $"Quest definition '{definition.Id}' is invalid.");
            var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var objective in definition.Objectives)
                if (!ValidIdentifier(objective.Id) ||
                    !objectiveIds.Add(objective.Id) ||
                    string.IsNullOrWhiteSpace(objective.Description) ||
                    !Enum.IsDefined(objective.EventType) ||
                    objective.Required is <= 0 or > MaximumEventAmount ||
                    (objective.TargetId is not null &&
                     !ValidIdentifier(
                         objective.TargetId, allowUnderscore: true)))
                    throw new InvalidOperationException(
                        $"Quest objective '{objective.Id}' is invalid.");
            known.Add(definition.Id);
        }
    }

    private static bool ValidIdentifier(
        string value,
        bool allowUnderscore = false)
    {
        if (value.Length is 0 or > MaximumIdentifierLength ||
            value != value.Trim())
            return false;
        foreach (var character in value)
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ||
                  allowUnderscore && character == '_'))
                return false;
        return true;
    }

    private static InvalidDataException Invalid(string detail) => new(detail);
}
