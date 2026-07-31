namespace IslandRpg.Gameplay;

internal static class DialogueResponseService
{
    public static string? Resolve(string? response, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(response)) return fallback;
        var line = response.Trim();
        return LooksTruncated(line) &&
               !string.IsNullOrWhiteSpace(fallback)
            ? fallback
            : line;
    }

    public static bool LooksTruncated(string line)
    {
        var finalWord = line
            .TrimEnd('"', '\'', ')', ']', '}')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim(',', ';', ':', '-');
        return finalWord is { Length: 1 } &&
               char.IsLetter(finalWord[0]);
    }
}
