using IslandRpg.Persistence;

internal static class SavedGameServerChecks
{
    public static void Run()
    {
        RepositoryPersistsAndDeduplicatesServers();
        Console.WriteLine(
            "Saved server list checks passed: unique host:port entries persist.");
    }

    private static void RepositoryPersistsAndDeduplicatesServers()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "IslandRpg",
            "SavedGameServerChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var saves = new GameSaveRepository(root);
            saves.UpsertSavedServer("Oak Hall", "192.168.1.10", 38_740);
            saves.UpsertSavedServer("Oak Hall", "192.168.1.10", 38_740);
            saves.UpsertSavedServer("River Camp", "10.0.0.2", 38_740);
            var loaded = saves.LoadSavedServers();
            Require(
                loaded.Count == 2 &&
                loaded.Any(value =>
                    value.Name == "Oak Hall" &&
                    value.Host == "192.168.1.10" &&
                    value.Port == 38_740) &&
                loaded.Any(value => value.Name == "River Camp"),
                "saved servers must persist unique host:port entries");
            var first = loaded.First(value => value.Name == "River Camp");
            saves.RemoveSavedServer(first.Id);
            Require(
                saves.LoadSavedServers().Count == 1 &&
                saves.LoadSavedServers()[0].Name == "Oak Hall",
                "removing a saved server must leave the others");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
