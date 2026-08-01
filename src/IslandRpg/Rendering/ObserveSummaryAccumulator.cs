using System.Text.Json;

namespace IslandRpg.Rendering;

internal sealed class ObserveSummaryAccumulator
{
    private const double FlushIntervalSeconds = 5;
    private const double StallThresholdSeconds = 30;
    private readonly string _jsonPath;
    private readonly string _logPath;
    private readonly Dictionary<string, int> _eventCounts = [];
    private readonly Dictionary<string, int> _failureCounts = [];
    private readonly Dictionary<string, VillagerSummary> _villagers = [];
    private double _nextFlushAt;
    private double _duration;
    private int _councilsStarted;
    private int _councilsCompleted;

    public ObserveSummaryAccumulator(string basePath)
    {
        _jsonPath = basePath + ".summary.json";
        _logPath = basePath + ".summary.log";
    }

    public void Observe(
        double realSeconds,
        string? villagerId,
        string eventType,
        object? data)
    {
        _duration = Math.Max(_duration, realSeconds);
        _eventCounts[eventType] =
            _eventCounts.GetValueOrDefault(eventType) + 1;
        var json = JsonSerializer.SerializeToElement(data);
        if (eventType == "settlement_council_gathering")
            _councilsStarted++;
        else if (eventType == "settlement_council")
            _councilsCompleted++;
        if (villagerId is not null)
            ObserveVillager(realSeconds, villagerId, eventType, json);
        if (eventType == "world_action_failed")
        {
            var key = $"{Text(json, "Action")}:{Text(json, "Reason")}";
            _failureCounts[key] = _failureCounts.GetValueOrDefault(key) + 1;
        }
        if (realSeconds < _nextFlushAt &&
            eventType is not (
                "session_finished" or
                "settlement_council" or
                "settlement_council_timeout"))
            return;
        _nextFlushAt = realSeconds + FlushIntervalSeconds;
        Flush();
    }

    private void ObserveVillager(
        double realSeconds,
        string villagerId,
        string eventType,
        JsonElement data)
    {
        if (!_villagers.TryGetValue(villagerId, out var summary))
        {
            summary = new(villagerId);
            _villagers.Add(villagerId, summary);
        }
        summary.LastEventRealSeconds = realSeconds;
        if (eventType == "villager_snapshot")
        {
            summary.Name = Text(data, "Name") ?? summary.Name;
            var signature = string.Join('|',
                Text(data, "Need"), Text(data, "Activity"),
                Text(data, "Action"), Text(data, "GoalObjectId"),
                Text(data, "ConversationPartnerId"));
            if (!string.Equals(summary.StateSignature, signature,
                    StringComparison.Ordinal))
            {
                summary.StateSignature = signature;
                summary.StateSinceRealSeconds = realSeconds;
            }
            summary.StateDurationSeconds = Math.Max(
                0, realSeconds - summary.StateSinceRealSeconds);
            summary.PotentiallyStalled =
                summary.StateDurationSeconds >= StallThresholdSeconds &&
                IsInactiveSnapshot(data);
        }
        else if (eventType == "world_action_succeeded")
        {
            summary.SuccessfulActions++;
            summary.LastSuccessRealSeconds = realSeconds;
            summary.PotentiallyStalled = false;
        }
        else if (eventType == "world_action_failed")
            summary.FailedActions++;
        else if (eventType is "world_decision" or "social_decision")
        {
            summary.Decisions++;
            var signature = eventType + ':' +
                (Text(data, "Kind") ?? Text(data, "Intent") ?? "None") + ':' +
                (Text(data, "ObjectId") ?? Text(data, "OtherActorId") ?? "");
            if (string.Equals(summary.DecisionSignature, signature,
                    StringComparison.Ordinal))
                summary.RepeatedDecisionCount++;
            else
            {
                summary.DecisionSignature = signature;
                summary.RepeatedDecisionCount = 1;
            }
        }
    }

    private void Flush()
    {
        var villagers = _villagers.Values
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .Select(value => new
            {
                value.Id,
                value.Name,
                value.LastEventRealSeconds,
                value.LastSuccessRealSeconds,
                SecondsSinceSuccess = value.LastSuccessRealSeconds is null
                    ? _duration
                    : Math.Max(0, _duration - value.LastSuccessRealSeconds.Value),
                value.SuccessfulActions,
                value.FailedActions,
                value.Decisions,
                value.StateSignature,
                value.StateDurationSeconds,
                value.PotentiallyStalled,
                value.DecisionSignature,
                value.RepeatedDecisionCount
            })
            .ToArray();
        var snapshot = new
        {
            UpdatedUtc = DateTime.UtcNow,
            DurationSeconds = _duration,
            Councils = new
            {
                Started = _councilsStarted,
                Completed = _councilsCompleted
            },
            EventCounts = _eventCounts,
            FailureCounts = _failureCounts,
            PotentialStalls = villagers.Where(value => value.PotentiallyStalled)
                .Select(value => new
                {
                    value.Id,
                    value.Name,
                    value.StateSignature,
                    value.StateDurationSeconds,
                    value.SecondsSinceSuccess,
                    value.DecisionSignature,
                    value.RepeatedDecisionCount
                }),
            Villagers = villagers
        };
        WriteAtomic(
            _jsonPath,
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        var lines = new List<string>
        {
            $"duration={_duration:F1}s councils={_councilsCompleted}/{_councilsStarted}",
            "name | state-seconds | since-success | successes/failures | repeated-decision | stall"
        };
        lines.AddRange(villagers.Select(value =>
            $"{value.Name} | {value.StateDurationSeconds:F1} | " +
            $"{value.SecondsSinceSuccess:F1} | " +
            $"{value.SuccessfulActions}/{value.FailedActions} | " +
            $"{value.RepeatedDecisionCount} | {value.PotentiallyStalled}"));
        WriteAtomic(_logPath, string.Join(Environment.NewLine, lines));
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsInactiveSnapshot(JsonElement data)
    {
        var activity = Text(data, "Activity");
        var action = Text(data, "Action");
        return activity is "0" or "1" or "2" or "7" or "8" or
                   "Idle" or "Conversing" or "Reflecting" or
                   "Resting" or "Blocked" ||
               action is "0" or "Idle";
    }

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private sealed class VillagerSummary(string id)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = id;
        public double LastEventRealSeconds { get; set; }
        public double? LastSuccessRealSeconds { get; set; }
        public int SuccessfulActions { get; set; }
        public int FailedActions { get; set; }
        public int Decisions { get; set; }
        public string? StateSignature { get; set; }
        public double StateSinceRealSeconds { get; set; }
        public double StateDurationSeconds { get; set; }
        public bool PotentiallyStalled { get; set; }
        public string? DecisionSignature { get; set; }
        public int RepeatedDecisionCount { get; set; }
    }
}
