using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal sealed record VillagerProjectRequirement(
    string ItemId,
    int Quantity);

internal sealed record VillagerProjectAssignment(
    string ProjectItemId,
    string BuilderId,
    IReadOnlyList<VillagerProjectRequirement> Requirements,
    double AssignedGameSeconds,
    string? LeaderId = null,
    float WorksiteX = 0,
    float WorksiteY = 0,
    int WorksiteLevel = 0);

internal sealed record VillagerSettlementProjectPlan(
    string ProjectItemId,
    string BuilderId,
    IReadOnlyDictionary<string, IReadOnlyList<VillagerProjectRequirement>>
        Assignments,
    string LeaderId,
    Vector2 Worksite,
    int WorksiteLevel);

internal static class VillagerSettlementProjectService
{
    public const double AccountabilityDelayGameSeconds = 30 * 60;

    public static VillagerSettlementProjectPlan? Plan(
        IReadOnlyList<VillagerState> villagers,
        IReadOnlySet<string> placedItems,
        string? leaderId = null)
    {
        var living = villagers.Where(value => value.Health > 0).ToArray();
        if (living.Length < 2) return null;
        string projectItemId;
        IReadOnlyList<VillagerProjectRequirement> requirements;
        if (!placedItems.Contains(ItemIds.Campfire))
        {
            projectItemId = ItemIds.Campfire;
            requirements = [new(ItemIds.LargeRock, 3)];
        }
        else if (!placedItems.Contains(ItemIds.Workbench))
        {
            projectItemId = ItemIds.Workbench;
            requirements = [new(ItemIds.Logs, 4), new(ItemIds.Sticks, 2)];
        }
        else if (!placedItems.Contains(ItemIds.StorageChest))
        {
            projectItemId = ItemIds.StorageChest;
            requirements =
            [
                new(ItemIds.Logs, 6), new(ItemIds.Sticks, 2),
                new(ItemIds.PlantFibres, 3)
            ];
        }
        else return null;
        var incumbentBuilderId = living
            .Select(value => value.ProjectAssignment)
            .Where(value => value?.ProjectItemId == projectItemId)
            .Select(value => value!.BuilderId)
            .FirstOrDefault(id => living.Any(value => value.Id == id));
        var builder = incumbentBuilderId is null
            ? living
            .OrderByDescending(value =>
                value.WorkRole == VillagerWorkRole.Crafting)
            .ThenByDescending(value => value.CraftingExperience)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .First()
            : living.First(value => value.Id == incumbentBuilderId);
        var leader = living.FirstOrDefault(value => value.Id == leaderId) ??
                     living.FirstOrDefault(value =>
                         value.Id == builder.RecognizedLeaderId) ?? builder;
        var incumbent = living.Select(value => value.ProjectAssignment)
            .FirstOrDefault(value => value?.ProjectItemId == projectItemId);
        var worksite = incumbent is null
            ? new Vector2(leader.PositionX, leader.PositionY)
            : new Vector2(incumbent.WorksiteX, incumbent.WorksiteY);
        var worksiteLevel = incumbent?.WorksiteLevel ?? leader.WorldLevel;
        var stableAssignments = living
            .Where(value => value.ProjectAssignment is { } assignment &&
                assignment.ProjectItemId == projectItemId &&
                assignment.BuilderId == builder.Id)
            .ToDictionary(
                value => value.Id,
                value => value.ProjectAssignment!.Requirements,
                StringComparer.Ordinal);
        if (stableAssignments.Count == living.Length)
            return new(
                projectItemId,
                builder.Id,
                stableAssignments,
                incumbent?.LeaderId ?? leader.Id,
                worksite,
                worksiteLevel);
        return Assign(
            projectItemId, builder.Id, leader.Id, worksite, worksiteLevel,
            living, requirements);
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
               left.LeaderId == right.LeaderId &&
               left.WorksiteX == right.WorksiteX &&
               left.WorksiteY == right.WorksiteY &&
               left.WorksiteLevel == right.WorksiteLevel &&
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
        string leaderId,
        Vector2 worksite,
        int worksiteLevel,
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
        return new(
            projectItemId, builderId, assignments,
            leaderId, worksite, worksiteLevel);
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
