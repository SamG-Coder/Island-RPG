namespace IslandRpg.Protocol;

/// <summary>Wrap-aware operations for the 16-bit UDP snapshot sequence space.</summary>
public static class SnapshotSequence
{
    private const int HalfRange = 1 << 15;

    public static bool IsNewer(ushort candidate, ushort reference) =>
        candidate != reference && (ushort)(candidate - reference) < HalfRange;

    public static int ForwardDistance(ushort newer, ushort older) => (ushort)(newer - older);

    /// <summary>Checks a sequence against an acknowledgement and its preceding 32-bit window.</summary>
    public static bool IsAcknowledged(ushort sequence, ushort acknowledgement, uint acknowledgementBits)
    {
        if (sequence == acknowledgement)
        {
            return true;
        }

        var distance = ForwardDistance(acknowledgement, sequence);
        return distance is >= 1 and <= 32 && (acknowledgementBits & (1u << (distance - 1))) != 0;
    }
}

/// <summary>Tracks the newest received UDP sequence and a compact 32-packet history.</summary>
public struct SnapshotAcknowledgementWindow
{
    private bool _initialized;
    public ushort Latest { get; private set; }
    public uint PreviousBits { get; private set; }

    /// <returns>True when this sequence had not already been observed in the window.</returns>
    public bool Observe(ushort sequence)
    {
        if (!_initialized)
        {
            _initialized = true;
            Latest = sequence;
            PreviousBits = 0;
            return true;
        }

        if (sequence == Latest)
        {
            return false;
        }

        if (SnapshotSequence.IsNewer(sequence, Latest))
        {
            var distance = SnapshotSequence.ForwardDistance(sequence, Latest);
            PreviousBits = distance > 32 ? 0 : (PreviousBits << distance) | (1u << (distance - 1));
            Latest = sequence;
            return true;
        }

        var age = SnapshotSequence.ForwardDistance(Latest, sequence);
        if (age is < 1 or > 32)
        {
            return false;
        }

        var mask = 1u << (age - 1);
        if ((PreviousBits & mask) != 0)
        {
            return false;
        }

        PreviousBits |= mask;
        return true;
    }
}
