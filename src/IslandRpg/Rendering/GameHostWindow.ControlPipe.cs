using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private GameControlPipe? _gameControlPipe;

    private void ProcessGameControlPipe()
    {
        if (_gameControlPipe is null) return;
        while (_gameControlPipe.TryDequeue(out var request))
        {
            try
            {
                using var document = JsonDocument.Parse(request.Json);
                var root = document.RootElement;
                var command = root.GetProperty("command").GetString();
                switch (command)
                {
                    case "load_latest":
                        var world = _saves.ListWorlds()
                            .Where(value => value.AiNpcsEnabled)
                            .OrderByDescending(value => value.UpdatedUtc)
                            .FirstOrDefault();
                        if (world is null)
                        {
                            request.Complete(Error("no_ai_world"));
                            break;
                        }
                        var player = _saves.ListPlayers().FirstOrDefault(value =>
                            value.Id == world.LastPlayerId) ??
                            _saves.ListPlayers().FirstOrDefault();
                        EnterWorld(world, player);
                        request.Complete(ControlSnapshot("world_loaded"));
                        break;
                    case "approach":
                        if (_player is null || _villagers.Count == 0)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var target = _villagers.First(value => value.Health > 0);
                        _player.TeleportTo(new(
                            target.PositionX - 1,
                            target.PositionY));
                        _player.Stop();
                        request.Complete(ControlSnapshot("approached"));
                        break;
                    case "chat":
                        if (_player is null)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var text = root.GetProperty("text").GetString() ?? "";
                        HandleChatSubmission(text);
                        request.Complete(ControlSnapshot("chat_submitted"));
                        break;
                    case "state":
                        request.Complete(ControlSnapshot("state"));
                        break;
                    case "stop":
                        request.Complete(ControlSnapshot("stopping"));
                        Close();
                        break;
                    default:
                        request.Complete(Error("unknown_command"));
                        break;
                }
            }
            catch (Exception exception)
            {
                request.Complete(Error(exception.Message));
            }
        }
    }

    private string ControlSnapshot(string eventType) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            eventType,
            screen = _screen.ToString(),
            gameSeconds = _worldGameSeconds,
            aiBusy = _npcAiSpeechTask is { IsCompleted: false },
            dialogueBusy = _npcAiDialogueTask is { IsCompleted: false },
            conversationFloorBusy = ConversationFloorBusy,
            player = _activePlayer is null ? null : new
            {
                _activePlayer.Id,
                _activePlayer.Name,
                position = _player is null ? null : new
                {
                    _player.Position.X,
                    _player.Position.Y
                },
                inventory = _activePlayer.Inventory?
                    .Where(value => value is not null)
            },
            villagers = _villagers.Select(villager => new
            {
                villager.Id,
                villager.Name,
                villager.Health,
                villager.Hunger,
                villager.Energy,
                villager.Activity,
                villager.ActivityUntilGameSeconds,
                villager.NextDecisionGameSeconds,
                villager.Action,
                position = new { X = villager.PositionX, Y = villager.PositionY },
                inventory = villager.Inventory
                    .Where(value => value is not null),
                villager.LastDeliberation,
                villager.Promises,
                villager.ActionPlan,
                conversation = villager.ConversationHistory?.TakeLast(6)
            })
        });

    private static string Error(string error) =>
        JsonSerializer.Serialize(new { ok = false, error });
}

internal sealed class GameControlPipe : IDisposable
{
    internal sealed class Request(string json)
    {
        private readonly TaskCompletionSource<string> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Json { get; } = json;
        public Task<string> Response => _completion.Task;
        public void Complete(string response) =>
            _completion.TrySetResult(response);
    }

    private readonly string _name;
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    public GameControlPipe(string name)
    {
        _name = name;
        _server = Task.Run(ServeAsync);
    }

    public bool TryDequeue(out Request request) =>
        _requests.TryDequeue(out request!);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true)
                    { AutoFlush = true };
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line is null) continue;
                var request = new Request(line);
                _requests.Enqueue(request);
                var response = await request.Response.WaitAsync(
                    TimeSpan.FromSeconds(30), _stop.Token);
                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
                // A stalled main loop must not terminate the control server.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _server.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
