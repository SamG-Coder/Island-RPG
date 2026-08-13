namespace IslandRpg.Simulation;

/// <summary>
/// Hard limits enforced before data is allowed to enter authoritative state.
/// A session takes a value-copy of this record at construction time.
/// </summary>
public sealed record SimulationLimits
{
    public static SimulationLimits Default { get; } = new();

    public int MaximumActors { get; init; } = 64;

    public int InboundCommandCapacity { get; init; } = 2_048;

    public int MaximumCommandsPerTick { get; init; } = 512;

    public int MaximumDisplayNameLength { get; init; } = 40;

    public int MaximumChatLength { get; init; } = 300;

    public int ChatHistoryCapacity { get; init; } = 128;

    /// <summary>
    /// Maximum number of idempotent gameplay-command receipts retained for
    /// each player. Older receipts are evicted in processing order.
    /// </summary>
    public int CommandReceiptCapacity { get; init; } = 256;

    public float ActorMovementSpeed { get; init; } = 4f;

    public float MaximumWalkIntentDistance { get; init; } = 512f;

    public float DestinationArrivalDistance { get; init; } = 0.01f;

    public float MinimumWorldCoordinate { get; init; } = -1_000_000f;

    public float MaximumWorldCoordinate { get; init; } = 1_000_000f;

    internal SimulationLimits ValidatedCopy()
    {
        if (MaximumActors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActors));
        }

        if (InboundCommandCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InboundCommandCapacity));
        }

        if (MaximumCommandsPerTick <= 0 || MaximumCommandsPerTick > InboundCommandCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCommandsPerTick));
        }

        if (MaximumDisplayNameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDisplayNameLength));
        }

        if (MaximumChatLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumChatLength));
        }

        if (ChatHistoryCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChatHistoryCapacity));
        }

        if (CommandReceiptCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandReceiptCapacity));
        }

        if (!float.IsFinite(ActorMovementSpeed) || ActorMovementSpeed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ActorMovementSpeed));
        }

        if (!float.IsFinite(MaximumWalkIntentDistance) || MaximumWalkIntentDistance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumWalkIntentDistance));
        }

        if (!float.IsFinite(DestinationArrivalDistance) || DestinationArrivalDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DestinationArrivalDistance));
        }

        if (!float.IsFinite(MinimumWorldCoordinate) ||
            !float.IsFinite(MaximumWorldCoordinate) ||
            MinimumWorldCoordinate >= MaximumWorldCoordinate)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumWorldCoordinate));
        }

        return this with { };
    }
}
