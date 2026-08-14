using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FontStashSharp;
using IslandRpg.Boats;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using IslandRpg.Server;
using IslandRpg.Server.Persistence;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const string NewHostedWorldId = "new";

    private readonly TextBoxControlState _multiplayerEndpointTextBox =
        new("127.0.0.1:38740") { MaximumLength = 64 };
    private readonly TextBoxControlState _multiplayerSeedTextBox =
        new(Random.Shared.NextInt64().ToString());
    private readonly ListControlState _hostedWorldList = new();
    private readonly List<HostedWorldChoice> _hostedWorldChoices = [];
    private bool _multiplayerIslandStart;
    private string? _multiplayerStatus;
    private Process? _hostedServerProcess;
    private bool _multiplayerBusy;

    private bool IsHostingWorld =>
        _hostedServerProcess is { HasExited: false };

    private Vector4 MultiplayerPanel() => FrontendPanel(760, 640);

    private bool IsNewHostedWorldSelected =>
        _hostedWorldList.SelectedId is null or NewHostedWorldId;

    private void OpenMultiplayerPage()
    {
        var settings = _saves.LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.LastMultiplayerEndpoint))
            _multiplayerEndpointTextBox.SetText(
                SanitizeJoinEndpoint(settings.LastMultiplayerEndpoint));
        else
            _multiplayerEndpointTextBox.SetText("127.0.0.1:38740");
        RefreshHostedWorldChoices();
        _hostedWorldList.SelectedId = NewHostedWorldId;
        _multiplayerStatus = null;
        _frontendError = null;
        _frontendPage = FrontendPage.Multiplayer;
        BlurTextBoxes();
    }

    private void RefreshHostedWorldChoices()
    {
        _hostedWorldChoices.Clear();
        _hostedWorldChoices.Add(HostedWorldChoice.NewWorld);
        var saveRoot = HostedWorldsRoot();
        Directory.CreateDirectory(saveRoot);
        var seen = new HashSet<Guid>();
        var worldsRoot = Path.Combine(saveRoot, "Worlds");
        if (Directory.Exists(worldsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(worldsRoot))
            {
                var name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name, "N", out var worldId))
                    continue;
                var checkpoint = TryLoadHostedCheckpoint(saveRoot, worldId);
                if (checkpoint is null) continue;
                seen.Add(worldId);
                var written = File.GetLastWriteTimeUtc(
                    Path.Combine(directory, "server-checkpoint.json"));
                _hostedWorldChoices.Add(new(
                    worldId.ToString("N"),
                    worldId,
                    checkpoint.WorldSeed,
                    checkpoint.IslandStart,
                    false,
                    null,
                    null,
                    written));
            }
        }

        var last = LoadHostedWorld(saveRoot);
        if (last is not null && seen.Add(last.WorldId))
            _hostedWorldChoices.Add(new(
                last.WorldId.ToString("N"),
                last.WorldId,
                last.Seed,
                last.IslandStart,
                false,
                last.SpawnX,
                last.SpawnY,
                DateTime.UtcNow));
    }

    private void LayoutHostedWorldList()
    {
        var list = HostedWorldListBounds();
        _hostedWorldList.Layout(
            list,
            _hostedWorldChoices.Select(value => value.Id).ToArray(),
            rowHeight: 44,
            rowGap: 6,
            deleteWidth: 72,
            actionGap: 6);
        _hostedWorldList.SelectedId ??= NewHostedWorldId;
    }

    private void RenderMultiplayerMenu()
    {
        RefreshHostedWorldChoices();
        LayoutHostedWorldList();
        var panel = MultiplayerPanel();
        DrawAoEPanelBorder(panel);
        DrawCenteredMenuTitle(
            "MULTIPLAYER",
            new(panel.X, panel.Y + 16, panel.Z, 38),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            "Host a saved world, start a new one, or join a friend",
            new(panel.X + 40, panel.Y + 54, panel.Z - 80, 22),
            new(180, 158, 107, 255));

        var character = new Vector4(
            panel.X + 44, panel.Y + 86, panel.Z - 88, 32);
        DrawUiColor(character, new(.038f, .035f, .026f, 1));
        DrawPanelOutline(character, 0, new(.23f, .19f, .11f, 1));
        DrawCenteredUiText(
            _selectedPlayer is null
                ? "SELECT A CHARACTER FIRST"
                : $"ADVENTURER   •   {_selectedPlayer.Name.ToUpperInvariant()}",
            character,
            _selectedPlayer is null
                ? new(220, 104, 82, 255)
                : new(199, 184, 142, 255));

        var hostBox = HostBoxBounds();
        DrawUiColor(hostBox, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(hostBox, 0, new(.28f, .23f, .14f, 1));
        DrawUiText(
            "HOST",
            new(hostBox.X + 18, hostBox.Y + 12),
            new(218, 202, 158, 255));
        DrawUiText(
            "Choose a saved world or create a new one.",
            new(hostBox.X + 70, hostBox.Y + 14),
            new(145, 138, 117, 255));

        RenderHostedWorldRows();
        RenderListScrollbar(_hostedWorldList);

        var seedBounds = HostSeedBounds();
        if (IsNewHostedWorldSelected)
        {
            _multiplayerSeedTextBox.Bounds = seedBounds;
            DrawUiText(
                "SEED",
                new(seedBounds.X, seedBounds.Y - 16),
                new(204, 190, 150, 255));
            DrawTextField(_multiplayerSeedTextBox);
            DrawMenuButton(HostRandomSeedBounds(), "Random");
            DrawMenuButton(
                HostIslandBounds(),
                _multiplayerIslandStart ? "Shore start: On" : "Shore start: Off");
        }
        else if (SelectedHostedWorld() is { } selected)
        {
            DrawUiText(
                selected.IslandStart
                    ? $"Resume shore world  •  seed {selected.Seed}"
                    : $"Resume open world  •  seed {selected.Seed}",
                new(seedBounds.X, seedBounds.Y + 8),
                new(180, 158, 107, 255));
        }

        DrawMainMenuButton(
            HostStartButtonBounds(),
            IsHostingWorld
                ? "HOSTING…"
                : IsNewHostedWorldSelected ? "HOST NEW WORLD" : "HOST THIS WORLD",
            primary: true);

        var joinBox = JoinBoxBounds();
        DrawUiColor(joinBox, new(.038f, .036f, .030f, .82f));
        DrawPanelOutline(joinBox, 0, new(.28f, .23f, .14f, 1));
        DrawUiText(
            "JOIN",
            new(joinBox.X + 18, joinBox.Y + 12),
            new(218, 202, 158, 255));
        var endpointBounds = JoinEndpointBounds();
        _multiplayerEndpointTextBox.Bounds = endpointBounds;
        DrawUiText(
            "HOST:PORT",
            new(endpointBounds.X, endpointBounds.Y - 16),
            new(204, 190, 150, 255));
        DrawTextField(_multiplayerEndpointTextBox);
        DrawUiText(
            "127.0.0.1:38740 on this PC  •  LAN IP:port for a friend",
            new(endpointBounds.X, endpointBounds.Y + 40),
            new(132, 124, 104, 255));
        DrawMainMenuButton(JoinButtonBounds(), "JOIN", primary: true);

        var status = _frontendError ?? _multiplayerStatus;
        if (!string.IsNullOrWhiteSpace(status))
            DrawCenteredUiText(
                status,
                new(panel.X + 44, panel.Y + panel.W - 86, panel.Z - 88, 22),
                _frontendError is null
                    ? new(199, 184, 142, 255)
                    : new(220, 104, 82, 255));
        DrawMainMenuButton(HostBackBounds(), "Back", quiet: true);
    }

    private void RenderHostedWorldRows()
    {
        foreach (var index in _hostedWorldList.VisibleIndices)
        {
            if ((uint)index >= (uint)_hostedWorldChoices.Count) continue;
            var choice = _hostedWorldChoices[index];
            var row = _hostedWorldList.RowBounds(index);
            var delete = _hostedWorldList.DeleteBounds(index);
            var selected = _hostedWorldList.SelectedId == choice.Id;
            var hovered = row.Contains(MouseState.Position);
            DrawUiColor(
                row,
                selected
                    ? new(.155f, .12f, .055f, 1)
                    : hovered
                        ? new(.10f, .086f, .052f, 1)
                        : new(.060f, .055f, .041f, 1));
            DrawPanelOutline(
                row, 0,
                selected
                    ? new(.57f, .42f, .14f, 1)
                    : new(.23f, .19f, .11f, 1));
            DrawUiText(
                choice.Title,
                new(row.X + 12, row.Y + 6),
                new FSColor(232, 217, 166, 255));
            DrawUiText(
                choice.Details,
                new(row.X + 12, row.Y + 24),
                new FSColor(142, 136, 116, 255));
            if (choice.IsNew) continue;
            var pending = _hostedWorldList.IsDeletePending(choice.Id);
            DrawMenuButton(delete, pending ? "CONFIRM" : "DELETE");
        }
    }

    private void UpdateMultiplayerClick(Vector2 pointer)
    {
        if (_multiplayerBusy) return;
        RefreshHostedWorldChoices();
        LayoutHostedWorldList();
        if (_hostedWorldList.TryHit(pointer, out var index, out var delete) &&
            (uint)index < (uint)_hostedWorldChoices.Count)
        {
            var choice = _hostedWorldChoices[index];
            if (delete && !choice.IsNew)
            {
                if (_hostedWorldList.ApproveDelete(choice.Id))
                    DeleteHostedWorld(choice.WorldId);
                return;
            }

            _hostedWorldList.SelectedId = choice.Id;
            _hostedWorldList.ClearDeleteApproval();
            _frontendError = null;
            return;
        }

        if (IsNewHostedWorldSelected &&
            HostSeedBounds().Contains(pointer))
            FocusTextBox(
                _multiplayerSeedTextBox, HostSeedBounds(), pointer);
        else if (JoinEndpointBounds().Contains(pointer))
            FocusTextBox(
                _multiplayerEndpointTextBox, JoinEndpointBounds(), pointer);
        else if (IsNewHostedWorldSelected &&
                 HostRandomSeedBounds().Contains(pointer))
        {
            _multiplayerSeedTextBox.SetText(
                Random.Shared.NextInt64().ToString());
            FocusTextBoxAtEnd(_multiplayerSeedTextBox);
        }
        else if (IsNewHostedWorldSelected &&
                 HostIslandBounds().Contains(pointer))
            _multiplayerIslandStart = !_multiplayerIslandStart;
        else if (HostStartButtonBounds().Contains(pointer))
            _ = HostMultiplayerWorldAsync();
        else if (JoinButtonBounds().Contains(pointer))
            JoinMultiplayerWorld();
        else if (HostBackBounds().Contains(pointer))
        {
            _frontendPage = FrontendPage.Main;
            BlurTextBoxes();
        }
        else
            BlurTextBoxes();
    }

    private async Task HostMultiplayerWorldAsync()
    {
        if (_multiplayerBusy) return;
        if (_selectedPlayer is null)
        {
            _frontendError = "Create or select a character first.";
            return;
        }

        RefreshHostedWorldChoices();
        var selected = SelectedHostedWorld();
        var createNew = selected is null || selected.IsNew;
        _multiplayerBusy = true;
        _frontendError = null;
        _multiplayerStatus = createNew
            ? "Creating hosted world…"
            : "Opening hosted world…";
        var islandStart = _multiplayerIslandStart;
        var seedText = _multiplayerSeedTextBox.Text.Trim();
        var resume = selected;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!long.TryParse(seedText, out var seed))
                    seed = Random.Shared.NextInt64();
                var saveRoot = HostedWorldsRoot();
                Directory.CreateDirectory(saveRoot);
                Guid worldId;
                bool useIslandStart;
                System.Numerics.Vector2 spawn;
                if (createNew || resume is null)
                {
                    worldId = Guid.NewGuid();
                    useIslandStart = islandStart;
                    spawn = BoatTravelRules.FindPlayableLandSpawn(seed);
                }
                else
                {
                    worldId = resume.WorldId;
                    seed = resume.Seed;
                    useIslandStart = resume.IslandStart;
                    spawn = resume.SpawnX is { } x && resume.SpawnY is { } y
                        ? new System.Numerics.Vector2(x, y)
                        : BoatTravelRules.FindPlayableLandSpawn(seed);
                }

                SaveHostedWorld(
                    saveRoot, worldId, seed, spawn, useIslandStart);
                await StopHostedServerAsync().ConfigureAwait(false);
                var endpoint = await StartHostedServerAsync(
                    worldId, seed, saveRoot, spawn, useIslandStart)
                    .ConfigureAwait(false);
                var lan = GuessLanAddress();
                _networkEvents.Enqueue(() =>
                {
                    RefreshHostedWorldChoices();
                    _hostedWorldList.SelectedId = worldId.ToString("N");
                    _multiplayerStatus =
                        $"Hosting on {lan}:{endpoint.Port}. Connecting…";
                    _multiplayerEndpointTextBox.SetText($"{lan}:{endpoint.Port}");
                    BeginJoin("127.0.0.1", endpoint.Port, worldId);
                    _multiplayerBusy = false;
                });
            }
            catch (Exception exception)
            {
                await StopHostedServerAsync().ConfigureAwait(false);
                _networkEvents.Enqueue(() =>
                {
                    _frontendError = $"Could not host: {exception.Message}";
                    _multiplayerStatus = null;
                    _multiplayerBusy = false;
                });
            }
        });
    }

    private void JoinMultiplayerWorld()
    {
        if (_multiplayerBusy) return;
        if (_selectedPlayer is null)
        {
            _frontendError = "Create or select a character first.";
            return;
        }

        try
        {
            var launch = NetworkLaunchOptions.Parse(
                _multiplayerEndpointTextBox.Text,
                _selectedPlayer.Name);
            _multiplayerEndpointTextBox.SetText($"{launch.Host}:{launch.Port}");
            _frontendError = null;
            _multiplayerStatus = $"Connecting to {launch.Host}:{launch.Port}…";
            BeginJoin(launch.Host, launch.Port, Guid.Empty);
        }
        catch (Exception exception)
        {
            _frontendError = exception.Message;
        }
    }

    private void BeginJoin(string host, int port, Guid worldId)
    {
        if (_selectedPlayer is null) return;
        host = NetworkLaunchOptions.NormalizeConnectHost(host);
        var localPlayerId = _selectedPlayer.Id;
        var session = _saves.LoadNetworkSession(localPlayerId);
        var reconnects = NetworkSessionReuse.CanReconnect(
            session, localPlayerId, host, port, worldId);
        _networkLaunch = new NetworkLaunchOptions(
            host,
            port,
            _selectedPlayer.Name,
            worldId,
            Guid.NewGuid(),
            reconnects ? session!.PlayerId : Guid.Empty,
            reconnects ? session!.ReconnectToken : "",
            _selectedPlayer.Gender,
            _selectedPlayer.TeamColor,
            localPlayerId);
        _networkConnectStarted = false;
        _networkWorldEntered = false;
        BeginNetworkConnection();
    }

    private static string SanitizeJoinEndpoint(string endpoint)
    {
        try
        {
            var launch = NetworkLaunchOptions.Parse(endpoint, "join");
            return $"{launch.Host}:{launch.Port}";
        }
        catch
        {
            return "127.0.0.1:38740";
        }
    }

    private async Task<IPEndPoint> StartHostedServerAsync(
        Guid worldId,
        long seed,
        string saveRoot,
        System.Numerics.Vector2 spawn,
        bool islandStart)
    {
        _ = spawn;
        var launch = ResolveDedicatedServerLaunch();
        Exception? last = null;
        for (var port = ServerOptions.DefaultPort;
             port < ServerOptions.DefaultPort + 10;
             port++)
        {
            var process = StartDedicatedServerProcess(
                launch, (ushort)port, worldId, seed, saveRoot, islandStart);
            try
            {
                var endpoint = await WaitForDedicatedServerListenAsync(
                    process, (ushort)port).ConfigureAwait(false);
                _hostedServerProcess = process;
                return endpoint;
            }
            catch (Exception exception)
            {
                last = exception;
                TerminateDedicatedServerProcess(process);
            }
        }

        throw last ?? new InvalidOperationException(
            "The dedicated world process did not bind a port.");
    }

    private async Task StopHostedServerAsync()
    {
        var process = _hostedServerProcess;
        _hostedServerProcess = null;
        if (process is null) return;
        await Task.Run(() => TerminateDedicatedServerProcess(process))
            .ConfigureAwait(false);
    }

    private readonly record struct DedicatedServerLaunch(
        string FileName,
        string ArgumentPrefix,
        string WorkingDirectory);

    private static DedicatedServerLaunch ResolveDedicatedServerLaunch()
    {
        var baseDir = AppContext.BaseDirectory;
        var windowsExe = Path.Combine(baseDir, "IslandRpg.Server.exe");
        if (OperatingSystem.IsWindows() && File.Exists(windowsExe))
            return new(windowsExe, "", baseDir);

        var unixHost = Path.Combine(baseDir, "IslandRpg.Server");
        if (!OperatingSystem.IsWindows() && File.Exists(unixHost))
            return new(unixHost, "", baseDir);

        var dll = Path.Combine(baseDir, "IslandRpg.Server.dll");
        if (File.Exists(dll))
            return new("dotnet", $"\"{dll}\" ", baseDir);

        throw new FileNotFoundException(
            "IslandRpg.Server was not found next to the game. Host starts a separate world process.");
    }

    private static Process StartDedicatedServerProcess(
        DedicatedServerLaunch launch,
        ushort port,
        Guid worldId,
        long seed,
        string saveRoot,
        bool islandStart)
    {
        var arguments =
            $"{launch.ArgumentPrefix}--listen 0.0.0.0:{port} " +
            $"--world-id {worldId:D} --world-seed {seed} " +
            $"--save-root \"{saveRoot}\" --max-clients 16";
        if (islandStart)
            arguments += " --island-start";

        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            Arguments = arguments,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(start);
        if (process is null)
            throw new InvalidOperationException(
                "The dedicated world process did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<IPEndPoint> WaitForDedicatedServerListenAsync(
        Process process,
        ushort port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    "The dedicated world process exited before it started listening.");
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, port);
                var finished = await Task.WhenAny(
                    connect, Task.Delay(200)).ConfigureAwait(false);
                if (finished == connect)
                {
                    await connect.ConfigureAwait(false);
                    if (client.Connected)
                        return new IPEndPoint(IPAddress.Loopback, port);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The dedicated world process did not start listening.");
    }

    private static void TerminateDedicatedServerProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(4000);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private HostedWorldChoice? SelectedHostedWorld()
    {
        var id = _hostedWorldList.SelectedId;
        return _hostedWorldChoices.FirstOrDefault(value => value.Id == id);
    }

    private void DeleteHostedWorld(Guid worldId)
    {
        var saveRoot = HostedWorldsRoot();
        var directory = Path.Combine(saveRoot, "Worlds", worldId.ToString("N"));
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var last = LoadHostedWorld(saveRoot);
        if (last?.WorldId == worldId)
        {
            var path = Path.Combine(saveRoot, "host.json");
            if (File.Exists(path)) File.Delete(path);
        }
        RefreshHostedWorldChoices();
        _hostedWorldList.SelectedId = NewHostedWorldId;
    }

    private sealed record HostedWorldChoice(
        string Id,
        Guid WorldId,
        long Seed,
        bool IslandStart,
        bool IsNew,
        float? SpawnX,
        float? SpawnY,
        DateTime UpdatedUtc)
    {
        public static HostedWorldChoice NewWorld { get; } = new(
            NewHostedWorldId, Guid.Empty, 0, false, true, null, null,
            DateTime.MinValue);

        public string Title => IsNew
            ? "NEW WORLD"
            : IslandStart ? "SHORE WORLD" : "OPEN WORLD";

        public string Details => IsNew
            ? "Fresh seed and shore-start setting"
            : $"SEED {Seed}   •   {UpdatedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    private sealed record HostedWorldRecord(
        Guid WorldId,
        long Seed,
        bool IslandStart,
        float? SpawnX = null,
        float? SpawnY = null);

    private string HostedWorldsRoot() =>
        Path.Combine(_saves.Root, "NetworkWorlds");

    private static ServerCheckpoint? TryLoadHostedCheckpoint(
        string saveRoot, Guid? worldId)
    {
        if (worldId is not { } id || id == Guid.Empty)
            return null;
        try
        {
            return new ServerCheckpointStore(saveRoot).Load(id)?.Checkpoint;
        }
        catch
        {
            return null;
        }
    }

    private static HostedWorldRecord? LoadHostedWorld(string saveRoot)
    {
        var path = Path.Combine(saveRoot, "host.json");
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HostedWorldRecord>(
                File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveHostedWorld(
        string saveRoot,
        Guid worldId,
        long seed,
        System.Numerics.Vector2 spawn,
        bool islandStart)
    {
        var path = Path.Combine(saveRoot, "host.json");
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                new HostedWorldRecord(
                    worldId,
                    seed,
                    islandStart,
                    spawn.X,
                    spawn.Y)));
    }

    private Vector4 HostBoxBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + 128, panel.Z - 88, 286);
    }

    private Vector4 HostedWorldListBounds()
    {
        var host = HostBoxBounds();
        return new(host.X + 18, host.Y + 40, host.Z - 36, 140);
    }

    private Vector4 HostSeedBounds()
    {
        var host = HostBoxBounds();
        return new(host.X + 18, host.Y + 202, 280, 32);
    }

    private Vector4 HostRandomSeedBounds()
    {
        var seed = HostSeedBounds();
        return new(seed.X + 292, seed.Y, 92, 32);
    }

    private Vector4 HostIslandBounds()
    {
        var seed = HostSeedBounds();
        return new(seed.X, seed.Y + 40, 220, 32);
    }

    private Vector4 HostStartButtonBounds()
    {
        var host = HostBoxBounds();
        return new(host.X + host.Z - 196, host.Y + host.W - 52, 178, 40);
    }

    private Vector4 JoinBoxBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + 424, panel.Z - 88, 128);
    }

    private Vector4 JoinEndpointBounds()
    {
        var join = JoinBoxBounds();
        return new(join.X + 18, join.Y + 56, join.Z - 214, 36);
    }

    private Vector4 JoinButtonBounds()
    {
        var join = JoinBoxBounds();
        return new(join.X + join.Z - 178, join.Y + 52, 160, 44);
    }

    private Vector4 HostBackBounds()
    {
        var panel = MultiplayerPanel();
        return new(panel.X + 44, panel.Y + panel.W - 52, 108, 40);
    }

    private static string GuessLanAddress()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is
                        NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily ==
                            AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                        return address.Address.ToString();
                }
            }
        }
        catch
        {
        }

        return "127.0.0.1";
    }
}
