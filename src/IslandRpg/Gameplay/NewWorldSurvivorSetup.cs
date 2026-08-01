namespace IslandRpg.Gameplay;

internal sealed record NewWorldSurvivorSetup(
    string Name,
    VillagerPersona Persona,
    IReadOnlyList<string> StartingItems);

internal static class NewWorldSurvivorSetupService
{
    public static IReadOnlyList<string> ParseItems(string text)
    {
        var result = new List<string>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                              StringSplitOptions.TrimEntries))
        {
            var item = ItemCatalog.All.FirstOrDefault(candidate =>
                candidate.Droppable &&
                (candidate.Id.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Name.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Caption.Equals(token, StringComparison.OrdinalIgnoreCase)));
            if (item is not null && result.Count < PlayerInventory.Capacity)
                result.Add(item.Id);
        }
        return result;
    }

    public static NewWorldSurvivorSetup[] Build(
        int population,
        IReadOnlyList<VillagerPersona> generated,
        IReadOnlyList<string> names,
        IReadOnlyList<string> personalities,
        IReadOnlyList<string> trades,
        IReadOnlyList<string> backstories,
        IReadOnlyList<string> itemLists,
        string sharedStory)
    {
        population = Math.Clamp(
            population, 0, VillagerSimulation.MaximumPopulation);
        var result = new NewWorldSurvivorSetup[population];
        for (var index = 0; index < population; index++)
        {
            var persona = index < generated.Count
                ? generated[index]
                : VillagerSimulation.DefaultPersona(index);
            var personality = Value(personalities, index);
            var trade = Value(trades, index);
            var backstory = Value(backstories, index);
            var story = sharedStory.Trim();
            persona = persona with
            {
                Personality = personality.Length > 0 ? personality : persona.Personality,
                PriorTrade = trade.Length > 0 ? trade : persona.PriorTrade,
                BackgroundStory = backstory.Length > 0 ? backstory : persona.BackgroundStory,
                ArrivalMemory = story.Length > 0 ? story : persona.ArrivalMemory
            };
            var name = Value(names, index);
            if (name.Length == 0)
                name = VillagerSimulation.NamesForPopulation(population)[index];
            result[index] = new(name, persona, ParseItems(Value(itemLists, index)));
        }
        return result;
    }

    public static string[] UnknownItems(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !ItemCatalog.All.Any(candidate =>
                candidate.Droppable &&
                (candidate.Id.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Name.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Caption.Equals(token, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

    private static string Value(IReadOnlyList<string> values, int index) =>
        index < values.Count ? values[index].Trim() : "";
}
