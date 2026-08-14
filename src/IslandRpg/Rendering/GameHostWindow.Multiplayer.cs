using System.Collections.Concurrent;
using System.Net;
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
    Guid WorldId,
    Guid ClientId = default,
    Guid ReconnectPlayerId = default,
    string ReconnectToken = "",
    EntityGender Gender = EntityGender.Male,
    int TeamColor = 1,
    string LocalPlayerId = "")
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

        host = NormalizeConnectHost(host);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > ushort.MaxValue)
            throw new ArgumentException("--connect must use a valid host and port.");
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 40 || playerName.Any(char.IsControl))
            throw new ArgumentException("--network-player must contain 1-40 printable characters.");
        return new(host, port, playerName, worldId);
    }

    /// <summary>
    /// 0.0.0.0 / :: are listen addresses. Connecting to them fails on Windows
    /// with "The requested address is not valid in its context."
    /// </summary>
    public static string NormalizeConnectHost(string host)
    {
        host = host.Trim();
        if (host is "*" or "")
            return "127.0.0.1";
        if (IPAddress.TryParse(host, out var address) &&
            (IPAddress.Any.Equals(address) ||
             IPAddress.IPv6Any.Equals(address)))
            return "127.0.0.1";
        return host;
    }
}

internal sealed partial class GameHostWindow
{
    private NetworkLaunchOptions? _networkLaunch;
    private readonly ConcurrentQueue<Action> _networkEvents = new();
    private readonly object _networkSendSync = new();
    private readonly Dictionary<ulong, WorldEntity> _networkActors = [];
    private NetworkGameClient? _networkClient;
    private CancellationTokenSource? _networkCancellation;
    private Task _networkSendTail = Task.CompletedTask;
    private bool _networkConnectStarted;
    private bool _networkWorldEntered;
    private double _nextNetworkMutationWarningAt;
    private ulong _networkWorldClockTick;
    private bool _networkPredictingMovement;
    private bool _networkFollowingLocally;
    private Guid _networkFollowTargetId;
    private Vector2 _networkPredictedDestination;
    private Vector2 _networkAuthoritativePosition;
    private int _networkAuthoritativeWorldLevel;
    private readonly HashSet<ulong> _networkSnapshotSeenActors = [];
    private readonly HashSet<ulong> _networkSnapshotSeenBoats = [];
    private NetworkPlayerGameplayState? _polledGameplay;
    private NetworkSocialState? _polledSocial;
    private object? _polledWorldObjects;
    private readonly NetworkPresentationApply _networkWorldIngest = new();
    private object? _polledBoats;
    private object? _polledEnemies;
    private object? _polledResources;
    private object? _polledContainers;
    private double _networkSkipPresentationUntilClock;

    private bool IsNetworkWorld => _networkLaunch is not null &&
                                   _networkWorldEntered;

    private void BeginNetworkConnection()
    {
        if (_networkLaunch is null || _networkConnectStarted) return;
        _networkLaunch = BindNetworkLaunchToSelectedPlayer(_networkLaunch);
        _networkLaunch = ApplySavedNetworkSession(_networkLaunch);
        _networkConnectStarted = true;
        _networkClient = new NetworkGameClient();
        _networkCancellation = new CancellationTokenSource();
        SubscribeNetworkCombat(_networkClient);
        _networkClient.ChatReceived += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkChat(value.Message));
        _networkClient.CommandCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkCommandResult(value.Result));
        _networkClient.ActionCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkActionResult(value.Result));
        _networkClient.CookingCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkCookingResult(
                value.Result));
        _networkClient.ResourceActionCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkResourceActionResult(
                value.Result));
        _networkClient.BoatActionCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkBoatActionResult(
                value.Result));
        _networkClient.CaveActionCompleted += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkCaveActionResult(
                value.Result));
        _networkClient.PlayerJoined += (_, value) =>
            _networkEvents.Enqueue(() => HandleNetworkPlayerJoined(value.Player));
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
            HandshakeAcceptedMessage accepted;
            try
            {
                accepted = await _networkClient!.ConnectAsync(
                    launch.Host,
                    launch.Port,
                    CreateHandshakeOptions(launch),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HandshakeRejectedException rejected)
                when (CanRetryAsFreshJoin(launch, rejected))
            {
                _saves.ClearNetworkSession(launch.LocalPlayerId);
                accepted = await _networkClient!.ConnectAsync(
                    launch.Host,
                    launch.Port,
                    CreateHandshakeOptions(launch with
                    {
                        ClientId = Guid.NewGuid(),
                        WorldId = Guid.Empty,
                        ReconnectPlayerId = Guid.Empty,
                        ReconnectToken = "",
                    }),
                    cancellationToken).ConfigureAwait(false);
            }

            // Scratch cache only. Joining a host must never write Worlds/.
            // The folder is wiped so a previous session cannot linger.
            var cacheRoot = Path.Combine(_saves.Root, "NetworkCache");
            ClearNetworkChunkCache(cacheRoot);
            var store = new WorldChunkStore(
                accepted.WorldSeed,
                cacheRoot,
                accepted.WorldId.ToString("N"));
            PersistNetworkSession(accepted);
            _networkEvents.Enqueue(() => EnterNetworkWorld(accepted, store));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _networkEvents.Enqueue(() =>
            {
                _chatUi.AddMessage(
                    $"Connection failed: {exception.Message}",
                    ChatMessageStyle.Warning);
                if (_frontendPage == FrontendPage.Multiplayer)
                {
                    _frontendError = exception.Message;
                    _multiplayerStatus = null;
                    _networkConnectStarted = false;
                }
            });
        }
    }

    private void ProcessNetworkEvents()
    {
        // Join dumps handshake, baselines, presence and the first snapshots
        // into this queue at once. Draining them all on one update frame is
        // what produces the 1000 ms hitch after "Connected".
        for (var processed = 0;
             processed < NetworkPresentationApply.MaximumEventsPerUpdate &&
             _networkEvents.TryDequeue(out var action);
             processed++)
            action();
    }

    private void EnterNetworkWorld(
        HandshakeAcceptedMessage accepted,
        WorldChunkStore store)
    {
        // Scratch terrain only. Solo Worlds/ stays untouched. Generate
        // off-thread the same way StreamWorld does.
        var seedChanged = _worldSeed != accepted.WorldSeed;
        var levelChanged = _activeWorldLevel != accepted.SpawnWorldLevel;
        CancelWorldLevelWork(clearMinimap: seedChanged || levelChanged);
        if (seedChanged || levelChanged)
            RetireLoadedChunks();
        ClearNetworkResourceProjection();
        ClearNetworkWorldObjects();
        _networkWorldIngest.Reset();
        _polledGameplay = null;
        _polledSocial = null;
        _polledWorldObjects = null;
        _polledBoats = null;
        _polledEnemies = null;
        _polledResources = null;
        _polledContainers = null;

        _worldSeed = accepted.WorldSeed;
        _activeWorldLevel = accepted.SpawnWorldLevel;
        _worldStore = store;
        _activeWorld = new WorldProfile(
            accepted.WorldId.ToString("N"), "Multiplayer World",
            accepted.WorldSeed, DateTime.UtcNow, DateTime.UtcNow,
            IslandStart: accepted.IslandStart);
        var gender = _selectedPlayer?.Gender ??
                     _networkLaunch!.Gender;
        var teamColor = _selectedPlayer?.TeamColor ??
                        _networkLaunch!.TeamColor;
        _activePlayer = new PlayerProfile(
            accepted.PlayerId.ToString("N"), _networkLaunch!.PlayerName,
            gender, 2, teamColor, DateTime.UtcNow, DateTime.UtcNow,
            Inventory: PlayerInventory.CreateStartingInventory());
        _player = new WorldEntity(
            new Vector2(accepted.SpawnX, accepted.SpawnY),
            gender);
        _networkAuthoritativePosition = _player.Position;
        _networkAuthoritativeWorldLevel = accepted.SpawnWorldLevel;
        _networkPredictingMovement = false;
        _networkFollowingLocally = false;
        _networkFollowTargetId = default;
        _gameSimulationAccumulator = 0;
        _networkSkipPresentationUntilClock = _clock + .05;
        _networkActors.Clear();
        InitializeNetworkBoats();
        _networkWorldEntered = true;
        _networkWorldClockTick = accepted.Tick;
        UpdateNetworkWorldClock(accepted.Tick);
        _playerDefeated = false;
        _modalScreen.Close(ModalScreenKind.Death);
        _villagers.Clear();
        InitializeNetworkCombatProjection();
        _queuedAction = null;
        _moveMarker = null;
        _camera = Vector2.Zero;
        SetZoomImmediate(.8f);
        FollowPlayer();
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
        if (state.Status is NetworkGameClientStatus.Disconnected or
            NetworkGameClientStatus.Disconnecting or
            NetworkGameClientStatus.Faulted)
        {
            CancelNetworkCaveInteraction();
            ClearNetworkBoatPresentation();
            ClearNetworkCombatProjection();
            ClearNetworkResourceProjection();
            ClearNetworkWorldObjects();
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
        if (_networkPredictingMovement)
        {
            _networkPredictingMovement = false;
            if (_player is not null)
                _player.SyncPosition(_networkAuthoritativePosition);
        }
        _chatUi.AddMessage(
            string.IsNullOrWhiteSpace(result.Detail)
                ? $"Server rejected the command ({result.RejectionCode})."
                : result.Detail,
            ChatMessageStyle.Warning);
    }

    private void HandleNetworkActionResult(ActionResultMessage result)
    {
        HandleNetworkWorldActionResult(result);
        if (!result.Accepted)
            _chatUi.AddMessage(
                string.IsNullOrWhiteSpace(result.Detail)
                    ? $"Server rejected the action ({result.RejectionCode})."
                    : result.Detail,
                ChatMessageStyle.Warning);
    }

    private void HandleNetworkCookingResult(CookingResultMessage result)
    {
        if (_networkCookingCommandId is { } commandId &&
            commandId != result.CommandId) return;
        var rawName = ItemCatalog.Get(result.RawItemId).Name;
        var outputName = ItemCatalog.Get(result.ResultItemId).Name;
        _chatUi.AddMessage(
            result.Interrupted
                ? $"The fire goes out; the {rawName} is returned."
                : result.Burnt
                    ? $"The {rawName} burns."
                    : $"You successfully cook the {rawName} into {outputName}.",
            result.Interrupted || result.Burnt
                ? ChatMessageStyle.Warning
                : ChatMessageStyle.Action);
        ClearNetworkCookingPresentation();
    }

    private void ApplyNetworkPlayerState(NetworkPlayerGameplayState state)
    {
        if (_activePlayer is null) return;
        var hadAuthoritativeQuestState = _activePlayer.Quests is { Count: > 0 };
        var previousQuests = QuestService.Normalize(_activePlayer.Quests);
        var authoritativeQuests = state.Quests is { Count: > 0 }
            ? QuestService.Normalize(state.Quests.Select(quest => new QuestProgress(
                quest.QuestId,
                (QuestStatus)quest.Status,
                quest.Objectives.ToDictionary(
                    value => value.ObjectiveId,
                    value => value.Count,
                    StringComparer.Ordinal),
                quest.CompletionTick)).ToArray())
            : previousQuests;
        ObserveNetworkResourceGameplayState(
            state,
            _activePlayer.WoodcuttingExperience,
            _activePlayer.FarmingExperience,
            _activePlayer.MiningExperience,
            _activePlayer.AdventureExperience);
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
            WoodcuttingExperience = state.WoodcuttingExperience,
            FarmingExperience = state.FarmingExperience,
            MiningExperience = state.MiningExperience,
            AdventureExperience = state.AdventureExperience,
            DiggingExperience = state.DiggingExperience,
            FishingExperience = state.FishingExperience,
            AttackExperience = state.AttackExperience,
            StrengthExperience = state.StrengthExperience,
            DefenceExperience = state.DefenceExperience,
            CombatStance = FromNetworkCombatStance(state.CombatStance),
            Quests = authoritativeQuests,
            UpdatedUtc = DateTime.UtcNow
        };
        if (hadAuthoritativeQuestState)
            PresentNetworkQuestCompletion(previousQuests, authoritativeQuests);
        ApplyNetworkCombatPlayerState(state);
        if (_activeInventorySlot >= 0 &&
            items[_activeInventorySlot] is null)
            _activeInventorySlot = -1;
    }

    private void UpdateNetworkGame(float elapsed)
    {
        if (_networkClient is null || _player is null) return;

        // Same order as single-player: input and the local 60 Hz step first.
        // The server layer is applied after so a click is never queued behind
        // ingest, snapshots, or chunk work.
        _worldActions.ProcessPendingPath();
        UpdateNetworkFollowWalk();
        var rightDown = MouseState.IsButtonDown(
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right);
        var leftDown = MouseState.IsButtonDown(
            OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left);
        if (!UpdateNetworkBoatInput(leftDown, rightDown))
            UpdateNetworkWorldInteractionInput(leftDown, rightDown);
        if (!_chatUi.Input.Focused && KeyboardState.IsKeyPressed(
                OpenTK.Windowing.GraphicsLibraryFramework.Keys.X))
        {
            _pendingNetworkWorldAction = null;
            StopNetworkRepeatedConstruction();
            CancelNetworkCaveInteraction();
            if (_fishingBoatBoarded)
            {
                CancelNetworkCombatTarget();
                SendNetworkBoatStop();
            }
            else
                SendNetworkStop();
        }

        const float simulationStep =
            1f / (float)DisplaySettingsController.SimulationUpdatesPerSecond;
        // Same 60 Hz clock as single-player. At most two steps so a hitch
        // cannot buy a 15-step spiral; leftover time is kept so 30 FPS still
        // walks at the solo speed.
        _gameSimulationAccumulator = Math.Min(
            simulationStep * 2,
            _gameSimulationAccumulator + Math.Min(elapsed, simulationStep * 2));
        var steps = 0;
        while (_gameSimulationAccumulator + .0000001 >= simulationStep &&
               steps < 2)
        {
            AdvanceNetworkPredictedMovement(simulationStep);
            AdvanceNetworkMoveMarker(simulationStep);
            _worldActions.CompleteQueuedAction();
            UpdateGroundObjectPickup();
            UpdateGroundObjectDrop();
            _gameSimulationAccumulator -= simulationStep;
            steps++;
        }

        if (_networkSkipPresentationUntilClock > 0 &&
            _clock < _networkSkipPresentationUntilClock)
        {
            UpdateNativeCursor();
            FollowPlayer();
            return;
        }

        _networkSkipPresentationUntilClock = 0;
        PollNetworkPresentation();
        UpdateNetworkWorldClock(_networkClient.State.ServerTick);
        ApplyNetworkSnapshot(elapsed);
        UpdatePendingNetworkWorldAction();
        UpdateNetworkCaveInteraction();
        UpdateNetworkResourceInteraction();
        UpdateNetworkBoatFishingPresentation(elapsed);
        UpdateNetworkCombatPresentation(elapsed);
        UpdateNativeCursor();
        FollowPlayer();
    }

    private void ApplyNetworkSnapshot(float elapsed)
    {
        if (_networkClient is null ||
            !_networkClient.SnapshotBuffer.TrySample(out var sampled) ||
            sampled is null)
            return;
        var seen = _networkSnapshotSeenActors;
        var seenBoats = _networkSnapshotSeenBoats;
        seen.Clear();
        seenBoats.Clear();
        foreach (var snapshot in sampled.Entities)
        {
            if (snapshot.EntityKind == NetworkEntityKind.Enemy)
            {
                ApplyNetworkEnemySnapshot(snapshot, elapsed);
                continue;
            }
            if (snapshot.EntityKind == NetworkEntityKind.Boat)
            {
                seenBoats.Add(snapshot.EntityId);
                ApplyNetworkBoatSnapshot(snapshot, elapsed);
                continue;
            }
            if (snapshot.EntityKind != NetworkEntityKind.Player ||
                snapshot.State.HasFlag(NetworkEntityState.Hidden) ||
                IsNetworkActorAboard(snapshot.EntityId))
                continue;
            seen.Add(snapshot.EntityId);
            var position = new Vector2(snapshot.X, snapshot.Y);
            var velocity = new Vector2(
                snapshot.VelocityX, snapshot.VelocityY);
            var isLocal =
                snapshot.EntityId == _networkClient.State.PlayerEntityId;
            if (isLocal)
            {
                ApplyLocalNetworkSnapshot(
                    position, velocity, snapshot.State, (int)snapshot.WorldLevel);
                continue;
            }
            var entity = GetOrCreateNetworkActor(snapshot.EntityId, position);
            var snapshotWorldLevel = (int)snapshot.WorldLevel;
            var preservePresentedAction =
                snapshotWorldLevel == _activeWorldLevel &&
                ((_networkCookingPresentationOwned &&
                  entity.Action == EntityAction.Gather) ||
                 (_networkResourcePresentationOwned &&
                  entity.Action is EntityAction.Gather or EntityAction.Work or
                      EntityAction.Mine) ||
                 (_networkCavePresentationOwned &&
                  entity.Action == EntityAction.Dig) ||
                 (_networkActiveFishing is not null &&
                  entity.Action == EntityAction.Fish));
            SyncNetworkEntity(entity, position, velocity, snapshot.State,
                elapsed, preservePresentedAction);
        }
        foreach (var id in _networkActors.Keys
                     .Where(id => !seen.Contains(id)).ToArray())
            _networkActors.Remove(id);
        PruneNetworkBoatTransforms(seenBoats);
    }

    private WorldEntity GetOrCreateNetworkActor(ulong id, Vector2 position)
    {
        if (_networkActors.TryGetValue(id, out var entity))
        {
            ApplyNetworkActorAppearance(entity, id);
            return entity;
        }
        entity = new WorldEntity(position, GenderForNetworkEntity(id));
        _networkActors.Add(id, entity);
        return entity;
    }

    private void HandleNetworkPlayerJoined(NetworkPlayerPresence player)
    {
        if (_networkClient is not null &&
            player.PlayerId != _networkClient.State.PlayerId)
            _chatUi.AddMessage(
                $"{player.PlayerName} joined the world.",
                ChatMessageStyle.Action);
        if (player.EntityId != 0 &&
            _networkActors.TryGetValue(player.EntityId, out var entity))
            ApplyNetworkActorAppearance(entity, player.EntityId);
    }

    private void ApplyNetworkActorAppearance(WorldEntity entity, ulong id) =>
        entity.SetGender(GenderForNetworkEntity(id));

    private EntityGender GenderForNetworkEntity(ulong entityId)
    {
        if (TryFindNetworkPresence(entityId, out var player) &&
            player.Gender == 1)
            return EntityGender.Female;
        return EntityGender.Male;
    }

    private int TeamColorForNetworkEntity(ulong entityId) =>
        TryFindNetworkPresence(entityId, out var player)
            ? Math.Clamp((int)player.TeamColor, 0, 7)
            : 0;

    private bool TryFindNetworkPresence(
        ulong entityId,
        out NetworkPlayerPresence player)
    {
        player = null!;
        if (_networkClient is null) return false;
        foreach (var candidate in _networkClient.State.Players.Values)
        {
            if (candidate.EntityId != entityId) continue;
            player = candidate;
            return true;
        }
        return false;
    }

    private static void SyncNetworkEntity(
        WorldEntity entity,
        Vector2 position,
        Vector2 velocity,
        NetworkEntityState state,
        float elapsed,
        bool preserveIdleAction)
    {
        if (state.HasFlag(NetworkEntityState.Dead))
        {
            entity.Die();
            entity.CorrectPosition(position);
            entity.AdvanceAction(elapsed);
            return;
        }

        var moving = velocity.LengthSquared > .0001f ||
                     state.HasFlag(NetworkEntityState.Moving);
        if (preserveIdleAction && !moving)
        {
            entity.CorrectPosition(position);
            entity.AdvanceAction(elapsed);
            return;
        }

        entity.PresentRemoteWalk(position, velocity, moving, elapsed);
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
            TeamColorForNetworkEntity(id));
    }

    private void SendNetworkWalk(
        Vector2 target,
        bool preserveResourceAction = false,
        bool preserveFishingAction = false,
        bool preserveBoatBoarding = false,
        bool preserveCombatAction = false)
    {
        if (_networkClient?.IsConnected != true) return;
        _networkFollowingLocally = false;
        _networkFollowTargetId = default;
        if (!preserveCombatAction)
            CancelNetworkCombatTarget();
        ReleaseNetworkCookingPresentation();
        if (!preserveResourceAction)
            CancelNetworkResourceInteraction();
        if (!preserveFishingAction)
            CancelNetworkFishingPresentation();
        if (!preserveBoatBoarding)
            _networkPendingBoardBoatId = null;
        _moveMarker = new MoveMarker(target, 0);
        BeginNetworkMovementPrediction(target);
        SendNetworkWalkCommand(target);
    }

    private void SendNetworkWalkCommand(Vector2 target)
    {
        if (_networkClient?.IsConnected != true) return;
        QueueNetworkSend(cancellationToken => _networkClient.SendWalkAsync(
            target.X, target.Y, _activeWorldLevel, cancellationToken).AsTask());
    }

    private void SendNetworkStop(
        bool preserveResourceAction = false,
        bool preserveFishingAction = false,
        bool preserveCombatAction = false)
    {
        if (_networkClient?.IsConnected != true) return;
        _networkFollowingLocally = false;
        _networkFollowTargetId = default;
        if (!preserveCombatAction)
            CancelNetworkCombatTarget();
        ReleaseNetworkCookingPresentation();
        if (!preserveResourceAction)
            CancelNetworkResourceInteraction();
        if (!preserveFishingAction)
            CancelNetworkFishingPresentation();
        _networkPendingBoardBoatId = null;
        _networkPredictingMovement = false;
        if (!preserveResourceAction &&
            !preserveFishingAction &&
            !preserveCombatAction)
            _player?.Stop();
        QueueNetworkSend(cancellationToken =>
            _networkClient.SendStopAsync(cancellationToken).AsTask());
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
        QueueNetworkSend(cancellationToken => _networkClient.SendChatAsync(
            text, ProtocolChatChannel.Local,
            cancellationToken: cancellationToken).AsTask());
    }

    private void SendNetworkInventorySwap(int source, int target) =>
        SendNetworkAction(new InventorySwapAction(source, target));

    private void SendNetworkItemCombination(int source, int target) =>
        SendNetworkAction(new CombineItemsAction(source, target));

    private void SendNetworkCraft(string recipeId) =>
        SendNetworkAction(new CraftRecipeAction(recipeId));

    private void SendNetworkConsume(int slot) =>
        SendNetworkAction(new ConsumeItemAction(slot));

    private void SendNetworkAction(
        IActionCommandPayload payload,
        Guid commandId = default)
    {
        if (_networkClient?.IsConnected != true)
        {
            _chatUi.AddMessage(
                "Not connected to the server.", ChatMessageStyle.Warning);
            return;
        }
        QueueNetworkSend(cancellationToken =>
            _networkClient.SendActionAsync(
                payload, commandId, cancellationToken).AsTask());
    }

    private void QueueNetworkSend(Func<CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var cancellationToken = _networkCancellation?.Token ??
            new CancellationToken(canceled: true);
        lock (_networkSendSync)
        {
            _networkSendTail = SendNetworkInOrderAsync(
                _networkSendTail, send, cancellationToken);
        }
    }

    private async Task SendNetworkInOrderAsync(
        Task previous,
        Func<CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        try
        {
            // A single tail preserves the order authored by the update thread,
            // including while the transport channel is applying backpressure.
            await previous.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await send(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _networkEvents.Enqueue(() => _chatUi.AddMessage(
                $"Network command failed: {exception.Message}",
                ChatMessageStyle.Warning));
        }
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

    private static bool CanRetryAsFreshJoin(
        NetworkLaunchOptions launch,
        HandshakeRejectedException rejected) =>
        (launch.WorldId != Guid.Empty ||
         launch.ReconnectPlayerId != Guid.Empty) &&
        rejected.Rejection.Code is
            HandshakeRejectionCode.ContentMismatch or
            HandshakeRejectionCode.ReconnectExpired or
            HandshakeRejectionCode.InvalidName or
            HandshakeRejectionCode.DuplicateClient;

    private NetworkLaunchOptions BindNetworkLaunchToSelectedPlayer(
        NetworkLaunchOptions launch)
    {
        if (_selectedPlayer is null) return launch;
        return launch with
        {
            LocalPlayerId = _selectedPlayer.Id,
            PlayerName = _selectedPlayer.Name,
            Gender = _selectedPlayer.Gender,
            TeamColor = _selectedPlayer.TeamColor
        };
    }

    private NetworkLaunchOptions ApplySavedNetworkSession(
        NetworkLaunchOptions launch)
    {
        if (launch.ReconnectPlayerId != Guid.Empty)
            return launch;
        var localPlayerId = !string.IsNullOrWhiteSpace(launch.LocalPlayerId)
            ? launch.LocalPlayerId
            : _selectedPlayer?.Id;
        var session = _saves.LoadNetworkSession(localPlayerId);
        if (!NetworkSessionReuse.CanReconnect(
                session,
                localPlayerId,
                launch.Host,
                launch.Port,
                launch.WorldId))
            return launch;
        return launch with
        {
            LocalPlayerId = localPlayerId ?? "",
            ReconnectPlayerId = session!.PlayerId,
            ReconnectToken = session.ReconnectToken
        };
    }

    private static ClientHandshakeOptions CreateHandshakeOptions(
        NetworkLaunchOptions launch) =>
        new(
            "0.3.0",
            "base",
            launch.ClientId == Guid.Empty ? Guid.NewGuid() : launch.ClientId,
            launch.PlayerName,
            launch.WorldId,
            launch.ReconnectPlayerId,
            launch.ReconnectToken ?? "",
            Gender: (byte)(launch.Gender == EntityGender.Female ? 1 : 0),
            TeamColor: (byte)Math.Clamp(launch.TeamColor, 0, 7));

    private void PersistNetworkSession(HandshakeAcceptedMessage accepted)
    {
        if (_networkLaunch is null) return;
        var launch = _networkLaunch;
        var gender = _activePlayer?.Gender ?? launch.Gender;
        var teamColor = _activePlayer?.TeamColor ?? launch.TeamColor;
        var host = NetworkLaunchOptions.NormalizeConnectHost(launch.Host);
        var endpoint = $"{host}:{launch.Port}";
        _ = Task.Run(() =>
        {
            _saves.SaveNetworkSession(new NetworkSessionRecord(
                host,
                launch.Port,
                accepted.WorldId,
                accepted.PlayerId,
                accepted.ReconnectToken,
                launch.PlayerName,
                gender,
                teamColor,
                LocalPlayerId: launch.LocalPlayerId));
            var settings = _saves.LoadSettings();
            if (!string.Equals(
                    settings.LastMultiplayerEndpoint,
                    endpoint,
                    StringComparison.OrdinalIgnoreCase))
                _saves.SaveSettings(settings with
                {
                    LastMultiplayerEndpoint = endpoint
                });
        });
    }

    private void BeginNetworkMovementPrediction(Vector2 target)
    {
        if (_player is null || _fishingBoatBoarded) return;
        _networkPredictedDestination = target;
        _networkPredictingMovement = true;
        _player.PrepareForPathRequest();
        _worldActions.QueuePredictedWalk(target);
    }

    private void AdvanceNetworkPredictedMovement(float elapsed)
    {
        if (_player is null || _fishingBoatBoarded) return;
        if (_player.Action == EntityAction.Move || _pendingPathTask is not null)
            _networkPredictingMovement = true;
        else if (_networkPredictingMovement &&
                 _player.Action != EntityAction.Move)
            _networkPredictingMovement = false;
        var current = SamplePlayerTerrain(
            _player.Position.X, _player.Position.Y);
        var next = SamplePlayerTerrain(_player.Target.X, _player.Target.Y);
        _player.TerrainSpeedMultiplier =
            ActorMovementService.TerrainSpeedMultiplier(
                current.Biome is Biome.ShallowWater or
                    Biome.RiverWater or Biome.MangroveShallows,
                current.Height,
                next.Height);
        _player.StatusSpeedMultiplier =
            _networkPlayerCombatStatus.HasFlag(CombatStatusFlags.Rooted)
                ? 0f
                : _networkPlayerCombatStatus.HasFlag(CombatStatusFlags.Slowed)
                    ? .58f
                    : 1f;
        _player.Update(elapsed);
    }

    private void AdvanceNetworkMoveMarker(float elapsed)
    {
        if (_moveMarker is null) return;
        var nextTime = _moveMarker.Time + elapsed;
        var duration = _moveMarkerAnimation is null
            ? 0
            : _moveMarkerAnimation.Textures.Length *
              _moveMarkerAnimation.SecondsPerFrame;
        _moveMarker = nextTime < duration
            ? _moveMarker with { Time = nextTime }
            : null;
    }

    private void ApplyLocalNetworkSnapshot(
        Vector2 position,
        Vector2 velocity,
        NetworkEntityState state,
        int snapshotWorldLevel)
    {
        if (_networkClient?.State.Entities.TryGetValue(
                _networkClient.State.PlayerEntityId, out var latest) == true)
        {
            snapshotWorldLevel = (int)latest.WorldLevel;
            state = latest.State;
        }

        _ = velocity;
        _networkAuthoritativePosition = position;
        _networkAuthoritativeWorldLevel = snapshotWorldLevel;
        if (_player is null) return;

        if (state.HasFlag(NetworkEntityState.Dead))
        {
            _networkPredictingMovement = false;
            _networkFollowingLocally = false;
            _player.Die();
            _player.SyncPosition(position);
            if (snapshotWorldLevel != _activeWorldLevel)
                ApplyNetworkWorldLevelTransition(snapshotWorldLevel, position);
            return;
        }

        if (snapshotWorldLevel != _activeWorldLevel)
        {
            _networkPredictingMovement = false;
            ApplyNetworkWorldLevelTransition(snapshotWorldLevel, position);
            return;
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return;

        // Follow and click-to-walk both use QueuePredictedWalk + Update.
        // Do not snap the local villager to a snapshot pose; that jumps to
        // the end of the server route and skips the walk cycle.
        var error = Vector2.Distance(_player.Position, position);
        if (float.IsFinite(error) && error > 8f)
            _player.CorrectPosition(position);
    }

    private const float NetworkFollowStandDistance = 1.6f;
    private const float NetworkFollowRetargetDistance = .6f;

    private void UpdateNetworkFollowWalk()
    {
        if (!_networkFollowingLocally ||
            _player is null ||
            _fishingBoatBoarded ||
            _networkClient?.IsConnected != true)
            return;
        var targetId = _networkClient.State.Social.FollowTargetPlayerId;
        if (targetId == Guid.Empty)
            targetId = _networkFollowTargetId;
        if (targetId == Guid.Empty) return;
        if (!TryGetNetworkPlayerWorldPosition(targetId, out var lead))
            return;
        if (Vector2.DistanceSquared(_player.Position, lead) <=
            NetworkFollowStandDistance * NetworkFollowStandDistance)
            return;
        var stand = NetworkFollowStandNear(_player.Position, lead);
        if (_pendingPathTask is not null) return;
        if (_player.Action == EntityAction.Move &&
            Vector2.DistanceSquared(_networkPredictedDestination, stand) <
            NetworkFollowRetargetDistance * NetworkFollowRetargetDistance)
            return;
        _networkPredictedDestination = stand;
        _worldActions.QueuePredictedWalk(stand);
    }

    private bool TryGetNetworkPlayerWorldPosition(
        Guid playerId, out Vector2 position)
    {
        position = default;
        if (_networkClient is null) return false;
        if (!_networkClient.State.Players.TryGetValue(playerId, out var player))
            return false;
        if (_networkActors.TryGetValue(player.EntityId, out var entity))
        {
            position = entity.Position;
            return true;
        }

        if (_networkClient.State.Entities.TryGetValue(
                player.EntityId, out var snapshot))
        {
            position = new(snapshot.X, snapshot.Y);
            return true;
        }

        return false;
    }

    private static Vector2 NetworkFollowStandNear(
        Vector2 follower, Vector2 leader)
    {
        var away = follower - leader;
        if (away.LengthSquared <= .0001f)
            away = Vector2.UnitX;
        else
            away = away.Normalized();
        return leader + away * NetworkFollowStandDistance;
    }

    private void PollNetworkPresentation()
    {
        if (_networkClient is null || _player is null) return;
        var state = _networkClient.State;
        HandleNetworkState(state);
        if (!ReferenceEquals(_polledGameplay, state.Gameplay) &&
            state.Gameplay is { } gameplay)
        {
            _polledGameplay = gameplay;
            ApplyNetworkPlayerState(gameplay);
        }
        if (!ReferenceEquals(_polledSocial, state.Social))
        {
            var startedFollow =
                (_polledSocial?.FollowTargetPlayerId ?? Guid.Empty) ==
                Guid.Empty &&
                state.Social.FollowTargetPlayerId != Guid.Empty;
            var stoppedFollow =
                (_polledSocial?.FollowTargetPlayerId ?? Guid.Empty) !=
                Guid.Empty &&
                state.Social.FollowTargetPlayerId == Guid.Empty;
            _polledSocial = state.Social;
            if (startedFollow)
            {
                _networkFollowingLocally = true;
                _networkFollowTargetId = state.Social.FollowTargetPlayerId;
                _networkPredictingMovement = false;
            }
            else if (stoppedFollow)
            {
                _networkFollowingLocally = false;
                _networkFollowTargetId = default;
            }
        }
        if (!ReferenceEquals(_polledWorldObjects, state.WorldObjects) ||
            _networkWorldIngest.HasPendingWorldObjects)
        {
            var slice = _networkWorldIngest.ApplyWorldObjects(
                state.WorldObjects, state.WorldChunkRevisions);
            var changes = NetworkPresentationApply.ToChanges(slice);
            if (changes.Count > 0)
                ApplyNetworkWorldObjectChanges(changes);
            if (slice.Complete)
                _polledWorldObjects = state.WorldObjects;
        }
        if (_polledBoats is null)
        {
            _polledBoats = state.Boats;
            SynchronizeNetworkBoats(state.Boats.Values);
        }
        else
            _polledBoats = state.Boats;
        if (_polledEnemies is null)
        {
            _polledEnemies = state.Enemies;
            SynchronizeNetworkEnemies(state.Enemies.Values);
        }
        else
            _polledEnemies = state.Enemies;
        if (!ReferenceEquals(_polledResources, state.ResourceChunks))
        {
            _polledResources = state.ResourceChunks;
            ObservePolledNetworkResources(state);
        }
        if (!ReferenceEquals(_polledContainers, state.Containers) &&
            _networkRequestedContainerId is { } containerId &&
            state.Containers.TryGetValue(containerId, out var container))
        {
            _polledContainers = state.Containers;
            ApplyNetworkContainerState(container);
        }
    }

    private void ObservePolledNetworkResources(NetworkGameClientState state)
    {
        if (_activeNetworkTreeAction is { } tree &&
            TryGetNetworkTreeState(tree.Target, out var treeState) &&
            (treeState.Depleted || treeState.Health <= 0))
        {
            _chatUi.AddMessage(
                $"The {TreeDisplayName(tree.Target.Visual.GraphicName)} falls.",
                ChatMessageStyle.Action);
            CancelNetworkResourceInteraction();
        }
        if (_activeNetworkVegetationAction is { } vegetation &&
            !NetworkVegetationIsReady(vegetation.Target))
            CancelNetworkResourceInteraction();
        if (_activeNetworkMiningAction is { } mining &&
            NetworkMiningIsDepleted(mining.Target))
        {
            _chatUi.AddMessage(
                $"The {mining.Target.Visual.DisplayName} is depleted.",
                ChatMessageStyle.Action);
            CancelNetworkResourceInteraction();
        }
    }

    private void RetireLoadedChunks()
    {
        foreach (var gpu in _worldChunks.Values)
            _networkGpuTeardown.Add(gpu);
        _worldChunks.Clear();
    }

    private void ReleaseRetiredNetworkChunks()
    {
        const int maximumReleasesPerFrame = 3;
        var released = 0;
        while (released < maximumReleasesPerFrame &&
               _networkGpuTeardown.Count > 0)
        {
            var last = _networkGpuTeardown.Count - 1;
            var gpu = _networkGpuTeardown[last];
            _networkGpuTeardown.RemoveAt(last);
            DeleteGpuWorldChunk(gpu);
            released++;
        }
    }

    internal void ClearNetworkMovementPrediction() =>
        _networkPredictingMovement = false;

    // Clicks and follow-up actions use the villager on screen. Using the
    // echoed server pose as the origin sends another Walk from behind the
    // player; the next snapshot is longer; that is the grow-over-time loop.
    private Vector2 NetworkActionPosition =>
        _player?.Position ?? _networkAuthoritativePosition;

    private void DisposeNetworkClient()
    {
        CancelNetworkCaveInteraction();
        ClearNetworkResourceProjection();
        ResetNetworkContainerInteraction();
        ClearNetworkWorldObjects();
        _networkCancellation?.Cancel();
        Task sendTail;
        lock (_networkSendSync) sendTail = _networkSendTail;
        try { sendTail.GetAwaiter().GetResult(); }
        catch { }
        lock (_networkSendSync) _networkSendTail = Task.CompletedTask;
        _networkCancellation?.Dispose();
        _networkCancellation = null;
        if (_networkClient is null) return;
        UnsubscribeNetworkCombat(_networkClient);
        try { _networkClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { }
        _networkClient = null;
        ClearNetworkChunkCache(Path.Combine(_saves.Root, "NetworkCache"));
    }

    private static void ClearNetworkChunkCache(string cacheRoot)
    {
        try
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LeaveNetworkWorldProjection()
    {
        CancelNetworkCaveInteraction();
        ClearNetworkResourceProjection();
        ClearNetworkCombatProjection();
        ResetNetworkContainerInteraction();
        ClearNetworkWorldObjects();
        _networkWorldEntered = false;
        _networkPredictingMovement = false;
        _networkConnectStarted = false;
        DisposeNetworkClient();
        _networkLaunch = null;
        _ = StopHostedServerAsync();
    }

    private void RenderNetworkNameplates(Vector4 scene)
    {
        if (!IsNetworkWorld ||
            _networkClient is null ||
            _chatFont is null)
            return;
        var scale = scene.Z / ReferenceWidth;
        foreach (var (id, entity) in _networkActors)
        {
            var name = NameForNetworkEntity(id);
            if (string.IsNullOrWhiteSpace(name) ||
                GetNetworkActorVisual(entity, id) is not { } visual)
                continue;
            var sprite = SpriteBounds(
                visual.Frame, visual.World, visual.Mirror);
            var centerX = scene.X +
                          (sprite.Left + sprite.Right) * .5f * scale;
            var size = _chatFont.MeasureString(name);
            var width = size.X + 12;
            var height = size.Y + 6;
            var x = Math.Clamp(
                centerX - width * .5f,
                scene.X + 4,
                scene.X + scene.Z - width - 4);
            var y = Math.Max(
                scene.Y + 4,
                scene.Y + sprite.Top * scale - height - 6);
            var bounds = new Vector4(
                MathF.Round(x), MathF.Round(y),
                MathF.Ceiling(width), MathF.Ceiling(height));
            DrawUiColor(bounds, new(.02f, .02f, .018f, .72f));
            DrawCenteredUiText(
                name, bounds, new(232, 219, 177, 255));
        }
    }

    private bool TryGetNetworkPlayerUnderMouse(
        Vector2 mouse, out Guid playerId)
    {
        playerId = default;
        if (_networkClient is null) return false;
        var localEntity = _networkClient.State.PlayerEntityId;
        var selectedDepth = float.NegativeInfinity;
        foreach (var (entityId, entity) in _networkActors)
        {
            if (entityId == localEntity) continue;
            if (GetNetworkActorVisual(entity, entityId) is not { } visual)
                continue;
            var bounds = SpriteBounds(
                visual.Frame, visual.World, visual.Mirror);
            if (mouse.X < bounds.Left || mouse.X >= bounds.Right ||
                mouse.Y < bounds.Top || mouse.Y >= bounds.Bottom)
                continue;
            if (!WorldHoverSelection.Prefer(visual.World.Y, ref selectedDepth))
                continue;
            foreach (var player in _networkClient.State.Players.Values)
            {
                if (player.EntityId != entityId ||
                    player.PlayerId == _networkClient.State.PlayerId)
                    continue;
                playerId = player.PlayerId;
                break;
            }
        }

        return playerId != default;
    }

    private void OpenNetworkPlayerContext(Guid playerId, Vector2 walkTarget)
    {
        _networkPlayerContextId = playerId;
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _miningContext.Close();
        _enemyContext.Close();
        _ = walkTarget;
        var options = NetworkPlayerContextOptions(playerId);
        _networkPlayerContext.Open(
            MouseState.Position,
            options,
            SceneClientBounds(),
            168);
    }

    private string[] NetworkPlayerContextOptions(Guid playerId)
    {
        var social = _networkClient?.State.Social ?? NetworkSocialState.Empty;
        var followLabel = social.FollowTargetPlayerId == playerId
            ? "Stop Follow"
            : "Follow";
        if (social.OpenTradeId != Guid.Empty &&
            social.TradePartnerPlayerId == playerId)
        {
            return social.TradeIncoming
                ? ["Accept Trade", "Decline Trade", followLabel]
                : ["Confirm Trade", "Cancel Trade", followLabel];
        }

        return ["Trade", followLabel];
    }

    private void HandleNetworkPlayerContextSelection(int option)
    {
        var playerId = _networkPlayerContextId;
        _networkPlayerContextId = default;
        if (playerId == default || _networkClient?.IsConnected != true)
            return;
        var options = NetworkPlayerContextOptions(playerId);
        if ((uint)option >= (uint)options.Length) return;
        var social = _networkClient.State.Social;
        switch (options[option])
        {
            case "Trade":
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.OfferTrade, playerId));
                break;
            case "Accept Trade":
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.RespondTrade,
                    playerId,
                    social.OpenTradeId,
                    Accept: true));
                break;
            case "Decline Trade":
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.RespondTrade,
                    playerId,
                    social.OpenTradeId,
                    Accept: false));
                break;
            case "Confirm Trade":
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.ConfirmTrade,
                    playerId,
                    social.OpenTradeId));
                break;
            case "Cancel Trade":
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.CancelTrade,
                    playerId,
                    social.OpenTradeId));
                break;
            case "Follow":
                _networkFollowingLocally = true;
                _networkFollowTargetId = playerId;
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.Follow, playerId));
                UpdateNetworkFollowWalk();
                break;
            case "Stop Follow":
                _networkFollowingLocally = false;
                _networkFollowTargetId = default;
                SendNetworkSocial(new SocialAction(
                    SocialActionKind.StopFollow, playerId));
                break;
        }
    }

    private bool TryHandleNetworkSocialCommand(string text)
    {
        if (!ChatCommandRegistry.TryParse(text, out var command))
            return false;
        if (command.Definition.Name is not (
            "/friend" or "/unfriend" or "/ignore" or "/unignore" or
            "/friends" or "/guild" or "/trade"))
            return false;
        if (!IsNetworkWorld || _networkClient?.IsConnected != true)
        {
            _chatUi.AddMessage(
                "That command is for multiplayer.", ChatMessageStyle.Warning);
            return true;
        }
        switch (command.Definition.Name)
        {
            case "/friend":
                return SendNamedSocial(
                    command, SocialActionKind.AddFriend, "Usage: /friend <name>");
            case "/unfriend":
                return SendNamedSocial(
                    command, SocialActionKind.RemoveFriend, "Usage: /unfriend <name>");
            case "/ignore":
                return SendNamedSocial(
                    command, SocialActionKind.Ignore, "Usage: /ignore <name>");
            case "/unignore":
                return SendNamedSocial(
                    command, SocialActionKind.Unignore, "Usage: /unignore <name>");
            case "/friends":
                ShowNetworkSocialLists();
                return true;
            case "/guild":
                return HandleNetworkGuildCommand(command.Arguments);
            case "/trade":
                return HandleNetworkTradeCommand(command.Arguments);
            default:
                return false;
        }
    }

    private bool SendNamedSocial(
        ParsedChatCommand command,
        SocialActionKind kind,
        string usage)
    {
        if (command.Arguments.Length == 0)
        {
            _chatUi.AddMessage(usage, ChatMessageStyle.Warning);
            return true;
        }

        if (!TryResolveNetworkPlayerName(
                string.Join(' ', command.Arguments), out var playerId))
        {
            _chatUi.AddMessage(
                "That player is not connected.", ChatMessageStyle.Warning);
            return true;
        }

        SendNetworkSocial(new SocialAction(kind, playerId));
        return true;
    }

    private bool HandleNetworkGuildCommand(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            ShowNetworkSocialLists();
            return true;
        }

        var verb = arguments[0];
        if (verb.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Length < 2)
            {
                _chatUi.AddMessage(
                    "Usage: /guild create <name>", ChatMessageStyle.Warning);
                return true;
            }

            SendNetworkSocial(new SocialAction(
                SocialActionKind.CreateGuild,
                Text: string.Join(' ', arguments.Skip(1))));
            return true;
        }

        if (verb.Equals("join", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Length < 2 ||
                !Guid.TryParse(arguments[1], out var guildId))
            {
                _chatUi.AddMessage(
                    "Usage: /guild join <id>", ChatMessageStyle.Warning);
                return true;
            }

            SendNetworkSocial(new SocialAction(
                SocialActionKind.JoinGuild, GuildId: guildId));
            return true;
        }

        if (verb.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            SendNetworkSocial(new SocialAction(SocialActionKind.LeaveGuild));
            return true;
        }

        _chatUi.AddMessage(
            "Usage: /guild [create <name>|join <id>|leave]",
            ChatMessageStyle.Warning);
        return true;
    }

    private bool HandleNetworkTradeCommand(string[] arguments)
    {
        var social = _networkClient!.State.Social;
        if (social.OpenTradeId == Guid.Empty)
        {
            _chatUi.AddMessage(
                "No trade is open. Right-click a player to offer one.",
                ChatMessageStyle.Warning);
            return true;
        }

        var verb = arguments.Length == 0 ? "" : arguments[0];
        if (verb.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            SendNetworkSocial(new SocialAction(
                SocialActionKind.RespondTrade,
                social.TradePartnerPlayerId,
                social.OpenTradeId,
                Accept: true));
            return true;
        }

        if (verb.Equals("decline", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            SendNetworkSocial(new SocialAction(
                social.TradeIncoming
                    ? SocialActionKind.RespondTrade
                    : SocialActionKind.CancelTrade,
                social.TradePartnerPlayerId,
                social.OpenTradeId,
                Accept: false));
            return true;
        }

        if (verb.Equals("confirm", StringComparison.OrdinalIgnoreCase))
        {
            SendNetworkSocial(new SocialAction(
                SocialActionKind.ConfirmTrade,
                social.TradePartnerPlayerId,
                social.OpenTradeId));
            return true;
        }

        _chatUi.AddMessage(
            "Usage: /trade <accept|decline|confirm|cancel>",
            ChatMessageStyle.Warning);
        return true;
    }

    private void ShowNetworkSocialLists()
    {
        var social = _networkClient!.State.Social;
        var players = _networkClient.State.Players;
        _chatUi.AddMessage(
            $"Friends: {FormatSocialNames(social.Friends, players)}",
            ChatMessageStyle.Action);
        _chatUi.AddMessage(
            $"Ignored: {FormatSocialNames(social.Ignored, players)}",
            ChatMessageStyle.Action);
        _chatUi.AddMessage(
            string.IsNullOrWhiteSpace(social.GuildName)
                ? "Guild: none"
                : $"Guild: {social.GuildName} ({social.GuildId:N})",
            ChatMessageStyle.Action);
    }

    private static string FormatSocialNames(
        IReadOnlyList<Guid> ids,
        IReadOnlyDictionary<Guid, NetworkPlayerPresence> players)
    {
        if (ids.Count == 0) return "(none)";
        return string.Join(", ", ids.Select(id =>
            players.TryGetValue(id, out var player)
                ? player.PlayerName
                : id.ToString("N")[..8]));
    }

    private bool TryResolveNetworkPlayerName(string name, out Guid playerId)
    {
        playerId = default;
        if (_networkClient is null) return false;
        foreach (var player in _networkClient.State.Players.Values)
        {
            if (player.PlayerId == _networkClient.State.PlayerId)
                continue;
            if (!player.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            playerId = player.PlayerId;
            return true;
        }

        return false;
    }

    private bool TryToggleNetworkTradeOffer(int slot)
    {
        if (_networkClient?.IsConnected != true) return false;
        var social = _networkClient.State.Social;
        if (social.OpenTradeId == Guid.Empty || !social.TradeAccepted)
            return false;
        var current = social.OwnOfferSlots.ToList();
        if (!current.Remove(slot))
            current.Add(slot);
        SendNetworkSocial(new SocialAction(
            SocialActionKind.SetTradeOffer,
            social.TradePartnerPlayerId,
            social.OpenTradeId,
            OfferSlots: current));
        return true;
    }

    private void SendNetworkSocial(SocialAction action)
    {
        if (_networkClient?.IsConnected != true) return;
        SendNetworkAction(action, Guid.NewGuid());
    }

    private string? NameForNetworkEntity(ulong entityId)
    {
        if (_networkClient is null ||
            !TryFindNetworkPresence(entityId, out var player) ||
            player.PlayerId == _networkClient.State.PlayerId)
            return null;
        return player.PlayerName;
    }
}
