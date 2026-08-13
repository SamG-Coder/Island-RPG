using IslandRpg.Gameplay;

namespace IslandRpg.Simulation;

/// <summary>
/// Canonical conversion shared by the headless authority and network-world
/// presentation. Multiplayer worlds begin at the same 03:00 epoch as local
/// worlds and advance one in-game minute per real second.
/// </summary>
public static class AuthoritativeWorldTime
{
    public static double FromElapsedRealSeconds(double elapsedRealSeconds)
    {
        if (!double.IsFinite(elapsedRealSeconds) || elapsedRealSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(elapsedRealSeconds),
                "Elapsed real time must be finite and non-negative.");
        return WorldTime.Advance(
            WorldTime.NewGameStartGameSeconds, elapsedRealSeconds);
    }

    /// <summary>
    /// Converts a deadline written by the legacy elapsed-real authority into
    /// the accelerated world-time domain. The remaining numeric duration was
    /// authored in game seconds, so it is deliberately not multiplied by the
    /// 60x world clock rate.
    /// </summary>
    public static double FromLegacyElapsedDeadline(
        double elapsedDeadlineSeconds,
        long checkpointTick)
    {
        if (!double.IsFinite(elapsedDeadlineSeconds) ||
            elapsedDeadlineSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(elapsedDeadlineSeconds),
                "The legacy deadline must be finite and non-negative.");
        if (checkpointTick < 0)
            throw new ArgumentOutOfRangeException(nameof(checkpointTick));
        if (elapsedDeadlineSeconds == 0) return 0;

        var elapsedRealSeconds =
            checkpointTick * SimulationTiming.FixedDeltaSeconds;
        var remainingGameSeconds = Math.Max(
            0, elapsedDeadlineSeconds - elapsedRealSeconds);
        return checked(FromElapsedRealSeconds(elapsedRealSeconds) +
                       remainingGameSeconds);
    }
}
