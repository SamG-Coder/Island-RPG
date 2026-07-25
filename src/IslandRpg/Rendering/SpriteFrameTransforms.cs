using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class SpriteFrameTransforms
{
    public static SpriteFrame Resize(SpriteFrame source, float scale)
    {
        if (scale <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(scale), "Sprite scale must be positive.");
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = Math.Min(
                (int)(x / (float)width * source.Width),
                source.Width - 1);
            var sourceY = Math.Min(
                (int)(y / (float)height * source.Height),
                source.Height - 1);
            Buffer.BlockCopy(
                source.Rgba,
                (sourceY * source.Width + sourceX) * 4,
                pixels,
                (y * width + x) * 4,
                4);
        }
        return new(
            width, height,
            (int)MathF.Round(source.HotspotX * scale),
            (int)MathF.Round(source.HotspotY * scale),
            pixels);
    }

    public static SpriteFrame Rotate(SpriteFrame source, float degreesClockwise)
    {
        var radians = degreesClockwise * MathF.PI / 180f;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        var width = (int)MathF.Ceiling(
            MathF.Abs(source.Width * cosine) +
            MathF.Abs(source.Height * sine));
        var height = (int)MathF.Ceiling(
            MathF.Abs(source.Width * sine) +
            MathF.Abs(source.Height * cosine));
        var pixels = new byte[width * height * 4];
        var sourceCenterX = (source.Width - 1) * .5f;
        var sourceCenterY = (source.Height - 1) * .5f;
        var targetCenterX = (width - 1) * .5f;
        var targetCenterY = (height - 1) * .5f;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var targetX = x - targetCenterX;
            var targetY = y - targetCenterY;
            var sourceX = cosine * targetX + sine * targetY +
                          sourceCenterX;
            var sourceY = -sine * targetX + cosine * targetY +
                          sourceCenterY;
            var sampleX = (int)MathF.Round(sourceX);
            var sampleY = (int)MathF.Round(sourceY);
            if ((uint)sampleX >= (uint)source.Width ||
                (uint)sampleY >= (uint)source.Height)
                continue;
            System.Buffer.BlockCopy(
                source.Rgba,
                (sampleY * source.Width + sampleX) * 4,
                pixels,
                (y * width + x) * 4,
                4);
        }

        var hotspotX = source.HotspotX - sourceCenterX;
        var hotspotY = source.HotspotY - sourceCenterY;
        return new SpriteFrame(
            width,
            height,
            (int)MathF.Round(
                cosine * hotspotX - sine * hotspotY + targetCenterX),
            (int)MathF.Round(
                sine * hotspotX + cosine * hotspotY + targetCenterY),
            pixels);
    }
}
