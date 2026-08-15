using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using IslandRpg.Navigation;
using IslandRpg.Caves;
using IslandRpg.Boats;
using IslandRpg.Fishing;
using IslandRpg.Gameplay;
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
    private readonly IWorldNavigationQuery _navigation;
    private readonly SemaphoreSlim _clientSlots;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>>
        _connectionObservers = [];
    private readonly ConcurrentDictionary<Guid, byte> _activeClientIds = [];
    private readonly ConcurrentDictionary<Guid, ConnectedPlayerPresence>
        _connectedPlayers = [];
    private readonly object _publicReplicationSync = new();
    private readonly OrderedPublications _publications = new();
    private readonly ConcurrentDictionary<CommandPublicationKey,
        OrderedPublications.Ticket>
        _commandPublications = [];
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
    private readonly object _spawnSync = new();
    private Vector2? _cachedSpawn;
    private readonly object _resourceBootstrapSync = new();
    private ResourceBootstrapState _resourceBootstrap =
        ResourceBootstrapState.Empty;
    private readonly object _boatBootstrapSync = new();
    private BoatBootstrapState _boatBootstrap = BoatBootstrapState.Empty;
    private readonly object _enemyBootstrapSync = new();
    private EnemyBootstrapState _enemyBootstrap = EnemyBootstrapState.Empty;
    private readonly ServerCheckpointStore? _checkpointStore;
    private readonly ServerCheckpointWriter? _checkpointWriter;
    private readonly IDisposable? _worldLease;
    private readonly ServerCheckpointLoadResult? _checkpointToRestore;
    private long _checkpointRevision;
    private long _nextAutosaveTick;
    private int _disposed;
    private LanDiscoveryAdvertiser? _lanDiscovery;
    private readonly List<PendingCombatPublication> _pendingCombatPublications = [];
    private List<EnemyStateDelta>? _collectingEnemyDeltas;
    private List<CombatEventSnapshot>? _collectingCombatEvents;
    private List<WorldTransactionResult>? _collectingCombatWorldTransactions;
    private List<BoatStateDelta>? _collectingCombatBoatDeltas;
    private OrderedPublications.Ticket? _collectingCombatPublication;
    private bool _seedingEnemyBootstrap;

    /// <summary>
    /// Deterministic test seam inside the activation/broadcast barrier and
    /// immediately before baseline capture. Production leaves this null.
    /// </summary>
    internal Action<ClientConnection>? DuringBootstrapActivation { get; set; }

    internal Action? AfterWorldBootstrapUpdatedForTest { get; set; }

    /// <summary>
    /// Deterministic test seam immediately before one reliable message is
    /// written. Production leaves this null.
    /// </summary>
    internal Func<ClientConnection, IProtocolMessage, CancellationToken,
        ValueTask>? BeforeOutboundWriteForTest { get; set; }

    /// <summary>
    /// Bounds how long one reliable publication may retain its queued state,
    /// including time spent behind earlier publications. The internal setter
    /// is a deterministic transport-stall seam for integration checks.
    /// </summary>
    internal TimeSpan OutboundPublicationWriteTimeout { get; set; } =
        TimeSpan.FromSeconds(30);

    public DedicatedServer(ServerOptions options)
        : this(options, null)
    {
    }

    internal DedicatedServer(
        ServerOptions options,
        ISessionIdentitySource? identitySource)
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
        var navigation = new ProceduralWorldNavigationQuery(options.WorldSeed);
        _navigation = navigation;
        var resourceTransactions = new AuthoritativeResourceTransactions(
            options.WorldSeed,
            resourceCatalog);
        var worldTransactions = new AuthoritativeWorldTransactions(
            caves: new ProceduralCaveExcavationEnvironment(
                options.WorldSeed),
            worldSeed: options.WorldSeed);
        var boatTransactions = new AuthoritativeBoatTransactions(
            new ProceduralBoatNavigationQuery(options.WorldSeed));
        var combatOptions = options.CombatOptions ??
            new AuthoritativeCombatOptions
            {
                RespawnPosition = options.StartingPosition ??
                    _cachedSpawn ?? Vector2.Zero
            };
        var combatTransactions = new AuthoritativeCombatTransactions(
            options.WorldSeed,
            navigation,
            combatOptions);
        _session = new AuthoritativeWorldSession(
            SimulationLimits.Default with
            {
                MaximumActors = NetworkPopulationLimits.MaximumActors,
                MaximumConnectedActors = options.MaximumClients
            },
            sessionId: new SessionId(options.WorldId),
            navigation: navigation,
            identitySource: identitySource,
            worldTransactions: worldTransactions,
            resourceTransactions: resourceTransactions,
            boatTransactions: boatTransactions,
            combatTransactions: combatTransactions);
        _session.WorldTransactionCommitted += QueueOrApplyWorldTransaction;
        _session.ResourceTransactionCommitted +=
            ApplyResourceTransactionToBootstrap;
        _session.CookingCompleted += BroadcastCookingCompletion;
        _session.BoatStateCommitted += ApplyBoatStateToBootstrap;
        _session.BoatAutonomousStateCommitted +=
            BroadcastBoatAutonomousState;
        _session.EnemyStateCommitted += QueueAutonomousEnemyDelta;
        _session.CombatEventCommitted += QueueAutonomousCombatEvent;
        _session.GameplayIntentCommitted += ReserveCommandPublication;
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
        if (_options.StartingPosition is { } configured)
            _cachedSpawn = configured;
        _simulationThread = new Thread(SimulationLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "IslandRpg.Authority"
        };
    }

    internal Vector2 ResolveSpawn(CancellationToken cancellationToken = default)
    {
        lock (_spawnSync)
        {
            if (_cachedSpawn is { } cached)
                return cached;
            var spawn = _options.StartingPosition ??
                BoatTravelRules.FindPlayableLandSpawn(
                    _options.WorldSeed,
                    cancellationToken);
            _cachedSpawn = spawn;
            return spawn;
        }
    }

    internal long CurrentTick => _session.LatestSnapshot.Clock.Tick;

    internal AuthoritativeWorldSession SessionForTest => _session;

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
            _lanDiscovery = new LanDiscoveryAdvertiser(() =>
                new LanDiscoveryBeacon(
                    checked((ushort)boundEndpoint.Port),
                    _options.WorldId,
                    _options.WorldSeed,
                    _options.IslandStart,
                    _connectedPlayers.Count,
                    _options.MaximumClients,
                    _options.IslandStart ? "Shore world" : "Open world"));
            _simulationThread.Start();
            simulationStarted = true;
            Console.WriteLine(
                $"Island RPG server listening on {boundEndpoint} " +
                $"(world {_options.WorldId:N}, seed {_options.WorldSeed}, max {_options.MaximumClients}).");
            Console.WriteLine(
                $"Join this machine at 127.0.0.1:{boundEndpoint.Port}.");
            if (TryGuessLanAddress(out var lan) && lan != "127.0.0.1")
                Console.WriteLine(
                    $"Join from the LAN at {lan}:{boundEndpoint.Port}.");
            Console.WriteLine(
                "Do not join 0.0.0.0 — that is the listen address, not a client target.");

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
                var observed = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_connectionObservers.TryAdd(
                        connection.Id.Value, observed))
                    throw new InvalidOperationException(
                        "A connection observer identity was reused.");
                _ = ObserveConnectionAsync(connection, observed);
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

                await StopLanDiscoveryAsync().ConfigureAwait(false);
                _listener.Stop();
                foreach (var connection in _clients.Values)
                    connection.Stop();

                // Wait for the fault-isolating observer rather than the raw
                // connection task. A peer can legitimately reset its socket
                // during shutdown; the observer absorbs that transport fault
                // and completes authoritative disconnect cleanup before the
                // final checkpoint is flushed.
                await Task.WhenAll(_connectionObservers.Values.Select(
                        static value => value.Task))
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

    private static bool TryGuessLanAddress(out string address)
    {
        address = "127.0.0.1";
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is
                        NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily ==
                            AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        address = unicast.Address.ToString();
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
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
                    var code = result.Status switch
                    {
                        ReconnectStatus.SessionFull =>
                            HandshakeRejectionCode.ServerFull,
                        ReconnectStatus.ExpiredPlayer =>
                            HandshakeRejectionCode.ReconnectExpired,
                        _ => HandshakeRejectionCode.InvalidName
                    };
                    throw new HandshakeFailure(
                        code,
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
                    result.WorldLevel,
                    result.Social);
                connection.ConfigureSnapshotTransport(
                    snapshotEndpoint,
                    datagramToken,
                    ActorNetworkEntityIdentity.Derive(result.Identity.ActorId),
                    request.Capabilities.HasFlag(ClientCapabilities.DeltaSnapshots));
                if (result.EvictedConnectionId.Value != Guid.Empty &&
                    _clients.TryGetValue(
                        result.EvictedConnectionId.Value, out var stale))
                {
                    stale.Stop();
                }
                return authenticated;
            }

            var spawn = ResolveSpawn(_lifetime.Token);
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
                join.WorldLevel,
                join.Social);
            connection.ConfigureSnapshotTransport(
                snapshotEndpoint,
                datagramToken,
                ActorNetworkEntityIdentity.Derive(join.Identity.ActorId),
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
            ActorNetworkEntityIdentity.Derive(player.Identity.ActorId),
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
        ulong sequence,
        AuthenticatedPlayer player)
    {
        return ToPlayerStateMessage(
            sequence,
            checked((ulong)CurrentTick),
            player,
            player.Gameplay,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            baselineActorRevision: 0,
            baselineInventoryRevision: 0);
    }

    internal SocialStateMessage CreateSocialStateBaseline(
        ulong sequence,
        AuthenticatedPlayer player) =>
        ToSocialStateMessage(
            sequence,
            checked((ulong)CurrentTick),
            player.Identity.PlayerId,
            player.Social);

    internal void PublishSocialFromIntent(IntentResult result) =>
        QueueSocialPublications(result, checked((ulong)CurrentTick));

    private void QueueSocialPublications(IntentResult result, ulong tick) =>
        QueueSocialPublications(result.Social, tick);

    private void QueueSocialPublications(
        ImmutableArray<PlayerSocialPublication> publications,
        ulong tick)
    {
        if (publications.IsDefaultOrEmpty) return;
        foreach (var publication in publications)
        {
            foreach (var connection in _clients.Values)
            {
                if (!connection.Authenticated ||
                    connection.PlayerId != publication.PlayerId.Value)
                    continue;
                if (!connection.TryQueueSequenced(sequence =>
                        ToSocialStateMessage(
                            sequence, tick, publication.PlayerId, publication.Social)))
                    connection.Stop();
            }
        }
    }

    private static SocialStateMessage ToSocialStateMessage(
        ulong sequence,
        ulong tick,
        PlayerId playerId,
        PlayerSocialSnapshot social) =>
        new(
            sequence,
            tick,
            playerId.Value,
            ToGuidList(social.Friends),
            ToGuidList(social.Ignored),
            social.GuildId ?? Guid.Empty,
            social.GuildName ?? "",
            social.FollowTarget?.Value ?? Guid.Empty,
            social.OpenTradeId ?? Guid.Empty,
            social.TradePartner?.Value ?? Guid.Empty,
            social.TradeAccepted,
            social.TradeIncoming,
            ToSlotList(social.OwnOfferSlots),
            ToSlotList(social.PartnerOfferSlots),
            social.OwnConfirmed,
            social.PartnerConfirmed);

    private static Guid[] ToGuidList(ImmutableArray<PlayerId> values)
    {
        if (values.IsDefaultOrEmpty) return [];
        var result = new Guid[values.Length];
        for (var index = 0; index < values.Length; index++)
            result[index] = values[index].Value;
        return result;
    }

    private static int[] ToSlotList(ImmutableArray<int> values) =>
        values.IsDefaultOrEmpty ? [] : values.ToArray();

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
            var enemies = Volatile.Read(ref _enemyBootstrap);
            var tick = checked((ulong)CurrentTick);
            var messageCount = CountPublicBaselineMessages(
                world, resources, boats, enemies);
            if (!connection.TryQueuePublicBootstrapAndActivate(
                world.ChunkRevisions,
                resources.Chunks,
                boats.Boats,
                enemies.Enemies,
                messageCount,
                firstSequence => EnumeratePublicBaselines(
                    firstSequence,
                    tick,
                    world,
                    resources,
                    boats,
                    enemies)))
            {
                connection.Stop();
                return false;
            }
            return true;
        }
    }

    private static int CountPublicBaselineMessages(
        WorldBootstrapState world,
        ResourceBootstrapState resources,
        BoatBootstrapState boats,
        EnemyBootstrapState enemies)
    {
        _ = boats;
        _ = enemies;
        var chunkBatchCount = checked(
            (world.ChunkRevisions.Count +
             ProtocolLimits.MaxWorldChunkRevisionsPerBatch - 1) /
            ProtocolLimits.MaxWorldChunkRevisionsPerBatch);
        var pickedBatch = world.PickedProceduralGroundObjects.Count > 0
            ? 1
            : 0;
        return checked(
            chunkBatchCount +
            world.Objects.Count +
            resources.Chunks.Count +
            pickedBatch +
            2); // Complete boat and enemy baselines are always present.
    }

    private static IEnumerable<IProtocolMessage> EnumeratePublicBaselines(
        ulong firstSequence,
        ulong tick,
        WorldBootstrapState world,
        ResourceBootstrapState resources,
        BoatBootstrapState boats,
        EnemyBootstrapState enemies)
    {
        var sequence = firstSequence;
        var chunkRevisions = world.ChunkRevisions;
        for (var offset = 0; offset < chunkRevisions.Count;
             offset += ProtocolLimits.MaxWorldChunkRevisionsPerBatch)
        {
            var count = Math.Min(
                ProtocolLimits.MaxWorldChunkRevisionsPerBatch,
                chunkRevisions.Count - offset);
            var batch = new WorldChunkRevisionState[count];
            for (var index = 0; index < count; index++)
                batch[index] = chunkRevisions[offset + index];
            yield return new WorldChunkRevisionBatchMessage(
                sequence,
                tick,
                batch);
            sequence = checked(sequence + 1);
        }

        if (world.PickedProceduralGroundObjects.Count > 0)
        {
            yield return new PickedProceduralGroundObjectsMessage(
                sequence,
                tick,
                world.PickedProceduralGroundObjects);
            sequence = checked(sequence + 1);
        }

        // Chunk revisions precede object baselines so an object-free chunk is
        // still actionable and every following object can reference a known
        // authoritative chunk revision.
        foreach (var value in world.Objects)
        {
            yield return WorldActionProtocolAdapter.ToPublicWorldState(
                sequence,
                tick,
                value.Object,
                value.ChunkRevision);
            sequence = checked(sequence + 1);
        }

        foreach (var chunk in resources.Chunks)
        {
            yield return ResourceActionProtocolAdapter.ToBaseline(
                sequence,
                tick,
                chunk);
            sequence = checked(sequence + 1);
        }

        yield return BoatActionProtocolAdapter.ToBaseline(
            sequence,
            tick,
            boats.Boats);
        sequence = checked(sequence + 1);
        yield return CombatActionProtocolAdapter.ToBaseline(
            sequence,
            tick,
            enemies.Enemies);
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
            PresentSkillCommandMessage present =>
                new PresentSkillIntent(
                    (EntityAction)present.Action,
                    present.DurationSeconds),
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
                        : action.Payload is CombatActionPayload combat
                            ? CombatActionProtocolAdapter.ToIntent(action, combat)
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
        if (command.Payload is CombatActionPayload combatAction)
        {
            QueueCombatActionOutcome(
                connection, player, command, combatAction, result, tick);
            return;
        }
        if (result.WorldTransaction is { } transaction)
        {
            QueueWorldActionOutcome(
                connection, player, command, result, transaction, tick);
            return;
        }

        // Send a complete private state after every accepted gameplay
        // transaction before its receipt. The connection projects only
        // sections newer than its queued high-water.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
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
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, null);
            return;
        }
        QueueSocialPublications(result, tick);
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
            connection.Stop();
    }

    private void ReserveCommandPublication(
        ActorCommand command,
        IntentResult result)
    {
        if (result.Duplicate || result.CommandId == Guid.Empty ||
            !HasPublicMutation(result))
            return;
        if (result.CombatTransaction?.EnemyDelta is { } enemy)
            UpdateEnemyBootstrap(enemy);
        var key = new CommandPublicationKey(
            command.PlayerId.Value, result.CommandId);
        if (!_commandPublications.TryAdd(key, _publications.Reserve()))
            throw new InvalidOperationException(
                "A command registered more than one public publication ticket.");
    }

    private static bool HasPublicMutation(IntentResult result) =>
        result.WorldTransaction is { } world &&
            WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(1, 0, world) is not null ||
        result.ResourceTransaction is { } resource &&
            ResourceActionProtocolAdapter.ToPublicDelta(1, 0, resource) is not null ||
        result.BoatTransaction?.BoatDelta is { } boat &&
            BoatActionProtocolAdapter.ToPublicDelta(1, 0, boat) is not null ||
        result.BoatDelta is { } detached &&
            BoatActionProtocolAdapter.ToPublicDelta(1, 0, detached) is not null ||
        result.CombatTransaction?.EnemyDelta is { } enemy &&
            CombatActionProtocolAdapter.ToPublicDelta(1, 0, enemy) is not null ||
        result.CombatTransaction?.Event is not null;

    private Action? CreateCommandPublication(IntentResult result, ulong tick)
    {
        if (result.Duplicate || !HasPublicMutation(result)) return null;
        return () =>
        {
            if (result.WorldTransaction is { } world &&
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    1, tick, world) is not null)
                Broadcast((_, sequence) =>
                    WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                        sequence, tick, world)!);
            if (result.ResourceTransaction is { } resource &&
                ResourceActionProtocolAdapter.ToPublicDelta(
                    1, tick, resource) is not null)
                Broadcast((_, sequence) =>
                    ResourceActionProtocolAdapter.ToPublicDelta(
                        sequence, tick, resource)!);
            if (result.BoatTransaction?.BoatDelta is { } boat &&
                BoatActionProtocolAdapter.ToPublicDelta(1, tick, boat) is not null)
                Broadcast((_, sequence) =>
                    BoatActionProtocolAdapter.ToPublicDelta(
                        sequence, tick, boat)!);
            if (result.BoatDelta is { } detached &&
                BoatActionProtocolAdapter.ToPublicDelta(
                    1, tick, detached) is not null)
                Broadcast((_, sequence) =>
                    BoatActionProtocolAdapter.ToPublicDelta(
                        sequence, tick, detached)!);
            if (result.CombatTransaction?.EnemyDelta is { } enemy &&
                CombatActionProtocolAdapter.ToPublicDelta(
                    1, tick, enemy) is not null)
                BroadcastEnemyDelta(enemy, tick);
            if (result.CombatTransaction?.Event is { } combatEvent)
                BroadcastCombatEvent(combatEvent, _session.LatestSnapshot);
        };
    }

    private void ReleaseCommandPublication(
        Guid playerId,
        Guid commandId,
        Action? publication)
    {
        if (!_commandPublications.TryRemove(
                new CommandPublicationKey(playerId, commandId),
                out var ticket))
        {
            if (publication is not null)
                throw new InvalidOperationException(
                    "A public command mutation had no ordered publication ticket.");
            return;
        }
        _publications.Release(ticket, publication);
    }

    private void QueueResourceActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        ResourceActionPayload action,
        IntentResult result,
        ulong tick)
    {
        var publication = CreateCommandPublication(result, tick);
        // Accepted gameplay state must precede the presentation receipt so a
        // continuous authored action reads the new optimistic revisions.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
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
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                ResourceActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        ReleaseCommandPublication(
            player.Identity.PlayerId.Value, command.CommandId, publication);
    }

    private void QueueBoatActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        BoatActionPayload action,
        IntentResult result,
        ulong tick)
    {
        var publication = CreateCommandPublication(result, tick);
        // Private gameplay state and the command receipt precede the public
        // semantic delta. This prevents the requesting client from observing
        // a boat revision it cannot yet correlate to its command.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
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
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                BoatActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        ReleaseCommandPublication(
            player.Identity.PlayerId.Value, command.CommandId, publication);
    }

    private void QueueCaveActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        CaveActionPayload action,
        IntentResult result,
        ulong tick)
    {
        var publication = CreateCommandPublication(result, tick);
        // State precedes the receipt so follow-up commands always see the
        // authoritative post-action revisions and digging experience.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
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
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                CaveActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        ReleaseCommandPublication(
            player.Identity.PlayerId.Value, command.CommandId, publication);
    }

    private void QueueCombatActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        CombatActionPayload action,
        IntentResult result,
        ulong tick)
    {
        var publication = CreateCommandPublication(result, tick);
        // The requester receives its new health/progression/target revision
        // and then its receipt before any semantic enemy mutation is public.
        if (result.Accepted && !result.Duplicate &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
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
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        if (!connection.TryQueueSequenced(sequence =>
                CombatActionProtocolAdapter.ToPrivateResult(
                    sequence, tick, command, action, result)))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }
        ReleaseCommandPublication(
            player.Identity.PlayerId.Value, command.CommandId, publication);
    }

    private void CommitAndBroadcastEnemyDelta(
        EnemyStateDelta delta,
        ulong tick)
    {
        lock (_publicReplicationSync)
        {
            UpdateEnemyBootstrap(delta);
            foreach (var observer in _clients.Values)
            {
                if (!observer.Authenticated) continue;
                if (!observer.TryQueuePublicSequenced(sequence =>
                        CombatActionProtocolAdapter.ToPublicDelta(
                            sequence, tick, delta)!))
                    observer.Stop();
            }
        }
    }

    private void BroadcastEnemyDelta(EnemyStateDelta delta, ulong tick)
    {
        Broadcast((_, sequence) =>
            CombatActionProtocolAdapter.ToPublicDelta(sequence, tick, delta)!);
    }

    private void UpdateEnemyBootstrap(EnemyStateDelta delta)
    {
        lock (_enemyBootstrapSync)
        {
            var enemies = _enemyBootstrap.Enemies.ToDictionary(
                static enemy => enemy.EnemyId);
            var id = delta.Current?.EnemyId ?? delta.Previous?.EnemyId ??
                throw new InvalidOperationException(
                    "A committed enemy delta omitted both states.");
            if (delta.Current is { } current)
                enemies[id] = current;
            else
                enemies.Remove(id);
            Volatile.Write(ref _enemyBootstrap, new EnemyBootstrapState(
                Array.AsReadOnly(enemies.Values
                    .OrderBy(static enemy => enemy.EnemyId.Value)
                    .ToArray())));
        }
    }

    private void QueueWorldActionOutcome(
        ClientConnection connection,
        AuthenticatedPlayer player,
        ActionCommandMessage command,
        IntentResult result,
        WorldTransactionResult transaction,
        ulong tick)
    {
        var publication = CreateCommandPublication(result, tick);
        var privatePlayerState = WorldActionProtocolAdapter.ToPrivatePlayerState(
                1,
                tick,
                player.Identity.PlayerId.Value,
                ActorNetworkEntityIdentity.Derive(player.Identity.ActorId),
                command,
                transaction,
                forceBaseline: false);
        if (privatePlayerState is not null &&
            !connection.TryQueuePrivateStateSequenced(sequence =>
                privatePlayerState with { Sequence = sequence }))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }

        if (WorldActionProtocolAdapter.ToPrivateContainerBaseline(
                1, tick, command, transaction) is not null &&
            !connection.TryQueueSequenced(sequence =>
                WorldActionProtocolAdapter.ToPrivateContainerBaseline(
                    sequence, tick, command, transaction)!))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }

        if (!connection.TryQueueSequenced(sequence =>
                WorldActionProtocolAdapter.ToActionResult(
                    sequence, tick, transaction)))
        {
            connection.Stop();
            ReleaseCommandPublication(
                player.Identity.PlayerId.Value, command.CommandId, publication);
            return;
        }

        // Duplicate receipts acknowledge the requester but never publish the
        // same public mutation twice. The original receipt already advanced
        // every observer's chunk/object revisions.
        ReleaseCommandPublication(
            player.Identity.PlayerId.Value, command.CommandId, publication);
    }

    internal void BroadcastPlayerJoined(
        Guid playerId,
        string playerName,
        byte gender = 0,
        byte teamColor = 0) =>
        Broadcast((connection, sequence) => new PlayerJoinedMessage(
            sequence,
            checked((ulong)_session.LatestSnapshot.Clock.Tick),
            playerId,
            playerName,
            FindConnectedEntityId(playerId),
            ClampGender(gender),
            ClampTeamColor(teamColor)));

    internal void AnnouncePlayerJoined(
        ClientConnection joinedConnection,
        Guid playerId,
        string playerName,
        byte gender = 0,
        byte teamColor = 0)
    {
        var presence = new ConnectedPlayerPresence(
            playerName.Trim(),
            ClampGender(gender),
            ClampTeamColor(teamColor));
        _connectedPlayers[playerId] = presence;

        // Bootstrap the joining connection with the complete presence set. A
        // snapshot alone has entity IDs but intentionally carries no names
        // or presentation.
        foreach (var player in _connectedPlayers.OrderBy(static value => value.Key))
        {
            if (!joinedConnection.TryQueueSequenced(sequence => new PlayerJoinedMessage(
                    sequence,
                    checked((ulong)_session.LatestSnapshot.Clock.Tick),
                    player.Key,
                    player.Value.Name,
                    FindConnectedEntityId(player.Key),
                    player.Value.Gender,
                    player.Value.TeamColor)))
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
                    presence.Name,
                    joinedConnection.PlayerEntityId,
                    presence.Gender,
                    presence.TeamColor)))
            {
                connection.Stop();
            }
        }
    }

    private static byte ClampGender(byte value) => value <= 1 ? value : (byte)0;

    private static byte ClampTeamColor(byte value) => value <= 7 ? value : (byte)0;

    private readonly record struct ConnectedPlayerPresence(
        string Name,
        byte Gender,
        byte TeamColor);

    private ulong FindConnectedEntityId(Guid playerId)
    {
        foreach (var connection in _clients.Values)
        {
            if (connection.Authenticated && connection.PlayerId == playerId)
                return connection.PlayerEntityId;
        }

        return 0;
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
            if (connection.PlayerId != sender.Identity.PlayerId.Value &&
                _session.IsIgnored(
                    new PlayerId(connection.PlayerId),
                    sender.Identity.PlayerId))
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
        IntentStatus.DestinationTooFar or
            IntentStatus.PathUnreachable => CommandRejectionCode.Impossible,
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
            EmptyBucketAction empty => new EmptyBucketIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                empty.Slot),
            FillBucketAction fill => new FillBucketIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                fill.Slot,
                new Vector2(fill.X, fill.Y),
                fill.WorldLevel),
            SocialAction social => new SocialIntent(
                command.CommandId,
                command.InventoryRevision,
                command.ActorRevision,
                (SocialCommandKind)social.Command,
                new PlayerId(social.TargetPlayerId),
                social.TradeId,
                social.GuildId,
                social.Text ?? "",
                social.Accept,
                social.OfferSlots is null
                    ? []
                    : [.. social.OfferSlots]),
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
            ActorNetworkEntityIdentity.Derive(player.Identity.ActorId),
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
            gameplay.FishingExperience,
            gameplay.MaximumHealth,
            gameplay.AttackExperience,
            gameplay.StrengthExperience,
            gameplay.DefenceExperience,
            CombatActionProtocolAdapter.ToProtocolStance(
                gameplay.CombatStance),
            CombatActionProtocolAdapter.ToLifeState(gameplay.LifeState),
            checked((ulong)gameplay.RespawnAvailableTick),
            CombatActionProtocolAdapter.ToStatusFlags(
                gameplay.StatusFlags(
                    tick / (double)SimulationTiming.TicksPerSecond)),
            gameplay.CombatTargetEnemyId?.Value ?? Guid.Empty,
            flags.HasFlag(PlayerStateFlags.Actor)
                ? WorldActionProtocolAdapter.ToQuestStates(gameplay)
                : []);

    private void BroadcastCookingCompletion(CookingCompletionSnapshot value)
    {
        var tick = checked((ulong)CurrentTick);
        foreach (var connection in _clients.Values)
        {
            if (!connection.Authenticated ||
                connection.PlayerId != value.PlayerId.Value)
                continue;
            if (!connection.TryQueuePrivateStateSequenced(sequence =>
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
            _publications.Publish(() => Broadcast((_, sequence) =>
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    sequence, tick, value.Transaction)!));
    }

    private void BroadcastBoatAutonomousState(BoatStateDelta delta)
    {
        if (_collectingCombatBoatDeltas is { } collecting)
        {
            ReserveAutonomousCombatPublication();
            collecting.Add(delta);
            return;
        }
        var tick = checked((ulong)CurrentTick);
        if (BoatActionProtocolAdapter.ToPublicDelta(1, tick, delta) is null)
            return;
        _publications.Publish(() => Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(sequence, tick, delta)!));
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
        gameplay.FishingExperience,
        gameplay.MaximumHealth,
        gameplay.AttackExperience,
        gameplay.StrengthExperience,
        gameplay.DefenceExperience,
        CombatActionProtocolAdapter.ToProtocolStance(gameplay.CombatStance),
        CombatActionProtocolAdapter.ToLifeState(gameplay.LifeState),
        checked((ulong)gameplay.RespawnAvailableTick),
        CombatActionProtocolAdapter.ToStatusFlags(
            gameplay.StatusFlags(
                tick / (double)SimulationTiming.TicksPerSecond)),
        gameplay.CombatTargetEnemyId?.Value ?? Guid.Empty,
        flags.HasFlag(PlayerStateFlags.Actor)
            ? WorldActionProtocolAdapter.ToQuestStates(gameplay)
            : []);

    internal async Task DisconnectAsync(
        ClientConnection connection,
        AuthenticatedPlayer player)
    {
        var result = await _session.EnqueueDisconnectAsync(
            new DisconnectRequest(
                connection.Id,
                player.Identity.PlayerId)).ConfigureAwait(false);
        var tick = checked((ulong)CurrentTick);
        QueueSocialPublications(result.Social, tick);
        if (result.BoatDelta is not { } delta) return;
        Broadcast((_, sequence) =>
            BoatActionProtocolAdapter.ToPublicDelta(
                sequence, tick, delta)!);
    }

    private async Task StopLanDiscoveryAsync()
    {
        var advertiser = Interlocked.Exchange(ref _lanDiscovery, null);
        if (advertiser is not null)
            await advertiser.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await StopLanDiscoveryAsync().ConfigureAwait(false);
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

    private async Task ObserveConnectionAsync(
        ClientConnection connection,
        TaskCompletionSource<bool> observed)
    {
        try
        {
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException)
        {
            // A peer can close or reset TCP while the server is concurrently
            // cancelling that connection. The observer still runs the full
            // authoritative disconnect path below; this is not a server fault.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Connection {connection.Id}: {exception}");
        }
        finally
        {
            try
            {
                _clients.TryRemove(connection.Id.Value, out _);
                _clientSlots.Release();
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                observed.TrySetResult(true);
                _connectionObservers.TryRemove(
                    connection.Id.Value, out _);
            }
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
                var combatOrigin = ResolveSpawn(cancellationToken);
                _seedingEnemyBootstrap = true;
                try
                {
                    foreach (var enemy in ProceduralEnemyBootstrap.Create(
                                 _options.WorldSeed,
                                 combatOrigin,
                                 _navigation))
                        _session.SeedEnemy(enemy);
                }
                finally
                {
                    _seedingEnemyBootstrap = false;
                }
            }
            RefreshWorldBootstrap();
            RefreshResourceBootstrap();
            RefreshBoatBootstrap();
            RefreshEnemyBootstrap();
            _nextAutosaveTick = checked(
                _session.Clock.Tick + AutosaveTicks(_options.AutosaveInterval));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            nextTick += (long)tickDuration;
            _collectingEnemyDeltas = [];
            _collectingCombatEvents = [];
            _collectingCombatWorldTransactions = [];
            _collectingCombatBoatDeltas = [];
            _collectingCombatPublication = null;
            SessionTickResult tick;
            try
            {
                tick = _session.Tick();
                if (_collectingEnemyDeltas.Count != 0 ||
                    _collectingCombatEvents.Count != 0 ||
                    _collectingCombatWorldTransactions.Count != 0 ||
                    _collectingCombatBoatDeltas.Count != 0)
                {
                    _pendingCombatPublications.Add(new(
                        _collectingCombatPublication ?? throw new(
                            "Combat replication committed without an ordered publication ticket."),
                        _collectingEnemyDeltas.ToArray(),
                        _collectingCombatEvents.ToArray(),
                        _collectingCombatWorldTransactions.ToArray(),
                        _collectingCombatBoatDeltas.ToArray()));
                }
            }
            finally
            {
                _collectingEnemyDeltas = null;
                _collectingCombatEvents = null;
                _collectingCombatWorldTransactions = null;
                _collectingCombatBoatDeltas = null;
                _collectingCombatPublication = null;
            }
            if (tick.PublishedSnapshot is { } snapshot)
            {
                FlushCombatReplication(snapshot);
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
                if (remaining > TimeSpan.FromMilliseconds(1))
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.Sleep(0);
                }
            }

            // Bound catch-up so a hitch cannot freeze an in-process host.
            // Ordinary loopback checks stay inside a few ticks of wall time.
            var slipped = stopwatch.ElapsedTicks - nextTick;
            if (slipped > tickDuration * 8)
                nextTick = stopwatch.ElapsedTicks;
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
        var picked = checkpoint.PickedProceduralGroundObjects.IsDefault
            ? Array.Empty<Guid>()
            : checkpoint.PickedProceduralGroundObjects.ToArray();
        lock (_worldBootstrapSync)
            Volatile.Write(ref _worldBootstrap, new WorldBootstrapState(
                Array.AsReadOnly(baselines),
                Array.AsReadOnly(chunkRevisions),
                Array.AsReadOnly(picked)));
    }

    private void ApplyWorldTransactionToBootstrap(
        WorldTransactionResult transaction,
        bool publishAutonomous = true)
    {
        // World loot bags are autonomous combat commits. Publish them here;
        // command-driven world actions publish only after requester-private
        // state in QueueWorldActionOutcome and therefore carry a command ID.
        var autonomous = transaction.ActorRevision == 0 &&
            transaction.InventoryRevision == 0 &&
            transaction.Gameplay is null &&
            transaction.ObjectDeltas.Any(delta =>
                delta.Object?.DefinitionId == ItemIds.LootBag);
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
            var picked = current.PickedProceduralGroundObjects.ToHashSet();
            foreach (var delta in transaction.ObjectDeltas)
            {
                if (delta.Kind == WorldObjectChangeKind.Removed)
                {
                    if (!objects.Remove(delta.ObjectId))
                        picked.Add(delta.ObjectId);
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
                Array.AsReadOnly(nextChunks),
                Array.AsReadOnly(
                    picked.OrderBy(static id => id).ToArray())));
            AfterWorldBootstrapUpdatedForTest?.Invoke();
        }
        if (autonomous && publishAutonomous)
            _publications.Publish(() => BroadcastWorldTransaction(transaction));
    }

    private void QueueOrApplyWorldTransaction(WorldTransactionResult transaction)
    {
        var autonomousCombatLoot = transaction.ActorRevision == 0 &&
            transaction.InventoryRevision == 0 &&
            transaction.Gameplay is null &&
            transaction.ObjectDeltas.Any(delta =>
                delta.Object?.DefinitionId == ItemIds.LootBag);
        if (autonomousCombatLoot &&
            _collectingCombatWorldTransactions is { } collecting)
        {
            ReserveAutonomousCombatPublication();
            collecting.Add(transaction);
            return;
        }
        ApplyWorldTransactionToBootstrap(transaction);
    }

    private void BroadcastWorldTransaction(
        WorldTransactionResult transaction)
    {
        var tick = checked((ulong)CurrentTick);
        if (WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                1, tick, transaction) is not null)
            Broadcast((_, sequence) =>
                WorldActionProtocolAdapter.ToPublicWorldDeltaBatch(
                    sequence, tick, transaction)!);
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

    private void RefreshEnemyBootstrap()
    {
        var enemies = _session.CaptureEnemies().ToArray();
        lock (_enemyBootstrapSync)
            Volatile.Write(ref _enemyBootstrap, new EnemyBootstrapState(
                Array.AsReadOnly(enemies)));
    }

    private void ApplyEnemyStateToBootstrap(EnemyStateDelta delta)
    {
        var tick = checked((ulong)CurrentTick);
        if (CombatActionProtocolAdapter.ToPublicDelta(1, tick, delta) is null)
            return;
        CommitAndBroadcastEnemyDelta(delta, tick);
    }

    private void QueueAutonomousEnemyDelta(EnemyStateDelta delta)
    {
        // Fresh-world SeedEnemy runs before the tick collector exists and no
        // clients are active yet. RefreshEnemyBootstrap captures those seeds
        // as one baseline after bootstrap, so there is no delta to publish.
        if (_collectingEnemyDeltas is null)
        {
            if (_seedingEnemyBootstrap)
                return;
            throw new InvalidOperationException(
                "An autonomous enemy delta escaped its simulation tick.");
        }
        ReserveAutonomousCombatPublication();
        _collectingEnemyDeltas.Add(delta);
    }

    private void QueueAutonomousCombatEvent(CombatEventSnapshot value)
    {
        if (_collectingCombatEvents is null)
            throw new InvalidOperationException(
                "An autonomous combat event escaped its simulation tick.");
        ReserveAutonomousCombatPublication();
        _collectingCombatEvents.Add(value);
    }

    private void ReserveAutonomousCombatPublication()
    {
        _collectingCombatPublication ??= _publications.Reserve();
    }

    private void FlushCombatReplication(SessionSnapshot snapshot)
    {
        var tick = checked((ulong)snapshot.Clock.Tick);

        // Private health, XP, status, and life state lead the public semantic
        // deltas/effects from the same tick. Per-actor high-water suppresses a
        // 60 Hz stream when combat did not mutate the requester.
        foreach (var actor in snapshot.Actors)
        {
            foreach (var connection in _clients.Values)
            {
                if (!connection.Authenticated ||
                    connection.PlayerId != actor.PlayerId.Value)
                    continue;
                if (!connection.TryQueuePrivateStateSequenced(
                        sequence => ToPlayerStateMessage(
                            sequence,
                            tick,
                            actor.PlayerId.Value,
                            connection.PlayerEntityId,
                            actor.Gameplay,
                            PlayerStateFlags.Baseline |
                            PlayerStateFlags.Actor |
                            PlayerStateFlags.Inventory,
                            0,
                            0)))
                    connection.Stop();
            }
        }

        foreach (var publication in _pendingCombatPublications)
        {
            _publications.Release(publication.Ticket, () =>
            {
                foreach (var transaction in publication.WorldTransactions)
                {
                    ApplyWorldTransactionToBootstrap(
                        transaction, publishAutonomous: false);
                    BroadcastWorldTransaction(transaction);
                }
                foreach (var delta in publication.BoatDeltas)
                {
                    var publicationTick = checked((ulong)snapshot.Clock.Tick);
                    if (BoatActionProtocolAdapter.ToPublicDelta(
                            1, publicationTick, delta) is not null)
                        Broadcast((_, sequence) =>
                            BoatActionProtocolAdapter.ToPublicDelta(
                                sequence, publicationTick, delta)!);
                }
                foreach (var delta in publication.EnemyDeltas)
                    ApplyEnemyStateToBootstrap(delta);
                foreach (var combatEvent in publication.Events)
                    BroadcastCombatEvent(combatEvent, snapshot);
            });
        }
        _pendingCombatPublications.Clear();
    }

    private void BroadcastCombatEvent(
        CombatEventSnapshot value,
        SessionSnapshot snapshot)
    {
        var enemies = (snapshot.Enemies.IsDefault
                ? ImmutableArray<AuthoritativeEnemySnapshot>.Empty
                : snapshot.Enemies)
            .ToDictionary(static enemy => enemy.EnemyId);
        var actors = snapshot.Actors.ToDictionary(
            static actor => actor.ActorId,
            static actor => (
                ActorNetworkEntityIdentity.Derive(actor.ActorId),
                actor.Position.X,
                actor.Position.Y,
                actor.WorldLevel));
        var projected = CombatActionProtocolAdapter.ToEvent(
            value, enemies, actors);
        if (projected is not { } combatEvent) return;
        Broadcast((_, sequence) => new CombatEventBatchMessage(
            sequence,
            checked((ulong)value.Tick),
            [combatEvent]));
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

    internal static EntitySnapshot[] MaterializeSnapshotEntities(
        SessionSnapshot snapshot)
    {
        var boatCount = snapshot.Boats.IsDefault ? 0 : snapshot.Boats.Length;
        var enemyCount = snapshot.Enemies.IsDefault ? 0 : snapshot.Enemies.Length;
        var entities = new EntitySnapshot[
            snapshot.Actors.Length + boatCount + enemyCount];
        var index = 0;
        var revision = checked((uint)Math.Min(snapshot.Sequence, uint.MaxValue));
        foreach (var actor in snapshot.Actors)
        {
            entities[index++] = new EntitySnapshot(
                ActorNetworkEntityIdentity.Derive(actor.ActorId),
                NetworkEntityKind.Player,
                actor.AnimationState,
                checked((short)actor.WorldLevel),
                actor.Position.X,
                actor.Position.Y,
                actor.Velocity.X,
                actor.Velocity.Y,
                (!actor.Connected ? NetworkEntityState.Hidden : NetworkEntityState.None) |
                (actor.Velocity != Vector2.Zero ? NetworkEntityState.Moving : NetworkEntityState.None) |
                (actor.Gameplay.LifeState == ActorLifeState.Dead ||
                 actor.Gameplay.Health <= 0
                    ? NetworkEntityState.Dead
                    : NetworkEntityState.None) |
                (IsPublishedSkillAnimation(actor.AnimationState)
                    ? NetworkEntityState.Interacting
                    : NetworkEntityState.None),
                revision);
        }
        if (!snapshot.Boats.IsDefault)
        {
            foreach (var boat in snapshot.Boats)
            {
                entities[index++] = new EntitySnapshot(
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
                    boat.Revision);
            }
        }
        if (!snapshot.Enemies.IsDefault)
        {
            foreach (var enemy in snapshot.Enemies)
            {
                entities[index++] = new EntitySnapshot(
                    enemy.NetworkEntityId,
                    NetworkEntityKind.Enemy,
                    checked((byte)enemy.Kind),
                    checked((short)enemy.WorldLevel),
                    enemy.Position.X,
                    enemy.Position.Y,
                    enemy.Velocity.X,
                    enemy.Velocity.Y,
                    (enemy.Velocity != Vector2.Zero
                        ? NetworkEntityState.Moving
                        : NetworkEntityState.None) |
                    (!enemy.Alive
                        ? NetworkEntityState.Dead
                        : enemy.TargetActorId is not null
                            ? NetworkEntityState.InCombat
                            : NetworkEntityState.None),
                    enemy.Revision);
            }
        }
        if (entities.Length > ProtocolLimits.MaxSnapshotEntities)
            throw new InvalidOperationException(
                "The authoritative snapshot exceeds its protocol entity bound.");
        return entities;
    }

    private static bool IsPublishedSkillAnimation(byte animation) =>
        ActorSkillStance.IsPublished(ActorSkillStance.UnpackAction(animation));

    private void BroadcastSnapshot(SessionSnapshot snapshot)
    {
        var entities = MaterializeSnapshotEntities(snapshot);
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

    internal static ReadOnlySpan<EntitySnapshot> SelectUdpEntities(
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
            ActorNetworkEntityIdentity.Derive(actor.ActorId) ==
            connection.PlayerEntityId);
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

        // Reserve a rotating remote-player slice before dense combat entities.
        // This preserves social/co-op motion without sacrificing self, ridden
        // boat, or the majority of the packet for nearby combat and travel.
        if (own >= 0)
        {
            var remotePlayers = Enumerable.Range(0, entities.Length)
                .Where(index => index != own &&
                                entities[index].EntityKind ==
                                    NetworkEntityKind.Player)
                .ToArray();
            var remoteQuota = Math.Min(
                remotePlayers.Length,
                Math.Max(1, result.Length / 8));
            if (remoteQuota > 0)
            {
                var remoteOffset = connection.NextInterestOffset(
                    remotePlayers.Length, remoteQuota);
                for (var scanned = 0;
                     scanned < remotePlayers.Length &&
                     scanned < remoteQuota && count < result.Length;
                     scanned++)
                {
                    var remoteIndex = remotePlayers[
                        (remoteOffset + scanned) % remotePlayers.Length];
                    result[count++] = entities[remoteIndex];
                }
            }

            // Same-level nearby enemies are combat-critical. Same-level boats
            // follow to keep collision and boarding affordances coherent.
            var nearbyEnemies = Enumerable.Range(0, entities.Length)
                .Where(index =>
                    index != own &&
                    entities[index].WorldLevel == entities[own].WorldLevel &&
                    entities[index].EntityKind == NetworkEntityKind.Enemy)
                .OrderBy(index => DistanceSquared(
                    entities[own], entities[index]))
                .ToArray();
            var enemyQuota = Math.Min(
                nearbyEnemies.Length,
                Math.Max(1, (result.Length - count) * 3 / 4));
            foreach (var index in nearbyEnemies.Take(enemyQuota))
            {
                if (count >= result.Length) break;
                result[count++] = entities[index];
            }
            var nearbyBoats = Enumerable.Range(0, entities.Length)
                .Where(index =>
                    index != own &&
                    entities[index].EntityId != occupiedBoatId &&
                    entities[index].WorldLevel == entities[own].WorldLevel &&
                    entities[index].EntityKind == NetworkEntityKind.Boat)
                .OrderBy(index => DistanceSquared(
                    entities[own], entities[index]))
                .ToArray();
            var boatQuota = Math.Min(
                nearbyBoats.Length,
                result.Length - count);
            foreach (var index in nearbyBoats.Take(boatQuota))
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
    IReadOnlyList<WorldChunkRevisionState> ChunkRevisions,
    IReadOnlyList<Guid> PickedProceduralGroundObjects)
{
    public static WorldBootstrapState Empty { get; } = new(
        Array.Empty<WorldObjectBaseline>(),
        Array.Empty<WorldChunkRevisionState>(),
        Array.Empty<Guid>());
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

internal sealed record EnemyBootstrapState(
    IReadOnlyList<AuthoritativeEnemySnapshot> Enemies)
{
    public static EnemyBootstrapState Empty { get; } = new(
        Array.Empty<AuthoritativeEnemySnapshot>());
}

internal sealed record PendingCombatPublication(
    OrderedPublications.Ticket Ticket,
    IReadOnlyList<EnemyStateDelta> EnemyDeltas,
    IReadOnlyList<CombatEventSnapshot> Events,
    IReadOnlyList<WorldTransactionResult> WorldTransactions,
    IReadOnlyList<BoatStateDelta> BoatDeltas);

internal readonly record struct CommandPublicationKey(
    Guid PlayerId,
    Guid CommandId);

internal readonly record struct AuthenticatedPlayer(
    Guid ClientId,
    PlayerIdentity Identity,
    string DisplayName,
    string ReconnectToken,
    ulong NextCommandSequence,
    bool Reconnected,
    PlayerGameplaySnapshot Gameplay,
    Vector2 Position,
    int WorldLevel,
    PlayerSocialSnapshot Social = default);

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
