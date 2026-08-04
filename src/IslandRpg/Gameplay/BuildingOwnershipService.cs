namespace IslandRpg.Gameplay;

using IslandRpg.World;

internal static class BuildingOwnershipService
{
    public const int MaximumResidents = 16;

    public static bool HasOwner(WorldGroundObject value) =>
        !string.IsNullOrWhiteSpace(value.OwnerId) ||
        !string.IsNullOrWhiteSpace(value.GroupOwnerId);

    public static bool CanManage(
        WorldGroundObject value, string actorId,
        SettlementGroupState? group = null) =>
        SettlementGroupService.CanAccess(
            group, actorId, value.OwnerId, value.GroupOwnerId);

    public static WorldGroundObject AssignIndividual(
        WorldGroundObject value, string ownerId) => value with
    {
        OwnerId = ownerId,
        GroupOwnerId = null
    };

    public static WorldGroundObject AssignGroup(
        WorldGroundObject value, string groupId) => value with
    {
        OwnerId = null,
        GroupOwnerId = groupId
    };

    public static WorldGroundObject SetResidents(
        WorldGroundObject value, IEnumerable<string> residentIds)
    {
        if (!HouseCatalog.IsHouse(value.ItemId)) return value;
        var residents = residentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumResidents)
            .ToArray();
        return value with { ResidentIds = residents };
    }
}
