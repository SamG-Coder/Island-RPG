using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

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
                LogControlPipeCommand(command, root);
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
                        var requestedActor = root.TryGetProperty(
                            "actor", out var actorElement)
                            ? actorElement.GetString()
                            : null;
                        var target = _villagers.FirstOrDefault(value =>
                            value.Health > 0 &&
                            (string.IsNullOrWhiteSpace(requestedActor) ||
                             value.Id.Equals(requestedActor,
                                 StringComparison.OrdinalIgnoreCase) ||
                             value.Name.Equals(requestedActor,
                                 StringComparison.OrdinalIgnoreCase))) ??
                            _villagers.First(value => value.Health > 0);
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
                    case "walk":
                        if (_player is null)
                        {
                            request.Complete(Error("world_not_loaded"));
                            break;
                        }
                        var x = root.GetProperty("x").GetSingle();
                        var y = root.GetProperty("y").GetSingle();
                        if (!float.IsFinite(x) || !float.IsFinite(y))
                        {
                            request.Complete(Error("invalid_position"));
                            break;
                        }
                        QueueWalk(new(x, y));
                        request.Complete(ControlSnapshot("walk_queued"));
                        break;
                    case "stop_player":
                        _player?.Stop();
                        request.Complete(ControlSnapshot("player_stopped"));
                        break;
                    case "act":
                        if (!TryQueueControlAction(root, out var actionError))
                        {
                            request.Complete(Error(actionError));
                            break;
                        }
                        request.Complete(ControlSnapshot("action_queued"));
                        break;
                    case "screenshot":
                        request.Complete(ControlScreenshot());
                        break;
                    case "events":
                        request.Complete(JsonSerializer.Serialize(new
                        {
                            ok = true,
                            eventType = "events",
                            events = _gameControlPipe.DrainPublished()
                        }));
                        break;
                    case "help":
                        request.Complete(JsonSerializer.Serialize(new
                        {
                            ok = true,
                            eventType = "help",
                            commands = new object[]
                            {
                                new { command = "state" },
                                new { command = "screenshot" },
                                new { command = "walk", arguments = "x, y" },
                                new
                                {
                                    command = "act",
                                    arguments = "action, x/y or actor, slot?",
                                    actions = new[]
                                    {
                                        "cut_tree", "gather_sticks", "pickup",
                                        "attack_villager", "give_item"
                                    }
                                },
                                new { command = "stop_player" },
                                new { command = "approach", arguments = "actor?" },
                                new { command = "chat", arguments = "text" },
                                new { command = "events" },
                                new { command = "load_latest" },
                                new { command = "stop" }
                            }
                        }));
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

    private bool TryQueueControlAction(
        JsonElement root, out string error)
    {
        error = "invalid_action";
        if (_player is null || _activePlayer is null)
        {
            error = "world_not_loaded";
            return false;
        }
        var action = root.GetProperty("action").GetString();
        switch (action)
        {
            case "cut_tree":
            case "gather_sticks":
                if (!TryControlPosition(root, out var treePosition))
                {
                    error = "invalid_position";
                    return false;
                }
                var tree = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.Chunk.Trees)
                    .Where(value =>
                        value.X == (int)MathF.Floor(treePosition.X) &&
                        value.Y == (int)MathF.Floor(treePosition.Y))
                    .FirstOrDefault();
                if (tree is null)
                {
                    error = "tree_not_found";
                    return false;
                }
                _worldActions.QueueTree(
                    tree,
                    action == "cut_tree"
                        ? WorldActionType.CutTree
                        : WorldActionType.GatherTreeSticks);
                return true;
            case "pickup":
                if (!TryControlPosition(root, out var pickupPosition))
                {
                    error = "invalid_position";
                    return false;
                }
                var groundObject = _worldChunks.Values
                    .Where(value =>
                        value.Chunk.Coordinate.Level == _activeWorldLevel)
                    .SelectMany(value => value.Chunk.GroundObjects)
                    .Where(value =>
                        Vector2.DistanceSquared(
                            new(value.X, value.Y), pickupPosition) <= .75f * .75f)
                    .OrderBy(value => Vector2.DistanceSquared(
                        new(value.X, value.Y), pickupPosition))
                    .FirstOrDefault();
                if (groundObject is null)
                {
                    error = "ground_object_not_found";
                    return false;
                }
                _worldActions.QueueGroundObjectPickup(groundObject);
                return true;
            case "attack_villager":
            case "give_item":
                var actor = root.TryGetProperty("actor", out var actorElement)
                    ? actorElement.GetString()
                    : null;
                var villager = _villagers.FirstOrDefault(value =>
                    value.Health > 0 &&
                    (value.Id.Equals(actor,
                         StringComparison.OrdinalIgnoreCase) ||
                     value.Name.Equals(actor,
                         StringComparison.OrdinalIgnoreCase)));
                if (villager is null)
                {
                    error = "villager_not_found";
                    return false;
                }
                if (action == "attack_villager")
                {
                    _worldActions.QueueVillagerAttack(villager);
                    return true;
                }
                var slot = root.TryGetProperty("slot", out var slotElement)
                    ? slotElement.GetInt32()
                    : -1;
                var inventory = PlayerInventory.Normalize(
                    _activePlayer.Inventory);
                if (slot < 0 || slot >= inventory.Length ||
                    inventory[slot] is not { } itemId)
                {
                    error = "invalid_inventory_slot";
                    return false;
                }
                _worldActions.QueueVillagerGift(
                    villager, slot, itemId);
                return true;
            default:
                return false;
        }
    }

    private static bool TryControlPosition(
        JsonElement root, out Vector2 position)
    {
        position = default;
        if (!root.TryGetProperty("x", out var xElement) ||
            !root.TryGetProperty("y", out var yElement))
            return false;
        var x = xElement.GetSingle();
        var y = yElement.GetSingle();
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        position = new(x, y);
        return true;
    }

    private string ControlScreenshot()
    {
        if (FramebufferSize.X <= 0 || FramebufferSize.Y <= 0)
            return Error("framebuffer_unavailable");
        var pixels = new byte[FramebufferSize.X * FramebufferSize.Y * 4];
        GL.ReadBuffer(ReadBufferMode.Front);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(
            0, 0, FramebufferSize.X, FramebufferSize.Y,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        var directory = Path.Combine(
            _saves.Root, "ControlPipe", "Screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"island-rpg-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
        PngScreenshotWriter.Write(
            path, pixels, FramebufferSize.X, FramebufferSize.Y,
            flipVertically: true);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            eventType = "screenshot",
            path,
            width = FramebufferSize.X,
            height = FramebufferSize.Y
        });
    }

    private void LogControlPipeCommand(
        string? command, JsonElement root)
    {
        var detail = command == "chat" &&
                     root.TryGetProperty("text", out var text)
            ? $" \"{text.GetString()}\""
            : "";
        _chatUi.AddMessage(
            $"Codex: {command ?? "unknown"}{detail}",
            ChatMessageStyle.Debug);
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
    private readonly ConcurrentQueue<string> _published = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    public GameControlPipe(string name)
    {
        _name = name;
        _server = Task.Run(ServeAsync);
    }

    public bool TryDequeue(out Request request) =>
        _requests.TryDequeue(out request!);

    public void Publish(object message) =>
        _published.Enqueue(JsonSerializer.Serialize(message));

    public JsonElement[] DrainPublished()
    {
        var result = new List<JsonElement>();
        while (_published.TryDequeue(out var message))
        {
            using var document = JsonDocument.Parse(message);
            result.Add(document.RootElement.Clone());
        }
        return result.ToArray();
    }

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
