using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class CharcoalService
{
    public static bool IsReady(
        WorldGroundObject campfire,
        double gameSeconds) =>
        CampfireService.IsCampfire(campfire) &&
        campfire.LitUntilGameSeconds > 0 &&
        campfire.LitUntilGameSeconds <= gameSeconds &&
        campfire.FuelItemId is { } fuelItemId &&
        ItemCatalog.Get(fuelItemId).HasTag(ItemTag.Log);
}
