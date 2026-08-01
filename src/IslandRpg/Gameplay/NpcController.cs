using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum NpcActionPhase : byte
{
    Queued,
    Acting,
    Recovering
}

internal readonly record struct NpcBrainIntent(
    string Name,
    EntityAction Action,
    Vector2? Target = null,
    string? TargetKey = null,
    long WorldRevision = 0);

internal readonly record struct NpcActionResult(
    NpcBrainIntent Intent,
    bool Succeeded,
    string? Reason = null);

/// <summary>
/// Turns brain intents into the same timed actions used by a player. The
/// controller owns timing only; its interaction callback is executed by the
/// simulation thread at the animation impact point.
/// </summary>
internal sealed class NpcController
{
    public const double DefaultImpactFraction = .55;

    private sealed class ControlledAction(
        NpcBrainIntent intent,
        Func<NpcActionResult> interaction,
        Action? cancelled,
        Func<bool>? targetAvailable)
    {
        public NpcBrainIntent Intent { get; } = intent;
        public Func<NpcActionResult> Interaction { get; } = interaction;
        public Action? Cancelled { get; } = cancelled;
        public Func<bool>? TargetAvailable { get; } = targetAvailable;
        public NpcActionPhase Phase { get; set; } = NpcActionPhase.Queued;
    }

    private readonly Dictionary<string, Queue<ControlledAction>> _queues = [];
    private readonly Dictionary<string, ControlledAction> _active = [];
    private readonly Queue<(string ActorId, NpcActionResult Result)> _results = [];

    public bool IsBusy(string actorId) =>
        _active.ContainsKey(actorId) ||
        _queues.TryGetValue(actorId, out var queue) && queue.Count > 0;

    public NpcActionPhase? Phase(string actorId) =>
        _active.TryGetValue(actorId, out var action) ? action.Phase : null;

    public bool TryBegin(
        string actorId,
        NpcBrainIntent intent,
        Func<NpcActionResult> interaction,
        Action? cancelled = null,
        Func<bool>? targetAvailable = null)
    {
        if (IsBusy(actorId)) return false;
        var action = new ControlledAction(
            intent, interaction, cancelled, targetAvailable)
        {
            Phase = NpcActionPhase.Acting
        };
        _active.Add(actorId, action);
        return true;
    }

    public void Advance(
        string actorId,
        EntityAction currentAction,
        double actionTime,
        double animationDuration)
    {
        if (!_active.TryGetValue(actorId, out var action) ||
            animationDuration <= 0)
            return;

        if (currentAction != action.Intent.Action)
        {
            action.Cancelled?.Invoke();
            _results.Enqueue((actorId,
                new(action.Intent, false, "interrupted")));
            _active.Remove(actorId);
            Promote(actorId);
            return;
        }
        if (action.TargetAvailable?.Invoke() == false)
        {
            action.Cancelled?.Invoke();
            _results.Enqueue((actorId,
                new(action.Intent, false, "target_unavailable")));
            _active.Remove(actorId);
            Promote(actorId);
            return;
        }

        if (action.Phase == NpcActionPhase.Acting &&
            actionTime >= animationDuration * DefaultImpactFraction)
        {
            NpcActionResult result;
            try
            {
                result = action.Interaction();
            }
            catch (Exception exception)
            {
                result = new(action.Intent, false, exception.Message);
            }
            _results.Enqueue((actorId, result));
            _active.Remove(actorId);
            Promote(actorId);
            return;
        }

        if (actionTime >= animationDuration)
        {
            _active.Remove(actorId);
            Promote(actorId);
        }
    }

    public bool TryDequeueResult(
        out string actorId,
        out NpcActionResult result)
    {
        if (_results.TryDequeue(out var entry))
        {
            actorId = entry.ActorId;
            result = entry.Result;
            return true;
        }
        actorId = string.Empty;
        result = default;
        return false;
    }

    public void Cancel(string actorId)
    {
        if (_active.Remove(actorId, out var action))
            action.Cancelled?.Invoke();
        _queues.Remove(actorId);
    }

    public void Clear()
    {
        _active.Clear();
        _queues.Clear();
        _results.Clear();
    }

    private void Promote(string actorId)
    {
        if (!_queues.TryGetValue(actorId, out var queue) ||
            queue.Count == 0)
            return;
        var next = queue.Dequeue();
        next.Phase = NpcActionPhase.Acting;
        _active[actorId] = next;
        if (queue.Count == 0) _queues.Remove(actorId);
    }
}
