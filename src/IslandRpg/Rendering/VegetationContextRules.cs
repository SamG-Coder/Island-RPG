using IslandRpg.Resources;

namespace IslandRpg.Rendering;

/// <summary>
/// Single-player and multiplayer share this vegetation context menu so
/// "Gather fibres" is the same option and the same resource action.
/// </summary>
internal static class VegetationContextRules
{
    public const string GatherFibresLabel = "Gather fibres";
    public const string PickBerriesLabel = "Pick berries";
    public const string WalkHereLabel = "Walk Here";
    public const string ExamineLabel = "Examine";

    public enum Choice
    {
        None,
        GatherFibres,
        GatherBerries,
        WalkHere,
        Examine
    }

    public static string PrimaryLabel(bool berries) =>
        berries ? PickBerriesLabel : GatherFibresLabel;

    public static string[] Labels(bool berries) =>
        [PrimaryLabel(berries), WalkHereLabel, ExamineLabel];

    public static Choice Resolve(int option, bool berries) =>
        option switch
        {
            0 => berries ? Choice.GatherBerries : Choice.GatherFibres,
            1 => Choice.WalkHere,
            2 => Choice.Examine,
            _ => Choice.None
        };

    public static ResourceActionKind? ResourceAction(Choice choice) =>
        choice switch
        {
            Choice.GatherFibres => ResourceActionKind.GatherFibre,
            Choice.GatherBerries => ResourceActionKind.GatherBerries,
            _ => null
        };
}