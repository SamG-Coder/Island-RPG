namespace IslandRpg.Rendering;

/// <summary>
/// Keeps chunk loading and terrain rendering on the same zoom-scaled radius.
/// A chunk deliberately loaded by the developer view must not be removed by a
/// smaller, independent render-circle policy.
/// </summary>
internal static class ZoomScaledWorldLoadingPolicy
{
    public const int StandardRadius = 5;
    public const int MaximumDeveloperRadius = 32;

    public static int Radius(bool enabled, float zoom)
    {
        if (!enabled) return StandardRadius;
        zoom = Math.Max(zoom, .001f);
        return Math.Clamp(
            (int)MathF.Ceiling(.9f / zoom),
            StandardRadius,
            MaximumDeveloperRadius);
    }
}
