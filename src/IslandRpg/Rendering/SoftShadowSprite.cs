using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class SoftShadowSprite
{
    public static SpriteFrame Create(
        int size = 128, int hotspotX = 64, int hotspotY = 120)
    {
        var pixels = new byte[size * size * 4];
        var radiusX = size * .34f;
        var radiusY = size * .09f;
        var centerX = hotspotX;
        var centerY = hotspotY - radiusY * .45f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = (x - centerX) / radiusX;
            var dy = (y - centerY) / radiusY;
            var distance = dx * dx + dy * dy;
            if (distance >= 1) continue;
            var softness = MathF.Pow(1 - distance, 1.7f);
            var index = (y * size + x) * 4;
            pixels[index] = 22;
            pixels[index + 1] = 24;
            pixels[index + 2] = 22;
            pixels[index + 3] = (byte)MathF.Round(105 * softness);
        }
        return new(size, size, hotspotX, hotspotY, pixels);
    }
}
