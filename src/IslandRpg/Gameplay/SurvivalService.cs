namespace IslandRpg.Gameplay;

internal readonly record struct FoodEffect(
    float HungerRestored,
    int HealthRestored,
    float WellFedSeconds);

internal readonly record struct SurvivalUpdate(
    float Hunger,
    float WellFedSeconds,
    int Health);

internal static class SurvivalService
{
    public const float MaximumHunger = 100;
    public const float BaseHungerLossPerSecond = 1f / 12f;
    public const float WellFedHungerMultiplier = .25f;
    public const float StarvationDamagePerSecond = .5f;

    public static bool TryFoodEffect(string itemId, out FoodEffect effect)
    {
        effect = itemId switch
        {
            ItemIds.WildBerries => new(8, 1, 20),
            ItemIds.TropicalBerries => new(10, 1, 25),
            ItemIds.RoastedWildBerries => new(16, 3, 55),
            ItemIds.RoastedTropicalBerries => new(19, 4, 70),
            ItemIds.CookedMinnows => new(18, 4, 60),
            ItemIds.CookedRiverPerch => new(24, 6, 85),
            ItemIds.CookedSilverHerring => new(29, 8, 110),
            ItemIds.CookedRedSnapper => new(34, 10, 140),
            ItemIds.CookedOceanMackerel => new(40, 13, 175),
            ItemIds.CookedBluefinTuna => new(48, 17, 220),
            ItemIds.FishBerryStew => new(65, 25, 360),
            _ => default
        };
        return effect != default;
    }

    public static SurvivalUpdate Advance(
        float hunger,
        float wellFedSeconds,
        int health,
        float elapsed)
    {
        elapsed = Math.Max(0, elapsed);
        hunger = Math.Clamp(hunger, 0, MaximumHunger);
        wellFedSeconds = Math.Max(0, wellFedSeconds);
        var protectedTime = Math.Min(elapsed, wellFedSeconds);
        var normalTime = elapsed - protectedTime;
        hunger = Math.Max(
            0,
            hunger - BaseHungerLossPerSecond *
            (protectedTime * WellFedHungerMultiplier + normalTime));
        wellFedSeconds = Math.Max(0, wellFedSeconds - elapsed);
        if (hunger <= 0)
            health = Math.Max(
                0, health -
                (int)MathF.Floor(StarvationDamagePerSecond * elapsed));
        return new(hunger, wellFedSeconds, health);
    }

    public static SurvivalUpdate Eat(
        FoodEffect effect,
        float hunger,
        float wellFedSeconds,
        int health,
        int maximumHealth) =>
        new(
            Math.Min(MaximumHunger, hunger + effect.HungerRestored),
            Math.Max(wellFedSeconds, effect.WellFedSeconds),
            Math.Min(maximumHealth, health + effect.HealthRestored));
}
