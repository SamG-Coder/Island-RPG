using IslandRpg.Assets;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class SpriteHitTesting
{
    public static bool Contains(
        SpriteFrame frame,
        (float Left, float Top, float Right, float Bottom) bounds,
        Vector2 pointer,
        float scale,
        int tolerancePixels = 0)
    {
        scale = Math.Max(scale, .001f);
        var padding = tolerancePixels * scale;
        if (pointer.X < bounds.Left - padding ||
            pointer.X >= bounds.Right + padding ||
            pointer.Y < bounds.Top - padding ||
            pointer.Y >= bounds.Bottom + padding)
            return false;

        var centerX = (int)((pointer.X - bounds.Left) / scale);
        var centerY = (int)((pointer.Y - bounds.Top) / scale);
        var radius = Math.Max(0, tolerancePixels);
        var fromX = Math.Max(0, centerX - radius);
        var toX = Math.Min(frame.Width - 1, centerX + radius);
        var fromY = Math.Max(0, centerY - radius);
        var toY = Math.Min(frame.Height - 1, centerY + radius);
        for (var y = fromY; y <= toY; y++)
        for (var x = fromX; x <= toX; x++)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            if (dx * dx + dy * dy > radius * radius ||
                frame.Rgba[(y * frame.Width + x) * 4 + 3] <= 24)
                continue;
            return true;
        }
        return false;
    }

    public static int SizeAwareTolerance(SpriteFrame frame) =>
        Math.Clamp(Math.Max(frame.Width, frame.Height) / 14, 4, 12);
}
