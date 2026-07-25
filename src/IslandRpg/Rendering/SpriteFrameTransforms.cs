using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class SpriteFrameTransforms
{
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
