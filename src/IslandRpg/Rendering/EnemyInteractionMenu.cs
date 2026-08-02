namespace IslandRpg.Rendering;

internal static class EnemyInteractionMenu
{
    public static IReadOnlyList<string> Options { get; } =
        ["Walk Here", "Attack", "Examine"];

    public const int WalkHereIndex = 0;
    public const int AttackIndex = 1;
    public const int ExamineIndex = 2;
}
