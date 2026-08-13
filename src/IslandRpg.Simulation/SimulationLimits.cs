using IslandRpg.Gameplay;

namespace IslandRpg.Simulation;

/// <summary>
/// Hard limits enforced before data is allowed to enter authoritative state.
/// A session takes a value-copy of this record at construction time.
/// </summary>
public sealed record SimulationLimits
{
    public static SimulationLimits Default { get; } = new();

    /// <summary>
    /// Maximum number of current player identities retained by the session.
    /// Connected players are never evicted. When this limit is reached by a
    /// valid new join, the least-recently disconnected player expires so
    /// unauthenticated connection churn cannot permanently exhaust admission.
    /// A retained disconnected player keeps its complete authoritative state
    /// and reconnect credential until it is selected for expiry.
    /// </summary>
    public int MaximumActors { get; init; } = 64;

    /// <summary>
    /// Maximum number of actors that may be connected at the same time. This
    /// is independent of <see cref="MaximumActors"/> so disconnecting a player
    /// releases a live slot without deleting their durable reconnect state.
    /// </summary>
    public int MaximumConnectedActors { get; init; } = 64;

    /// <summary>
    /// Maximum number of recently expired player IDs remembered in memory so
    /// reconnect attempts can distinguish an expired credential from an
    /// unknown identity. Tombstones contain no credential or gameplay state,
    /// are not checkpointed, and therefore become unknown after restart.
    /// </summary>
    public int ExpiredPlayerTombstoneCapacity { get; init; } =
        NetworkPopulationLimits.MaximumActors;

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

    public float ActorMovementSpeed { get; init; } =
        IslandRpg.Navigation.ActorMovementService.BaseMoveSpeed;

    public int MaximumPathSearchVisited { get; init; } =
        IslandRpg.Navigation.ActionPathSearchPolicy.MaximumVisited;

    public int MaximumPathWaypoints { get; init; } = 4_096;

    public float MaximumWalkIntentDistance { get; init; } = 512f;

    public float DestinationArrivalDistance { get; init; } = 0.01f;

    public float MinimumWorldCoordinate { get; init; } = -1_000_000f;

    public float MaximumWorldCoordinate { get; init; } = 1_000_000f;

    internal SimulationLimits ValidatedCopy()
    {
        if (MaximumActors is <= 0 or >
            NetworkPopulationLimits.MaximumActors)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActors));
        }

        if (MaximumConnectedActors is <= 0 or >
            NetworkPopulationLimits.MaximumActors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConnectedActors));
        }

        if (ExpiredPlayerTombstoneCapacity is < 0 or >
            NetworkPopulationLimits.MaximumActors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpiredPlayerTombstoneCapacity));
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

        if (MaximumPathSearchVisited <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPathSearchVisited));
        }

        if (MaximumPathWaypoints <= 0 ||
            MaximumPathWaypoints > MaximumPathSearchVisited)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPathWaypoints));
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
