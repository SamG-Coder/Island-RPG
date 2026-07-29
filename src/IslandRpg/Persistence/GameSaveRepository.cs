using System.Text.Json;
using System.Text.RegularExpressions;
using IslandRpg.Gameplay;

namespace IslandRpg.Persistence;

internal sealed record WorldProfile(
    string Id,
    string Name,
    long Seed,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? LastPlayerId = null,
    double ElapsedGameSeconds = 8 * 60 * 60);

internal sealed record PlayerProfile(
    string Id,
    string Name,
    EntityGender Gender,
    int SkinTone,
    int TeamColor,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    int WoodcuttingExperience = 0,
    string?[]? Inventory = null,
    bool HasDiscoveredTreeSeed = false,
    int FarmingExperience = 0,
    int CraftingExperience = 0,
    int FishingExperience = 0,
    int CookingExperience = 0,
    int FiremakingExperience = 0,
    int DiggingExperience = 0,
    int MiningExperience = 0,
    int AdventureExperience = 0,
    int Health = AdventureService.BaseMaximumHealth,
    float Hunger = SurvivalService.MaximumHunger,
    float WellFedSeconds = 0,
    int AttackExperience = 0,
    int StrengthExperience = 0,
    int DefenceExperience = 0,
    MeleeCombatStance CombatStance = MeleeCombatStance.Accurate);

internal sealed record WorldPlayerState(
    string PlayerId,
    float PositionX,
    float PositionY,
    DateTime UpdatedUtc,
    int WorldLevel = (int)IslandRpg.World.WorldLevel.Overworld);

internal enum DisplayVSyncMode
{
    On,
    Adaptive,
    Off
}

internal sealed record GameSettings(
    float UiScale = 1,
    float MasterVolume = 1,
    bool Fullscreen = false,
    bool PerformanceMetrics = false,
    DisplayVSyncMode VSyncMode = DisplayVSyncMode.Adaptive,
    int FrameRateLimit = 0);

internal sealed class GameSaveRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Root { get; }
    public string WorldsRoot => Path.Combine(Root, "Worlds");
    public string PlayersRoot => Path.Combine(Root, "Players");
    public string SettingsPath => Path.Combine(Root, "settings.json");

    public GameSaveRepository(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IslandRpg");
        Directory.CreateDirectory(WorldsRoot);
        Directory.CreateDirectory(PlayersRoot);
    }

    public IReadOnlyList<WorldProfile> ListWorlds()
    {
        ImportLegacyWorlds();
        return ReadProfiles<WorldProfile>(WorldsRoot, "profile.json")
            .OrderByDescending(profile => profile.UpdatedUtc)
            .ToArray();
    }

    public IReadOnlyList<PlayerProfile> ListPlayers() =>
        ReadProfiles<PlayerProfile>(PlayersRoot, "player.json")
            .OrderByDescending(profile => profile.UpdatedUtc)
            .ToArray();

    public WorldProfile CreateWorld(string name, long seed, string? playerId)
    {
        var now = DateTime.UtcNow;
        var id = UniqueId(WorldsRoot, name);
        var profile = new WorldProfile(
            id, CleanName(name, "New World"), seed, now, now, playerId);
        SaveWorld(profile);
        return profile;
    }

    public PlayerProfile CreatePlayer(
        string name,
        EntityGender gender,
        int skinTone,
        int teamColor)
    {
        var now = DateTime.UtcNow;
        var id = UniqueId(PlayersRoot, name);
        var profile = new PlayerProfile(
            id, CleanName(name, "Adventurer"), gender,
            Math.Clamp(skinTone, 0, 4),
            Math.Clamp(teamColor, 0, 7),
            now, now,
            Inventory: PlayerInventory.CreateStartingInventory());
        SavePlayer(profile);
        return profile;
    }

    public void SaveWorld(WorldProfile profile) =>
        WriteJson(
            Path.Combine(WorldsRoot, profile.Id, "profile.json"),
            profile with { UpdatedUtc = DateTime.UtcNow });

    public void SavePlayer(PlayerProfile profile) =>
        WriteJson(
            Path.Combine(PlayersRoot, profile.Id, "player.json"),
            profile with { UpdatedUtc = DateTime.UtcNow });

    public void DeletePlayer(string playerId)
    {
        var playerDirectory = Path.GetFullPath(
            Path.Combine(PlayersRoot, playerId));
        var playersRoot = Path.GetFullPath(PlayersRoot) +
                          Path.DirectorySeparatorChar;
        if (!playerDirectory.StartsWith(
                playersRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid player save path.");
        if (Directory.Exists(playerDirectory))
            Directory.Delete(playerDirectory, recursive: true);

        foreach (var worldDirectory in
                 Directory.EnumerateDirectories(WorldsRoot))
        {
            var state = Path.Combine(
                worldDirectory, "players", playerId + ".json");
            if (File.Exists(state)) File.Delete(state);
        }
    }

    public void DeleteWorld(string worldId)
    {
        var worldDirectory = Path.GetFullPath(
            Path.Combine(WorldsRoot, worldId));
        var worldsRoot = Path.GetFullPath(WorldsRoot) +
                         Path.DirectorySeparatorChar;
        if (!worldDirectory.StartsWith(
                worldsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid world save path.");
        if (Directory.Exists(worldDirectory))
            Directory.Delete(worldDirectory, recursive: true);
    }

    public WorldPlayerState? LoadWorldPlayer(
        string worldId, string playerId) =>
        ReadJson<WorldPlayerState>(Path.Combine(
            WorldsRoot, worldId, "players", playerId + ".json"));

    public void SaveWorldPlayer(string worldId, WorldPlayerState state) =>
        WriteJson(
            Path.Combine(
                WorldsRoot, worldId, "players", state.PlayerId + ".json"),
            state with { UpdatedUtc = DateTime.UtcNow });

    public GameSettings LoadSettings() =>
        ReadJson<GameSettings>(SettingsPath) ?? new();

    public void SaveSettings(GameSettings settings) =>
        WriteJson(SettingsPath, settings);

    private void ImportLegacyWorlds()
    {
        foreach (var directory in Directory.EnumerateDirectories(WorldsRoot))
        {
            var profilePath = Path.Combine(directory, "profile.json");
            if (File.Exists(profilePath)) continue;
            var id = Path.GetFileName(directory);
            if (!long.TryParse(id, out var seed)) continue;
            var updated = Directory.GetLastWriteTimeUtc(directory);
            var profile = new WorldProfile(
                id, $"World {seed}", seed, updated, updated);
            WriteJson(profilePath, profile);
        }
    }

    private static IEnumerable<T> ReadProfiles<T>(string root, string fileName)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var profile = ReadJson<T>(Path.Combine(directory, fileName));
            if (profile is not null) yield return profile;
        }
    }

    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Save path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static string UniqueId(string root, string name)
    {
        var baseId = Regex.Replace(
            CleanName(name, "save").ToLowerInvariant(),
            @"[^a-z0-9]+", "-").Trim('-');
        if (baseId.Length == 0) baseId = "save";
        var id = baseId;
        for (var suffix = 2; Directory.Exists(Path.Combine(root, id)); suffix++)
            id = $"{baseId}-{suffix}";
        return id;
    }

    private static string CleanName(string name, string fallback)
    {
        var clean = new string(name
            .Where(character => !char.IsControl(character))
            .ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean[..Math.Min(clean.Length, 32)];
    }
}
