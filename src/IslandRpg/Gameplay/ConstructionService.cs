using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal enum ConstructionStage
{
    Planned,
    Foundation,
    Frame,
    NearlyComplete,
    Complete
}

internal static class ConstructionService
{
    public const int WoodenWallMaximumHealth = 120;

    public static bool IsConstructible(string itemId) =>
        itemId == ItemIds.WoodenWall;

    public static bool IsConstructionSite(WorldGroundObject value) =>
        IsConstructible(value.ItemId) &&
        value.MaxHealth > 0 &&
        value.Health < value.MaxHealth;

    public static WorldGroundObject Begin(WorldGroundObject value) =>
        !IsConstructible(value.ItemId)
            ? value
            : value with
            {
                Health = 1,
                MaxHealth = WoodenWallMaximumHealth
            };

    public static WorldGroundObject AddWork(
        WorldGroundObject value, int health)
    {
        if (!IsConstructionSite(value) || health <= 0) return value;
        return value with
        {
            Health = Math.Min(value.MaxHealth, value.Health + health)
        };
    }

    public static string? DemolitionRefund(WorldGroundObject value) =>
        IsConstructionSite(value) && value.ItemId == ItemIds.WoodenWall
            ? ItemIds.Logs
            : null;

    public static ConstructionStage Stage(WorldGroundObject value)
    {
        if (!IsConstructible(value.ItemId) ||
            value.MaxHealth <= 0 || value.Health >= value.MaxHealth)
            return ConstructionStage.Complete;
        var progress = Math.Clamp(
            value.Health / (float)value.MaxHealth, 0, 1);
        return progress switch
        {
            < .10f => ConstructionStage.Planned,
            < .40f => ConstructionStage.Foundation,
            < .70f => ConstructionStage.Frame,
            _ => ConstructionStage.NearlyComplete
        };
    }

    public static int Angle(WorldGroundObject value) =>
        value.VisualFrame is >= 0 and < 5
            ? value.VisualFrame
            : value.Id.ToByteArray()[0] % 5;

    public static int WorkHealth(int craftingLevel, float energy) =>
        Math.Max(4, (int)MathF.Round(
            (12 + Math.Clamp(craftingLevel, 1, 20) * .7f) *
            Math.Clamp(.5f + energy / 200f, .5f, 1f)));
}
