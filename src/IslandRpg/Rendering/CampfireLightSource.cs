using IslandRpg.Gameplay;

namespace IslandRpg.Rendering;

internal static class CampfireLightSource
{
    public static float Opacity(double time, float darkness)
    {
        var flicker =
            .94f +
            MathF.Sin((float)time * 7.1f) * .035f +
            MathF.Sin((float)time * 11.7f) * .025f;
        return Math.Clamp(darkness * flicker, 0, 1);
    }
}
