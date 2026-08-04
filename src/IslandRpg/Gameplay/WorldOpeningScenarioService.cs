namespace IslandRpg.Gameplay;

internal static class WorldOpeningScenarioService
{
    public static VillagerPersona ApplyArrival(
        VillagerPersona persona,
        bool islandStart)
    {
        if (islandStart) return persona;
        var trade = string.IsNullOrWhiteSpace(persona.PriorTrade)
            ? "traveller"
            : persona.PriorTrade.ToLowerInvariant();
        return persona with
        {
            BackgroundStory =
                $"A {trade} who joined a merchant caravan bound for a distant market.",
            ArrivalMemory =
                "Remembers armed raiders striking the caravan before waking beside scattered barrels.",
            SocialDrive =
                "Wants to learn who survived the attack and protect the caravan supplies."
        };
    }

    public static string PersonaTimeline(bool islandStart) => islandStart
        ? "Day 1, 03:00; newly awake after a shipwreck on an unknown island"
        : "Day 1, 03:00; newly awake after raiders attacked a merchant caravan";
}
