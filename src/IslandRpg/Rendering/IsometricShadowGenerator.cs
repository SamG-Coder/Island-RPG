using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class IsometricShadowGenerator
{
    private const int Width = 64;
    private const int Height = 40;
    private const int HotspotX = 46;
    private const int HotspotY = 30;

    public static SpriteFrame Create(SpriteFrame source)
    {
        var projected = ProjectMask(source);
        var closed = CloseProjectionGaps(projected);
        return new(
            Width, Height, HotspotX, HotspotY,
            Colorize(closed));
    }

    private static byte[] ProjectMask(SpriteFrame source)
    {
        var mask = new byte[Width * Height];
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var alpha = source.Rgba[
                (y * source.Width + x) * 4 + 3];
            if (alpha <= 12) continue;

            var lateral = x - source.HotspotX;
            var objectHeight = Math.Max(0, source.HotspotY - y);
            var targetX = (int)MathF.Round(
                HotspotX + lateral * .70f - objectHeight * .60f);
            var targetY = (int)MathF.Round(
                HotspotY + lateral * .10f - objectHeight * .30f);
            if ((uint)targetX >= Width || (uint)targetY >= Height)
                continue;
            var index = targetY * Width + targetX;
            mask[index] = Math.Max(mask[index], alpha);
        }
        return mask;
    }

    private static byte[] CloseProjectionGaps(byte[] mask)
    {
        var closed = new byte[mask.Length];
        for (var y = 1; y < Height - 1; y++)
        for (var x = 1; x < Width - 1; x++)
        {
            var index = y * Width + x;
            var neighbour = 0;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
                neighbour = Math.Max(
                    neighbour,
                    mask[(y + offsetY) * Width + x + offsetX]);
            closed[index] = Math.Max(
                mask[index], (byte)(neighbour * 55 / 100));
        }
        return closed;
    }

    private static byte[] Colorize(byte[] mask)
    {
        var rgba = new byte[Width * Height * 4];
        for (var index = 0; index < mask.Length; index++)
        {
            var alpha = mask[index];
            if (alpha == 0) continue;
            rgba[index * 4] = 48;
            rgba[index * 4 + 1] = 48;
            rgba[index * 4 + 2] = 39;
            rgba[index * 4 + 3] = (byte)(alpha * 42 / 100);
        }
        return rgba;
    }
}
