using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class ItemShadowGenerator
{
    public static SpriteFrame Create(SpriteFrame item)
    {
        const int offset = 3;
        var width = item.Width + offset;
        var height = item.Height + offset;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < item.Height; y++)
        for (var x = 0; x < item.Width; x++)
        {
            var source = (y * item.Width + x) * 4;
            var alpha = item.Rgba[source + 3];
            if (alpha == 0) continue;
            var target = (y * width + x) * 4;
            pixels[target] = 25;
            pixels[target + 1] = 23;
            pixels[target + 2] = 20;
            pixels[target + 3] = (byte)(alpha * 112 / 255);
        }

        return new SpriteFrame(
            width,
            height,
            item.HotspotX + offset,
            item.HotspotY + offset,
            pixels);
    }
}
