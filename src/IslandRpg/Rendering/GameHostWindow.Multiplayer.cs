using System.Collections.Concurrent;
using IslandRpg.Client;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Protocol;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using ProtocolChatChannel = IslandRpg.Protocol.ChatChannel;

namespace IslandRpg.Rendering;

internal sealed record NetworkLaunchOptions(
    string Host,
    int Port,
    string PlayerName,
    Guid WorldId)
{
    public static NetworkLaunchOptions Parse(
        string endpoint,
        string playerName,
        Guid worldId = default)
    {
        endpoint = endpoint.Trim();
        var host = endpoint;
        var port = 38_740;
        if (endpoint.StartsWith('['))
        {
            var closing = endpoint.IndexOf(']');
            if (closing <= 1)
                throw new ArgumentException("--connect contains an invalid IPv6 address.");
            host = endpoint[1..closing];
            if (closing + 1 < endpoint.Length &&
                (endpoint[closing + 1] != ':' ||
                 !int.TryParse(endpoint[(closing + 2)..], out port)))
                throw new ArgumentException("--connect must use host:port.");
        }
        else
        {
            var separator = endpoint.LastIndexOf(':');
            if (separator > 0)
            {
                host = endpoint[..separator];
                if (!int.TryParse(endpoint[(separator + 1)..], out port))
                    throw new ArgumentException("--connect must use host:port.");
            }
        }

        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > ushort.MaxValue)
            throw new ArgumentException("--connect must use a valid host and port.");
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 40 || playerName.Any(char.IsControl))
            throw new ArgumentException("--network-player must contain 1-40 printable characters.");
        return new(host, port, playerName, worldId);
    }
}

internal sealed partial class GameHostWindow
{
    private readonly NetworkLaunchOptions? _networkLaunch;
    private readonly ConcurrentQueue<Action> _networkEvents = new();
    private readonly Dictionary<ulong, WorldEntity> _networkActors = [];
    private NetworkGameClient? _networkClient;
    private CancellationTokenSource? _networkCancellation;
    private bool _networkConnectStarted;
    private bool _networkWorldEntered;
    private double _nextNetworkMutationWarningAt;
    private ulong _networkWorldClockTick;

    private bool IsNetworkWorld => _networkLaunch is not null &&
                                   _networkWorldEntered;

    private void BeginNetworkConnection()
    {
        if (_networkLaunch is null || _networkConnectStarted) return;
        _networkConnectStarted = true;
        _networkClient = new NetworkGameClient();
        _networkCancellation = new CancellationTokenSource();
        _networkClient.StateChanged += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkState(value.State));
        _networkClient.ChatReceived += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkChat(value.Message));
        _networkClient.CommandCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkCommandResult(value.Result));
        _networkClient.ActionCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkActionResult(value.Result));
        _networkClient.PlayerStateChanged += (_, value) =>
            _networkEvents.Enqueue(() => ApplyNetworkPlayerState(value.State));
        _networkClient.PlayerJoined += (_, value) =>
            _networkEvents.Enqueue(() => _chatUi.AddMessage(
                $"{value.Player.PlayerName} joined the world.",
                ChatMessageStyle.Action));
        _networkClient.PlayerLeft += (_, _) =>
            _networkEvents.Enqueue(() => _chatUi.AddMessage(
                "A player left the world.", ChatMessageStyle.Action));

        _chatUi.AddMessage(
            $"Connecting to {_networkLaunch.Host}:{_networkLaunch.Port}...",
            ChatMessageStyle.Action);
        _ = ConnectNetworkAsync(_networkLaunch, _networkCancellation.Token);
    }

    private async Task ConnectNetworkAsync(
        NetworkLaunchOptions launch,
        CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await _networkClient!.ConnectAsync(
                launch.Host,
                launch.Port,
                new ClientHandshakeOptions(
                    "0.3.0", "base", Guid.NewGuid(), launch.PlayerName,
                    launch.WorldId),
                cancellationToken).ConfigureAwait(false);
            _networkEvents.Enqueue(() => EnterNetworkWorld(accepted));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _networkEvents.Enqueue(() => _chatUi.AddMessage(
                $"Connection failed: {exception.Message}",
                ChatMessageStyle.Warning));
        }
    }

    private void ProcessNetworkEvents()
    {
        while (_networkEvents.TryDequeue(out var action)) action();
    }

    private void EnterNetworkWorld(HandshakeAcceptedMessage accepted)
    {
        CancelWorldLevelWork(clearMinimap: true);
        FinishPendingMenuChunk();
        foreach (var coordinate in _worldChunks.Keys.ToArray())
            UnloadWorldChunk(coordinate, save: false);

        _worldSeed = accepted.WorldSeed;
        _activeWorldLevel = accepted.SpawnWorldLevel;
        _activeWorld = new WorldProfile(
            accepted.WorldId.ToString("N"), "Multiplayer World",
            accepted.WorldSeed, DateTime.UtcNow, DateTime.UtcNow);
        _activePlayer = new PlayerProfile(
            accepted.PlayerId.ToString("N"), _networkLaunch!.PlayerName,
            EntityGender.Male, 2, 1, DateTime.UtcNow, DateTime.UtcNow,
            Inventory: PlayerInventory.CreateStartingInventory());
        var cacheRoot = Path.Combine(
            Path.GetTempPath(), "IslandRpg", "NetworkCache");
        _worldStore = new WorldChunkStore(
            accepted.WorldSeed, cacheRoot, accepted.WorldId.ToString("N"));
        _player = new WorldEntity(
            new Vector2(accepted.SpawnX, accepted.SpawnY),
            EntityGender.Male);
        _networkActors.Clear();
        _networkWorldEntered = true;
        _networkWorldClockTick = accepted.Tick;
        UpdateNetworkWorldClock(accepted.Tick);
        _playerDefeated = false;
        _modalScreen.Close(ModalScreenKind.Death);
        _villagers.Clear();
        _enemies.Clear();
        _queuedAction = null;
        _moveMarker = null;
        _camera = Vector2.Zero;
        SetZoomImmediate(.8f);
        FollowPlayer();
        StreamWorld();
        BlurTextBoxes();
        _screen = ScreenState.WorldPreview;
        _chatUi.AddMessage(
            $"Connected to multiplayer world {accepted.WorldId:N}.",
            ChatMessageStyle.Reward);
    }

    private void HandleNetworkState(NetworkGameClientState state)
    {
        if (state.ServerTick > _networkWorldClockTick)
        {
            _networkWorldClockTick = state.ServerTick;
            UpdateNetworkWorldClock(state.ServerTick);
        }
        if (state.Status == NetworkGameClientStatus.Faulted)
            _chatUi.AddMessage(
                $"Network connection lost: {state.LastError ?? "unknown error"}",
                ChatMessageStyle.Warning);
    }

    private void HandleNetworkChat(NetworkChatEvent message)
    {
        if (_networkClient?.State.PlayerId == message.SenderPlayerId)
        {
            ShowOverheadSpeech(message.Text);
            return;
        }
        _chatUi.AddMessage(
            $"{message.SenderPlayerName}: {message.Text}",
            ChatMessageStyle.Player);
    }

    private void HandleNetworkCommandResult(CommandResultMessage result)
    {
        if (result.Accepted) return;
        _chatUi.AddMessage(
            string.IsNullOrWhiteSpace(result.Detail)
                ? $"Server rejected the command ({result.RejectionCode})."
                : result.Detail,
            ChatMessageStyle.Warning);
    }

    private void HandleNetworkActionResult(ActionResultMessage result)
    {
        if (result.Accepted) return;
        _chatUi.AddMessage(
            string.IsNullOrWhiteSpace(result.Detail)
                ? $"Server rejected the action ({result.RejectionCode})."
                : result.Detail,
            ChatMessageStyle.Warning);
    }

    private void ApplyNetworkPlayerState(NetworkPlayerGameplayState state)
    {
        if (_activePlayer is null) return;
        var items = new string?[PlayerInventory.Capacity];
        var quantities = new int[PlayerInventory.Capacity];
        foreach (var slot in state.InventorySlots)
        {
            if ((uint)slot.Slot >= (uint)items.Length || slot.IsEmpty)
                continue;
            items[slot.Slot] = slot.ItemId;
            quantities[slot.Slot] = slot.Quantity;
        }
        _activePlayer = _activePlayer with
        {
            Inventory = items,
            InventoryQuantities = quantities,
            Health = state.Health,
            Hunger = state.Hunger,
            WellFedSeconds = state.WellFedSeconds,
            CraftingExperience = state.CraftingExperience,
            CookingExperience = state.CookingExperience,
            UpdatedUtc = DateTime.UtcNow
        };
        if (_activeInventorySlot >= 0 &&
            items[_activeInventorySlot] is null)
            _activeInventorySlot = -1;
    }

    private void UpdateNetworkGame(float elapsed)
    {
        if (_networkClient is null || _player is null) return;
        UpdateNetworkWorldClock(_networkClient.State.ServerTick);
        ApplyNetworkSnapshot(elapsed);
        var rightDown = MouseState.IsButtonDown(
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);
        var leftDown = MouseState.IsButtonDown(
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        if (rightDown && !_gameRightWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
            SendNetworkWalk(ScreenToTerrain(SceneMousePosition()));
        if (leftDown && !_gameLeftWasDown &&
            !IsPointerOverGameUi(MouseState.Position))
            WarnNetworkMutationUnavailable();
        if (!_chatUi.Input.Focused && KeyboardState.IsKeyPressed(
                OpenTK.Windowing.GraphicsLibraryFramework.Keys.X))
            SendNetworkStop();
        _gameRightWasDown = rightDown;
        _gameLeftWasDown = leftDown;
        UpdateNativeCursor();
        FollowPlayer();
    }

    private void ApplyNetworkSnapshot(float elapsed)
    {
        if (_networkClient is null ||
            !_networkClient.SnapshotBuffer.TrySample(out var sampled) ||
            sampled is null)
            return;
        var seen = new HashSet<ulong>();
        foreach (var snapshot in sampled.Entities)
        {
            if (snapshot.EntityKind != NetworkEntityKind.Player ||
                snapshot.State.HasFlag(NetworkEntityState.Hidden))
                continue;
            seen.Add(snapshot.EntityId);
            var position = new Vector2(snapshot.X, snapshot.Y);
            var velocity = new Vector2(
                snapshot.VelocityX, snapshot.VelocityY);
            var entity = snapshot.EntityId == _networkClient.State.PlayerEntityId
                ? _player!
                : GetOrCreateNetworkActor(snapshot.EntityId, position);
            SyncNetworkEntity(entity, position, velocity, snapshot.State, elapsed);
            if (snapshot.EntityId == _networkClient.State.PlayerEntityId)
                _activeWorldLevel = snapshot.WorldLevel;
        }
        foreach (var id in _networkActors.Keys
                     .Where(id => !seen.Contains(id)).ToArray())
            _networkActors.Remove(id);
    }

    private WorldEntity GetOrCreateNetworkActor(ulong id, Vector2 position)
    {
        if (_networkActors.TryGetValue(id, out var entity)) return entity;
        entity = new WorldEntity(position, EntityGender.Male);
        _networkActors.Add(id, entity);
        return entity;
    }

    private static void SyncNetworkEntity(
        WorldEntity entity,
        Vector2 position,
        Vector2 velocity,
        NetworkEntityState state,
        float elapsed)
    {
        if (state.HasFlag(NetworkEntityState.Dead))
            entity.Die();
        else if (velocity.LengthSquared > .0001f ||
                 state.HasFlag(NetworkEntityState.Moving))
        {
            entity.Face(velocity);
            if (entity.Action != EntityAction.Move)
                entity.MoveTo(position + velocity);
        }
        else
            entity.Stop();
        entity.SyncPosition(position);
        entity.AdvanceAction(elapsed);
    }

    private void AddNetworkActorVisuals(List<ActorVisual> actors)
    {
        if (!IsNetworkWorld) return;
        foreach (var (id, entity) in _networkActors)
            if (GetNetworkActorVisual(entity, id) is { } visual &&
                IsActorVisible(visual))
                actors.Add(visual);
    }

    private ActorVisual? GetNetworkActorVisual(WorldEntity entity, ulong id)
    {
        const int storedAngles = 5;
        if (!_entityAnimations.TryGetValue(
                (entity.Gender, entity.Action), out var animation))
            return null;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedAngles);
        var rawFrame = (int)(entity.ActionTime /
                             animation.SecondsPerFrame);
        var directional = VillagerDirectionRig.Resolve(
            entity.Facing, animation.Graphic.Sprite.Frames.Count,
            storedAngles, rawFrame % framesPerAngle);
        var terrain = SamplePlayerTerrain(
            entity.Position.X, entity.Position.Y);
        var world = IsometricTerrainProjection.Project(
            entity.Position.X, entity.Position.Y, terrain.Height);
        return new ActorVisual(
            animation.Graphic.Sprite.Frames[directional.Index],
            animation.Textures[directional.Index], world,
            directional.Mirror,
            terrain.Biome is Biome.ShallowWater or Biome.RiverWater or
                Biome.MangroveShallows,
            1 + (int)(id % 7));
    }

    private void SendNetworkWalk(Vector2 target)
    {
        if (_networkClient?.IsConnected != true) return;
        _moveMarker = new MoveMarker(target, 0);
        QueueNetworkSend(() => _networkClient.SendWalkAsync(
            target.X, target.Y, _activeWorldLevel).AsTask());
    }

    private void SendNetworkStop()
    {
        if (_networkClient?.IsConnected != true) return;
        QueueNetworkSend(() => _networkClient.SendStopAsync().AsTask());
    }

    private void SendNetworkChat(string text)
    {
        if (_networkClient?.IsConnected != true)
        {
            _chatUi.AddMessage("Not connected to the server.",
                ChatMessageStyle.Warning);
            return;
        }
        ShowOverheadSpeech(text);
        QueueNetworkSend(() => _networkClient.SendChatAsync(
            text, ProtocolChatChannel.Local).AsTask());
    }

    private void SendNetworkInventorySwap(int source, int target) =>
        SendNetworkAction(new InventorySwapAction(source, target));

    private void SendNetworkItemCombination(int source, int target) =>
        SendNetworkAction(new CombineItemsAction(source, target));

    private void SendNetworkCraft(string recipeId) =>
        SendNetworkAction(new CraftRecipeAction(recipeId));

    private void SendNetworkConsume(int slot) =>
        SendNetworkAction(new ConsumeItemAction(slot));

    private void SendNetworkAction(IActionCommandPayload payload)
    {
        if (_networkClient?.IsConnected != true)
        {
            _chatUi.AddMessage(
                "Not connected to the server.", ChatMessageStyle.Warning);
            return;
        }
        QueueNetworkSend(() =>
            _networkClient.SendActionAsync(payload).AsTask());
    }

    private void QueueNetworkSend(Func<Task> send)
    {
        _ = Task.Run(async () =>
        {
            try { await send().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _networkEvents.Enqueue(() => _chatUi.AddMessage(
                    $"Network command failed: {exception.Message}",
                    ChatMessageStyle.Warning));
            }
        });
    }

    private void WarnNetworkMutationUnavailable()
    {
        if (_clock < _nextNetworkMutationWarningAt) return;
        _nextNetworkMutationWarningAt = _clock + 1.5;
        _chatUi.AddMessage(
            "That interaction is waiting for server-authoritative action support.",
            ChatMessageStyle.Warning);
    }

    private void RunLocalOnlyUiAction(Action action)
    {
        if (IsNetworkWorld)
        {
            WarnNetworkMutationUnavailable();
            return;
        }
        action();
    }

    private void UpdateNetworkWorldClock(ulong serverTick)
    {
        _networkWorldClockTick = Math.Max(_networkWorldClockTick, serverTick);
        _worldGameSeconds = WorldTime.NewGameStartGameSeconds +
            _networkWorldClockTick /
            Math.Max(1d, _networkClient?.State.ServerTickRate ?? 60) *
            WorldTime.GameMinutesPerRealSecond * 60;
    }

    private bool TryHandleNetworkChatSubmission(string text)
    {
        if (!IsNetworkWorld || text.StartsWith('/')) return false;
        SendNetworkChat(text);
        return true;
    }

    private void DisposeNetworkClient()
    {
        _networkCancellation?.Cancel();
        _networkCancellation?.Dispose();
        _networkCancellation = null;
        if (_networkClient is null) return;
        try { _networkClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
        _networkClient = null;
    }
}
