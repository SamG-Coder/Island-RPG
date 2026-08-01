namespace IslandRpg.Gameplay;

internal readonly record struct FoodEffect(
    float HungerRestored,
    int HealthRestored,
    float WellFedSeconds);

internal readonly record struct SurvivalUpdate(
    float Hunger,
    float WellFedSeconds,
    int Health,
    float StarvationDamageRemainder = 0);

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
        float elapsed,
        float hungerLossMultiplier = 1,
        float starvationDamageRemainder = 0)
    {
        elapsed = Math.Max(0, elapsed);
        hungerLossMultiplier = Math.Max(0, hungerLossMultiplier);
        hunger = Math.Clamp(hunger, 0, MaximumHunger);
        wellFedSeconds = Math.Max(0, wellFedSeconds);
        starvationDamageRemainder = Math.Clamp(
            starvationDamageRemainder, 0, .999999f);
        var protectedTime = Math.Min(elapsed, wellFedSeconds);
        var normalTime = elapsed - protectedTime;
        var starvingTime = ConsumePeriod(
            ref hunger,
            protectedTime,
            BaseHungerLossPerSecond * WellFedHungerMultiplier *
            hungerLossMultiplier);
        starvingTime += ConsumePeriod(
            ref hunger,
            normalTime,
            BaseHungerLossPerSecond * hungerLossMultiplier);
        wellFedSeconds = Math.Max(0, wellFedSeconds - elapsed);
        var accumulatedDamage = starvationDamageRemainder +
                                StarvationDamagePerSecond * starvingTime;
        var wholeDamage = (int)MathF.Floor(accumulatedDamage);
        health = Math.Max(0, health - wholeDamage);
        return new(
            hunger,
            wellFedSeconds,
            health,
            accumulatedDamage - wholeDamage);
    }

    private static float ConsumePeriod(
        ref float hunger,
        float elapsed,
        float hungerLossPerSecond)
    {
        if (elapsed <= 0) return 0;
        if (hunger <= 0) return elapsed;
        if (hungerLossPerSecond <= 0) return 0;
        var secondsUntilStarving = hunger / hungerLossPerSecond;
        if (secondsUntilStarving >= elapsed)
        {
            hunger = Math.Max(0, hunger - hungerLossPerSecond * elapsed);
            return 0;
        }
        hunger = 0;
        return elapsed - secondsUntilStarving;
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
