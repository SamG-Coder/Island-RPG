using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using IslandRpg.Protocol;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

public sealed class DedicatedServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly TcpListener _listener;
    private readonly AuthoritativeWorldSession _session;
    private readonly SemaphoreSlim _clientSlots;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = [];
    private readonly ConcurrentDictionary<Guid, byte> _activeClientIds = [];
    private readonly ConcurrentDictionary<Guid, string> _connectedPlayers = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<IPEndPoint> _startedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _simulationThread;
    private int _started;

    public DedicatedServer(ServerOptions options)
    {
        _options = options;
        _listener = new TcpListener(options.ListenAddress, options.ListenPort);
        _session = new AuthoritativeWorldSession(
            SimulationLimits.Default with { MaximumActors = options.MaximumClients },
            sessionId: new SessionId(options.WorldId));
        _clientSlots = new SemaphoreSlim(options.MaximumClients, options.MaximumClients);
        _simulationThread = new Thread(SimulationLoop)
        {
            IsBackground = true,
            Name = "IslandRpg.Authority"
        };
    }

    internal long CurrentTick => _session.LatestSnapshot.Clock.Tick;

    /// <summary>
    /// Completes once the listener is bound. This allows hosts and tests to use
    /// port zero without racing the first connection attempt.
    /// </summary>
    public Task<IPEndPoint> Started => _startedSignal.Task;

    /// <summary>The endpoint selected by the socket after startup.</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The dedicated server can only be started once.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        _listener.Start(_options.MaximumClients);
        var boundEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        BoundEndpoint = boundEndpoint;
        _startedSignal.TrySetResult(boundEndpoint);
        _simulationThread.Start();
        Console.WriteLine(
            $"Island RPG server listening on {boundEndpoint} " +
            $"(world {_options.WorldId:N}, seed {_options.WorldSeed}, max {_options.MaximumClients}).");

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                if (!_clientSlots.Wait(0))
                {
                    tcpClient.Dispose();
                    continue;
                }

                tcpClient.NoDelay = true;
                tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var connection = new ClientConnection(
                    ClientConnectionId.New(),
                    tcpClient,
                    this,
                    linked.Token);
                _clients.TryAdd(connection.Id.Value, connection);
                _ = ObserveConnectionAsync(connection);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_startedSignal.Task.IsCompleted)
            {
                _startedSignal.TrySetCanceled(linked.Token);
            }

            _listener.Stop();
            foreach (var connection in _clients.Values)
            {
                connection.Stop();
            }

            await Task.WhenAll(_clients.Values.Select(static value => value.Completion))
                .ConfigureAwait(false);
            _lifetime.Cancel();
            _simulationThread.Join(TimeSpan.FromSeconds(5));
            Console.WriteLine("Island RPG server stopped.");
        }
    }

    internal bool TryRegisterClientId(Guid clientId) =>
        clientId != Guid.Empty && _activeClientIds.TryAdd(clientId, 0);

    internal void ReleaseClientId(Guid clientId)
    {
        if (clientId != Guid.Empty)
        {
            _activeClientIds.TryRemove(clientId, out _);
        }
    }

    internal async Task<AuthenticatedPlayer> AuthenticateAsync(
        ClientConnection connection,
        HandshakeRequestMessage request)
    {
        if (request.ProtocolVersion != ProtocolConstants.CurrentVersion)
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.ProtocolMismatch,
                $"Protocol {ProtocolConstants.CurrentVersion} is required.");
        }

        if (!string.Equals(request.BuildVersion, _options.BuildVersion, StringComparison.Ordinal))
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.BuildMismatch,
                $"Build '{_options.BuildVersion}' is required.");
        }

        if (!string.Equals(request.ContentVersion, _options.ContentVersion, StringComparison.Ordinal))
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.ContentMismatch,
                $"Content '{_options.ContentVersion}' is required.");
        }

        if (request.RequestedWorldId != Guid.Empty && request.RequestedWorldId != _options.WorldId)
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.ContentMismatch,
                $"Requested world {request.RequestedWorldId:N} is not hosted by this server.");
        }

        if (!IsValidName(request.PlayerName))
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.InvalidName,
                "Player name must contain 1-40 printable characters.");
        }

        if (!TryRegisterClientId(request.ClientId))
        {
            throw new HandshakeFailure(
                HandshakeRejectionCode.DuplicateClient,
                "This client is already connected.");
        }

        try
        {
            if (request.ReconnectPlayerId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(request.ReconnectToken))
            {
                var result = await _session.EnqueueReconnectAsync(new ReconnectRequest(
                    connection.Id,
                    new PlayerId(request.ReconnectPlayerId),
                    new ReconnectToken(request.ReconnectToken))).ConfigureAwait(false);
                if (!result.Accepted)
                {
                    throw new HandshakeFailure(
                        HandshakeRejectionCode.InvalidName,
                        result.Error ?? "Reconnect was rejected.");
                }

                return new AuthenticatedPlayer(
                    request.ClientId,
                    result.Identity,
                    request.PlayerName.Trim(),
                    request.ReconnectToken,
                    checked((ulong)result.NextCommandSequence),
                    true,
                    result.Gameplay);
            }

            var join = await _session.EnqueueJoinAsync(new JoinRequest(
                connection.Id,
                request.PlayerName,
                Vector2.Zero,
                _options.StartingInventory,
                _options.StartingHunger)).ConfigureAwait(false);
            if (!join.Accepted)
            {
                var code = join.Status == JoinStatus.SessionFull
                    ? HandshakeRejectionCode.ServerFull
                    : HandshakeRejectionCode.InvalidName;
                throw new HandshakeFailure(code, join.Error ?? "Join was rejected.");
            }

            return new AuthenticatedPlayer(
                request.ClientId,
                join.Identity,
                request.PlayerName.Trim(),
                join.ReconnectToken.Value,
                checked((ulong)join.NextCommandSequence),
                false,
                join.Gameplay);
        }
        catch
        {
            ReleaseClientId(request.ClientId);
            throw;
        }
    }

    internal HandshakeAcceptedMessage CreateHandshakeAccepted(
        ClientConnection connection,
        HandshakeRequestMessage request,
        AuthenticatedPlayer player) =>
        new(
            connection.NextOutboundSequence(),
            checked((ulong)_session.LatestSnapshot.Clock.Tick),
            ProtocolConstants.CurrentVersion,
            _options.BuildVersion,
            _options.ContentVersion,
            _session.Id.Value,
            player.Identity.PlayerId.Value,
            StableNetworkId(player.Identity.ActorId.Value),
            _options.WorldId,
            _options.WorldSeed,
            0,
            0,
            0,
            0,
            request.ClientNonce,
            player.NextCommandSequence,
            player.ReconnectToken,
            0,
            SimulationTiming.TicksPerSecond,
            ServerCapabilities.None);

    internal HandshakeRejectedMessage CreateHandshakeRejected(
        ClientConnection connection,
        HandshakeFailure failure) =>
        new(
            connection.NextOutboundSequence(),
            checked((ulong)_session.LatestSnapshot.Clock.Tick),
            ProtocolConstants.CurrentVersion,
            _options.BuildVersion,
            _options.ContentVersion,
            failure.Code,
            failure.Message);

    internal PlayerStateMessage CreatePlayerStateBaseline(
        ClientConnection connection,
        AuthenticatedPlayer player)
    {
        return ToPlayerStateMessage(
            connection,
            player,
            player.Gameplay,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            baselineActorRevision: 0,
            baselineInventoryRevision: 0);
    }

    internal async Task<IntentResult> ProcessCommandAsync(
        ClientConnection connection,
        AuthenticatedPlayer player,
        IProtocolMessage message)
    {
        if (message.Sequence > long.MaxValue)
        {
            return new IntentResult(IntentStatus.InvalidSequence, 0, "Command sequence is too large.");
        }

        SessionIntent intent = message switch
        {
            WalkCommandMessage walk when walk.WorldLevel == 0 =>
                new WalkIntent(new Vector2(walk.DestinationX, walk.DestinationY)),
            WalkCommandMessage => throw new CommandFailure(
                CommandRejectionCode.Impossible,
                "This server foundation currently hosts world level 0."),
            StopCommandMessage => StopIntent.Instance,
            ChatCommandMessage chat => new ChatIntent(chat.Text),
            ActionCommandMessage action => ToGameplayIntent(action),
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                $"Message {message.Kind} is not valid after handshake.")
        };

        var result = await _session.EnqueueIntentAsync(new ActorCommand(
            connection.Id,
            player.Identity.PlayerId,
            checked((long)message.Sequence),
            intent)).ConfigureAwait(false);

        if (result.Accepted && message is ChatCommandMessage chatMessage)
        {
            BroadcastChat(player, chatMessage);
        }

        return result;
    }

    internal void QueueActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        IntentResult result)
    {
        var rejection = MapRejection(result.Status);
        if (!connection.TryQueue(new ActionResultMessage(
                connection.NextOutboundSequence(),
                checked((ulong)CurrentTick),
                command.CommandId,
                result.Accepted,
                rejection,
                result.Error ?? string.Empty,
                result.ActorRevision,
                result.InventoryRevision)))
        {
            connection.Stop();
            return;
        }

        // Send a complete private state after every accepted gameplay
        // transaction. The transport can replace this with section deltas
        // later without changing the authoritative transaction boundary.
        if (!result.Accepted || result.Duplicate) return;
        if (!connection.TryQueue(ToPlayerStateMessage(
                connection,
                player,
                result.Gameplay,
                PlayerStateFlags.Baseline |
                PlayerStateFlags.Actor |
                PlayerStateFlags.Inventory,
                0,
                0)))
            connection.Stop();
    }

    internal void BroadcastPlayerJoined(Guid playerId, string playerName) =>
        Broadcast(connection => new PlayerJoinedMessage(
            connection.NextOutboundSequence(),
            checked((ulong)_session.LatestSnapshot.Clock.Tick),
            playerId,
            playerName));

    internal void AnnouncePlayerJoined(
        ClientConnection joinedConnection,
        Guid playerId,
        string playerName)
    {
        _connectedPlayers[playerId] = playerName;

        // Bootstrap the joining connection with the complete presence set. A
        // snapshot alone has entity IDs but intentionally carries no names.
        foreach (var player in _connectedPlayers.OrderBy(static value => value.Key))
        {
            if (!joinedConnection.TryQueue(new PlayerJoinedMessage(
                    joinedConnection.NextOutboundSequence(),
                    checked((ulong)_session.LatestSnapshot.Clock.Tick),
                    player.Key,
                    player.Value)))
            {
                joinedConnection.Stop();
                return;
            }
        }

        foreach (var connection in _clients.Values)
        {
            if (connection == joinedConnection || !connection.Authenticated)
            {
                continue;
            }

            if (!connection.TryQueue(new PlayerJoinedMessage(
                    connection.NextOutboundSequence(),
                    checked((ulong)_session.LatestSnapshot.Clock.Tick),
                    playerId,
                    playerName)))
            {
                connection.Stop();
            }
        }
    }

    internal void BroadcastPlayerLeft(Guid playerId, PlayerLeaveReason reason, string detail)
    {
        _connectedPlayers.TryRemove(playerId, out _);
        Broadcast(connection => new PlayerLeftMessage(
            connection.NextOutboundSequence(),
            checked((ulong)_session.LatestSnapshot.Clock.Tick),
            playerId,
            reason,
            detail));
    }

    internal void BroadcastChat(AuthenticatedPlayer sender, ChatCommandMessage message)
    {
        foreach (var connection in _clients.Values)
        {
            if (!connection.Authenticated ||
                message.Channel == ChatChannel.Whisper &&
                connection.PlayerId != sender.Identity.PlayerId.Value &&
                connection.PlayerId != message.TargetPlayerId)
            {
                continue;
            }

            if (!connection.TryQueue(new ChatBroadcastMessage(
                connection.NextOutboundSequence(),
                checked((ulong)_session.LatestSnapshot.Clock.Tick),
                sender.Identity.PlayerId.Value,
                sender.DisplayName,
                message.Channel,
                message.TargetPlayerId,
                message.Text)))
            {
                connection.Stop();
            }
        }
    }

    internal static CommandRejectionCode MapRejection(IntentStatus status) => status switch
    {
        IntentStatus.Accepted => CommandRejectionCode.None,
        IntentStatus.StaleSequence or IntentStatus.InvalidSequence => CommandRejectionCode.OutOfOrder,
        IntentStatus.UnknownPlayer or IntentStatus.InvalidConnection or IntentStatus.Disconnected =>
            CommandRejectionCode.NotAuthorized,
        IntentStatus.DestinationTooFar => CommandRejectionCode.Impossible,
        IntentStatus.StaleInventoryRevision or
            IntentStatus.StaleActorRevision or
            IntentStatus.CommandIdConflict => CommandRejectionCode.OutOfOrder,
        IntentStatus.MissingResources or
            IntentStatus.MissingStation or
            IntentStatus.InventoryFull or
            IntentStatus.CraftingLocked or
            IntentStatus.AlreadyFull => CommandRejectionCode.Impossible,
        IntentStatus.QueueFull => CommandRejectionCode.ServerBusy,
        _ => CommandRejectionCode.Invalid
    };

    private static GameplayIntent ToGameplayIntent(
        ActionCommandMessage command) => command.Payload switch
        {
            InventorySwapAction swap => new SwapInventorySlotsIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                swap.SourceSlot,
                swap.TargetSlot),
            CombineItemsAction combine => new CombineInventorySlotsIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                combine.SourceSlot,
                combine.TargetSlot),
            CraftRecipeAction craft => new CraftRecipeIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                craft.RecipeId),
            ConsumeItemAction consume => new ConsumeFoodIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                consume.Slot),
            _ => throw new CommandFailure(
                CommandRejectionCode.Invalid,
                "The action payload is unsupported.")
        };

    private PlayerStateMessage ToPlayerStateMessage(
        ClientConnection connection,
        AuthenticatedPlayer player,
        PlayerGameplaySnapshot gameplay,
        PlayerStateFlags flags,
        uint baselineActorRevision,
        uint baselineInventoryRevision) =>
        new(
            connection.NextOutboundSequence(),
            checked((ulong)CurrentTick),
            player.Identity.PlayerId.Value,
            StableNetworkId(player.Identity.ActorId.Value),
            flags,
            baselineActorRevision,
            baselineInventoryRevision,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            gameplay.Health,
            gameplay.Hunger,
            gameplay.WellFedSeconds,
            gameplay.CraftingExperience,
            gameplay.CookingExperience,
            gameplay.Inventory.Slots.Select(slot => new InventorySlotState(
                slot.Slot,
                slot.ItemId ?? string.Empty,
                slot.Quantity)).ToArray());

    internal Task DisconnectAsync(ClientConnection connection, AuthenticatedPlayer player) =>
        _session.EnqueueDisconnectAsync(new DisconnectRequest(
            connection.Id,
            player.Identity.PlayerId));

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _lifetime.Dispose();
        _clientSlots.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ObserveConnectionAsync(ClientConnection connection)
    {
        try
        {
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Connection {connection.Id}: {exception.Message}");
        }
        finally
        {
            _clients.TryRemove(connection.Id.Value, out _);
            _clientSlots.Release();
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SimulationLoop()
    {
        var cancellationToken = _lifetime.Token;
        var stopwatch = Stopwatch.StartNew();
        var tickDuration = Stopwatch.Frequency / (double)SimulationTiming.TicksPerSecond;
        var nextTick = stopwatch.ElapsedTicks;

        while (!cancellationToken.IsCancellationRequested)
        {
            nextTick += (long)tickDuration;
            var tick = _session.Tick();
            if (tick.PublishedSnapshot is { } snapshot)
            {
                BroadcastSnapshot(snapshot);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var remainingTicks = nextTick - stopwatch.ElapsedTicks;
                if (remainingTicks <= 0)
                {
                    break;
                }

                var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
                if (remaining > TimeSpan.FromMilliseconds(2))
                {
                    Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
                }
                else
                {
                    Thread.SpinWait(64);
                }
            }

            // Never skip simulation steps. If overloaded, successive iterations run
            // immediately until authoritative time catches wall time again.
        }
    }

    private void BroadcastSnapshot(SessionSnapshot snapshot)
    {
        var entities = snapshot.Actors
            .Select(actor => new EntitySnapshot(
                StableNetworkId(actor.ActorId.Value),
                NetworkEntityKind.Player,
                0,
                0,
                actor.Position.X,
                actor.Position.Y,
                actor.Velocity.X,
                actor.Velocity.Y,
                (!actor.Connected ? NetworkEntityState.Hidden : NetworkEntityState.None) |
                (actor.Velocity != Vector2.Zero ? NetworkEntityState.Moving : NetworkEntityState.None),
                checked((uint)Math.Min(snapshot.Sequence, uint.MaxValue))))
            .ToArray();
        var snapshotSequence = unchecked((ushort)snapshot.Sequence);

        Broadcast(connection => new EntitySnapshotMessage(
            connection.NextOutboundSequence(),
            checked((ulong)snapshot.Clock.Tick),
            new SnapshotMetadata(
                0,
                snapshotSequence,
                0,
                0,
                checked((ulong)snapshot.Clock.Tick),
                0,
                SnapshotFlags.Keyframe),
            entities));
    }

    private void Broadcast(Func<ClientConnection, IProtocolMessage> createMessage)
    {
        foreach (var connection in _clients.Values)
        {
            if (connection.Authenticated && !connection.TryQueue(createMessage(connection)))
            {
                connection.Stop();
            }
        }
    }

    private static bool IsValidName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 40 &&
        value.All(character => !char.IsControl(character) && !char.IsSurrogate(character));

    private static ulong StableNetworkId(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}

internal readonly record struct AuthenticatedPlayer(
    Guid ClientId,
    PlayerIdentity Identity,
    string DisplayName,
    string ReconnectToken,
    ulong NextCommandSequence,
    bool Reconnected,
    PlayerGameplaySnapshot Gameplay);

internal sealed class HandshakeFailure(
    HandshakeRejectionCode code,
    string message) : Exception(message)
{
    public HandshakeRejectionCode Code { get; } = code;
}

internal sealed class CommandFailure(
    CommandRejectionCode code,
    string message) : Exception(message)
{
    public CommandRejectionCode Code { get; } = code;
}
