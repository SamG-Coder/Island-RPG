using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Boats;

namespace IslandRpg.Simulation;

/// <summary>
/// Single-owner authority for stable boats, occupancy and water routes.
/// Route planning and integration are server-owned; clients submit targets.
/// Semantic revisions deliberately do not churn during fixed-tick movement.
/// </summary>
public sealed class AuthoritativeBoatTransactions
{
    private readonly IBoatNavigationQuery _navigation;
    private readonly AuthoritativeBoatTransactionOptions _options;
    private readonly Func<BoatId, ulong> _networkEntityId;
    private readonly Func<IBoatNavigationQuery, Vector2, Vector2, int,
        IReadOnlyList<Vector2>> _routePlanner;
    private readonly Dictionary<BoatId, BoatState> _boats = [];
    private readonly Dictionary<ActorId, BoatId> _boatsByOccupant = [];
    private int _plansRemaining;
    private int? _ownerThreadId;

    public AuthoritativeBoatTransactions(
        IBoatNavigationQuery navigation,
        AuthoritativeBoatTransactionOptions? options = null)
        : this(navigation, options, DeriveNetworkEntityId)
    {
    }

    internal AuthoritativeBoatTransactions(
        IBoatNavigationQuery navigation,
        AuthoritativeBoatTransactionOptions? options,
        Func<BoatId, ulong> networkEntityId,
        Func<IBoatNavigationQuery, Vector2, Vector2, int,
            IReadOnlyList<Vector2>>? routePlanner = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(networkEntityId);
        _navigation = navigation;
        _options = (options ?? new AuthoritativeBoatTransactionOptions())
            .ValidatedCopy();
        _networkEntityId = networkEntityId;
        _routePlanner = routePlanner ?? PlanRoute;
        _plansRemaining = _options.MaximumPlansPerAdvance;
    }

    public AuthoritativeBoatSnapshot Seed(AuthoritativeBoatSeed seed)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(seed);
        if (_boats.Count >= _options.MaximumBoats || seed.BoatId.IsEmpty ||
            seed.OwnerPlayerId.Value == Guid.Empty || seed.WorldLevel != 0 ||
            seed.Revision == 0 || !IsFinite(seed.Position) ||
            !_navigation.IsNavigable(seed.Position) ||
            IsBoatPositionOccupied(seed.Position) ||
            !TryNormalizeFacing(seed.Facing, out var facing) ||
            !ValidGroup(seed.GroupId) || _boats.ContainsKey(seed.BoatId))
            throw new ArgumentException("The authoritative boat seed is invalid.",
                nameof(seed));

        var networkEntityId = _networkEntityId(seed.BoatId);
        if (networkEntityId == 0 || _boats.Values.Any(value =>
                value.NetworkEntityId == networkEntityId))
            throw new ArgumentException(
                "The authoritative boat network identity is invalid or duplicated.",
                nameof(seed));

        var state = new BoatState(
            seed.BoatId, seed.OwnerPlayerId, seed.GroupId,
            seed.Position, facing, seed.WorldLevel, seed.Revision,
            networkEntityId);
        _boats.Add(seed.BoatId, state);
        return state.ToSnapshot();
    }

    /// <summary>
    /// Trusted island-start seam. One stable boat is derived from the owner
    /// and placed at the nearest unoccupied shoreline mooring.
    /// </summary>
    public AuthoritativeBoatSnapshot ProvisionPlayerBoat(
        PlayerId ownerPlayerId,
        Vector2 origin,
        string? groupId = null)
    {
        EnsureOwner();
        if (ownerPlayerId.Value == Guid.Empty || !IsFinite(origin) ||
            !ValidGroup(groupId))
            throw new ArgumentException("The boat owner or origin is invalid.");

        var id = DerivePlayerBoatId(ownerPlayerId);
        if (_boats.TryGetValue(id, out var existing))
            return existing.ToSnapshot();
        var position = BoatTravelRules.FindInitialPosition(
            _navigation, origin, value => IsBoatPositionOccupied(value));
        if (!_navigation.IsNavigable(position) ||
            IsBoatPositionOccupied(position))
            throw new InvalidOperationException(
                "No unoccupied shoreline mooring was found for this player.");
        return Seed(new AuthoritativeBoatSeed(
            id, ownerPlayerId, position, GroupId: groupId));
    }

    public BoatTransactionResult Execute(
        BoatTransactionActorInput actor,
        BoardBoatTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var validation = Validate(actor, command.Context, command.Boat,
            out var boat);
        if (validation is not null) return validation;
        if (_boatsByOccupant.ContainsKey(actor.ActorId))
            return Rejected(actor, command.Context,
                BoatTransactionStatus.AlreadyAboard,
                "The actor is already aboard a boat.");
        if (boat.OccupantActorId is not null)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.BoatOccupied,
                "The boat already has an occupant.");
        if (!CanUse(actor, boat))
            return Rejected(actor, command.Context,
                BoatTransactionStatus.AccessDenied,
                "The actor does not own or share access to this boat.");
        if (Vector2.DistanceSquared(actor.Position, boat.Position) >
            _options.InteractionRange * _options.InteractionRange)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.OutOfRange,
                "The boat is outside boarding range.");

        var previous = boat.ToSnapshot();
        boat.OccupantActorId = actor.ActorId;
        boat.OccupantPlayerId = actor.PlayerId;
        boat.ClearRoute();
        boat.Revision = checked(boat.Revision + 1);
        _boatsByOccupant.Add(actor.ActorId, boat.BoatId);
        var gameplay = AdvanceActor(actor.Gameplay);
        return Accepted(command.Context, gameplay,
            new(BoatChangeKind.Updated, previous, boat.ToSnapshot()),
            new(boat.Position, boat.WorldLevel, boat.BoatId));
    }

    public BoatTransactionResult Execute(
        BoatTransactionActorInput actor,
        MoveBoatTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var validation = ValidateOccupant(
            actor, command.Context, command.Boat, out var boat);
        if (validation is not null) return validation;
        if (!IsFinite(command.Target))
            return Rejected(actor, command.Context,
                BoatTransactionStatus.InvalidDestination,
                "The boat destination must contain finite coordinates.");
        if (Vector2.DistanceSquared(boat.Position, command.Target) >
            _options.MaximumMoveDistance * _options.MaximumMoveDistance)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.DestinationTooFar,
                "The boat destination exceeds the authoritative command range.");

        // A retransmitted semantic destination is already represented by the
        // active authority route. Coalesce it before cadence accounting so it
        // cannot create route/revision churn or another A* invocation.
        if (boat.Destination == command.Target)
            return Accepted(command.Context, actor.Gameplay);
        if (Vector2.DistanceSquared(boat.Position, command.Target) <=
            _options.DestinationArrivalDistance *
            _options.DestinationArrivalDistance)
            return Accepted(command.Context, actor.Gameplay);
        if (boat.PlanningCooldownSeconds > 0 || _plansRemaining <= 0)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.PlanningCadenceLocked,
                "Boat route planning is waiting for the next authority cadence.");

        // Charge both guards before entering the planner. Failed and
        // unreachable requests are intentionally charged: otherwise hostile
        // clients could make rejected A* searches free and monopolize the
        // owner thread.
        boat.PlanningCooldownSeconds = _options.PlanningCadenceSeconds;
        _plansRemaining--;

        if (IsBoatPositionOccupied(command.Target, boat.BoatId))
            return Rejected(actor, command.Context,
                BoatTransactionStatus.RouteUnreachable,
                "Another boat occupies that destination.");

        // Route admission treats every other boat's current cell as water that
        // is temporarily unavailable. Dynamic reservations in Advance provide
        // the second line of defence when independently accepted routes later
        // converge on the same cell.
        var occupiedNavigation = new OccupancyNavigationQuery(
            _navigation,
            boat.Position,
            _boats.Values
                .Where(value => value.BoatId != boat.BoatId)
                .Select(value => Cell(value.Position)));
        var route = _routePlanner(
            occupiedNavigation, boat.Position, command.Target,
            _options.MaximumPathSearchVisited);
        if (route.Count == 0 && Vector2.DistanceSquared(
                boat.Position, command.Target) >
            _options.DestinationArrivalDistance *
            _options.DestinationArrivalDistance)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.RouteUnreachable,
                "No navigable water route reaches that destination.");
        if (route.Count > _options.MaximumRouteWaypoints)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.RouteUnreachable,
                "The water route exceeds the authoritative route bound.");

        var previous = boat.ToSnapshot();
        boat.ReplaceRoute(route);
        boat.Revision = checked(boat.Revision + 1);
        return Accepted(command.Context, actor.Gameplay,
            new(BoatChangeKind.Updated, previous, boat.ToSnapshot()));
    }

    public BoatTransactionResult Execute(
        BoatTransactionActorInput actor,
        StopBoatTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var validation = ValidateOccupant(
            actor, command.Context, command.Boat, out var boat);
        if (validation is not null) return validation;
        if (boat.CurrentWaypoint is null)
            return Accepted(command.Context, actor.Gameplay);
        var previous = boat.ToSnapshot();
        boat.ClearRoute();
        boat.Revision = checked(boat.Revision + 1);
        return Accepted(command.Context, actor.Gameplay,
            new(BoatChangeKind.Updated, previous, boat.ToSnapshot()));
    }

    public BoatTransactionResult Execute(
        BoatTransactionActorInput actor,
        DisembarkBoatTransaction command)
    {
        EnsureOwner();
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var validation = ValidateOccupant(
            actor, command.Context, command.Boat, out var boat);
        if (validation is not null) return validation;
        var landing = BoatTravelRules.FindDisembarkLanding(
            _navigation, boat.Position, command.RequestedLanding);
        if (landing is null)
            return Rejected(actor, command.Context,
                BoatTransactionStatus.InvalidLanding,
                "No safe shore landing is within disembark range.");

        var previous = boat.ToSnapshot();
        boat.OccupantActorId = null;
        boat.OccupantPlayerId = null;
        boat.ClearRoute();
        boat.Revision = checked(boat.Revision + 1);
        _boatsByOccupant.Remove(actor.ActorId);
        var gameplay = AdvanceActor(actor.Gameplay);
        return Accepted(command.Context, gameplay,
            new(BoatChangeKind.Updated, previous, boat.ToSnapshot()),
            new(landing.Value, boat.WorldLevel, null));
    }

    public BoatStateDelta? StopForOccupant(ActorId actorId)
    {
        EnsureOwner();
        if (!_boatsByOccupant.TryGetValue(actorId, out var id) ||
            !_boats.TryGetValue(id, out var boat) ||
            boat.CurrentWaypoint is null)
            return null;
        var previous = boat.ToSnapshot();
        boat.ClearRoute();
        boat.Revision = checked(boat.Revision + 1);
        return new(BoatChangeKind.Updated, previous, boat.ToSnapshot());
    }

    public ImmutableArray<BoatStateDelta> Advance(double elapsedSeconds)
    {
        EnsureOwner();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        _plansRemaining = _options.MaximumPlansPerAdvance;
        var completed = ImmutableArray.CreateBuilder<BoatStateDelta>();
        var occupied = _boats.Values
            .Select(static value => Cell(value.Position))
            .ToHashSet();
        var claimedThisStep = new HashSet<(int X, int Y)>();
        foreach (var boat in _boats.Values.OrderBy(static value =>
                     value.BoatId.Value))
        {
            boat.AdvancePlanningCooldown(elapsedSeconds);
            if (boat.CurrentWaypoint is null) continue;
            var previous = boat.ToSnapshot();
            var origin = Cell(boat.Position);
            var blocked = new HashSet<(int X, int Y)>(occupied);
            blocked.Remove(origin);
            blocked.UnionWith(claimedThisStep);
            var traversed = new HashSet<(int X, int Y)> { origin };
            var outcome = Advance(
                boat, (float)elapsedSeconds, blocked, traversed);
            occupied.Remove(origin);
            occupied.Add(Cell(boat.Position));
            claimedThisStep.UnionWith(traversed);
            if (outcome == BoatAdvanceOutcome.InProgress) continue;
            boat.Revision = checked(boat.Revision + 1);
            completed.Add(new(
                BoatChangeKind.Updated, previous, boat.ToSnapshot()));
        }
        // Completion count is data-dependent, so the builder's spare capacity
        // cannot be transferred directly into an immutable array.
        return completed.ToImmutable();
    }

    public AuthoritativeBoatSnapshot? FindByOccupant(ActorId actorId)
    {
        EnsureOwner();
        return _boatsByOccupant.TryGetValue(actorId, out var id) &&
               _boats.TryGetValue(id, out var boat)
            ? boat.ToSnapshot()
            : null;
    }

    public AuthoritativeBoatSnapshot Capture(BoatId boatId)
    {
        EnsureOwner();
        return _boats.TryGetValue(boatId, out var boat)
            ? boat.ToSnapshot()
            : throw new KeyNotFoundException("The boat does not exist.");
    }

    public ImmutableArray<AuthoritativeBoatSnapshot> CaptureBoats()
    {
        EnsureOwner();
        return _boats.Values.OrderBy(static value => value.BoatId.Value)
            .Select(static value => value.ToSnapshot()).ToImmutableArray();
    }

    public AuthoritativeBoatTransactionsCheckpoint CaptureCheckpoint()
    {
        EnsureOwner();
        return new(_boats.Values
            .OrderBy(static value => value.BoatId.Value)
            .Select(static value => value.ToCheckpoint())
            .ToImmutableArray());
    }

    internal void ValidateCheckpoint(
        AuthoritativeBoatTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        _ = PrepareCheckpoint(checkpoint);
    }

    public void RestoreCheckpoint(
        AuthoritativeBoatTransactionsCheckpoint checkpoint)
    {
        EnsureOwner();
        if (_boats.Count != 0 || _boatsByOccupant.Count != 0)
            throw new InvalidOperationException(
                "Boats can only restore into an empty aggregate.");
        var prepared = PrepareCheckpoint(checkpoint);
        foreach (var boat in prepared.Values)
        {
            _boats.Add(boat.BoatId, boat);
            if (boat.OccupantActorId is { } actorId)
                _boatsByOccupant.Add(actorId, boat.BoatId);
        }
    }

    public static BoatId DerivePlayerBoatId(PlayerId ownerPlayerId)
    {
        if (ownerPlayerId.Value == Guid.Empty)
            throw new ArgumentException("A boat owner is required.",
                nameof(ownerPlayerId));
        Span<byte> input = stackalloc byte[32];
        input.Clear();
        "IRPG-PLAYER-BOAT"u8.CopyTo(input);
        ownerPlayerId.Value.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new(new Guid(digest[..16], bigEndian: true));
    }

    public static ulong DeriveNetworkEntityId(BoatId boatId)
    {
        if (boatId.IsEmpty)
            throw new ArgumentException("A boat identity is required.",
                nameof(boatId));
        Span<byte> input = stackalloc byte[32];
        input.Clear();
        "IRPG-BOAT-ENTITY"u8.CopyTo(input);
        boatId.Value.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        // The high bit reserves a disjoint network-identity namespace for
        // travel entities. Actor IDs are emitted with this bit clear, so a
        // boat and actor cannot alias even when their stable hashes collide.
        return BinaryPrimitives.ReadUInt64BigEndian(digest) |
               (1UL << 63);
    }

    private BoatTransactionResult? ValidateOccupant(
        BoatTransactionActorInput actor,
        WorldTransactionContext context,
        BoatReference reference,
        out BoatState boat)
    {
        var validation = Validate(actor, context, reference, out boat);
        if (validation is not null) return validation;
        return boat.OccupantActorId != actor.ActorId ||
               boat.OccupantPlayerId != actor.PlayerId ||
               !_boatsByOccupant.TryGetValue(actor.ActorId, out var id) ||
               id != boat.BoatId
            ? Rejected(actor, context, BoatTransactionStatus.NotAboard,
                "The actor is not aboard this boat.")
            : null;
    }

    private BoatTransactionResult? Validate(
        BoatTransactionActorInput actor,
        WorldTransactionContext context,
        BoatReference reference,
        out BoatState boat)
    {
        boat = null!;
        if (context.CommandId == Guid.Empty ||
            context.ActorId != actor.ActorId || !reference.IsWellFormed)
            return Rejected(actor, context,
                BoatTransactionStatus.InvalidCommand,
                "The boat command is malformed.");
        if (actor.ActorId.Value == Guid.Empty ||
            actor.PlayerId.Value == Guid.Empty ||
            actor.Gameplay.ActorRevision == 0 ||
            actor.Gameplay.Inventory.Revision == 0)
            return Rejected(actor, context,
                BoatTransactionStatus.ActorNotFound,
                "The actor gameplay state is invalid.");
        if (actor.Gameplay.Health <= 0)
            return Rejected(actor, context, BoatTransactionStatus.DeadActor,
                "Dead actors cannot use boats.");
        if (context.ExpectedActorRevision != actor.Gameplay.ActorRevision)
            return Rejected(actor, context,
                BoatTransactionStatus.StaleActorRevision,
                "The actor revision is stale.");
        if (context.ExpectedInventoryRevision !=
            actor.Gameplay.Inventory.Revision)
            return Rejected(actor, context,
                BoatTransactionStatus.StaleInventoryRevision,
                "The inventory revision is stale.");
        if (!_boats.TryGetValue(reference.BoatId, out boat!))
            return Rejected(actor, context,
                BoatTransactionStatus.BoatNotFound,
                "The boat does not exist.");
        if (reference.ExpectedRevision != boat.Revision)
            return Rejected(actor, context,
                BoatTransactionStatus.StaleBoatRevision,
                "The boat revision is stale.");
        if (actor.WorldLevel != boat.WorldLevel)
            return Rejected(actor, context,
                BoatTransactionStatus.WrongWorldLevel,
                "The actor and boat are on different world levels.");
        return null;
    }

    private Dictionary<BoatId, BoatState> PrepareCheckpoint(
        AuthoritativeBoatTransactionsCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Boats.IsDefault ||
            checkpoint.Boats.Length > _options.MaximumBoats)
            throw new InvalidDataException("The boat checkpoint is invalid.");
        var result = new Dictionary<BoatId, BoatState>();
        var occupants = new HashSet<ActorId>();
        var networkIds = new HashSet<ulong>();
        foreach (var value in checkpoint.Boats)
        {
            var networkEntityId = value.BoatId.IsEmpty
                ? 0
                : _networkEntityId(value.BoatId);
            if (value.BoatId.IsEmpty || value.OwnerPlayerId.Value == Guid.Empty ||
                value.WorldLevel != 0 || value.Revision == 0 ||
                !IsFinite(value.Position) ||
                !_navigation.IsNavigable(value.Position) ||
                !TryNormalizeFacing(value.Facing, out var facing) ||
                Vector2.DistanceSquared(facing, value.Facing) > 1e-6f ||
                !ValidGroup(value.GroupId) ||
                value.RemainingRoute.IsDefault ||
                value.RemainingRoute.Length > _options.MaximumRouteWaypoints ||
                !double.IsFinite(value.PlanningCooldownSeconds) ||
                value.PlanningCooldownSeconds < 0 ||
                value.PlanningCooldownSeconds >
                    _options.PlanningCadenceSeconds + 1e-9 ||
                value.RemainingRoute.Any(point =>
                    !IsFinite(point) || !_navigation.IsNavigable(point)) ||
                (value.OccupantActorId is null) !=
                    (value.OccupantPlayerId is null) ||
                value.OccupantActorId is { Value: var actorValue } &&
                    actorValue == Guid.Empty ||
                value.OccupantPlayerId is { Value: var playerValue } &&
                    playerValue == Guid.Empty ||
                value.OccupantActorId is { } occupant &&
                    !occupants.Add(occupant) ||
                networkEntityId == 0 || !networkIds.Add(networkEntityId) ||
                result.ContainsKey(value.BoatId))
                throw new InvalidDataException(
                    "The boat checkpoint contains an invalid boat.");
            var boat = new BoatState(
                value.BoatId, value.OwnerPlayerId, value.GroupId,
                value.Position, facing, value.WorldLevel, value.Revision,
                networkEntityId)
            {
                OccupantActorId = value.OccupantActorId,
                OccupantPlayerId = value.OccupantPlayerId,
                PlanningCooldownSeconds = value.PlanningCooldownSeconds
            };
            boat.ReplaceRoute(value.RemainingRoute);
            result.Add(value.BoatId, boat);
        }
        var positions = new HashSet<(int X, int Y)>();
        if (result.Values.Any(value => !positions.Add(Cell(value.Position))))
            throw new InvalidDataException(
                "The boat checkpoint contains overlapping boats.");
        return result;
    }

    private BoatAdvanceOutcome Advance(
        BoatState boat,
        float elapsedSeconds,
        HashSet<(int X, int Y)> blocked,
        HashSet<(int X, int Y)> traversed)
    {
        var remainingSeconds = elapsedSeconds;
        boat.Velocity = Vector2.Zero;
        while (remainingSeconds > 0 && boat.CurrentWaypoint is { } waypoint)
        {
            var difference = waypoint - boat.Position;
            var distanceSquared = difference.LengthSquared();
            if (!float.IsFinite(distanceSquared))
            {
                boat.ClearRoute();
                return BoatAdvanceOutcome.Stopped;
            }
            if (distanceSquared <= _options.DestinationArrivalDistance *
                _options.DestinationArrivalDistance)
            {
                if (!TryMoveSafely(boat, waypoint, blocked, traversed))
                {
                    boat.ClearRoute();
                    return BoatAdvanceOutcome.Stopped;
                }
                boat.CompleteWaypoint();
                continue;
            }
            var distance = MathF.Sqrt(distanceSquared);
            var direction = difference / distance;
            boat.Facing = direction;
            boat.Velocity = direction * _options.MovementSpeed;
            var available = _options.MovementSpeed * remainingSeconds;
            if (available + _options.DestinationArrivalDistance < distance)
            {
                if (!TryMoveSafely(
                        boat, boat.Position + direction * available,
                        blocked, traversed))
                {
                    boat.ClearRoute();
                    return BoatAdvanceOutcome.Stopped;
                }
                return BoatAdvanceOutcome.InProgress;
            }
            if (!TryMoveSafely(boat, waypoint, blocked, traversed))
            {
                boat.ClearRoute();
                return BoatAdvanceOutcome.Stopped;
            }
            remainingSeconds = Math.Max(
                0, remainingSeconds - distance / _options.MovementSpeed);
            boat.CompleteWaypoint();
        }
        if (boat.CurrentWaypoint is null) boat.Velocity = Vector2.Zero;
        return boat.CurrentWaypoint is null
            ? BoatAdvanceOutcome.Stopped
            : BoatAdvanceOutcome.InProgress;
    }

    private static bool TryMoveSafely(
        BoatState boat,
        Vector2 target,
        HashSet<(int X, int Y)> blocked,
        HashSet<(int X, int Y)> traversed)
    {
        const float maximumProbeDistance = .25f;
        var start = boat.Position;
        var distance = Vector2.Distance(start, target);
        var steps = Math.Max(1, (int)MathF.Ceiling(
            distance / maximumProbeDistance));
        var previousCell = Cell(start);
        for (var step = 1; step <= steps; step++)
        {
            var candidate = Vector2.Lerp(start, target, step / (float)steps);
            var nextCell = Cell(candidate);
            if (nextCell != previousCell)
            {
                if (blocked.Contains(nextCell)) return false;
                if (nextCell.X != previousCell.X &&
                    nextCell.Y != previousCell.Y)
                {
                    var horizontal = (nextCell.X, previousCell.Y);
                    var vertical = (previousCell.X, nextCell.Y);
                    if (blocked.Contains(horizontal) || blocked.Contains(vertical))
                        return false;
                    traversed.Add(horizontal);
                    traversed.Add(vertical);
                }
                traversed.Add(nextCell);
                previousCell = nextCell;
            }
            boat.Position = candidate;
        }
        return true;
    }

    private bool IsBoatPositionOccupied(
        Vector2 value,
        BoatId? except = null) =>
        _boats.Values.Any(boat =>
            boat.BoatId != except && Cell(boat.Position) == Cell(value));

    private static (int X, int Y) Cell(Vector2 value) =>
        ((int)MathF.Floor(value.X), (int)MathF.Floor(value.Y));

    private static bool CanUse(
        BoatTransactionActorInput actor,
        BoatState boat) =>
        actor.PlayerId == boat.OwnerPlayerId ||
        !string.IsNullOrWhiteSpace(actor.GroupId) &&
        string.Equals(actor.GroupId, boat.GroupId,
            StringComparison.OrdinalIgnoreCase);

    private static PlayerGameplaySnapshot AdvanceActor(
        PlayerGameplaySnapshot gameplay) => gameplay with
        {
            ActorRevision = checked(gameplay.ActorRevision + 1)
        };

    private static BoatTransactionResult Accepted(
        WorldTransactionContext context,
        PlayerGameplaySnapshot gameplay,
        BoatStateDelta? delta = null,
        BoatActorTransition? transition = null) => new(
            context.CommandId, BoatTransactionStatus.Accepted,
            gameplay.ActorRevision, gameplay.Inventory.Revision,
            gameplay, delta, transition);

    private static BoatTransactionResult Rejected(
        BoatTransactionActorInput actor,
        WorldTransactionContext context,
        BoatTransactionStatus status,
        string detail) => new(
            context.CommandId, status,
            actor.Gameplay.ActorRevision, actor.Gameplay.Inventory.Revision,
            actor.Gameplay, Detail: detail);

    private static bool TryNormalizeFacing(Vector2 value, out Vector2 facing)
    {
        if (value == Vector2.Zero)
        {
            facing = Vector2.UnitY;
            return true;
        }
        var length = value.Length();
        if (!float.IsFinite(length) || length <= 0)
        {
            facing = default;
            return false;
        }
        facing = value / length;
        return true;
    }

    private static bool ValidGroup(string? value) =>
        value is null || value.Length is > 0 and <= 128 &&
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => !char.IsControl(character));

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static IReadOnlyList<Vector2> PlanRoute(
        IBoatNavigationQuery query,
        Vector2 start,
        Vector2 target,
        int maximumVisited) => BoatRoutePlanner.Find(
        query, start, target, maximumVisited);

    private enum BoatAdvanceOutcome : byte
    {
        InProgress,
        Stopped
    }

    private sealed class OccupancyNavigationQuery : IBoatNavigationQuery
    {
        private readonly IBoatNavigationQuery _inner;
        private readonly (int X, int Y) _start;
        private readonly HashSet<(int X, int Y)> _occupied;

        public OccupancyNavigationQuery(
            IBoatNavigationQuery inner,
            Vector2 start,
            IEnumerable<(int X, int Y)> occupied)
        {
            _inner = inner;
            _start = Cell(start);
            _occupied = occupied.ToHashSet();
        }

        public bool IsNavigable(Vector2 point)
        {
            var cell = Cell(point);
            return _inner.IsNavigable(point) &&
                   (cell == _start || !_occupied.Contains(cell));
        }

        public bool IsLanding(Vector2 point) => _inner.IsLanding(point);

        public bool IsInitialMooring(Vector2 point) =>
            _inner.IsInitialMooring(point);
    }

    private void EnsureOwner()
    {
        var threadId = Environment.CurrentManagedThreadId;
        _ownerThreadId ??= threadId;
        if (_ownerThreadId != threadId)
            throw new InvalidOperationException(
                "Boat transactions must execute on their owning simulation thread.");
    }

    private sealed class BoatState
    {
        private readonly List<Vector2> _route = [];
        private int _routeIndex;

        public BoatState(
            BoatId boatId,
            PlayerId ownerPlayerId,
            string? groupId,
            Vector2 position,
            Vector2 facing,
            int worldLevel,
            uint revision,
            ulong networkEntityId)
        {
            BoatId = boatId;
            OwnerPlayerId = ownerPlayerId;
            GroupId = groupId;
            Position = position;
            Facing = facing;
            WorldLevel = worldLevel;
            Revision = revision;
            NetworkEntityId = networkEntityId;
        }

        public BoatId BoatId { get; }
        public PlayerId OwnerPlayerId { get; }
        public string? GroupId { get; }
        public ActorId? OccupantActorId { get; set; }
        public PlayerId? OccupantPlayerId { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Facing { get; set; }
        public Vector2 Velocity { get; set; }
        public int WorldLevel { get; }
        public uint Revision { get; set; }
        public ulong NetworkEntityId { get; }
        public double PlanningCooldownSeconds { get; set; }
        public Vector2? CurrentWaypoint =>
            _routeIndex < _route.Count ? _route[_routeIndex] : null;
        public Vector2? Destination =>
            _routeIndex < _route.Count ? _route[^1] : null;

        public void ReplaceRoute(IEnumerable<Vector2> route)
        {
            _route.Clear();
            _route.AddRange(route);
            _routeIndex = 0;
            Velocity = Vector2.Zero;
        }

        public void CompleteWaypoint()
        {
            if (_routeIndex < _route.Count) _routeIndex++;
            if (_routeIndex < _route.Count) return;
            _route.Clear();
            _routeIndex = 0;
        }

        public void ClearRoute()
        {
            _route.Clear();
            _routeIndex = 0;
            Velocity = Vector2.Zero;
        }

        public void AdvancePlanningCooldown(double elapsedSeconds) =>
            PlanningCooldownSeconds = Math.Max(
                0, PlanningCooldownSeconds - elapsedSeconds);

        public AuthoritativeBoatSnapshot ToSnapshot() => new(
            BoatId,
            NetworkEntityId,
            OwnerPlayerId,
            GroupId,
            OccupantActorId,
            OccupantPlayerId,
            Position,
            Facing,
            Velocity,
            Destination,
            WorldLevel,
            Revision);

        public AuthoritativeBoatCheckpoint ToCheckpoint() => new(
            BoatId,
            OwnerPlayerId,
            GroupId,
            OccupantActorId,
            OccupantPlayerId,
            Position,
            Facing,
            WorldLevel,
            Revision,
            _route.Skip(_routeIndex).ToImmutableArray(),
            PlanningCooldownSeconds);
    }
}
