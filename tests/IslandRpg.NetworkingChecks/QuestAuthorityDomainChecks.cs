using IslandRpg.Gameplay;
using System.Text.Json;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused checks for the shared deterministic quest domain.
/// </summary>
internal static class QuestAuthorityDomainChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "quest normalization is strict compact and immutable",
            NormalizationIsStrictCompactAndImmutable);
        checks.Add(
            "quest events use authoritative completion ticks and exact XP",
            EventsUseAuthoritativeCompletionTicksAndExactExperience);
        checks.Add(
            "legacy quest JSON migrates wall-clock completion deterministically",
            LegacyJsonMigratesCompletionDeterministically);
    }

    private static void NormalizationIsStrictCompactAndImmutable()
    {
        var rawCounts = new Dictionary<string, int>
        {
            ["sticks"] = 1
        };
        var canonical = QuestService.Normalize(
        [
            new QuestProgress(
                "WASHED-ASHORE",
                QuestStatus.NotStarted,
                rawCounts,
                CompletionTick: 999)
        ]);
        CheckAssert.Equal(QuestService.Definitions.Count, canonical.Length,
            "normalization must materialize every definition in stable order");
        CheckAssert.Equal("washed-ashore", canonical[0].QuestId,
            "normalization must canonicalize a known quest ID");
        CheckAssert.Equal(QuestStatus.InProgress, canonical[0].Status,
            "the first incomplete quest must be the sole active quest");
        CheckAssert.Equal(QuestProgress.IncompleteTick,
            canonical[0].CompletionTick,
            "incomplete state must discard noncanonical completion metadata");
        CheckAssert.True(
            canonical[0].ObjectiveCounts is QuestObjectiveCounts,
            "canonical counters must use the compact immutable representation");
        QuestService.Validate(canonical);

        rawCounts["sticks"] = 2;
        CheckAssert.Equal(1, canonical[0].ObjectiveCounts!["sticks"],
            "canonical progress must not alias a mutable input dictionary");
        CheckAssert.False(QuestService.TryValidate(
                [new QuestProgress(
                    "washed-ashore",
                    QuestStatus.InProgress,
                    new Dictionary<string, int> { ["sticks"] = 1 })],
                out _),
            "strict validation must reject noncanonical mutable counters");
        CheckAssert.Throws<InvalidDataException>(
            () => QuestService.Normalize(
            [
                new QuestProgress(
                    "washed-ashore",
                    QuestStatus.InProgress,
                    new Dictionary<string, int> { ["invented"] = 1 })
            ]),
            "normalization must reject unknown objective IDs");
        CheckAssert.Throws<ArgumentException>(
            () => QuestService.Apply(
                canonical,
                0,
                new QuestEvent(
                    QuestEventType.GatherItem,
                    "large_rock",
                    Amount: 0),
                completionTick: 1),
            "authority must reject zero or negative event deltas");
    }

    private static void EventsUseAuthoritativeCompletionTicksAndExactExperience()
    {
        var start = QuestService.Normalize(null);
        var first = QuestService.Apply(
            start,
            0,
            new QuestEvent(
                QuestEventType.GatherItem,
                "large_rock",
                5),
            completionTick: 40);
        var second = QuestService.Apply(
            first.Progress,
            first.AdventureExperience,
            new QuestEvent(
                QuestEventType.GatherItem,
                "sticks",
                2),
            completionTick: 41);
        var completed = QuestService.Apply(
            second.Progress,
            second.AdventureExperience,
            new QuestEvent(
                QuestEventType.GatherItem,
                "plant_fibres",
                2),
            completionTick: 42);

        CheckAssert.True(completed.Changed && completed.Completed,
            "the final committed objective event must report both transitions");
        CheckAssert.Equal(50, completed.AdventureExperience,
            "quest reward must enter the authoritative Adventure total");
        CheckAssert.Equal(50, completed.AdventureExperienceGained,
            "the result must expose the exact clamped XP delta");
        CheckAssert.Equal(42L, completed.Progress[0].CompletionTick,
            "completion must preserve the supplied authoritative tick exactly");
        CheckAssert.Equal(QuestStatus.InProgress,
            completed.Progress[1].Status,
            "completion must deterministically unlock the next definition");
        QuestService.Validate(completed.Progress);

        var replay = QuestService.Apply(
            completed.Progress,
            completed.AdventureExperience,
            new QuestEvent(
                QuestEventType.GatherItem,
                "plant_fibres",
                2),
            completionTick: 99);
        CheckAssert.False(replay.Changed || replay.Completed,
            "an irrelevant replay must not report a state transition");
        CheckAssert.Equal(0, replay.AdventureExperienceGained,
            "an irrelevant replay must not award Adventure XP twice");
        CheckAssert.Equal(completed.AdventureExperience,
            replay.AdventureExperience,
            "an irrelevant replay must retain the authoritative XP total");

        var deterministicReplay = QuestService.Apply(
            second.Progress,
            second.AdventureExperience,
            new QuestEvent(
                QuestEventType.GatherItem,
                "plant_fibres",
                2),
            completionTick: 42);
        CheckAssert.SequenceEqual(
            completed.Progress,
            deterministicReplay.Progress,
            "the same canonical state event and tick must replay exactly");
        CheckAssert.Equal(completed.AdventureExperience,
            deterministicReplay.AdventureExperience,
            "deterministic replay must reproduce the Adventure total");
    }

    private static void LegacyJsonMigratesCompletionDeterministically()
    {
        const string legacyJson = """
            {
              "QuestId": "washed-ashore",
              "Status": 3,
              "ObjectiveCounts": {
                "large-rocks": 5,
                "sticks": 2,
                "fibres": 2
              },
              "CompletedUtc": "2025-01-02T03:04:05Z"
            }
            """;
        var legacy = JsonSerializer.Deserialize<QuestProgress>(legacyJson);
        CheckAssert.True(legacy is not null,
            "the previous PlayerProfile quest payload must still deserialize");
        var migrated = QuestService.Normalize([legacy!]);
        CheckAssert.Equal(QuestStatus.Complete, migrated[0].Status,
            "the legacy completed status must remain complete");
        CheckAssert.Equal(0L, migrated[0].CompletionTick,
            "wall-clock completion metadata must migrate to logical tick zero");
        CheckAssert.True(
            migrated[0].ObjectiveCounts is QuestObjectiveCounts,
            "legacy dictionaries must migrate into immutable counters");
        QuestService.Validate(migrated);
    }
}
