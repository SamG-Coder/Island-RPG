using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class ObserveConsole
{
    private const uint AttachParentProcess = 0xffffffff;
    private const int ErrorAccessDenied = 5;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;

    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outputHandle = GetStdHandle(StandardOutputHandle);
        var errorHandle = GetStdHandle(StandardErrorHandle);
        if (!IsUsable(outputHandle) || !IsUsable(errorHandle))
        {
            if (!AttachConsole(AttachParentProcess))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorAccessDenied)
                    throw new InvalidOperationException(
                        $"Observe mode could not attach to its parent " +
                        $"output (Win32 error {error}).");
            }
            outputHandle = GetStdHandle(StandardOutputHandle);
            errorHandle = GetStdHandle(StandardErrorHandle);
        }
        Console.SetOut(CreateWriter(Open(outputHandle)));
        Console.SetError(CreateWriter(Open(errorHandle)));
    }

    private static bool IsUsable(nint handle) =>
        handle != 0 && handle != -1;

    private static FileStream Open(nint handle)
    {
        if (!IsUsable(handle))
            throw new InvalidOperationException(
                "Observe mode has no writable parent output stream.");
        return new(
            new SafeFileHandle(handle, ownsHandle: false),
            FileAccess.Write);
    }

    private static StreamWriter CreateWriter(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);
}

internal sealed record ObserveModeOptions(
    string WorldId,
    string PlayerId,
    double DurationSeconds = 0,
    double LogIntervalSeconds = 2,
    string Scenario = ObserveScenarioService.Default,
    float HungerRateMultiplier = 1,
    int StartingFoodCount = 20);

internal static class ObserveModePolicy
{
    public static int RequiredVillagerCount(string scenario) =>
        ObserveScenarioService.RequiredVillagerCount(scenario);
    public static bool ObserverParticipatesInSimulation => false;

    public static Vector2 Focus(
        IReadOnlyList<VillagerState> villagers,
        int worldLevel,
        Vector2 fallback)
    {
        var living = villagers.Where(value =>
            value.Health > 0 && value.WorldLevel == worldLevel).ToArray();
        return living.Length == 0
            ? fallback
            : new(
                living.Average(value => value.PositionX),
                living.Average(value => value.PositionY));
    }
}

internal static class ObserveEventLog
{
    private static readonly object OutputLock = new();
    private static readonly ConcurrentQueue<PendingEvent> Pending = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static StreamWriter? _fileWriter;
    private static ObserveSummaryAccumulator? _summary;
    private static readonly Task OutputTask = Task.Run(ProcessOutput);
    public static string? OutputPath { get; private set; }
    private sealed record PendingEvent(
        TextWriter Writer,
        double RealSeconds,
        double GameSeconds,
        string GameTime,
        string? VillagerId,
        string EventType,
        object? Data);

    public static void ConfigureOutputFolder(string folder)
    {
        var resolved = Path.GetFullPath(folder);
        Directory.CreateDirectory(resolved);
        var path = Path.Combine(
            resolved,
            $"observe-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            FileOptions.SequentialScan);
        lock (OutputLock)
        {
            _fileWriter?.Dispose();
            _fileWriter = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };
            _summary = new(Path.Combine(
                resolved, Path.GetFileNameWithoutExtension(path)));
            OutputPath = path;
        }
    }

    public static string Serialize(
        double realSeconds,
        double gameSeconds,
        string gameTime,
        string? villagerId,
        string eventType,
        object? data) =>
        "[OBSERVE] " + JsonSerializer.Serialize(new
        {
            RealSeconds = realSeconds,
            GameSeconds = gameSeconds,
            GameTime = gameTime,
            VillagerId = villagerId,
            EventType = eventType,
            Data = data
        });

    public static void Write(
        TextWriter writer,
        double realSeconds,
        double gameSeconds,
        string gameTime,
        string? villagerId,
        string eventType,
        object? data)
    {
        // Unit tests and explicit callers retain immediate TextWriter
        // semantics. The real observe stream is queued so JSON, console/file
        // I/O, summary parsing, and disk flushes never block OpenTK's thread.
        if (!ReferenceEquals(writer, Console.Out))
        {
            var line = Serialize(
                realSeconds, gameSeconds, gameTime,
                villagerId, eventType, data);
            writer.WriteLine(line);
            writer.Flush();
            return;
        }
        Pending.Enqueue(new(
            writer,
            realSeconds,
            gameSeconds,
            gameTime,
            villagerId,
            eventType,
            data));
        PendingSignal.Set();
    }

    private static void ProcessOutput()
    {
        while (true)
        {
            PendingSignal.WaitOne(100);
            if (Pending.IsEmpty) continue;
            var writers = new HashSet<TextWriter>();
            lock (OutputLock)
            {
                while (Pending.TryDequeue(out var pending))
                {
                    var line = Serialize(
                        pending.RealSeconds,
                        pending.GameSeconds,
                        pending.GameTime,
                        pending.VillagerId,
                        pending.EventType,
                        pending.Data);
                    pending.Writer.WriteLine(line);
                    writers.Add(pending.Writer);
                    _fileWriter?.WriteLine(line);
                    _summary?.Observe(
                        pending.RealSeconds,
                        pending.VillagerId,
                        pending.EventType,
                        pending.Data);
                }
                foreach (var output in writers)
                    output.Flush();
                _fileWriter?.Flush();
            }
        }
    }
}

internal sealed partial class GameHostWindow
{
    private bool _observeStarted;
    private double _observeStartedAt;
    private double _observeNextLogAt;
    private readonly Dictionary<string, string> _observeState = [];

    private void TryBeginObserveMode()
    {
        if (_observeMode is null || _observeStarted ||
            _screen != ScreenState.MainMenu)
            return;
        var world = _saves.ListWorlds().FirstOrDefault(value =>
            value.Id == _observeMode.WorldId) ??
            throw new InvalidOperationException(
                "Observe world no longer exists.");
        var player = _saves.ListPlayers().FirstOrDefault(value =>
            value.Id == _observeMode.PlayerId) ??
            throw new InvalidOperationException(
                "Observe character no longer exists.");
        if (!world.AiNpcsEnabled ||
            world.AiNpcCount != ObserveModePolicy.RequiredVillagerCount(
                _observeMode.Scenario))
            throw new InvalidOperationException(
                $"Observe scenario '{_observeMode.Scenario}' requires " +
                $"{ObserveModePolicy.RequiredVillagerCount(_observeMode.Scenario)} enabled AI villagers.");

        EnterWorld(world, player);
        if (_observeMode.Scenario != ObserveScenarioService.Default)
        {
            var configured = ObserveScenarioService.Configure(
                _observeMode.Scenario,
                _worldSeed,
                _villagers,
                _observeMode.StartingFoodCount);
            _villagers.Clear();
            _villagers.AddRange(configured);
            _villagersDirty = true;
            ObserveLog("scenario_started", null, new
            {
                _observeMode.Scenario,
                _observeMode.StartingFoodCount,
                Biome = InfiniteWorldGenerator.BiomeAt(
                    _worldSeed,
                    (int)MathF.Floor(_villagers[0].PositionX),
                    (int)MathF.Floor(_villagers[0].PositionY)).ToString(),
                Villagers = _villagers.Select(value => new
                {
                    value.Id,
                    value.Name,
                    Position = new { value.PositionX, value.PositionY },
                    Inventory = value.Inventory
                }).ToArray()
            });
        }
        _observeStarted = true;
        _observeStartedAt = _clock;
        _observeNextLogAt = _clock;
        ObserveLog("session_started", null, new
        {
            world.Id,
            world.Name,
            world.Seed,
            ObserverId = player.Id,
            VillagerCount = _villagers.Count,
            _observeMode.Scenario,
            _observeMode.HungerRateMultiplier,
            _observeMode.StartingFoodCount,
            ObserverVisible = false,
            ObserverPerceived = false
        });
    }

    private Vector2 ObservationFocusPosition()
    {
        var fallback = _player?.Position ?? Vector2.Zero;
        if (_activeWorld?.ObserveWorld == true && _observeMode is null)
            return ScreenToTerrain(
                new(ReferenceWidth * .5f, ReferenceHeight * .5f));
        return _observeMode is null
            ? fallback
            : ObserveModePolicy.Focus(_villagers, _activeWorldLevel, fallback);
    }

    private void UpdateObserveMode()
    {
        if (_observeMode is null || !_observeStarted) return;
        if (_clock >= _observeNextLogAt)
        {
            LogObserveSnapshot();
            _observeNextLogAt = _clock +
                _observeMode.LogIntervalSeconds;
        }
        if (_observeMode.DurationSeconds <= 0 ||
            _clock - _observeStartedAt < _observeMode.DurationSeconds)
            return;
        SaveVillagers();
        ObserveLog("session_finished", null, new
        {
            DurationSeconds = _clock - _observeStartedAt,
            LivingVillagers = _villagers.Count(value => value.Health > 0)
        });
        Close();
    }

    private void LogObserveSnapshot()
    {
        var focus = ObservationFocusPosition();
        var time = WorldTime.At(_worldGameSeconds);
        var nearby = _worldChunks.Values
            .Where(IsActiveSimulationChunk)
            .SelectMany(value => value.Chunk.GroundObjects)
            .Select(value => new
            {
                value.Id,
                value.ItemId,
                value.OwnerId,
                Distance = Vector2.Distance(
                    focus, new(value.X, value.Y))
            })
            .Where(value => value.Distance <= 16)
            .OrderBy(value => value.Distance)
            .Take(12)
            .ToArray();
        ObserveLog("world_snapshot", null, new
        {
            Day = time.Day,
            time.Hour,
            time.Minute,
            Focus = new { X = focus.X, Y = focus.Y },
            ActiveChunks = _worldChunks.Values.Count(IsActiveSimulationChunk),
            NearbyResources = nearby
        });
        foreach (var villager in _villagers)
        {
            var state = string.Join('|',
                villager.Health, MathF.Round(villager.Hunger, 2),
                villager.Need, villager.Activity, villager.Action,
                villager.GoalObjectId, villager.ConversationPartnerId,
                villager.Inventory.Count(value => value is not null),
                villager.Memories?.Count ?? 0);
            if (!_observeState.TryGetValue(villager.Id, out var previous) ||
                previous != state)
            {
                ObserveLog("state_changed", villager.Id, new
                {
                    Previous = previous,
                    Current = state
                });
                _observeState[villager.Id] = state;
            }
            ObserveLog("villager_snapshot", villager.Id, new
            {
                villager.Name,
                Position = new { X = villager.PositionX, Y = villager.PositionY },
                villager.WorldLevel,
                villager.Health,
                villager.Hunger,
                villager.WellFedSeconds,
                villager.Need,
                villager.Activity,
                villager.Action,
                villager.GoalObjectId,
                villager.ConversationPartnerId,
                villager.FollowingActorId,
                Inventory = villager.Inventory,
                Goals = villager.Goals,
                Promises = villager.Promises,
                Relationships = villager.Relationships,
                Memories = villager.Memories?.TakeLast(8),
                Conversation = villager.ConversationHistory?.TakeLast(8),
                villager.LastDeliberation
            });
        }
    }

    private void ObserveChatMessage(ChatMessage message) =>
        ObserveLog("chat", null, new
        {
            Style = message.Style.ToString(),
            message.Text
        });

    private void ObserveLog(string eventType, string? villagerId, object? data)
    {
        if (_observeMode is null) return;
        var time = WorldTime.At(_worldGameSeconds);
        ObserveEventLog.Write(
            Console.Out,
            Math.Max(0, _clock - _observeStartedAt),
            _worldGameSeconds,
            $"Day {time.Day} {time.Hour:00}:{time.Minute:00}",
            villagerId,
            eventType,
            data);
    }
}
