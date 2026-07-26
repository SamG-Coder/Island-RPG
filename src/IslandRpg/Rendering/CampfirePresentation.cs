using IslandRpg.Gameplay;
using IslandRpg.World;

namespace IslandRpg.Rendering;

internal static class CampfirePresentation
{
    public const int CanvasWidth = 58;
    public const int CanvasHeight = 58;
    public const int FuelWidth = 26;
    public const int FuelHeight = 13;
    public const int FuelAnchorX = 29;
    public const int FuelAnchorY = 33;

    public static string FueledAtlasKey(string fuelItemId) =>
        $"PLACEABLE_OBJECT#campfire:fueled:{fuelItemId}";

    public static string LitAtlasKey(string fuelItemId, int frame) =>
        $"PLACEABLE_OBJECT#campfire:lit:{fuelItemId}:{Math.Clamp(
            frame, 0, CampfireService.AnimationFrameCount - 1)}";

    public static string AtlasKey(
        WorldGroundObject campfire,
        double gameSeconds,
        double realSeconds) =>
        CampfireService.State(campfire, gameSeconds) switch
        {
            CampfireState.Fueled => FueledAtlasKey(
                campfire.FuelItemId!),
            CampfireState.Lit => LitAtlasKey(
                campfire.FuelItemId!,
                CampfireService.AnimationFrame(realSeconds)),
            _ => $"PLACEABLE_OBJECT#{ItemIds.Campfire}"
        };
}
