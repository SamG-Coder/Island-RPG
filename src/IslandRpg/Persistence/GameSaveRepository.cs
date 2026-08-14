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
    double ElapsedGameSeconds = WorldTime.NewGameStartGameSeconds,
    bool AiNpcsEnabled = false,
    int AiNpcCount = 0,
    IReadOnlyList<VillagerPersona>? AiNpcPersonas = null,
    bool ObserveWorld = false,
    string SharedStory = "",
    IReadOnlyList<NewWorldSurvivorSetup>? AiNpcSetups = null,
    string AiModelOverride = "",
    bool SkipOpeningCouncil = false,
    bool IslandStart = false);

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
    MeleeCombatStance CombatStance = MeleeCombatStance.Accurate,
    IReadOnlyList<QuestProgress>? Quests = null,
    float HealthRegenerationRemainder = 0,
    int[]? InventoryQuantities = null);

internal sealed record WorldPlayerState(
    string PlayerId,
    float PositionX,
    float PositionY,
    DateTime UpdatedUtc,
    int WorldLevel = (int)IslandRpg.World.WorldLevel.Overworld,
    float? FishingBoatX = null,
    float? FishingBoatY = null,
    float FishingBoatFacingX = 1,
    float FishingBoatFacingY = 1,
    bool FishingBoatBoarded = false);

internal sealed record SavedGameServer(
    string Id,
    string Name,
    string Host,
    int Port,
    DateTime AddedUtc = default);

internal sealed record NetworkSessionRecord(
    string Host,
    int Port,
    Guid WorldId,
    Guid PlayerId,
    string ReconnectToken,
    string PlayerName,
    EntityGender Gender,
    int TeamColor,
    DateTime SavedUtc = default,
    string LocalPlayerId = "");

internal sealed record PlayerDeathMarker(
    float PositionX,
    float PositionY,
    int WorldLevel,
    EntityGender Gender,
    DateTime DiedUtc,
    float FacingX = 1,
    float FacingY = 1,
    string? Name = null,
    string? Cause = null);

internal enum DisplayVSyncMode
{
    On,
    Adaptive,
    Off
}

internal enum ChatDisplaySize
{
    Small,
    Medium,
    Large
}

internal sealed record GameSettings(
    float UiScale = 1,
    float MasterVolume = 1,
    bool Fullscreen = false,
    bool PerformanceMetrics = false,
    DisplayVSyncMode VSyncMode = DisplayVSyncMode.Adaptive,
    int FrameRateLimit = 0,
    bool OccludedPlayerOutline = true,
    int FullscreenWidth = 0,
    int FullscreenHeight = 0,
    bool MusicEnabled = true,
    float EffectsVolume = .85f,
    bool UseTestAssets = false,
    bool UnlimitedBuildMode = false,
    bool UnlimitedZoom = true,
    ChatDisplaySize ChatSize = ChatDisplaySize.Small,
    bool WrapChatText = true,
    bool AutoRetaliate = true,
    bool CrtMode = false,
    bool ClassicPcMode = false,
    NpcAiSettings? Ai = null,
    string LastMultiplayerEndpoint = "127.0.0.1:38740")
{
    public NpcAiSettings EffectiveAi
    {
        get
        {
            var settings = Ai ?? new();
            return NpcAiModelDefaults.IsRetiredDefault(settings.Model)
                ? settings with { Model = NpcAiModelDefaults.Current }
                : settings;
        }
    }
}

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

    public WorldProfile CreateWorld(
        string name,
        long seed,
        string? playerId,
        bool aiNpcsEnabled = false,
        int aiNpcCount = 0,
        IReadOnlyList<VillagerPersona>? aiNpcPersonas = null,
        bool observeWorld = false,
        string sharedStory = "",
        IReadOnlyList<NewWorldSurvivorSetup>? aiNpcSetups = null,
        string aiModelOverride = "",
        bool skipOpeningCouncil = false,
        bool islandStart = false)
    {
        var now = DateTime.UtcNow;
        var id = UniqueId(WorldsRoot, name);
        aiNpcCount = aiNpcsEnabled
            ? Math.Clamp(
                aiNpcCount, 0, VillagerSimulation.MaximumPopulation)
            : 0;
        var profile = new WorldProfile(
            id, CleanName(name, "New World"), seed, now, now, playerId,
            AiNpcsEnabled: aiNpcsEnabled && aiNpcCount > 0,
            AiNpcCount: aiNpcCount,
            AiNpcPersonas: aiNpcPersonas?.Take(aiNpcCount).ToArray(),
            ObserveWorld: observeWorld && aiNpcCount > 0,
            SharedStory: sharedStory.Trim(),
            AiNpcSetups: aiNpcSetups?.Take(aiNpcCount).ToArray(),
            AiModelOverride: aiNpcsEnabled
                ? aiModelOverride.Trim()
                : "",
            SkipOpeningCouncil: aiNpcsEnabled && skipOpeningCouncil,
            IslandStart: islandStart);
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

    public void SavePlayer(PlayerProfile profile)
    {
        var inventory = PlayerInventory.Load(
            profile.Inventory, profile.InventoryQuantities);
        WriteJson(
            Path.Combine(PlayersRoot, profile.Id, "player.json"),
            profile with
            {
                Inventory = inventory.ItemIds(),
                InventoryQuantities = inventory.Quantities(),
                UpdatedUtc = DateTime.UtcNow
            });
    }

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
        ClearNetworkSession(playerId);

        foreach (var worldDirectory in
                 Directory.EnumerateDirectories(WorldsRoot))
        {
            var players = Path.Combine(worldDirectory, "players");
            foreach (var path in new[]
                     {
                         Path.Combine(players, playerId + ".json"),
                         Path.Combine(players, playerId + "-deaths.json")
                     })
                if (File.Exists(path)) File.Delete(path);
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

    public IReadOnlyList<VillagerState> LoadVillagers(string worldId) =>
        ReadJson<List<VillagerState>>(Path.Combine(
            WorldsRoot, worldId, "villagers.json")) ?? [];

    public void SaveVillagers(
        string worldId,
        IReadOnlyList<VillagerState> villagers) =>
        WriteJson(
            Path.Combine(WorldsRoot, worldId, "villagers.json"),
            villagers);

    public SettlementGroupState? LoadSettlementGroup(string worldId) =>
        ReadJson<SettlementGroupState>(Path.Combine(
            WorldsRoot, worldId, "settlement-group.json"));

    public void SaveSettlementGroup(
        string worldId, SettlementGroupState group) =>
        WriteJson(
            Path.Combine(WorldsRoot, worldId, "settlement-group.json"),
            group);

    public void DeleteSettlementGroup(string worldId)
    {
        var path = Path.Combine(
            WorldsRoot, worldId, "settlement-group.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public IReadOnlyList<PlayerDeathMarker> LoadPlayerDeaths(
        string worldId,
        string playerId) =>
        (ReadJson<List<PlayerDeathMarker>>(Path.Combine(
             WorldsRoot, worldId, "players", playerId + "-deaths.json")) ?? [])
        .OrderByDescending(marker => marker.DiedUtc)
        .Take(PlayerDeathService.MaximumRememberedDeaths)
        .ToArray();

    public void AddPlayerDeath(
        string worldId,
        string playerId,
        PlayerDeathMarker marker)
    {
        var deaths = LoadPlayerDeaths(worldId, playerId)
            .Prepend(marker)
            .OrderByDescending(value => value.DiedUtc)
            .Take(PlayerDeathService.MaximumRememberedDeaths)
            .ToArray();
        WriteJson(
            Path.Combine(
                WorldsRoot, worldId, "players", playerId + "-deaths.json"),
            deaths);
    }

    public IReadOnlyList<PlayerDeathMarker> LoadVillagerDeaths(
        string worldId) =>
        (ReadJson<List<PlayerDeathMarker>>(Path.Combine(
             WorldsRoot, worldId, "villager-deaths.json")) ?? [])
        .OrderByDescending(marker => marker.DiedUtc)
        .Take(256)
        .ToArray();

    public void AddVillagerDeath(
        string worldId,
        PlayerDeathMarker marker)
    {
        var deaths = LoadVillagerDeaths(worldId)
            .Prepend(marker)
            .OrderByDescending(value => value.DiedUtc)
            .Take(256)
            .ToArray();
        WriteJson(
            Path.Combine(WorldsRoot, worldId, "villager-deaths.json"),
            deaths);
    }

    public GameSettings LoadSettings() =>
        ReadJson<GameSettings>(SettingsPath) ?? new();

    public void SaveSettings(GameSettings settings) =>
        WriteJson(SettingsPath, settings);

    public string SavedServersPath =>
        Path.Combine(Root, "saved-servers.json");

    public const int MaximumSavedServers = 32;

    public IReadOnlyList<SavedGameServer> LoadSavedServers()
    {
        var stored = ReadJson<List<SavedGameServer>>(SavedServersPath);
        if (stored is null || stored.Count == 0)
            return [];
        return stored
            .Where(value =>
                !string.IsNullOrWhiteSpace(value.Id) &&
                !string.IsNullOrWhiteSpace(value.Host) &&
                value.Port is > 0 and <= 65_535)
            .GroupBy(value =>
                $"{value.Host.Trim()}:{value.Port}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(MaximumSavedServers)
            .ToArray();
    }

    public void SaveSavedServers(IReadOnlyList<SavedGameServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        WriteJson(
            SavedServersPath,
            servers
                .Where(value =>
                    !string.IsNullOrWhiteSpace(value.Id) &&
                    !string.IsNullOrWhiteSpace(value.Host) &&
                    value.Port is > 0 and <= 65_535)
                .Take(MaximumSavedServers)
                .ToArray());
    }

    public SavedGameServer UpsertSavedServer(
        string name, string host, int port)
    {
        host = host.Trim();
        name = string.IsNullOrWhiteSpace(name)
            ? $"{host}:{port}"
            : name.Trim();
        if (name.Length > 32) name = name[..32];
        var servers = LoadSavedServers().ToList();
        var existing = servers.FindIndex(value =>
            value.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            value.Port == port);
        var record = existing >= 0
            ? servers[existing] with { Name = name }
            : new SavedGameServer(
                Guid.NewGuid().ToString("N"),
                name,
                host,
                port,
                DateTime.UtcNow);
        if (existing >= 0)
            servers[existing] = record;
        else
            servers.Insert(0, record);
        SaveSavedServers(servers);
        return record;
    }

    public void RemoveSavedServer(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        SaveSavedServers(
            LoadSavedServers()
                .Where(value =>
                    !value.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    public string NetworkSessionsRoot =>
        Path.Combine(Root, "NetworkSessions");

    public string NetworkSessionPath =>
        Path.Combine(Root, "network-session.json");

    public NetworkSessionRecord? LoadNetworkSession(string? localPlayerId = null)
    {
        if (!string.IsNullOrWhiteSpace(localPlayerId))
        {
            var stored = ReadJson<NetworkSessionRecord>(
                PerPlayerNetworkSessionPath(localPlayerId));
            if (stored is not null)
                return stored with { LocalPlayerId = localPlayerId };

            var legacy = ReadJson<NetworkSessionRecord>(NetworkSessionPath);
            if (legacy is null ||
                !CanClaimLegacyNetworkSession(legacy, localPlayerId))
                return null;
            var claimed = legacy with { LocalPlayerId = localPlayerId };
            SaveNetworkSession(claimed);
            TryDeleteFile(NetworkSessionPath);
            return claimed;
        }

        return ReadJson<NetworkSessionRecord>(NetworkSessionPath);
    }

    public void SaveNetworkSession(NetworkSessionRecord session)
    {
        var saved = session with { SavedUtc = DateTime.UtcNow };
        if (!string.IsNullOrWhiteSpace(saved.LocalPlayerId))
            WriteJson(PerPlayerNetworkSessionPath(saved.LocalPlayerId), saved);
        else
            WriteJson(NetworkSessionPath, saved);
    }

    public void ClearNetworkSession(string? localPlayerId = null)
    {
        if (!string.IsNullOrWhiteSpace(localPlayerId))
        {
            TryDeleteFile(PerPlayerNetworkSessionPath(localPlayerId));
            var legacy = ReadJson<NetworkSessionRecord>(NetworkSessionPath);
            if (legacy is not null &&
                (string.Equals(
                     legacy.LocalPlayerId, localPlayerId,
                     StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(legacy.LocalPlayerId)))
                TryDeleteFile(NetworkSessionPath);
            return;
        }

        TryDeleteFile(NetworkSessionPath);
    }

    private string PerPlayerNetworkSessionPath(string localPlayerId) =>
        Path.Combine(
            NetworkSessionsRoot,
            SanitizeNetworkSessionFileName(localPlayerId) + ".json");

    private bool CanClaimLegacyNetworkSession(
        NetworkSessionRecord legacy,
        string localPlayerId)
    {
        if (!string.IsNullOrWhiteSpace(legacy.LocalPlayerId) &&
            !string.Equals(
                legacy.LocalPlayerId, localPlayerId, StringComparison.Ordinal))
            return false;
        var player = ListPlayers().FirstOrDefault(value =>
            value.Id == localPlayerId);
        return player is not null &&
               (string.IsNullOrWhiteSpace(legacy.PlayerName) ||
                string.Equals(
                    legacy.PlayerName,
                    player.Name,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeNetworkSessionFileName(string localPlayerId)
    {
        var clean = new string(localPlayerId
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_')
            .ToArray());
        return clean.Length == 0 ? "player" : clean;
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

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
