using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Keeps path approach selection and queued-action completion on the same
/// distance boundary.
/// </summary>
internal static class WorldActionReach
{
    public const float CompletionTolerance = .08f;

    public static float CompletionRange(float standOff) =>
        Math.Max(standOff, .72f) + CompletionTolerance;

    public static bool CanComplete(
        Vector2 position, Vector2 target, float standOff) =>
        (position - target).Length <= CompletionRange(standOff);
}
