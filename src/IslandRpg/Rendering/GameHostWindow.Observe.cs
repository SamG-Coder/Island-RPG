using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
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
            AutoFlush = true
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
    float HungerRateMultiplier = 1);

internal static class ObserveModePolicy
{
    public const int RequiredVillagerCount = 2;
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
        writer.WriteLine(Serialize(
            realSeconds, gameSeconds, gameTime,
            villagerId, eventType, data));
        writer.Flush();
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
            world.AiNpcCount != ObserveModePolicy.RequiredVillagerCount)
            throw new InvalidOperationException(
                "Observe mode requires exactly two enabled AI villagers.");

        EnterWorld(world, player);
        if (_observeMode.Scenario != ObserveScenarioService.Default)
        {
            var configured = ObserveScenarioService.Configure(
                _observeMode.Scenario, _worldSeed, _villagers);
            _villagers.Clear();
            _villagers.AddRange(configured);
            _villagersDirty = true;
            ObserveLog("scenario_started", null, new
            {
                _observeMode.Scenario,
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
            ObserverVisible = false,
            ObserverPerceived = false
        });
    }

    private Vector2 ObservationFocusPosition()
    {
        var fallback = _player?.Position ?? Vector2.Zero;
        return _observeMode is null
            ? fallback
            : ObserveModePolicy.Focus(
                _villagers, _activeWorldLevel, fallback);
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
