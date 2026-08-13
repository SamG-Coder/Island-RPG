using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Navigation;
using IslandRpg.Caves;
using IslandRpg.Boats;
using IslandRpg.Fishing;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Server.Persistence;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

public sealed class DedicatedServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly TcpListener _listener;
    private readonly Socket _snapshotSocket;
    private readonly AuthoritativeWorldSession _session;
    private readonly SemaphoreSlim _clientSlots;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = [];
    private readonly ConcurrentDictionary<Guid, byte> _activeClientIds = [];
    private readonly ConcurrentDictionary<Guid, string> _connectedPlayers = [];
    private readonly object _publicReplicationSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<IPEndPoint> _startedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stoppedSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _simulationThread;
    private ushort _boundSnapshotPort;
    private int _started;
    private int _worldBootstrapPending = 1;
    private readonly object _worldBootstrapSync = new();
    private WorldBootstrapState _worldBootstrap = WorldBootstrapState.Empty;
    private readonly object _resourceBootstrapSync = new();
    private ResourceBootstrapState _resourceBootstrap =
        ResourceBootstrapState.Empty;
    private readonly object _boatBootstrapSync = new();
    private BoatBootstrapState _boatBootstrap = BoatBootstrapState.Empty;
    private readonly ServerCheckpointStore? _checkpointStore;
    private readonly ServerCheckpointWriter? _checkpointWriter;
    private readonly IDisposable? _worldLease;
    private readonly ServerCheckpointLoadResult? _checkpointToRestore;
    private long _checkpointRevision;
    private long _nextAutosaveTick;
    private int _disposed;

    /// <summary>
    /// Deterministic test seam inside the activation/broadcast barrier and
    /// immediately before baseline capture. Production leaves this null.
    /// </summary>
    internal Action<ClientConnection>? DuringBootstrapActivation { get; set; }

    internal Action? AfterWorldBootstrapUpdatedForTest { get; set; }

    public DedicatedServer(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IslandStart &&
            options.MaximumClients > ServerOptions.MaximumIslandStartClients)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Island-start worlds support at most " +
                $"{ServerOptions.MaximumIslandStartClients} clients because " +
                "each player owns one authoritative raft.");
        }

        _options = options;
        _listener = new TcpListener(options.ListenAddress, options.ListenPort);
        _snapshotSocket = new Socket(
            options.ListenAddress.AddressFamily,
            SocketType.Dgram,
            ProtocolType.Udp);
        var resourceCatalog = new ProceduralResourceCatalog(
            new CompositeResourceDescriptorSource(
                new SurfaceTreeResourceDescriptorSource(),
                new SurfaceVegetationResourceDescriptorSource(),
                new UndergroundMiningResourceDescriptorSource(),
                new ProceduralFishSchoolSource()));
        var resourceTransactions = new AuthoritativeResourceTransactions(
            options.WorldSeed,
            resourceCatalog);
        var worldTransactions = new AuthoritativeWorldTransactions(
            caves: new ProceduralCaveExcavationEnvironment(
                options.WorldSeed));
        var boatTransactions = new AuthoritativeBoatTransactions(
            new ProceduralBoatNavigationQuery(options.WorldSeed));
        _session = new AuthoritativeWorldSession(
            SimulationLimits.Default with { MaximumActors = options.MaximumClients },
            sessionId: new SessionId(options.WorldId),
            navigation: new ProceduralWorldNavigationQuery(options.WorldSeed),
            worldTransactions: worldTransactions,
            resourceTransactions: resourceTransactions,
            boatTransactions: boatTransactions);
        _session.WorldTransactionCommitted += ApplyWorldTransactionToBootstrap;
        _session.ResourceTransactionCommitted +=
            ApplyResourceTransactionToBootstrap;
        _session.CookingCompleted += BroadcastCookingCompletion;
        _session.BoatRouteCompleted += BroadcastBoatRouteCompletion;
        _session.BoatStateCommitted += ApplyBoatStateToBootstrap;
        if (!string.IsNullOrWhiteSpace(options.SaveRoot))
        {
            _checkpointStore = new ServerCheckpointStore(options.SaveRoot);
            _worldLease = _checkpointStore.AcquireWorldLease(options.WorldId);
            try
            {
                _checkpointToRestore = _checkpointStore.Load(options.WorldId);
                _checkpointRevision = _checkpointToRestore?.Checkpoint.Revision ?? 0;
                _checkpointWriter = new ServerCheckpointWriter(_checkpointStore);
            }
            catch
            {
                _worldLease.Dispose();
                throw;
            }
        }
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
        var simulationStarted = false;
        try
        {
            _listener.Start(_options.MaximumClients);
            _snapshotSocket.Bind(new IPEndPoint(
                _options.ListenAddress,
                _options.SnapshotPort));
            _boundSnapshotPort = checked((ushort)
                ((IPEndPoint)_snapshotSocket.LocalEndPoint!).Port);
            var boundEndpoint = (IPEndPoint)_listener.LocalEndpoint;
            BoundEndpoint = boundEndpoint;
            _startedSignal.TrySetResult(boundEndpoint);
            _simulationThread.Start();
            simulationStarted = true;
            Console.WriteLine(
                $"Island RPG server listening on {boundEndpoint} " +
                $"(world {_options.WorldId:N}, seed {_options.WorldSeed}, max {_options.MaximumClients}).");

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
            try
            {
                if (!_startedSignal.Task.IsCompleted)
                    _startedSignal.TrySetCanceled(linked.Token);

                _listener.Stop();
                foreach (var connection in _clients.Values)
                    connection.Stop();

                await Task.WhenAll(_clients.Values.Select(static value => value.Completion))
                    .ConfigureAwait(false);
                _lifetime.Cancel();
                if (simulationStarted &&
                    !_simulationThread.Join(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException(
                        "The authoritative simulation thread did not stop cleanly.");
                if (_checkpointWriter is not null)
                {
                    await _checkpointWriter.FlushAsync().ConfigureAwait(false);
                    await _checkpointWriter.DisposeAsync().ConfigureAwait(false);
                }
                Console.WriteLine("Island RPG server stopped.");
            }
            finally
            {
                _worldLease?.Dispose();
                _snapshotSocket.Close();
                _stoppedSignal.TrySetResult();
            }
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

        var udpRequested =
            request.Capabilities.HasFlag(ClientCapabilities.UdpSnapshots) &&
            request.ClientSnapshotPort != 0 &&
            _boundSnapshotPort != 0;
        var remoteAddress = NormalizeAddress(
            ((IPEndPoint)connection.RemoteEndPoint).Address,
            _snapshotSocket.AddressFamily);
        var snapshotEndpoint = udpRequested
            ? new IPEndPoint(remoteAddress, request.ClientSnapshotPort)
            : null;
        var datagramToken = udpRequested ? CreateDatagramToken() : 0;

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

                var authenticated = new AuthenticatedPlayer(
                    request.ClientId,
                    result.Identity,
                    request.PlayerName.Trim(),
                    request.ReconnectToken,
                    checked((ulong)result.NextCommandSequence),
                    true,
                    result.Gameplay,
                    result.Position,
                    result.WorldLevel);
                connection.ConfigureSnapshotTransport(
                    snapshotEndpoint,
                    datagramToken,
                    StableNetworkId(result.Identity.ActorId.Value),
                    request.Capabilities.HasFlag(ClientCapabilities.DeltaSnapshots));
                return authenticated;
            }

            var spawn = _options.StartingPosition ??
                BoatTravelRules.FindPlayableLandSpawn(
                    _options.WorldSeed,
                    _lifetime.Token);
            var join = await _session.EnqueueJoinAsync(new JoinRequest(
                connection.Id,
                request.PlayerName,
                spawn,
                _options.StartingInventory,
                _options.StartingHunger,
                ProvisionBoat: _options.IslandStart)).ConfigureAwait(false);
            if (!join.Accepted)
            {
                var code = join.Status == JoinStatus.SessionFull
                    ? HandshakeRejectionCode.ServerFull
                    : HandshakeRejectionCode.InvalidName;
                throw new HandshakeFailure(code, join.Error ?? "Join was rejected.");
            }

            if (join.Boat is { } boat)
                BroadcastBoatProvisioned(boat);

            var joinedPlayer = new AuthenticatedPlayer(
                request.ClientId,
                join.Identity,
                request.PlayerName.Trim(),
                join.ReconnectToken.Value,
                checked((ulong)join.NextCommandSequence),
                false,
                join.Gameplay,
                join.Position,
                join.WorldLevel);
            connection.ConfigureSnapshotTransport(
                snapshotEndpoint,
                datagramToken,
                StableNetworkId(join.Identity.ActorId.Value),
                request.Capabilities.HasFlag(ClientCapabilities.DeltaSnapshots));
            return joinedPlayer;
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
            player.Position.X,
            player.Position.Y,
            player.WorldLevel,
            connection.DatagramToken,
            request.ClientNonce,
            player.NextCommandSequence,
            player.ReconnectToken,
            connection.UdpSnapshotsEnabled ? _boundSnapshotPort : (ushort)0,
            SimulationTiming.TicksPerSecond,
            connection.UdpSnapshotsEnabled
                ? ServerCapabilities.UdpSnapshots |
                  (connection.DeltaSnapshotsEnabled
                      ? ServerCapabilities.DeltaSnapshots
                      : ServerCapabilities.None)
                : ServerCapabilities.None,
            _options.IslandStart);

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
            connection.NextOutboundSequence(),
            checked((ulong)CurrentTick),
            player,
            player.Gameplay,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            baselineActorRevision: 0,
            baselineInventoryRevision: 0);
    }

    /// <summary>
    /// Atomically enters public replication and queues a fresh projection of
    /// every public aggregate. A commit before this lock is represented by
    /// the baselines; a commit after it observes Authenticated and broadcasts
    /// its delta. There is therefore no unauthenticated baseline/delta gap.
    /// </summary>
    internal bool ActivateAndQueuePublicBaselines(ClientConnection connection)
    {
        lock (_publicReplicationSync)
        {
            DuringBootstrapActivation?.Invoke(connection);
            var world = Volatile.Read(ref _worldBootstrap);
            var resources = Volatile.Read(ref _resourceBootstrap);
            var boats = Volatile.Read(ref _boatBootstrap);
            connection.SetPublicBaselineHighWater(
                world.ChunkRevisions,
                resources.Chunks,
                boats.Boats);
            if (!QueueWorldObjectBaselines(connection, world) ||
                !QueueResourceBaselines(connection, resources) ||
                !QueueBoatBaseline(connection, boats))
                return false;
            connection.Activate();
            return true;
        }
    }

    private bool QueueWorldObjectBaselines(
        ClientConnection connection,
        WorldBootstrapState bootstrap)
    {
        var chunkRevisions = bootstrap.ChunkRevisions;
        for (var offset = 0; offset < chunkRevisions.Count;
             offset += ProtocolLimits.MaxWorldChunkRevisionsPerBatch)
        {
            var count = Math.Min(
                ProtocolLimits.MaxWorldChunkRevisionsPerBatch,
                chunkRevisions.Count - offset);
            var batch = new WorldChunkRevisionState[count];
            for (var index = 0; index < count; index++)
                batch[index] = chunkRevisions[offset + index];
            if (!connection.TryQueueSequenced(sequence =>
                    new WorldChunkRevisionBatchMessage(
                        sequence,
                        checked((ulong)CurrentTick),
                        batch)))
            {
                connection.Stop();
                return false;
            }
        }

        // Chunk revisions precede object baselines so an object-free chunk is
        // still actionable and every following object can reference a known
        // authoritative chunk revision.
        foreach (var value in bootstrap.Objects)
        {
            if (!connection.TryQueueSequenced(sequence =>
                    WorldActionProtocolAdapter.ToPublicWorldState(
                        sequence,
                        checked((ulong)CurrentTick),
                        value.Object,
                        value.ChunkRevision)))
            {
                connection.Stop();
                return false;
            }
        }
        return true;
    }

    private bool QueueResourceBaselines(
        ClientConnection connection,
        ResourceBootstrapState bootstrap)
    {
        foreach (var chunk in bootstrap.Chunks)
        {
            if (!connection.TryQueueSequenced(sequence =>
                    ResourceActionProtocolAdapter.ToBaseline(
                        sequence,
                        checked((ulong)CurrentTick),
                        chunk)))
            {
                connection.Stop();
                return false;
            }
        }
        return true;
    }

    private bool QueueBoatBaseline(
        ClientConnection connection,
        BoatBootstrapState bootstrap)
    {
        if (!connection.TryQueueSequenced(sequence =>
                BoatActionProtocolAdapter.ToBaseline(
                    sequence,
                    checked((ulong)CurrentTick),
                    bootstrap.Boats)))
        {
            connection.Stop();
            return false;
        }
        return true;
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
            WalkCommandMessage walk => new WalkIntent(
                new Vector2(walk.DestinationX, walk.DestinationY),
                walk.WorldLevel),
            StopCommandMessage => StopIntent.Instance,
            ChatCommandMessage chat => new ChatIntent(chat.Text),
            ActionCommandMessage action =>
                WorldActionProtocolAdapter.TryToWorldIntent(
                    action,
                    out var worldIntent)
                    ? worldIntent!
                    : action.Payload is ResourceActionPayload resource
                        ? ResourceActionProtocolAdapter.ToIntent(
                            action, resource)
                        : action.Payload is BoatActionPayload boat
                            ? BoatActionProtocolAdapter.ToIntent(action, boat)
                        : action.Payload is CaveActionPayload cave
                            ? CaveActionProtocolAdapter.ToIntent(action, cave)
                        : ToGameplayIntent(action),
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
        var tick = checked((ulong)CurrentTick);
        if (command.Payload is ResourceActionPayload resourceAction)
        {
            QueueResourceActionOutcome(
                connection, player, command, resourceAction, result, tick);
            return;
        }
        if (command.Payload is BoatActionPayload boatAction)
        {
            QueueBoatActionOutcome(
                connection, player, command, boatAction, result, tick);
            return;
        }
        if (command.Payload is CaveActionPayload caveAction)
        {
            QueueCaveActionOutcome(
                connection, player, command, caveAction, result, tick);
            return;
        }
        if (result.WorldTransaction is { } transaction)
        {
            QueueWorldActionOutcome(
                connection, player, command, result, transaction, tick);
            return;
        }

        var rejection = MapRejection(result.Status);
        if (!connection.TryQueueSequenced(sequence => new ActionResultMessage(
                sequence,
                tick,
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
        if (!connection.TryQueueSequenced(sequence => ToPlayerStateMessage(
                sequence,
                tick,
                player,
                result.Gameplay,
                PlayerStateFlags.Baseline |
                PlayerStateFlags.Actor |
                PlayerStateFlags.Inventory,
                0,
                0)))
            connection.Stop();
    }

    private void QueueResourceActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        ResourceActionPayload action,
        IntentResult result,
        ulong tick)
    {
        // Accepted gameplay state must precede the presentation receipt so a
        // continuous authored action reads the new optimistic revisions.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueueSequenced(sequence =>
                ToPlayerStateMessage(
                    sequence,
                    tick,
                    player,
                    result.Gameplay,
                    PlayerStateFlags.Baseline |
                    PlayerStateFlags.Actor |
                    PlayerStateFlags.Inventory,
                    0,
                    0)))
        {
            connection.Stop();
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                ResourceActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            return;
        }
        if (result.Duplicate) return;
        if (result.ResourceTransaction is { } transaction &&
            ResourceActionProtocolAdapter.ToPublicDelta(
                1, tick, transaction) is not null)
        {
            Broadcast((_, sequence) =>
                ResourceActionProtocolAdapter.ToPublicDelta(
                    sequence, tick, transaction)!);
        }
    }

    private void QueueBoatActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        BoatActionPayload action,
        IntentResult result,
        ulong tick)
    {
        // Private gameplay state and the command receipt precede the public
        // semantic delta. This prevents the requesting client from observing
        // a boat revision it cannot yet correlate to its command.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueueSequenced(sequence => ToPlayerStateMessage(
                sequence,
                tick,
                player,
                result.Gameplay,
                PlayerStateFlags.Baseline |
                PlayerStateFlags.Actor |
                PlayerStateFlags.Inventory,
                0,
                0)))
        {
            connection.Stop();
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                BoatActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            return;
        }
        if (result.Duplicate ||
            result.BoatTransaction?.BoatDelta is not { } delta ||
            BoatActionProtocolAdapter.ToPublicDelta(
                1, tick, delta) is null)
            return;
        Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(
                sequence, tick, delta)!);
    }

    private void QueueCaveActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        CaveActionPayload action,
        IntentResult result,
        ulong tick)
    {
        // State precedes the receipt so follow-up commands always see the
        // authoritative post-action revisions and digging experience.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueueSequenced(sequence => ToPlayerStateMessage(
                sequence,
                tick,
                player,
                result.Gameplay,
                PlayerStateFlags.Baseline |
                PlayerStateFlags.Actor |
                PlayerStateFlags.Inventory,
                0,
                0)))
        {
            connection.Stop();
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                CaveActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            return;
        }
        if (result.Duplicate || result.WorldTransaction is not { } transaction)
            return;
        if (WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                1, tick, transaction) is not null)
            Broadcast((_, sequence) =>
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    sequence, tick, transaction)!);
    }

    private void QueueWorldActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        IntentResult result,
        WorldTransactionResult transaction,
        ulong tick)
    {
        if (!connection.TryQueueSequenced(sequence =>
                WorldActionProtocolAdapter.ToActionResult(
                    sequence, tick, transaction)))
        {
            connection.Stop();
            return;
        }

        // Duplicate receipts acknowledge the requester but never publish the
        // same public mutation twice. The original receipt already advanced
        // every observer's chunk/object revisions.
        if (result.Duplicate) return;

        if (WorldActionProtocolAdapter.ToPrivatePlayerState(
                1,
                tick,
                player.Identity.PlayerId.Value,
                StableNetworkId(player.Identity.ActorId.Value),
                command,
                transaction) is not null &&
            !connection.TryQueueSequenced(sequence =>
                WorldActionProtocolAdapter.ToPrivatePlayerState(
                    sequence,
                    tick,
                    player.Identity.PlayerId.Value,
                    StableNetworkId(player.Identity.ActorId.Value),
                    command,
                    transaction)!))
        {
            connection.Stop();
            return;
        }

        if (WorldActionProtocolAdapter.ToPrivateContainerBaseline(
                1, tick, command, transaction) is not null &&
            !connection.TryQueueSequenced(sequence =>
                WorldActionProtocolAdapter.ToPrivateContainerBaseline(
                    sequence, tick, command, transaction)!))
        {
            connection.Stop();
            return;
        }

        if (WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                1, tick, transaction) is not null)
        {
            Broadcast((candidate, sequence) =>
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    sequence, tick, transaction)!);
        }
    }

    internal void BroadcastPlayerJoined(Guid playerId, string playerName) =>
        Broadcast((connection, sequence) => new PlayerJoinedMessage(
            sequence,
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
            if (!joinedConnection.TryQueueSequenced(sequence => new PlayerJoinedMessage(
                    sequence,
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

            if (!connection.TryQueueSequenced(sequence => new PlayerJoinedMessage(
                    sequence,
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
        Broadcast((connection, sequence) => new PlayerLeftMessage(
            sequence,
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

            if (!connection.TryQueueSequenced(sequence => new ChatBroadcastMessage(
                sequence,
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
        IntentStatus.AlreadyCooking or
            IntentStatus.NotCookable or
            IntentStatus.CookingLocked or
            IntentStatus.InvalidCampfireState => CommandRejectionCode.Impossible,
        IntentStatus.ResourceCadenceLocked => CommandRejectionCode.RateLimited,
        IntentStatus.ExcavationCadenceLocked =>
            CommandRejectionCode.RateLimited,
        IntentStatus.BoatPlanningLocked => CommandRejectionCode.RateLimited,
        IntentStatus.StaleNodeRevision or
            IntentStatus.StaleResourceChunkRevision or
            IntentStatus.StaleBoatRevision =>
            CommandRejectionCode.OutOfOrder,
        IntentStatus.ResourceNotFound or
            IntentStatus.WrongResourceKind or
            IntentStatus.MissingTool or
            IntentStatus.ResourceDepleted or
            IntentStatus.InvalidExcavation or
            IntentStatus.MissingExcavationTool or
            IntentStatus.InvalidCaveLink or
            IntentStatus.OutOfRange or
            IntentStatus.BoatNotFound or
            IntentStatus.AlreadyAboard or
            IntentStatus.BoatOccupied or
            IntentStatus.NotAboard or
            IntentStatus.InvalidBoatDestination or
            IntentStatus.BoatDestinationTooFar or
            IntentStatus.BoatRouteUnreachable or
            IntentStatus.InvalidBoatLanding => CommandRejectionCode.Impossible,
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

    private static PlayerStateMessage ToPlayerStateMessage(
        ulong sequence,
        ulong tick,
        AuthenticatedPlayer player,
        PlayerGameplaySnapshot gameplay,
        PlayerStateFlags flags,
        uint baselineActorRevision,
        uint baselineInventoryRevision) =>
        new(
            sequence,
            tick,
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
                slot.Quantity)).ToArray(),
            gameplay.WoodcuttingExperience,
            gameplay.FarmingExperience,
            gameplay.MiningExperience,
            gameplay.AdventureExperience,
            gameplay.DiggingExperience,
            gameplay.FishingExperience);

    private void BroadcastCookingCompletion(CookingCompletionSnapshot value)
    {
        var tick = checked((ulong)CurrentTick);
        foreach (var connection in _clients.Values)
        {
            if (!connection.Authenticated ||
                connection.PlayerId != value.PlayerId.Value)
                continue;
            if (!connection.TryQueueSequenced(sequence =>
                    ToPlayerStateMessage(
                        sequence,
                        tick,
                        value.PlayerId.Value,
                        connection.PlayerEntityId,
                        value.Gameplay,
                        PlayerStateFlags.Baseline |
                        PlayerStateFlags.Actor |
                        PlayerStateFlags.Inventory,
                        0,
                        0)) ||
                !connection.TryQueueSequenced(sequence =>
                    new CookingResultMessage(
                        sequence,
                        tick,
                        value.CommandId,
                        value.RawItemId,
                        value.ResultItemId,
                        value.Burnt,
                        value.Interrupted,
                        value.ActorRevision,
                        value.InventoryRevision)))
                connection.Stop();
        }

        if (WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                1, tick, value.Transaction) is not null)
            Broadcast((_, sequence) =>
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    sequence, tick, value.Transaction)!);
    }

    private void BroadcastBoatRouteCompletion(BoatStateDelta delta)
    {
        var tick = checked((ulong)CurrentTick);
        if (BoatActionProtocolAdapter.ToPublicDelta(1, tick, delta) is null)
            return;
        Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(sequence, tick, delta)!);
    }

    private static PlayerStateMessage ToPlayerStateMessage(
        ulong sequence,
        ulong tick,
        Guid playerId,
        ulong playerEntityId,
        PlayerGameplaySnapshot gameplay,
        PlayerStateFlags flags,
        uint baselineActorRevision,
        uint baselineInventoryRevision) => new(
        sequence,
        tick,
        playerId,
        playerEntityId,
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
            slot.Slot, slot.ItemId ?? string.Empty, slot.Quantity)).ToArray(),
        gameplay.WoodcuttingExperience,
        gameplay.FarmingExperience,
        gameplay.MiningExperience,
        gameplay.AdventureExperience,
        gameplay.DiggingExperience,
        gameplay.FishingExperience);

    internal async Task DisconnectAsync(
        ClientConnection connection,
        AuthenticatedPlayer player)
    {
        var result = await _session.EnqueueDisconnectAsync(
            new DisconnectRequest(
                connection.Id,
                player.Identity.PlayerId)).ConfigureAwait(false);
        if (result.BoatDelta is not { } delta) return;
        var tick = checked((ulong)CurrentTick);
        Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(
                sequence, tick, delta)!);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _listener.Stop();
        if (Volatile.Read(ref _started) != 0)
        {
            // RunAsync owns connection drain, authority join and the final
            // durable flush. Disposal waits for that one shutdown path.
            await _stoppedSignal.Task.ConfigureAwait(false);
        }
        else
        {
            if (_checkpointWriter is not null)
                await _checkpointWriter.DisposeAsync().ConfigureAwait(false);
            _worldLease?.Dispose();
            _snapshotSocket.Dispose();
        }
        _lifetime.Dispose();
        _clientSlots.Dispose();
    }

    private async Task ObserveConnectionAsync(ClientConnection connection)
    {
        try
        {
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Connection {connection.Id}: {exception}");
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

        if (Interlocked.Exchange(ref _worldBootstrapPending, 0) != 0)
        {
            if (_checkpointToRestore is { } load)
            {
                _session.RestoreCheckpoint(
                    ServerCheckpointMapper.ToSimulation(
                        load.Checkpoint,
                        _options));
                if (load.RecoveredFromBackup)
                    Console.Error.WriteLine(
                        "Recovered the authoritative world from its last known good backup.");
            }
            else
            {
                foreach (var value in _options.StartingWorldObjects)
                    _session.SeedWorldObject(value);
            }
            RefreshWorldBootstrap();
            RefreshResourceBootstrap();
            RefreshBoatBootstrap();
            _nextAutosaveTick = checked(
                _session.Clock.Tick + AutosaveTicks(_options.AutosaveInterval));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            nextTick += (long)tickDuration;
            var tick = _session.Tick();
            if (tick.PublishedSnapshot is { } snapshot)
            {
                BroadcastSnapshot(snapshot);
            }
            if (_checkpointWriter is not null &&
                _session.Clock.Tick >= _nextAutosaveTick)
            {
                QueueCheckpoint();
                _nextAutosaveTick = checked(
                    _session.Clock.Tick +
                    AutosaveTicks(_options.AutosaveInterval));
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

        if (_checkpointWriter is not null) QueueCheckpoint();
    }

    private void QueueCheckpoint()
    {
        var revision = checked(++_checkpointRevision);
        var durable = ServerCheckpointMapper.ToDurable(
            _session.CaptureCheckpoint(),
            _options,
            revision);
        if (!_checkpointWriter!.TryQueue(durable))
            throw new InvalidOperationException(
                "The newest authoritative checkpoint was not accepted.");
    }

    private void RefreshWorldBootstrap()
    {
        var checkpoint = _session.CaptureCheckpoint().World;
        var chunks = checkpoint.ChunkRevisions.ToDictionary(
            static value => value.Chunk,
            static value => value.Revision);
        var chunkRevisions = checkpoint.ChunkRevisions.Select(value =>
            new WorldChunkRevisionState(
                value.Chunk.X,
                value.Chunk.Y,
                checked((short)value.Chunk.WorldLevel),
                value.Revision)).ToArray();
        var baselines = checkpoint.Objects.Select(value =>
            new WorldObjectBaseline(
                value.Object,
                chunks[value.Object.Chunk])).ToArray();
        lock (_worldBootstrapSync)
            Volatile.Write(ref _worldBootstrap, new WorldBootstrapState(
                Array.AsReadOnly(baselines),
                Array.AsReadOnly(chunkRevisions)));
    }

    private void ApplyWorldTransactionToBootstrap(
        WorldTransactionResult transaction)
    {
        lock (_worldBootstrapSync)
        {
            var current = _worldBootstrap;
            var chunks = current.ChunkRevisions.ToDictionary(
                static value => new WorldChunkKey(
                    value.ChunkX,
                    value.ChunkY,
                    value.WorldLevel),
                static value => value.Revision);
            foreach (var delta in transaction.ChunkDeltas)
            {
                chunks.TryGetValue(delta.Chunk, out var known);
                if (known != delta.PreviousRevision &&
                    known != delta.CurrentRevision)
                    throw new InvalidOperationException(
                        "The public world bootstrap lost its chunk revision chain.");
                chunks[delta.Chunk] = delta.CurrentRevision;
            }

            var objects = current.Objects.ToDictionary(
                static value => value.Object.ObjectId);
            foreach (var delta in transaction.ObjectDeltas)
            {
                if (delta.Kind == WorldObjectChangeKind.Removed)
                {
                    objects.Remove(delta.ObjectId);
                    continue;
                }
                if (delta.Object is not { } value ||
                    !chunks.TryGetValue(delta.Chunk, out var chunkRevision))
                    throw new InvalidOperationException(
                        "A committed object has no authoritative chunk revision.");
                objects[delta.ObjectId] = new WorldObjectBaseline(
                    value, chunkRevision);
            }

            var nextChunks = chunks
                .OrderBy(static value => value.Key.WorldLevel)
                .ThenBy(static value => value.Key.X)
                .ThenBy(static value => value.Key.Y)
                .Select(static value => new WorldChunkRevisionState(
                    value.Key.X,
                    value.Key.Y,
                    checked((short)value.Key.WorldLevel),
                    value.Value))
                .ToArray();
            var nextObjects = objects.Values
                .OrderBy(static value => value.Object.ObjectId)
                .ToArray();
            Volatile.Write(ref _worldBootstrap, new WorldBootstrapState(
                Array.AsReadOnly(nextObjects),
                Array.AsReadOnly(nextChunks)));
            AfterWorldBootstrapUpdatedForTest?.Invoke();
        }
    }

    private void RefreshResourceBootstrap()
    {
        var checkpoint = _session.CaptureCheckpoint().Resources ??
                         AuthoritativeResourceTransactionsCheckpoint.Empty;
        Volatile.Write(ref _resourceBootstrap, new ResourceBootstrapState(
            Array.AsReadOnly(checkpoint.Chunks.ToArray())));
    }

    private void ApplyResourceTransactionToBootstrap(
        ResourceTransactionResult transaction)
    {
        if (transaction.NodeDelta is not { } node ||
            transaction.ChunkDelta is not { } revision)
            return;
        lock (_resourceBootstrapSync)
        {
            var chunks = _resourceBootstrap.Chunks.ToDictionary(
                static value => value.Chunk);
            chunks.TryGetValue(revision.Chunk, out var existing);
            var nodes = existing?.Nodes.ToDictionary(
                static value => value.Id) ?? [];
            nodes[node.Current.Id] = node.Current;
            chunks[revision.Chunk] = new ResourceChunkSparseState(
                revision.Chunk,
                revision.CurrentRevision,
                nodes.Values.OrderBy(static value => value.Id.Value)
                    .ToImmutableArray());
            Volatile.Write(ref _resourceBootstrap,
                new ResourceBootstrapState(Array.AsReadOnly(
                    chunks.Values
                        .OrderBy(static value => value.Chunk.WorldLevel)
                        .ThenBy(static value => value.Chunk.X)
                        .ThenBy(static value => value.Chunk.Y)
                        .ToArray())));
        }
    }

    private void RefreshBoatBootstrap()
    {
        var boats = _session.CaptureBoats().ToArray();
        lock (_boatBootstrapSync)
            Volatile.Write(ref _boatBootstrap, new BoatBootstrapState(
                Array.AsReadOnly(boats)));
    }

    private void ApplyBoatStateToBootstrap(BoatStateDelta delta)
    {
        lock (_boatBootstrapSync)
        {
            var boats = _boatBootstrap.Boats.ToDictionary(
                static boat => boat.BoatId);
            var id = delta.Current?.BoatId ?? delta.Previous?.BoatId ??
                throw new InvalidOperationException(
                    "A committed boat delta omitted both states.");
            if (delta.Current is { } current)
                boats[id] = current;
            else
                boats.Remove(id);
            Volatile.Write(ref _boatBootstrap, new BoatBootstrapState(
                Array.AsReadOnly(boats.Values
                    .OrderBy(static boat => boat.BoatId.Value)
                    .ToArray())));
        }
    }

    private void BroadcastBoatProvisioned(AuthoritativeBoatSnapshot boat)
    {
        var tick = checked((ulong)CurrentTick);
        var delta = new BoatStateDelta(BoatChangeKind.Added, null, boat);
        Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(
                sequence, tick, delta)!);
    }

    private static long AutosaveTicks(TimeSpan interval) => checked((long)
        Math.Ceiling(interval.TotalSeconds * SimulationTiming.TicksPerSecond));

    private void BroadcastSnapshot(SessionSnapshot snapshot)
    {
        var actorEntities = snapshot.Actors
            .Select(actor => new EntitySnapshot(
                StableNetworkId(actor.ActorId.Value),
                NetworkEntityKind.Player,
                0,
                checked((short)actor.WorldLevel),
                actor.Position.X,
                actor.Position.Y,
                actor.Velocity.X,
                actor.Velocity.Y,
                (!actor.Connected ? NetworkEntityState.Hidden : NetworkEntityState.None) |
                (actor.Velocity != Vector2.Zero ? NetworkEntityState.Moving : NetworkEntityState.None),
                checked((uint)Math.Min(snapshot.Sequence, uint.MaxValue))))
            .ToArray();
        var boatEntities = (snapshot.Boats.IsDefault
                ? Enumerable.Empty<AuthoritativeBoatSnapshot>()
                : snapshot.Boats)
            .Select(boat => new EntitySnapshot(
                boat.NetworkEntityId,
                NetworkEntityKind.Boat,
                0,
                checked((short)boat.WorldLevel),
                boat.Position.X,
                boat.Position.Y,
                boat.Velocity.X,
                boat.Velocity.Y,
                boat.Destination is not null
                    ? NetworkEntityState.Moving
                    : NetworkEntityState.None,
                boat.Revision))
            .ToArray();
        var entities = actorEntities.Concat(boatEntities).ToArray();
        if (entities.Length > ProtocolLimits.MaxSnapshotEntities)
            throw new InvalidOperationException(
                "The authoritative snapshot exceeds its protocol entity bound.");
        var snapshotSequence = unchecked((ushort)snapshot.Sequence);

        foreach (var connection in _clients.Values)
        {
            if (!connection.Authenticated)
            {
                continue;
            }

            var reliableRecovery = connection.UdpSnapshotsEnabled &&
                snapshot.Clock.Tick % SimulationTiming.TicksPerSecond == 0;
            if (connection.UdpSnapshotsEnabled && !reliableRecovery)
            {
                SendUdpSnapshot(connection, snapshot, entities);
            }

            // Reliable keyframes remain a recovery path when UDP packets are
            // lost, reordered, filtered, or a client cannot use UDP at all.
            if (!connection.UdpSnapshotsEnabled || reliableRecovery)
            {
                if (!connection.TryQueueSequenced(sequence => new EntitySnapshotMessage(
                        sequence,
                        checked((ulong)snapshot.Clock.Tick),
                        new SnapshotMetadata(
                            connection.DatagramToken,
                            snapshotSequence,
                            0,
                            0,
                            checked((ulong)snapshot.Clock.Tick),
                            0,
                            SnapshotFlags.Keyframe),
                        entities)))
                {
                    connection.Stop();
                }
            }
        }
    }

    private void SendUdpSnapshot(
        ClientConnection connection,
        SessionSnapshot snapshot,
        EntitySnapshot[] entities)
    {
        var endpoint = connection.SnapshotEndpoint;
        if (endpoint is null)
        {
            return;
        }

        var selected = SelectUdpEntities(connection, snapshot, entities);
        if (selected.IsEmpty)
            return;
        var metadata = new SnapshotMetadata(
            connection.DatagramToken,
            connection.NextSnapshotSequence(),
            0,
            0,
            checked((ulong)snapshot.Clock.Tick),
            0,
            selected.Length == entities.Length
                ? SnapshotFlags.Keyframe
                : SnapshotFlags.Delta);
        Span<byte> sendBuffer = stackalloc byte[ProtocolConstants.MaxUdpDatagramBytes];
        if (!UdpSnapshotCodec.TryEncode(
                metadata,
                selected,
                sendBuffer,
                out var bytesWritten))
        {
            return;
        }

        try
        {
            _snapshotSocket.SendTo(
                sendBuffer[..bytesWritten],
                SocketFlags.None,
                endpoint);
        }
        catch (SocketException) when (!_lifetime.IsCancellationRequested)
        {
            // UDP is opportunistic. The reliable keyframe will recover and
            // the next publication will try again without stopping authority.
        }
    }

    private static ReadOnlySpan<EntitySnapshot> SelectUdpEntities(
        ClientConnection connection,
        SessionSnapshot snapshot,
        EntitySnapshot[] entities)
    {
        if (entities.Length <= UdpSnapshotCodec.MaxEntitiesPerDatagram)
            return entities;

        if (!connection.DeltaSnapshotsEnabled)
            return ReadOnlySpan<EntitySnapshot>.Empty;

        var result = connection.SnapshotSelectionBuffer;
        var own = Array.FindIndex(
            entities,
            entity => entity.EntityId == connection.PlayerEntityId);
        var count = 0;
        if (own >= 0)
            result[count++] = entities[own];

        var ownActor = snapshot.Actors.FirstOrDefault(actor =>
            StableNetworkId(actor.ActorId.Value) == connection.PlayerEntityId);
        var occupiedBoatId = ownActor.BoardedBoatId is { } boarded
            ? snapshot.Boats.FirstOrDefault(boat => boat.BoatId == boarded)
                ?.NetworkEntityId ?? 0
            : 0;
        if (occupiedBoatId != 0 && count < result.Length)
        {
            var occupied = Array.FindIndex(
                entities, entity => entity.EntityId == occupiedBoatId);
            if (occupied >= 0) result[count++] = entities[occupied];
        }

        // Nearby boats are more relevant than round-robin overflow: their
        // collision and boarding affordances must remain spatially coherent.
        if (own >= 0)
        {
            foreach (var index in Enumerable.Range(0, entities.Length)
                         .Where(index =>
                             index != own &&
                             entities[index].EntityId != occupiedBoatId &&
                             entities[index].EntityKind ==
                                 NetworkEntityKind.Boat)
                         .OrderBy(index => DistanceSquared(
                             entities[own], entities[index])))
            {
                if (count >= result.Length) break;
                result[count++] = entities[index];
            }
        }

        // Rotate the overflow window every publication. This keeps the local
        // actor present while ensuring every other entity receives fresh UDP
        // state even in worlds larger than one 1200-byte packet.
        var offset = connection.NextInterestOffset(
            entities.Length,
            UdpSnapshotCodec.MaxEntitiesPerDatagram - count);
        for (var scanned = 0;
             scanned < entities.Length && count < result.Length;
             scanned++)
        {
            var index = (offset + scanned) % entities.Length;
            if (index == own || entities[index].EntityId == occupiedBoatId ||
                result[..count].Contains(entities[index]))
                continue;
            result[count++] = entities[index];
        }

        return result[..count];
    }

    private static float DistanceSquared(
        EntitySnapshot left,
        EntitySnapshot right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return x * x + y * y;
    }

    private void Broadcast(
        Func<ClientConnection, ulong, IProtocolMessage> createMessage)
    {
        lock (_publicReplicationSync)
        {
            foreach (var connection in _clients.Values)
            {
                if (!connection.Authenticated) continue;
                if (!connection.TryQueuePublicSequenced(sequence =>
                        createMessage(connection, sequence)))
                {
                    connection.Stop();
                }
            }
        }
    }

    private static bool IsValidName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 40 &&
        value.All(character => !char.IsControl(character) && !char.IsSurrogate(character));

    internal static ulong StableNetworkId(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        var result = (BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])) &
            0x7fff_ffff_ffff_ffffUL;
        return result == 0 ? 1 : result;
    }

    private static ulong CreateDatagramToken()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (BinaryPrimitives.ReadUInt64LittleEndian(bytes) == 0);

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static IPAddress NormalizeAddress(
        IPAddress address,
        AddressFamily socketFamily)
    {
        if (socketFamily == AddressFamily.InterNetwork)
        {
            return address.AddressFamily == AddressFamily.InterNetwork
                ? address
                : address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? address
            : address.MapToIPv6();
    }
}

internal readonly record struct WorldObjectBaseline(
    AuthoritativeWorldObjectSnapshot Object,
    uint ChunkRevision);

internal sealed record WorldBootstrapState(
    IReadOnlyList<WorldObjectBaseline> Objects,
    IReadOnlyList<WorldChunkRevisionState> ChunkRevisions)
{
    public static WorldBootstrapState Empty { get; } = new(
        Array.Empty<WorldObjectBaseline>(),
        Array.Empty<WorldChunkRevisionState>());
}

internal sealed record ResourceBootstrapState(
    IReadOnlyList<ResourceChunkSparseState> Chunks)
{
    public static ResourceBootstrapState Empty { get; } = new(
        Array.Empty<ResourceChunkSparseState>());
}

internal sealed record BoatBootstrapState(
    IReadOnlyList<AuthoritativeBoatSnapshot> Boats)
{
    public static BoatBootstrapState Empty { get; } = new(
        Array.Empty<AuthoritativeBoatSnapshot>());
}

internal readonly record struct AuthenticatedPlayer(
    Guid ClientId,
    PlayerIdentity Identity,
    string DisplayName,
    string ReconnectToken,
    ulong NextCommandSequence,
    bool Reconnected,
    PlayerGameplaySnapshot Gameplay,
    Vector2 Position,
    int WorldLevel);

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
