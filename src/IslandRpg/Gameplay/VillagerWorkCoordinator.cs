namespace IslandRpg.Gameplay;

internal enum VillagerWorkRole : byte
{
    Unassigned,
    Food,
    Wood,
    Crafting,
    Exploration
}

internal readonly record struct VillagerTargetReservation(
    string TargetKey,
    string ActorId,
    double ExpiresGameSeconds);

/// <summary>
/// Small, allocation-stable coordinator for scarce world targets. Rendering
/// controllers provide stable keys; the coordinator owns contention and expiry.
/// </summary>
internal sealed class VillagerWorkCoordinator
{
    public const double ReservationSeconds = 20 * 60;

    private readonly Dictionary<string, VillagerTargetReservation> _targets =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _actorTargets =
        new(StringComparer.Ordinal);
    private readonly List<VillagerTargetReservation> _expired = [];

    public int Count => _targets.Count;

    public bool IsAvailable(
        string targetKey, string actorId, double gameSeconds)
    {
        if (!_targets.TryGetValue(targetKey, out var reservation))
            return true;
        if (reservation.ExpiresGameSeconds <= gameSeconds)
        {
            Remove(reservation);
            return true;
        }
        return reservation.ActorId == actorId;
    }

    public bool TryReserve(
        string targetKey,
        string actorId,
        double gameSeconds,
        double durationGameSeconds = ReservationSeconds)
    {
        if (!IsAvailable(targetKey, actorId, gameSeconds)) return false;
        if (_actorTargets.TryGetValue(actorId, out var priorKey) &&
            priorKey != targetKey &&
            _targets.TryGetValue(priorKey, out var prior))
            Remove(prior);
        var reservation = new VillagerTargetReservation(
            targetKey,
            actorId,
            gameSeconds + Math.Max(1, durationGameSeconds));
        _targets[targetKey] = reservation;
        _actorTargets[actorId] = targetKey;
        return true;
    }

    public void ReleaseActor(string actorId)
    {
        if (_actorTargets.TryGetValue(actorId, out var key) &&
            _targets.TryGetValue(key, out var reservation))
            Remove(reservation);
    }

    public void ReleaseTarget(string targetKey, string actorId)
    {
        if (_targets.TryGetValue(targetKey, out var reservation) &&
            reservation.ActorId == actorId)
            Remove(reservation);
    }

    public void Expire(double gameSeconds)
    {
        if (_targets.Count == 0) return;
        _expired.Clear();
        foreach (var reservation in _targets.Values)
            if (reservation.ExpiresGameSeconds <= gameSeconds)
                _expired.Add(reservation);
        foreach (var reservation in _expired)
            Remove(reservation);
        _expired.Clear();
    }

    public void Clear()
    {
        _targets.Clear();
        _actorTargets.Clear();
        _expired.Clear();
    }

    public static IReadOnlyDictionary<string, VillagerWorkRole> AssignRoles(
        IReadOnlyList<VillagerState> villagers)
    {
        var living = villagers.Where(value => value.Health > 0)
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var roles = new Dictionary<string, VillagerWorkRole>(
            living.Length, StringComparer.Ordinal);
        if (living.Length == 0) return roles;

        var food = living
            .OrderBy(value => value.Hunger)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .First();
        roles[food.Id] = VillagerWorkRole.Food;

        var remaining = living.Where(value => value.Id != food.Id).ToArray();
        if (remaining.Length > 0)
        {
            var wood = remaining
                .OrderByDescending(value =>
                    PlayerInventory.BestAxe(value.Inventory) is not null)
                .ThenByDescending(value => value.WoodcuttingExperience)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .First();
            roles[wood.Id] = VillagerWorkRole.Wood;
        }
        foreach (var villager in remaining.Where(value =>
                     !roles.ContainsKey(value.Id)))
            roles[villager.Id] = villager.Inventory.Any(item =>
                    item == ItemIds.StoneKnife ||
                    item == ItemIds.StoneHammer)
                ? VillagerWorkRole.Crafting
                : VillagerWorkRole.Exploration;
        return roles;
    }

    private void Remove(VillagerTargetReservation reservation)
    {
        _targets.Remove(reservation.TargetKey);
        if (_actorTargets.TryGetValue(reservation.ActorId, out var key) &&
            key == reservation.TargetKey)
            _actorTargets.Remove(reservation.ActorId);
    }
}
