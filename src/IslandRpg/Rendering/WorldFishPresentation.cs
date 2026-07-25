using IslandRpg.Assets;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class WorldFishPresentation
{
    public const string DepthAtlasKey = "FISH_DEPTH";

    public static bool BaseHitTest(
        Vector2 mouse, Vector2 anchor, float zoom)
    {
        var halfWidth = Math.Max(10, 13 * zoom);
        var halfHeight = Math.Max(6, 7 * zoom);
        return mouse.X >= anchor.X - halfWidth &&
               mouse.X <= anchor.X + halfWidth &&
               mouse.Y >= anchor.Y - halfHeight &&
               mouse.Y <= anchor.Y + halfHeight;
    }

    public static SpriteFrame CreateDepthFrame()
    {
        const int width = 48;
        const int height = 24;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = (x + .5f - width * .5f) / (width * .5f);
            var dy = (y + .5f - height * .5f) / (height * .5f);
            var distance = dx * dx + dy * dy;
            if (distance >= 1) continue;
            var softness = MathF.Pow(1 - distance, 2.2f);
            var index = (y * width + x) * 4;
            pixels[index] = 8;
            pixels[index + 1] = 45;
            pixels[index + 2] = 79;
            pixels[index + 3] = (byte)(softness * 92);
        }
        return new(width, height, width / 2, height / 2, pixels);
    }
}
