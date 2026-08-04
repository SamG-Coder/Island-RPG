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
    public const short WallGraphicId = 605;
    public const short ShadowGraphicId = 604;
    // The single post is used for the build icon, hover ghost and wall caps.
    public const int FrontFrame = 2;

    public static string WallFrame(int frame) =>
        $"{WallGraphic}@{WallGraphicId}#{frame}";
    public static string ShadowFrame(int frame) =>
        $"{ShadowGraphic}@{ShadowGraphicId}#{frame}";
    public static string FrontFrameKey => WallFrame(FrontFrame);

    private static WallDefinition Definition(string itemId) =>
        WallCatalog.Get(itemId);

    public static IReadOnlyCollection<string> RequiredGraphics =>
    new[]
    {
        WallGraphic, ShadowGraphic,
        "FENCENNG", "FENCEN1G", "FENCEN0G",
        "WALL2NNW", "WALL2N0W",
        "WALL3NNW", "WALL3N0W",
        "WCON2NNW", "WCON2N0W"
    }.Concat(WallCatalog.All.SelectMany(value =>
            value.ShadowGraphicName is null
                ? new[] { value.GraphicName }
                : new[] { value.GraphicName, value.ShadowGraphicName }))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsWallGraphic(string graphicName) =>
        RequiredGraphics.Contains(graphicName, StringComparer.OrdinalIgnoreCase);

    public static string WallFrame(string itemId, int frame)
    {
        var definition = Definition(itemId);
        return $"{definition.GraphicName}@{definition.GraphicId}#" +
               Math.Clamp(frame, 0, 4);
    }

    public static string FrontFrameKeyFor(string itemId) =>
        WallFrame(itemId, FrontFrame);

    public static (string Wall, string? Shadow) Resolve(
        WorldGroundObject value, int frame)
    {
        frame = Math.Clamp(frame, 0, 4);
        var definition = Definition(value.ItemId);
        var stage = ConstructionService.Stage(value);
        if (stage == ConstructionStage.Complete)
            return (
                WallFrame(value.ItemId, frame),
                definition.ShadowGraphicName is null
                    ? null
                    : $"{definition.ShadowGraphicName}@" +
                      $"{definition.ShadowGraphicId}#{frame}");
        // Wooden palisades have one authored base per direction and then the
        // completed wall. Later WCON2 stages belong to stone construction.
        var stageIndex = definition.UsesStoneConstructionStages
            ? Math.Min(3, (int)stage)
            : (int)ConstructionStage.Planned;
        return (
            $"WCON2NNW#{frame * 4 + stageIndex}",
            $"WCON2N0W#{frame * 4 + stageIndex}");
    }
}
