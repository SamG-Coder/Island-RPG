namespace IslandRpg.Rendering;

using IslandRpg.Gameplay;
using IslandRpg.World;

internal static class PalisadeWallVisuals
{
    // WALL1NNG is the AoE composite definition. Its visible palisade is the
    // WALL1N1G delta layer; the composite's own SLP only contains selection
    // flags, so render the visible layer directly.
    public const string WallGraphic = "WALL1N1G";
    public const string ShadowGraphic = "WALL1N0G";
    public const short WallGraphicId = 587;
    public const short ShadowGraphicId = 586;
    // The single post is used for the build icon, hover ghost and wall caps.
    public const int FrontFrame = 2;

    public static string WallFrame(int frame) =>
        $"{WallGraphic}@{WallGraphicId}#{frame}";
    public static string ShadowFrame(int frame) =>
        $"{ShadowGraphic}@{ShadowGraphicId}#{frame}";
    public static string FrontFrameKey => WallFrame(FrontFrame);

    public static (string Wall, string? Shadow) Resolve(
        WorldGroundObject value, int frame)
    {
        frame = Math.Clamp(frame, 0, 4);
        var stage = ConstructionService.Stage(value);
        if (stage == ConstructionStage.Complete)
            return (WallFrame(frame), ShadowFrame(frame));
        // Wooden palisades have one authored base per direction and then the
        // completed wall. Later WCON2 stages belong to stone construction.
        const int stageIndex = (int)ConstructionStage.Planned;
        return (
            $"WCON2NNW#{frame * 4 + stageIndex}",
            $"WCON2N0W#{frame * 4 + stageIndex}");
    }
}
