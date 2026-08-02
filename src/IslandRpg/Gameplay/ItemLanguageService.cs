namespace IslandRpg.Gameplay;

/// <summary>
/// Resolves player language against the complete item catalog. The resolver is
/// deterministic and deliberately separate from Ollama so model wording can
/// guide plans without being trusted to invent item identifiers.
/// </summary>
internal static class ItemLanguageService
{
    private sealed record Alias(string Phrase, ItemDefinition Item);

    private static readonly IReadOnlyList<Alias> Aliases = BuildAliases();

    private static readonly IReadOnlyDictionary<string, string> CommonAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fibre"] = ItemIds.PlantFibres,
            ["fibres"] = ItemIds.PlantFibres,
            ["fiber"] = ItemIds.PlantFibres,
            ["fibers"] = ItemIds.PlantFibres,
            ["berry"] = ItemIds.WildBerries,
            ["berries"] = ItemIds.WildBerries,
            ["wood"] = ItemIds.Logs,
            ["timber"] = ItemIds.Logs,
            ["kindling"] = ItemIds.Sticks,
            ["twig"] = ItemIds.Sticks,
            ["twigs"] = ItemIds.Sticks,
            ["stone"] = ItemIds.SmallRocks,
            ["stones"] = ItemIds.SmallRocks,
            ["rock"] = ItemIds.SmallRocks,
            ["rocks"] = ItemIds.SmallRocks
        };

    public static bool TryResolveMention(
        string text,
        out ItemDefinition item)
    {
        var normalized = $" {Normalize(text)} ";
        foreach (var alias in Aliases)
            if (normalized.Contains(
                    $" {alias.Phrase} ",
                    StringComparison.Ordinal))
            {
                item = alias.Item;
                return true;
            }
        foreach (var alias in CommonAliases)
            if (normalized.Contains(
                    $" {alias.Key} ",
                    StringComparison.Ordinal) &&
                ItemCatalog.TryGet(alias.Value, out item!))
                return true;
        item = null!;
        return false;
    }

    private static IReadOnlyList<Alias> BuildAliases()
    {
        var aliases = new List<Alias>();
        foreach (var item in ItemCatalog.All.Where(value => value.Droppable))
        {
            var phrases = new HashSet<string>(StringComparer.Ordinal)
            {
                Normalize(item.Id.Replace('_', ' ')),
                Normalize(item.Name),
                Normalize(item.Caption)
            };
            foreach (var phrase in phrases.ToArray())
            {
                var singular = Singularize(phrase);
                if (singular != phrase) phrases.Add(singular);
                if (phrase.Contains("fibre", StringComparison.Ordinal))
                    phrases.Add(phrase.Replace(
                        "fibre", "fiber", StringComparison.Ordinal));
            }
            aliases.AddRange(phrases
                .Where(phrase => phrase.Length > 0)
                .Select(phrase => new Alias(phrase, item)));
        }
        return aliases
            .OrderByDescending(alias => alias.Phrase.Split(' ').Length)
            .ThenByDescending(alias => alias.Phrase.Length)
            .ThenBy(alias => alias.Item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Normalize(string value)
    {
        var characters = value.ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(characters).Split(
            ' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Singularize(string phrase)
    {
        var words = phrase.Split(' ');
        if (words.Length == 0) return phrase;
        var word = words[^1];
        words[^1] = word.EndsWith("ies", StringComparison.Ordinal) &&
                    word.Length > 3
            ? word[..^3] + "y"
            : word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) &&
              word.Length > 2
                ? word[..^1]
                : word;
        return string.Join(' ', words);
    }
}
