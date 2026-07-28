using IslandRpg.Assets;
using IslandRpg.World;

namespace IslandRpg.Rendering;

internal static class UndergroundResourcePresentation
{
    public static IEnumerable<(string Key, SpriteFrame Frame)> CreateOreFrames(
        SpriteFrame goldOre)
    {
        yield return (UndergroundResourceGenerator.Coal,
            Recolor(goldOre, (12, 14, 16), (88, 94, 98)));
        yield return (UndergroundResourceGenerator.Tin,
            Recolor(goldOre, (62, 69, 72), (205, 216, 216)));
        yield return (UndergroundResourceGenerator.Copper,
            Recolor(goldOre, (62, 22, 10), (214, 101, 39)));
        yield return (UndergroundResourceGenerator.Iron,
            Recolor(goldOre, (47, 31, 25), (151, 85, 59)));
    }

    internal static SpriteFrame Recolor(
        SpriteFrame source,
        (byte Red, byte Green, byte Blue) shadow,
        (byte Red, byte Green, byte Blue) highlight)
    {
        var rgba = (byte[])source.Rgba.Clone();
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset + 3] == 0) continue;
            var luminance = Math.Clamp(
                (rgba[offset] * .2126f +
                 rgba[offset + 1] * .7152f +
                 rgba[offset + 2] * .0722f) / 255f,
                0f, 1f);
            // Preserve the original sprite's contrast while replacing its hue.
            var amount = MathF.Pow(luminance, .82f);
            rgba[offset] = Lerp(shadow.Red, highlight.Red, amount);
            rgba[offset + 1] = Lerp(
                shadow.Green, highlight.Green, amount);
            rgba[offset + 2] = Lerp(
                shadow.Blue, highlight.Blue, amount);
        }
        return new(
            source.Width,
            source.Height,
            source.HotspotX,
            source.HotspotY,
            rgba);
    }

    private static byte Lerp(byte from, byte to, float amount) =>
        (byte)Math.Clamp(
            (int)MathF.Round(from + (to - from) * amount),
            byte.MinValue,
            byte.MaxValue);
}
