using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;

namespace IslandRpg.Simulation;

public readonly record struct BoatId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() => Value.ToString("N");
}

public readonly record struct BoatReference(
    BoatId BoatId,
    uint ExpectedRevision)
{
    public bool IsWellFormed => !BoatId.IsEmpty && ExpectedRevision > 0;
}

public sealed record AuthoritativeBoatSnapshot(
    BoatId BoatId,
    ulong NetworkEntityId,
    PlayerId OwnerPlayerId,
    string? GroupId,
    ActorId? OccupantActorId,
    PlayerId? OccupantPlayerId,
    Vector2 Position,
    Vector2 Facing,
    Vector2 Velocity,
    Vector2? Destination,
    int WorldLevel,
    uint Revision);

public enum BoatChangeKind : byte
{
    Added = 1,
    Updated = 2,
    Removed = 3
}

public sealed record BoatStateDelta(
    BoatChangeKind Kind,
    AuthoritativeBoatSnapshot? Previous,
    AuthoritativeBoatSnapshot? Current);

public enum BoatTransactionStatus : byte
{
    Accepted = 0,
    InvalidCommand,
    ActorNotFound,
    DeadActor,
    StaleActorRevision,
    StaleInventoryRevision,
    BoatNotFound,
    StaleBoatRevision,
    WrongWorldLevel,
    OutOfRange,
    AccessDenied,
    AlreadyAboard,
    BoatOccupied,
    NotAboard,
    InvalidDestination,
    DestinationTooFar,
    RouteUnreachable,
    InvalidLanding,
    PlanningCadenceLocked
}

public sealed record BoatActorTransition(
    Vector2 Position,
    int WorldLevel,
    BoatId? BoardedBoatId);

public sealed record BoatTransactionResult(
    Guid CommandId,
    BoatTransactionStatus Status,
    uint ActorRevision,
    uint InventoryRevision,
    PlayerGameplaySnapshot Gameplay,
    BoatStateDelta? BoatDelta = null,
    BoatActorTransition? ActorTransition = null,
    string Detail = "")
{
    public bool Accepted => Status == BoatTransactionStatus.Accepted;
}

public sealed record BoatTransactionActorInput(
    ActorId ActorId,
    PlayerId PlayerId,
    Vector2 Position,
    int WorldLevel,
    PlayerGameplaySnapshot Gameplay,
    string? GroupId = null);

public sealed record BoardBoatTransaction(
    WorldTransactionContext Context,
    BoatReference Boat);

public sealed record MoveBoatTransaction(
    WorldTransactionContext Context,
    BoatReference Boat,
    Vector2 Target);

public sealed record StopBoatTransaction(
    WorldTransactionContext Context,
    BoatReference Boat);

public sealed record DisembarkBoatTransaction(
    WorldTransactionContext Context,
    BoatReference Boat,
    Vector2 RequestedLanding);

public sealed record AuthoritativeBoatSeed(
    BoatId BoatId,
    PlayerId OwnerPlayerId,
    Vector2 Position,
    int WorldLevel = 0,
    Vector2 Facing = default,
    string? GroupId = null,
    uint Revision = 1);

public sealed record AuthoritativeBoatCheckpoint(
    BoatId BoatId,
    PlayerId OwnerPlayerId,
    string? GroupId,
    ActorId? OccupantActorId,
    PlayerId? OccupantPlayerId,
    Vector2 Position,
    Vector2 Facing,
    int WorldLevel,
    uint Revision,
    ImmutableArray<Vector2> RemainingRoute,
    double PlanningCooldownSeconds = 0);

public sealed record AuthoritativeBoatTransactionsCheckpoint(
    ImmutableArray<AuthoritativeBoatCheckpoint> Boats)
{
    public static AuthoritativeBoatTransactionsCheckpoint Empty { get; } =
        new([]);
}

public sealed record AuthoritativeBoatTransactionOptions
{
    public float InteractionRange { get; init; } = 2.4f;

    public float MovementSpeed { get; init; } = 3.4f;

    public float DestinationArrivalDistance { get; init; } = .01f;

    public float MaximumMoveDistance { get; init; } = 512f;

    public int MaximumPathSearchVisited { get; init; } = 16_384;

    public int MaximumRouteWaypoints { get; init; } = 4_096;

    public int MaximumBoats { get; init; } =
        NetworkPopulationLimits.MaximumBoats;

    /// <summary>
    /// Fixed-authority time between distinct route-planning requests for the
    /// same boat. Client clocks never participate in this cooldown.
    /// </summary>
    public double PlanningCadenceSeconds { get; init; } = .2;

    /// <summary>
    /// Maximum expensive route searches admitted between authority advances.
    /// This prevents a fleet of boats bypassing the per-boat cadence in one
    /// simulation turn.
    /// </summary>
    public int MaximumPlansPerAdvance { get; init; } = 2;

    internal AuthoritativeBoatTransactionOptions ValidatedCopy()
    {
        if (!float.IsFinite(InteractionRange) || InteractionRange <= 0 ||
            !float.IsFinite(MovementSpeed) || MovementSpeed <= 0 ||
            !float.IsFinite(DestinationArrivalDistance) ||
            DestinationArrivalDistance < 0 ||
            !float.IsFinite(MaximumMoveDistance) || MaximumMoveDistance <= 0 ||
            MaximumPathSearchVisited <= 0 || MaximumRouteWaypoints <= 0 ||
            MaximumRouteWaypoints > MaximumPathSearchVisited ||
            MaximumBoats is <= 0 or >
                NetworkPopulationLimits.MaximumBoats ||
            !double.IsFinite(PlanningCadenceSeconds) ||
            PlanningCadenceSeconds is < 0 or > 60 ||
            MaximumPlansPerAdvance <= 0)
            throw new ArgumentOutOfRangeException(nameof(InteractionRange));
        return this with { };
    }
}
