namespace IslandRpg.Gameplay;

internal readonly record struct PlayerRecovery(
    int Health,
    float Hunger,
    float WellFedSeconds);

internal static class PlayerDeathService
{
    public const int MaximumRememberedDeaths = 10;
    public const float RecoveryHunger = 25;

    public static int ApplyDamage(int health, int damage) =>
        Math.Max(0, health - Math.Max(0, damage));

    public static PlayerRecovery Recover(int maximumHealth) =>
        new(
            Math.Max(1, maximumHealth / 2),
            RecoveryHunger,
            0);
}
