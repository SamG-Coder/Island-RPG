using IslandRpg.Assets;

namespace IslandRpg.Rendering;

internal static class CampfireLightSource
{
    public const int TextureSize = 128;
    public const float RadiusPixels = 142;

    public static SpriteFrame CreateFrame()
    {
        var pixels = new byte[TextureSize * TextureSize * 4];
        var center = (TextureSize - 1) * .5f;
        for (var y = 0; y < TextureSize; y++)
        for (var x = 0; x < TextureSize; x++)
        {
            var deltaX = (x - center) / center;
            var deltaY = (y - center) / center;
            var distance = MathF.Sqrt(
                deltaX * deltaX + deltaY * deltaY);
            var strength = Math.Clamp(1 - distance, 0, 1);
            strength *= strength * (3 - 2 * strength);
            var offset = (y * TextureSize + x) * 4;
            pixels[offset] = 255;
            pixels[offset + 1] = 174;
            pixels[offset + 2] = 74;
            pixels[offset + 3] =
                (byte)MathF.Round(strength * 86);
        }
        return new(
            TextureSize,
            TextureSize,
            TextureSize / 2,
            TextureSize / 2,
            pixels);
    }

    public static float Opacity(double time, float darkness)
    {
        var flicker =
            .94f +
            MathF.Sin((float)time * 7.1f) * .035f +
            MathF.Sin((float)time * 11.7f) * .025f;
        return Math.Clamp(darkness * flicker, 0, 1);
    }
}
