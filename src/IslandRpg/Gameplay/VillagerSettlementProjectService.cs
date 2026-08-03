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
    public const double BuilderReplacementDelayGameSeconds = 30 * 60;

    public static VillagerSettlementProjectPlan? Plan(
        IReadOnlyList<VillagerState> villagers,
        IReadOnlySet<string> placedItems,
        string? leaderId = null,
        double gameSeconds = 0)
    {
        var living = villagers.Where(value =>
            value.Health > 0 && !value.IndependentByChoice).ToArray();
        if (!IndependentSurvivorPolicy.CanFormSettlement(living.Length))
            return null;
        var available = living
            .Where(VillagerWorkCoordinator.IsAvailableForWork)
            .ToArray();
        string projectItemId;
        IReadOnlyList<VillagerProjectRequirement> requirements;
        if (!placedItems.Contains(ItemIds.Campfire))
        {
            projectItemId = ItemIds.Campfire;
            requirements = RequirementsFor(projectItemId);
        }
        else if (!placedItems.Contains(ItemIds.Workbench))
        {
            projectItemId = ItemIds.Workbench;
            requirements = RequirementsFor(projectItemId);
        }
        else if (!placedItems.Contains(ItemIds.StorageChest))
        {
            projectItemId = ItemIds.StorageChest;
            requirements = RequirementsFor(projectItemId);
        }
        else return null;
        var incumbentAssignment = living
            .Select(value => value.ProjectAssignment)
            .Where(value => value?.ProjectItemId == projectItemId)
            .FirstOrDefault();
        var incumbentBuilder = incumbentAssignment is null
            ? null
            : living.FirstOrDefault(value =>
                value.Id == incumbentAssignment.BuilderId);
        var retainIncumbent = incumbentBuilder is not null &&
            (CanRemainBuilder(incumbentBuilder) ||
             gameSeconds - incumbentAssignment!.AssignedGameSeconds <
             BuilderReplacementDelayGameSeconds);
        var builder = retainIncumbent
            ? incumbentBuilder!
            : available
            .OrderByDescending(value =>
                value.WorkRole == VillagerWorkRole.Crafting)
            .ThenByDescending(value => value.CraftingExperience)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .FirstOrDefault() ?? incumbentBuilder;
        if (builder is null) return null;
        var participants = retainIncumbent || incumbentBuilder is null
            ? living
            : living.Where(value => value.Id != incumbentBuilder.Id)
                .ToArray();
        var leader = living.FirstOrDefault(value => value.Id == leaderId) ??
                     living.FirstOrDefault(value =>
                         value.Id == builder.RecognizedLeaderId) ?? builder;
        var incumbent = incumbentAssignment;
        var worksite = incumbent is null
            ? new Vector2(leader.PositionX, leader.PositionY)
            : new Vector2(incumbent.WorksiteX, incumbent.WorksiteY);
        var worksiteLevel = incumbent?.WorksiteLevel ?? leader.WorldLevel;
        var stableAssignments = participants
            .Where(value => value.ProjectAssignment is { } assignment &&
                assignment.ProjectItemId == projectItemId &&
                assignment.BuilderId == builder.Id)
            .ToDictionary(
                value => value.Id,
                value => value.ProjectAssignment!.Requirements,
                StringComparer.Ordinal);
        if (stableAssignments.Count == participants.Length)
            return new(
                projectItemId,
                builder.Id,
                stableAssignments,
                incumbent?.LeaderId ?? leader.Id,
                worksite,
                worksiteLevel);
        return Assign(
            projectItemId, builder.Id, leader.Id, worksite, worksiteLevel,
            participants, requirements);
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

    public static IReadOnlyList<VillagerProjectRequirement> RequirementsFor(
        string projectItemId) => projectItemId switch
    {
        ItemIds.Campfire => [new(ItemIds.LargeRock, 3)],
        ItemIds.Workbench =>
            [new(ItemIds.Logs, 4), new(ItemIds.Sticks, 2)],
        ItemIds.StorageChest =>
        [
            new(ItemIds.Logs, 6), new(ItemIds.Sticks, 2),
            new(ItemIds.PlantFibres, 3)
        ],
        _ => []
    };

    public static VillagerProjectRequirement? SuggestedContribution(
        VillagerSettlementProjectPlan plan) =>
        plan.Assignments.Values
            .SelectMany(value => value)
            .GroupBy(value => value.ItemId, StringComparer.Ordinal)
            .Select(group => new VillagerProjectRequirement(
                group.Key, group.Sum(value => value.Quantity)))
            .OrderByDescending(value => value.Quantity)
            .ThenBy(value => value.ItemId, StringComparer.Ordinal)
            .FirstOrDefault();

    public static bool NeedsItem(VillagerState villager, string itemId) =>
        villager.ProjectAssignment?.Requirements.Any(requirement =>
            MatchesRequirement(itemId, requirement.ItemId) &&
            CountMatching(villager.Inventory, requirement.ItemId) <
            requirement.Quantity) == true;

    public static bool CarriesCompletedProject(VillagerState villager) =>
        villager.ProjectAssignment is { } assignment &&
        assignment.BuilderId == villager.Id &&
        villager.Inventory.Any(value => value == assignment.ProjectItemId);

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
            foreach (var requirement in RequirementsFor(
                         assignment.ProjectItemId))
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

    public static Vector2 ContinuingExplorationTarget(
        VillagerState villager,
        double gameSeconds,
        float arrivalDistance = 1f)
    {
        if (villager.TargetX is { } x && villager.TargetY is { } y)
        {
            var existing = new Vector2(x, y);
            if (Vector2.DistanceSquared(
                    new(villager.PositionX, villager.PositionY), existing) >
                arrivalDistance * arrivalDistance)
                return existing;
        }
        return ExplorationTarget(villager, gameSeconds);
    }

    public static Vector2 RendezvousPoint(
        Vector2 worksite,
        string actorId,
        bool isBuilder)
    {
        if (isBuilder) return worksite;
        var hash = StableHash(actorId);
        var angle = (hash & 0xffff) / 65535f * MathF.Tau;
        return worksite + new Vector2(
            MathF.Cos(angle), MathF.Sin(angle)) * .8f;
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

    private static bool CanRemainBuilder(VillagerState villager) =>
        villager.Health > 20 &&
        villager.ConflictIntent == VillagerConflictIntent.None;

    private static int TotalProjectRequirement(
        string projectItemId,
        string itemId) => RequirementsFor(projectItemId)
        .Where(value => MatchesRequirement(itemId, value.ItemId))
        .Sum(value => value.Quantity);

    private static int CountMatching(string?[] inventory, string itemId) =>
        inventory.Count(value =>
            value is not null && MatchesRequirement(value, itemId));

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619;
            return (int)hash;
        }
    }
}
