namespace IslandRpg.Gameplay;

/// <summary>
/// Stores action cadence by actor rather than by controller or target. This
/// keeps movement, retargeting and presentation restarts from resetting an
/// entity's gameplay cooldown.
/// </summary>
internal sealed class EntityActionCooldowns
{
    private readonly Dictionary<(string ActorId, EntityAction Action), double>
        _readyAt = [];

    public double ReadyAt(string actorId, EntityAction action) =>
        _readyAt.GetValueOrDefault((actorId, action));

    public bool TryCommit(
        string actorId,
        EntityAction action,
        double now,
        double intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(actorId) ||
            !double.IsFinite(now) ||
            !double.IsFinite(intervalSeconds) ||
            intervalSeconds <= 0 ||
            now < ReadyAt(actorId, action))
            return false;
        _readyAt[(actorId, action)] = now + intervalSeconds;
        return true;
    }

    public void Forget(string actorId)
    {
        foreach (var key in _readyAt.Keys
                     .Where(key => key.ActorId == actorId)
                     .ToArray())
            _readyAt.Remove(key);
    }

    public void Clear() => _readyAt.Clear();
}
