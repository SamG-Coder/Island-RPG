using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal sealed record VillagerProjectRequirement(
    string ItemId,
    int Quantity);

internal sealed record VillagerProjectAssignment(
    string ProjectItemId,
    string BuilderId,
    IReadOnlyList<VillagerProjectRequirement> Requirements,
    double AssignedGameSeconds);

internal sealed record VillagerSettlementProjectPlan(
    string ProjectItemId,
    string BuilderId,
    IReadOnlyDictionary<string, IReadOnlyList<VillagerProjectRequirement>>
        Assignments);

internal static class VillagerSettlementProjectService
{
    public const double AccountabilityDelayGameSeconds = 30 * 60;

    public static VillagerSettlementProjectPlan? Plan(
        IReadOnlyList<VillagerState> villagers,
        IReadOnlySet<string> placedItems)
    {
        var living = villagers.Where(value => value.Health > 0).ToArray();
        if (living.Length < 2) return null;
        var builder = living
            .OrderByDescending(value =>
                value.WorkRole == VillagerWorkRole.Crafting)
            .ThenByDescending(value => value.CraftingExperience)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .First();
        if (!placedItems.Contains(ItemIds.Campfire))
            return Assign(
                ItemIds.Campfire,
                builder.Id,
                living,
                [new(ItemIds.LargeRock, 3)]);
        if (!placedItems.Contains(ItemIds.Workbench))
            return Assign(
                ItemIds.Workbench,
                builder.Id,
                living,
                [new(ItemIds.Logs, 4), new(ItemIds.Sticks, 2)]);
        if (!placedItems.Contains(ItemIds.StorageChest))
            return Assign(
                ItemIds.StorageChest,
                builder.Id,
                living,
                [
                    new(ItemIds.Logs, 6),
                    new(ItemIds.Sticks, 2),
                    new(ItemIds.PlantFibres, 3)
                ]);
        return null;
    }

    public static bool MatchesRequirement(
        string itemId,
        string requirementItemId)
    {
        if (string.Equals(
                itemId, requirementItemId,
                StringComparison.OrdinalIgnoreCase))
            return true;
        return requirementItemId == ItemIds.Logs &&
               ItemCatalog.TryGet(itemId, out var item) &&
               item.HasTag(ItemTag.Log);
    }

    public static bool NeedsItem(VillagerState villager, string itemId) =>
        villager.ProjectAssignment?.Requirements.Any(requirement =>
            MatchesRequirement(itemId, requirement.ItemId) &&
            CountMatching(villager.Inventory, requirement.ItemId) <
            requirement.Quantity) == true;

    public static bool SameAssignment(
        VillagerProjectAssignment? left,
        VillagerProjectAssignment? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.ProjectItemId == right.ProjectItemId &&
               left.BuilderId == right.BuilderId &&
               left.AssignedGameSeconds == right.AssignedGameSeconds &&
               left.Requirements.SequenceEqual(right.Requirements);
    }

    public static int ContributionSlot(
        VillagerState contributor,
        VillagerState builder)
    {
        if (contributor.ProjectAssignment is not { } assignment ||
            contributor.Id == assignment.BuilderId)
            return -1;
        for (var slot = 0; slot < contributor.Inventory.Length; slot++)
        {
            if (contributor.Inventory[slot] is not { } itemId) continue;
            foreach (var requirement in assignment.Requirements)
                if (MatchesRequirement(itemId, requirement.ItemId) &&
                    CountMatching(builder.Inventory, requirement.ItemId) <
                    TotalProjectRequirement(
                        assignment.ProjectItemId,
                        requirement.ItemId))
                    return slot;
        }
        return -1;
    }

    public static bool IsStalled(
        VillagerState villager,
        double gameSeconds) =>
        villager.ProjectAssignment is { Requirements.Count: > 0 } assignment &&
        gameSeconds - assignment.AssignedGameSeconds >=
        AccountabilityDelayGameSeconds &&
        assignment.Requirements.Any(requirement =>
            CountMatching(villager.Inventory, requirement.ItemId) <
            requirement.Quantity);

    public static Vector2 ExplorationTarget(
        VillagerState villager,
        double gameSeconds)
    {
        var phase = (int)Math.Floor(gameSeconds /
            VillagerSimulation.NearbyDecisionSeconds);
        var hash = HashCode.Combine(villager.Id, phase);
        var angle = (uint)hash / (float)uint.MaxValue * MathF.Tau;
        return new Vector2(villager.PositionX, villager.PositionY) +
               new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 8;
    }

    private static VillagerSettlementProjectPlan Assign(
        string projectItemId,
        string builderId,
        IReadOnlyList<VillagerState> villagers,
        IReadOnlyList<VillagerProjectRequirement> requirements)
    {
        var assignments = villagers.ToDictionary(
            value => value.Id,
            _ => (IReadOnlyList<VillagerProjectRequirement>)
                new List<VillagerProjectRequirement>(),
            StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            var ordered = villagers
                .OrderByDescending(value => Suitability(
                    value, requirement.ItemId, builderId))
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            for (var unit = 0; unit < requirement.Quantity; unit++)
            {
                var villager = ordered[unit % ordered.Length];
                var list = (List<VillagerProjectRequirement>)
                    assignments[villager.Id];
                var existing = list.FindIndex(value =>
                    value.ItemId == requirement.ItemId);
                if (existing < 0)
                    list.Add(new(requirement.ItemId, 1));
                else
                    list[existing] = list[existing] with
                    {
                        Quantity = list[existing].Quantity + 1
                    };
            }
        }
        return new(projectItemId, builderId, assignments);
    }

    private static int Suitability(
        VillagerState villager,
        string itemId,
        string builderId)
    {
        if (itemId == ItemIds.Logs)
            return (villager.WorkRole == VillagerWorkRole.Wood ? 100 : 0) +
                   (PlayerInventory.BestAxe(villager.Inventory)?
                        .WoodcuttingPower ?? 0) * 20;
        if (itemId == ItemIds.LargeRock)
            return (villager.Id == builderId ? 30 : 0) +
                   (villager.WorkRole == VillagerWorkRole.Exploration
                       ? 20
                       : 0);
        if (itemId == ItemIds.PlantFibres)
            return villager.WorkRole == VillagerWorkRole.Food ? 50 : 0;
        return villager.Id == builderId ? 25 : 0;
    }

    private static int TotalProjectRequirement(
        string projectItemId,
        string itemId) => projectItemId switch
    {
        ItemIds.Campfire when itemId == ItemIds.LargeRock => 3,
        ItemIds.Workbench when itemId == ItemIds.Logs => 4,
        ItemIds.Workbench when itemId == ItemIds.Sticks => 2,
        ItemIds.StorageChest when itemId == ItemIds.Logs => 6,
        ItemIds.StorageChest when itemId == ItemIds.Sticks => 2,
        ItemIds.StorageChest when itemId == ItemIds.PlantFibres => 3,
        _ => 1
    };

    private static int CountMatching(string?[] inventory, string itemId) =>
        inventory.Count(value =>
            value is not null && MatchesRequirement(value, itemId));
}
