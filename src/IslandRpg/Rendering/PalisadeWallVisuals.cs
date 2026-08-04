namespace IslandRpg.Rendering;

internal static class PalisadeWallVisuals
{
    // WALL1NNG is the AoE composite definition. Its visible palisade is the
    // WALL1N1G delta layer; the composite's own SLP only contains selection
    // flags, so render the visible layer directly.
    public const string WallGraphic = "WALL1N1G";
    public const string ShadowGraphic = "WALL1N0G";
    public const short WallGraphicId = 587;
    public const short ShadowGraphicId = 586;
    public const int FrontFrame = 3;

    public static string WallFrame(int frame) =>
        $"{WallGraphic}@{WallGraphicId}#{frame}";
    public static string ShadowFrame(int frame) =>
        $"{ShadowGraphic}@{ShadowGraphicId}#{frame}";
    public static string FrontFrameKey => WallFrame(FrontFrame);
}
