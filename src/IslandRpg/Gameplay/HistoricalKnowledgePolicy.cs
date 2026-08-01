namespace IslandRpg.Gameplay;

internal static class HistoricalKnowledgePolicy
{
    public const string PromptRule =
        "The setting and every person are limited to knowledge plausible by 1200 AD. " +
        "Do not mention modern science, engineering, medicine, industry, nations, " +
        "technology, professions, or events.";

    private static readonly string[] Anachronisms =
    [
        "biologist", "engineer", "electric", "computer", "laser",
        "machine", "concrete", "plastic", "motor", "radio", "rifle",
        "acidification", "infrastructure", "ngo", "scientist",
        "research paper", "great barrier reef"
    ];

    public static bool IsPlausible(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !Anachronisms.Any(term => text.Contains(
            term, StringComparison.OrdinalIgnoreCase));
}
