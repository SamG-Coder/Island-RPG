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
    public const float FoodRoleHungerHysteresis = 15;
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
        var allLiving = villagers.Where(value => value.Health > 0)
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var roles = new Dictionary<string, VillagerWorkRole>(
            allLiving.Length, StringComparer.Ordinal);
        if (allLiving.Length == 0) return roles;
        var living = allLiving.Where(IsAvailableForWork).ToArray();
        foreach (var unavailable in allLiving.Where(value =>
                     !IsAvailableForWork(value)))
            roles[unavailable.Id] = VillagerWorkRole.Unassigned;
        if (living.Length == 0) return roles;
        var forecast = VillagerWorkPlanner.Forecast(allLiving);

        if (living.Length == 1)
        {
            roles[living[0].Id] = DevelopmentRole(living[0], forecast);
            return roles;
        }
        var foodCandidates = living
            .Where(value => value.Health > 20 && value.Hunger > 15)
            .DefaultIfEmpty(living
                .OrderByDescending(value => value.Health)
                .First())
            .ToArray();
        var foodSlots = living.Length < 4
            ? 1
            : Math.Clamp(
                (forecast.FoodDeficit + 7) / 8,
                1,
                Math.Max(1, (living.Length + 2) / 3));
        foreach (var food in RankForRole(
                     foodCandidates, VillagerWorkRole.Food, forecast)
                 .Take(foodSlots))
            roles[food.Id] = VillagerWorkRole.Food;

        var remaining = living.Where(value => !roles.ContainsKey(value.Id))
            .ToArray();
        if (remaining.Length > 0)
        {
            if (living.Length == 2)
            {
                roles[remaining[0].Id] =
                    DevelopmentRole(remaining[0], forecast);
                return roles;
            }
            var woodSlots = living.Length < 4
                ? 1
                : Math.Clamp(
                    (forecast.WoodDeficit + 19) / 20,
                    1,
                    Math.Max(1, (living.Length + 2) / 3));
            foreach (var wood in RankForRole(
                         remaining, VillagerWorkRole.Wood, forecast)
                     .Take(Math.Min(woodSlots, remaining.Length)))
                roles[wood.Id] = VillagerWorkRole.Wood;
        }
        var development = remaining.Where(value =>
                !roles.ContainsKey(value.Id))
            .ToList();
        if (development.Count >= 2)
        {
            var explorer = RankForRole(
                    development, VillagerWorkRole.Exploration, forecast)
                .First();
            roles[explorer.Id] = VillagerWorkRole.Exploration;
            development.Remove(explorer);
            var crafter = RankForRole(
                    development, VillagerWorkRole.Crafting, forecast)
                .First();
            roles[crafter.Id] = VillagerWorkRole.Crafting;
            development.Remove(crafter);
        }
        foreach (var villager in development)
        {
            var crafting = VillagerWorkPlanner.Suitability(
                villager, VillagerWorkRole.Crafting, forecast);
            var exploration = VillagerWorkPlanner.Suitability(
                villager, VillagerWorkRole.Exploration, forecast);
            roles[villager.Id] = crafting > exploration
                ? VillagerWorkRole.Crafting
                : VillagerWorkRole.Exploration;
        }
        return roles;
    }

    public static bool IsAvailableForWork(VillagerState villager) =>
        villager.Health > 20 &&
        villager.Energy >= VillagerFatigueService.RestThreshold &&
        villager.ConflictIntent == VillagerConflictIntent.None &&
        villager.Activity is not (
            VillagerActivity.Conversing or
            VillagerActivity.Reflecting or
            VillagerActivity.Following or
            VillagerActivity.Resting or
            VillagerActivity.Blocked);

    private static IOrderedEnumerable<VillagerState> RankForRole(
        IEnumerable<VillagerState> villagers,
        VillagerWorkRole role,
        VillagerResourceForecast forecast) =>
        villagers.OrderByDescending(value =>
                VillagerWorkPlanner.Suitability(value, role, forecast) +
                (value.WorkRole == role ? FoodRoleHungerHysteresis : 0))
            .ThenBy(value => value.Id, StringComparer.Ordinal);

    private static VillagerWorkRole DevelopmentRole(
        VillagerState villager,
        VillagerResourceForecast forecast)
    {
        var crafting = VillagerWorkPlanner.Suitability(
            villager, VillagerWorkRole.Crafting, forecast);
        var exploration = VillagerWorkPlanner.Suitability(
            villager, VillagerWorkRole.Exploration, forecast);
        return crafting >= exploration
            ? VillagerWorkRole.Crafting
            : VillagerWorkRole.Exploration;
    }

    private void Remove(VillagerTargetReservation reservation)
    {
        _targets.Remove(reservation.TargetKey);
        if (_actorTargets.TryGetValue(reservation.ActorId, out var key) &&
            key == reservation.TargetKey)
            _actorTargets.Remove(reservation.ActorId);
    }
}
