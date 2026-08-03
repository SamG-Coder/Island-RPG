namespace IslandRpg.Rendering;

internal readonly record struct EntityFeedback(
    string TargetKey,
    double HealthVisibleUntil,
    int Damage = 0,
    bool Hit = false,
    double ImpactAt = double.NegativeInfinity,
    string? Label = null,
    bool LabelSuccess = false);

/// <summary>
/// Actor-neutral presentation state for any damageable world entity.
/// Gameplay systems publish impacts; renderers consume the same health and
/// hit-splat timing regardless of entity type.
/// </summary>
internal sealed class EntityFeedbackState
{
    public const double HealthDisplaySeconds = 3;
    private readonly Dictionary<string, EntityFeedback> _entries = [];

    public string? LatestImpactTargetKey { get; private set; }

    public void ShowHealth(string targetKey, double clock)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) return;
        var current = _entries.GetValueOrDefault(targetKey);
        _entries[targetKey] = current with
        {
            TargetKey = targetKey,
            HealthVisibleUntil = clock + HealthDisplaySeconds
        };
    }

    public void ShowImpact(
        string targetKey, int damage, bool hit, double clock)
    {
        var missed = damage <= 0;
        ShowHealth(targetKey, clock);
        var current = _entries[targetKey];
        _entries[targetKey] = current with
        {
            Damage = Math.Max(0, damage),
            Hit = hit && !missed,
            ImpactAt = clock,
            Label = missed ? "Miss" : null,
            LabelSuccess = false
        };
        LatestImpactTargetKey = targetKey;
    }

    public void ShowLabel(
        string targetKey, string label, bool success, double clock)
    {
        if (string.IsNullOrWhiteSpace(targetKey) ||
            string.IsNullOrWhiteSpace(label)) return;
        var current = _entries.GetValueOrDefault(targetKey);
        _entries[targetKey] = current with
        {
            TargetKey = targetKey,
            ImpactAt = clock,
            Label = label,
            LabelSuccess = success
        };
        LatestImpactTargetKey = targetKey;
    }

    public bool TryGet(string targetKey, out EntityFeedback feedback) =>
        _entries.TryGetValue(targetKey, out feedback);

    public bool HealthVisible(string targetKey, double clock) =>
        TryGet(targetKey, out var value) &&
        clock < value.HealthVisibleUntil;

    public void Prune(double clock, double impactDisplaySeconds)
    {
        foreach (var key in _entries
                     .Where(entry =>
                         clock >= entry.Value.HealthVisibleUntil &&
                         clock - entry.Value.ImpactAt >=
                         impactDisplaySeconds)
                     .Select(entry => entry.Key)
                     .ToArray())
            _entries.Remove(key);
        if (LatestImpactTargetKey is { } latest &&
            !_entries.ContainsKey(latest))
            LatestImpactTargetKey = null;
    }

    public void Clear()
    {
        _entries.Clear();
        LatestImpactTargetKey = null;
    }
}
