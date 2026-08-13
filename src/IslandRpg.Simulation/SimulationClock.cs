namespace IslandRpg.Simulation;

/// <summary>
/// Fixed simulation rates shared by the headless host and clients.
/// Wall-clock scheduling belongs to the host; authoritative time advances only
/// when one fixed session tick is executed.
/// </summary>
public static class SimulationTiming
{
    public const int TicksPerSecond = 60;
    public const int SnapshotsPerSecond = 20;
    public const int TicksPerSnapshot = TicksPerSecond / SnapshotsPerSecond;
    public const double FixedDeltaSeconds = 1d / TicksPerSecond;
    public static readonly TimeSpan FixedDelta = TimeSpan.FromSeconds(FixedDeltaSeconds);
}

public readonly record struct SimulationClockSnapshot(
    long Tick,
    long SnapshotSequence,
    TimeSpan Elapsed)
{
    public double ElapsedSeconds => Tick * SimulationTiming.FixedDeltaSeconds;
}

/// <summary>
/// A deterministic fixed-step clock. It intentionally never reads a system clock.
/// </summary>
public sealed class DeterministicSimulationClock
{
    public long Tick { get; private set; }

    public long SnapshotSequence { get; private set; }

    public TimeSpan Elapsed => TimeSpan.FromTicks(checked((long)Math.Round(
        Tick * (double)TimeSpan.TicksPerSecond / SimulationTiming.TicksPerSecond,
        MidpointRounding.AwayFromZero)));

    public SimulationClockSnapshot Current => new(Tick, SnapshotSequence, Elapsed);

    internal bool AdvanceOneTick()
    {
        Tick = checked(Tick + 1);
        return Tick % SimulationTiming.TicksPerSnapshot == 0;
    }

    internal long AdvanceSnapshotSequence()
    {
        SnapshotSequence = checked(SnapshotSequence + 1);
        return SnapshotSequence;
    }
}
