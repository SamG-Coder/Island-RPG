using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum WallDragOrientation
{
    HorizontalFirst,
    VerticalFirst
}

internal sealed record WallDragPath(
    IReadOnlyList<Vector2> Tiles,
    WallDragOrientation? Orientation);

internal static class WallPlacementPlanner
{
    public const int MaximumSegments = 64;
    private const int OrientationSwitchBias = 1;

    public static WallDragPath Generate(
        Vector2 start, Vector2 end,
        WallDragOrientation? previousOrientation = null,
        int maximum = MaximumSegments)
    {
        maximum = Math.Clamp(maximum, 1, MaximumSegments);
        var startX = (int)MathF.Floor(start.X);
        var startY = (int)MathF.Floor(start.Y);
        var endX = (int)MathF.Floor(end.X);
        var endY = (int)MathF.Floor(end.Y);
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var absoluteX = Math.Abs(deltaX);
        var absoluteY = Math.Abs(deltaY);
        var result = new List<Vector2>(Math.Min(
            maximum, absoluteX + absoluteY + 1));
        result.Add(new(startX + .5f, startY + .5f));

        // Equal world deltas are the only true screen-straight runs. They use
        // FENCEN1G 003/004 and never become an L.
        if (absoluteX == absoluteY)
        {
            AppendSteps(result, ref startX, ref startY,
                Math.Sign(deltaX), Math.Sign(deltaY), absoluteX, maximum);
            return new(result, previousOrientation);
        }

        // Every other two-axis drag becomes an isometric L made only from the
        // top-left/top-right diagonal pieces (001/000).
        var orientation = absoluteX == 0 || absoluteY == 0
            ? previousOrientation
            : SelectOrientation(absoluteX, absoluteY, previousOrientation);
        if (orientation != WallDragOrientation.VerticalFirst)
        {
            AppendSteps(result, ref startX, ref startY,
                Math.Sign(deltaX), 0, absoluteX, maximum);
            AppendSteps(result, ref startX, ref startY,
                0, Math.Sign(deltaY), absoluteY, maximum);
        }
        else
        {
            AppendSteps(result, ref startX, ref startY,
                0, Math.Sign(deltaY), absoluteY, maximum);
            AppendSteps(result, ref startX, ref startY,
                Math.Sign(deltaX), 0, absoluteX, maximum);
        }
        return new(result, orientation);
    }

    public static IReadOnlyList<Vector2> Line(
        Vector2 start, Vector2 end, int maximum = MaximumSegments) =>
        Generate(start, end, maximum: maximum).Tiles;

    public static int FrameAt(IReadOnlyList<Vector2> line, int index)
    {
        if ((uint)index >= (uint)line.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (line.Count == 1 || index == 0 || index == line.Count - 1)
            return 2;
        var incoming = GridDirection(line[index - 1], line[index]);
        var outgoing = GridDirection(line[index], line[index + 1]);
        if (incoming == outgoing)
            return FrameForDirection(incoming.X, incoming.Y);

        // Odd isometric parity introduces a one-cell diagonal connector.
        // That creates two adjacent direction changes, but only the first is
        // the authored L break. The second tile renders the connector itself.
        if (index > 1)
        {
            var previousIncoming = GridDirection(
                line[index - 2], line[index - 1]);
            if (previousIncoming != incoming)
                return FrameForDirection(incoming.X, incoming.Y);
        }
        return 2;
    }

    public static int FrameForNeighbors(
        Vector2 target, IReadOnlySet<(int X, int Y)> occupied)
    {
        var x = (int)MathF.Floor(target.X);
        var y = (int)MathF.Floor(target.Y);
        var directions = new List<(int X, int Y)>(8);
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        for (var offsetX = -1; offsetX <= 1; offsetX++)
            if ((offsetX != 0 || offsetY != 0) &&
                occupied.Contains((x + offsetX, y + offsetY)))
                directions.Add((offsetX, offsetY));
        if (directions.Count < 2) return 2;
        var first = directions[0];
        var second = directions
            .OrderByDescending(value =>
                (value.X - first.X) * (value.X - first.X) +
                (value.Y - first.Y) * (value.Y - first.Y))
            .First();
        var incoming = (-first.X, -first.Y);
        return incoming == second
            ? FrameForDirection(incoming.Item1, incoming.Item2)
            : 2;
    }

    private static WallDragOrientation SelectOrientation(
        int absoluteX, int absoluteY,
        WallDragOrientation? previous)
    {
        if (previous == WallDragOrientation.HorizontalFirst)
            return absoluteY > absoluteX + OrientationSwitchBias
                ? WallDragOrientation.VerticalFirst
                : WallDragOrientation.HorizontalFirst;
        if (previous == WallDragOrientation.VerticalFirst)
            return absoluteX > absoluteY + OrientationSwitchBias
                ? WallDragOrientation.HorizontalFirst
                : WallDragOrientation.VerticalFirst;
        return absoluteX >= absoluteY
            ? WallDragOrientation.HorizontalFirst
            : WallDragOrientation.VerticalFirst;
    }

    private static void AppendSteps(
        List<Vector2> result,
        ref int x, ref int y,
        int stepX, int stepY, int count, int maximum)
    {
        while (count-- > 0 && result.Count < maximum)
        {
            x += stepX;
            y += stepY;
            result.Add(new(x + .5f, y + .5f));
        }
    }

    private static (int X, int Y) GridDirection(Vector2 from, Vector2 to) =>
        (Math.Sign((int)MathF.Round(to.X - from.X)),
         Math.Sign((int)MathF.Round(to.Y - from.Y)));

    private static int FrameForDirection(int x, int y)
    {
        // FENCEN1G is authored in screen space while placement uses the
        // isometric world grid. A world-X run needs the top-left diagonal
        // sprite; a world-Y run needs the top-right diagonal sprite.
        if (x != 0 && y == 0) return 1;
        if (y != 0 && x == 0) return 0;
        if (x != 0 && y != 0)
            return Math.Sign(x) == Math.Sign(y) ? 4 : 3;
        return 2;
    }
}
