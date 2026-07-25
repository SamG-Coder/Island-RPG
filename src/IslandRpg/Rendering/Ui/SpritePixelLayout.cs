using IslandRpg.Assets;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal static class SpritePixelLayout
{
    public static Vector4 CenterOpaquePixels(
        SpriteFrame frame, Vector4 bounds)
    {
        if (frame.Rgba.Length < frame.Width * frame.Height * 4)
            return bounds;

        var minimumX = frame.Width;
        var minimumY = frame.Height;
        var maximumX = -1;
        var maximumY = -1;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            if (frame.Rgba[(y * frame.Width + x) * 4 + 3] == 0)
                continue;
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
        }
        if (maximumX < minimumX || maximumY < minimumY)
            return bounds;

        var opaqueCenterX = (minimumX + maximumX + 1) * .5f;
        var opaqueCenterY = (minimumY + maximumY + 1) * .5f;
        return new(
            bounds.X +
            (frame.Width * .5f - opaqueCenterX) *
            bounds.Z / frame.Width,
            bounds.Y +
            (frame.Height * .5f - opaqueCenterY) *
            bounds.W / frame.Height,
            bounds.Z,
            bounds.W);
    }
}
