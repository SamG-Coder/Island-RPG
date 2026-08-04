using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class SettlementScoutDialogueService
{
    public static string NaturalReport(
        SettlementScoutReport report,
        Vector2 origin)
    {
        var useful = new List<string>(4);
        if (report.Water) useful.Add("fresh water");
        if (report.Food) useful.Add("food");
        if (report.Wood) useful.Add("good timber");
        if (report.Stone) useful.Add("workable stone");
        var findings = useful.Count switch
        {
            0 => "nothing we can rely upon",
            1 => useful[0],
            2 => $"{useful[0]} and {useful[1]}",
            _ => string.Join(", ", useful.Take(useful.Count - 1)) +
                 $", and {useful[^1]}"
        };
        var ground = report.Danger
            ? "I saw danger there"
            : report.DefensibleGround
                ? "the ground would be easier to defend"
                : "the ground is exposed";
        return $"I searched {Direction(origin, new(
            report.PositionX, report.PositionY))} and found {findings}; " +
               $"{ground}.";
    }

    private static string Direction(Vector2 origin, Vector2 target)
    {
        var delta = target - origin;
        if (MathF.Abs(delta.X) > MathF.Abs(delta.Y))
            return delta.X >= 0 ? "east of here" : "west of here";
        return delta.Y >= 0 ? "south of here" : "north of here";
    }
}
