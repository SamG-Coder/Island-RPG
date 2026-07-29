using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal readonly record struct SpriteGroundContact(
    float LateralOffset,
    float Width,
    float Depth);

internal static class SpriteGroundContactCalculator
{
    private const byte AlphaThreshold = 24;
    private const int PixelsPerWorldFootprint = 96;

    public static SpriteGroundContact Measure(SpriteFrame frame)
    {
        var groundY = Math.Clamp(frame.HotspotY, 0, frame.Height - 1);
        var bandHeight = Math.Clamp(frame.Height / 12, 4, 12);
        var minimumY = Math.Max(0, groundY - bandHeight);
        var maximumY = Math.Min(frame.Height - 1, groundY + 2);
        var minimumX = frame.Width;
        var maximumX = -1;

        for (var y = minimumY; y <= maximumY; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            if (frame.Rgba[(y * frame.Width + x) * 4 + 3] <=
                AlphaThreshold)
                continue;
            minimumX = Math.Min(minimumX, x);
            maximumX = Math.Max(maximumX, x);
        }

        if (maximumX < minimumX)
            return new(0, .18f, .18f);

        var pixelWidth = maximumX - minimumX + 1;
        var centerX = (minimumX + maximumX + 1) * .5f;
        var width = Math.Clamp(
            pixelWidth / (float)PixelsPerWorldFootprint,
            .16f,
            1.25f);
        var lateralOffset =
            (centerX - frame.HotspotX) /
            PixelsPerWorldFootprint;
        return new(lateralOffset, width, width);
    }
}
