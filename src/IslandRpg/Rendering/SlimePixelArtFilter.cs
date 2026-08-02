namespace IslandRpg.Rendering;

// A deliberately small virtual canvas removes sub-pixel detail from the
// generated 128px source before palette reduction in the sprite shader.
internal static class SlimePixelArtFilter
{
    public const float VirtualGrid = 48;
    public const int ShadeBands = 6;

    public static float QuantizeShade(float shade) =>
        MathF.Round(Math.Clamp(shade, 0, 1) * ShadeBands) / ShadeBands;
}
