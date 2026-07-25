namespace IslandRpg.Gameplay;

internal sealed class RepeatedActionMonologue
{
    private const double AttemptWindowSeconds = 12;
    private const double MonologueCooldownSeconds = 8;
    private const int AttemptsBeforeMonologue = 3;

    private static readonly string[] Lines =
    [
        "What am I doing?",
        "I should try something else.",
        "This clearly isn't working.",
        "Maybe I should stop and think.",
        "I keep making the same mistake.",
        "That was never going to work.",
        "Why am I trying this again?",
        "There must be another way.",
        "I ought to pay attention.",
        "Once was enough, surely.",
        "Perhaps I need the right equipment.",
        "I should check my inventory.",
        "I need to rethink this.",
        "Doing it again won't help.",
        "I am getting nowhere.",
        "Maybe I missed something.",
        "This is becoming a habit.",
        "I should know better by now.",
        "Right... different plan.",
        "Let's pretend that didn't happen."
    ];

    private readonly Dictionary<string, AttemptState> _attempts =
        new(StringComparer.OrdinalIgnoreCase);
    private double _lastMonologueAt = double.NegativeInfinity;
    private int _lastLine = -1;

    public string? RecordFailure(string action, double now)
    {
        if (!_attempts.TryGetValue(action, out var state) ||
            now - state.LastAttemptAt > AttemptWindowSeconds)
            state = new(0, now);

        state = new(state.Count + 1, now);
        _attempts[action] = state;
        if (state.Count < AttemptsBeforeMonologue ||
            now - _lastMonologueAt < MonologueCooldownSeconds)
            return null;

        _attempts[action] = new(0, now);
        _lastMonologueAt = now;
        int line;
        do
            line = Random.Shared.Next(Lines.Length);
        while (Lines.Length > 1 && line == _lastLine);
        _lastLine = line;
        return Lines[line];
    }

    private readonly record struct AttemptState(
        int Count, double LastAttemptAt);
}
