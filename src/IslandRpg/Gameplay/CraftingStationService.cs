using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class CraftingStationService
{
    public const float InteractionRange = 3.5f;
    private static readonly HashSet<string> StationItemIds =
        CraftingSkill.Recipes
            .Where(recipe => recipe.RequiredStationItemId is not null)
            .Select(recipe => recipe.RequiredStationItemId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsStation(string itemId) =>
        StationItemIds.Contains(itemId);

    public static string ActionLabel(string itemId) =>
        itemId switch
        {
            ItemIds.Bloomery => "Smelt",
            ItemIds.SmithingAnvil => "Smith",
            _ => "Craft"
        };

    public static bool IsWithinRange(
        IReadOnlyList<WorldGroundObject> groundObjects,
        string stationItemId,
        Vector2 playerPosition)
    {
        var rangeSquared = InteractionRange * InteractionRange;
        for (var index = 0; index < groundObjects.Count; index++)
        {
            var groundObject = groundObjects[index];
            if (!string.Equals(
                    groundObject.ItemId, stationItemId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var delta = new Vector2(
                groundObject.X, groundObject.Y) - playerPosition;
            if (delta.LengthSquared <= rangeSquared)
                return true;
        }
        return false;
    }

    public static void CollectWithinRange(
        IReadOnlyList<WorldGroundObject> groundObjects,
        Vector2 playerPosition,
        ISet<string> destination)
    {
        var rangeSquared = InteractionRange * InteractionRange;
        for (var index = 0; index < groundObjects.Count; index++)
        {
            var groundObject = groundObjects[index];
            if (!IsStation(groundObject.ItemId))
                continue;
            var delta = new Vector2(
                groundObject.X, groundObject.Y) - playerPosition;
            if (delta.LengthSquared <= rangeSquared)
                destination.Add(groundObject.ItemId);
        }
    }
}
