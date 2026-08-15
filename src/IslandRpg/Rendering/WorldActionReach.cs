using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Keeps path approach selection and queued-action completion on the same
/// distance boundary. Multiplayer walk-to-act uses these same stand-off
/// values as single-player <c>WorldActionController</c>.
/// </summary>
internal static class WorldActionReach
{
    public const float CompletionTolerance = .08f;
    public const float GroundPickup = .46f;
    public const float Vegetation = .72f;
    public const float Campfire = .72f;
    public const float CaveEnter = .72f;
    public const float Mining = .82f;
    public const float CaveDig = .82f;
    public const float Container = .9f;
    public const float CraftingStation = 1.15f;
    public const float Construction = .24f;
    public const float CookStew = .82f;
    public const float FillBucket = .72f;
    public const float BoatBoard = 1.25f;
    public const float Melee = MeleeCombatService.AttackRange;

    public static float Placeable(string? itemId)
    {
        if (!string.IsNullOrEmpty(itemId) &&
            PlaceableObjectCatalog.TryGet(itemId, out var definition))
            return Math.Max(
                definition.FootprintWidth,
                definition.FootprintDepth) * .5f + .55f;
        return GroundPickup;
    }

    public static float CompletionRange(float standOff) =>
        Math.Max(standOff, .72f) + CompletionTolerance;

    public static bool CanComplete(
        Vector2 position, Vector2 target, float standOff) =>
        (position - target).Length <= CompletionRange(standOff);

    public static bool InRange(
        Vector2 position, Vector2 target, float standOff)
    {
        var range = CompletionRange(standOff);
        return (position - target).LengthSquared <= range * range;
    }

    public static Vector2 StandOff(Vector2 from, Vector2 to, float range)
    {
        var delta = from - to;
        var length = delta.Length;
        if (length <= range || length < .0001f) return from;
        return to + delta * (range / length);
    }
}
