using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using IslandRpg.Gameplay;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Client;

/// <summary>
/// Rendering-independent reliable game client. Event callbacks run on transport
/// tasks and should be marshalled by UI consumers rather than blocking the reader.
/// </summary>
public sealed class NetworkGameClient : IAsyncDisposable
{
    private const int OutboundCapacity = 256;
    private readonly object _stateSync = new();
    private readonly object _snapshotSync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _commandAuthorship = new(1, 1);
    private NetworkGameClientState _state = NetworkGameClientState.Disconnected;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Socket? _snapshotSocket;
    private Task? _snapshotReaderTask;
    private UdpSnapshotReceiver? _snapshotReceiver;
    private Channel<IProtocolMessage>? _outbound;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _readerTask;
    private Task? _writerTask;
    private long _outboundSequence;
    private ulong _lastInboundSequence;
    private readonly Dictionary<Guid, uint> _worldObjectRevisions = [];
    private readonly Dictionary<Guid, uint> _boatRevisions = [];
    private readonly Dictionary<Guid, uint> _enemyRevisions = [];
    private ulong _lastCombatEventOrdinal;
    private readonly EntitySnapshotReconstructor _snapshotReconstructor = new();
    private int _disposed;

    public NetworkGameClient(TimeSpan? interpolationDelay = null) =>
        SnapshotBuffer = new SnapshotInterpolationBuffer(interpolationDelay);

    public SnapshotInterpolationBuffer SnapshotBuffer { get; }
    public NetworkGameClientState State => Volatile.Read(ref _state);
    public bool IsConnected => State.Status == NetworkGameClientStatus.Connected;

    public event EventHandler<NetworkClientStateChangedEventArgs>? StateChanged;
    public event EventHandler<NetworkCommandResultEventArgs>? CommandCompleted;
    public event EventHandler<NetworkPlayerEventArgs>? PlayerJoined;
    public event EventHandler<NetworkPlayerLeftEventArgs>? PlayerLeft;
    public event EventHandler<NetworkChatEventArgs>? ChatReceived;
    public event EventHandler<NetworkSnapshotEventArgs>? SnapshotReceived;
    public event EventHandler<NetworkPlayerStateEventArgs>? PlayerStateChanged;
    public event EventHandler<NetworkActionResultEventArgs>? ActionCompleted;
    public event EventHandler<NetworkCookingResultEventArgs>? CookingCompleted;
    public event EventHandler<NetworkResourceActionResultEventArgs>?
        ResourceActionCompleted;
    public event EventHandler<NetworkBoatActionResultEventArgs>?
        BoatActionCompleted;
    public event EventHandler<NetworkCombatActionResultEventArgs>?
        CombatActionCompleted;
    public event EventHandler<NetworkCombatEventsEventArgs>? CombatEventsReceived;

    public event EventHandler<NetworkCaveActionResultEventArgs>?
        CaveActionCompleted;
    public event EventHandler<NetworkWorldObjectsChangedEventArgs>? WorldObjectsChanged;
    public event EventHandler<NetworkContainerStateEventArgs>? ContainerStateChanged;
    public event EventHandler<NetworkResourcesChangedEventArgs>? ResourcesChanged;
    public event EventHandler<NetworkBoatsChangedEventArgs>? BoatsChanged;
    public event EventHandler<NetworkEnemiesChangedEventArgs>? EnemiesChanged;

    public bool TryGetBoatReference(Guid boatId, out BoatReference reference)
    {
        if (State.Boats.TryGetValue(boatId, out var boat))
        {
            reference = new BoatReference(boatId, boat.Revision);
            return true;
        }

        reference = default;
        return false;
    }

    public BoatReference GetBoatReference(Guid boatId) =>
        TryGetBoatReference(boatId, out var reference)
            ? reference
            : throw new KeyNotFoundException($"Boat {boatId} is not known.");

    public bool TryGetEnemyReference(
        Guid enemyId,
        out CombatEnemyReference reference)
    {
        if (State.Enemies.TryGetValue(enemyId, out var enemy))
        {
            reference = new CombatEnemyReference(enemyId, enemy.Revision);
            return true;
        }
        reference = default;
        return false;
    }

    public CombatEnemyReference GetEnemyReference(Guid enemyId) =>
        TryGetEnemyReference(enemyId, out var reference)
            ? reference
            : throw new KeyNotFoundException($"Enemy {enemyId} is not known.");

    /// <summary>
    /// Resolves the exact optimistic-concurrency token for one known sparse or
    /// tombstoned resource. Procedural nodes absent from authoritative state
    /// have revision zero and should use the chunk overload.
    /// </summary>
    public bool TryGetResourceReference(
        ResourceNodeId nodeId,
        out ResourceNodeReference reference)
    {
        foreach (var pair in State.ResourceChunks)
        {
            if (pair.Value.NodeRevisionHighWater.TryGetValue(
                    nodeId, out var nodeRevision))
            {
                reference = new ResourceNodeReference(
                    nodeId,
                    pair.Key,
                    nodeRevision,
                    pair.Value.ResourceChunkRevision);
                return true;
            }
        }
        reference = default;
        return false;
    }

    /// <summary>
    /// Resolves an exact resource reference in a known chunk. This also covers
    /// an untouched deterministic node, whose node revision is zero.
    /// </summary>
    public ResourceNodeReference GetResourceReference(
        WorldChunkKey chunk,
        ResourceNodeId nodeId)
    {
        State.ResourceChunks.TryGetValue(chunk, out var state);
        var nodeRevision = 0u;
        state?.NodeRevisionHighWater.TryGetValue(nodeId, out nodeRevision);
        return new ResourceNodeReference(
            nodeId,
            chunk,
            nodeRevision,
            state?.ResourceChunkRevision ?? 0);
    }

    public async Task<HandshakeAcceptedMessage> ConnectAsync(
        string host,
        int port,
        ClientHandshakeOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        if (port is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
        if (options.ClientId == Guid.Empty) throw new ArgumentException("ClientId cannot be empty.", nameof(options));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State.Status is not NetworkGameClientStatus.Disconnected and not NetworkGameClientStatus.Faulted)
                throw new InvalidOperationException("The client is already connecting or connected.");

            if (State.Status == NetworkGameClientStatus.Faulted) CleanupConnection();
            Interlocked.Exchange(ref _outboundSequence, 0);
            SetState(NetworkGameClientState.Disconnected with { Status = NetworkGameClientStatus.Connecting });
            ClearSnapshotProjection();
            _lastInboundSequence = 0;
            var tcpClient = new TcpClient { NoDelay = true };
            Socket? snapshotSocket = null;
            try
            {
                await tcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                var stream = tcpClient.GetStream();
                var requestedCapabilities = options.Capabilities;
                ushort snapshotPort = 0;
                if (requestedCapabilities.HasFlag(ClientCapabilities.UdpSnapshots))
                {
                    var remoteAddress =
                        ((IPEndPoint)tcpClient.Client.RemoteEndPoint!).Address;
                    var snapshotFamily = remoteAddress.AddressFamily == AddressFamily.InterNetwork ||
                        remoteAddress.IsIPv4MappedToIPv6
                            ? AddressFamily.InterNetwork
                            : AddressFamily.InterNetworkV6;
                    snapshotSocket = new Socket(
                        snapshotFamily,
                        SocketType.Dgram,
                        ProtocolType.Udp);
                    snapshotSocket.Bind(new IPEndPoint(
                        snapshotFamily == AddressFamily.InterNetworkV6
                            ? IPAddress.IPv6Any
                            : IPAddress.Any,
                        options.ClientSnapshotPort));
                    snapshotPort = checked((ushort)
                        ((IPEndPoint)snapshotSocket.LocalEndPoint!).Port);
                }
                var nonceBytes = RandomNumberGenerator.GetBytes(sizeof(ulong));
                var nonce = BinaryPrimitives.ReadUInt64LittleEndian(nonceBytes);
                var request = new HandshakeRequestMessage(
                    NextSequence(),
                    0,
                    ProtocolConstants.CurrentVersion,
                    options.BuildVersion,
                    options.ContentVersion,
                    options.ClientId,
                    options.RequestedWorldId,
                    options.PlayerName,
                    nonce,
                    snapshotPort,
                    requestedCapabilities,
                    options.ReconnectPlayerId,
                    options.ReconnectToken,
                    options.Gender,
                    options.TeamColor);
                await TcpFrameCodec.WriteAsync(stream, request, cancellationToken).ConfigureAwait(false);
                var response = await TcpFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (response is HandshakeRejectedMessage rejected) throw new HandshakeRejectedException(rejected);
                if (response is not HandshakeAcceptedMessage accepted)
                    throw new ProtocolException("The server did not answer the handshake with acceptance or rejection.");
                ValidateAcceptance(options, nonce, accepted);

                _lastInboundSequence = accepted.Sequence;
                if (accepted.NextCommandSequence == 0 || accepted.NextCommandSequence > long.MaxValue)
                    throw new ProtocolException("Server acceptance specified an invalid next command sequence.");
                Interlocked.Exchange(ref _outboundSequence, checked((long)accepted.NextCommandSequence - 1));
                _tcpClient = tcpClient;
                _stream = stream;
                if (accepted.Capabilities.HasFlag(ServerCapabilities.UdpSnapshots))
                {
                    if (snapshotSocket is null || accepted.DatagramToken == 0 ||
                        accepted.ServerSnapshotPort == 0)
                        throw new ProtocolException(
                            "Server negotiated incomplete UDP snapshot transport.");
                    var snapshotAddress = NormalizeAddress(
                        ((IPEndPoint)tcpClient.Client.RemoteEndPoint!).Address,
                        snapshotSocket.AddressFamily);
                    var snapshotEndpoint = new IPEndPoint(
                        snapshotAddress,
                        accepted.ServerSnapshotPort);
                    try
                    {
                        snapshotSocket.Connect(snapshotEndpoint);
                    // Open the client's NAT mapping so the server's first UDP
                    // snapshot is not dropped as unsolicited inbound traffic.
                    try
                    {
                        snapshotSocket.Send(
                            [0x49, 0x52, 0x55, 0x44],
                            SocketFlags.None);
                    }
                    catch (SocketException)
                    {
                    }
                    }
                    catch (SocketException exception)
                    {
                        throw new ProtocolException(
                            $"Unable to connect UDP {snapshotSocket.LocalEndPoint} " +
                            $"({snapshotSocket.AddressFamily}) to {snapshotEndpoint} " +
                            $"({snapshotAddress.AddressFamily}).",
                            exception);
                    }
                    _snapshotSocket = snapshotSocket;
                    snapshotSocket = null;
                    _snapshotReceiver = new UdpSnapshotReceiver(
                        accepted.DatagramToken);
                }
                else
                {
                    snapshotSocket?.Dispose();
                    snapshotSocket = null;
                }
                _outbound = CreateOutboundChannel();
                _connectionCancellation = new CancellationTokenSource();
                var localPlayer = new NetworkPlayerPresence(
                    accepted.PlayerId,
                    options.PlayerName,
                    accepted.PlayerEntityId,
                    options.Gender,
                    options.TeamColor);
                var players = ReadOnly(new Dictionary<Guid, NetworkPlayerPresence> { [accepted.PlayerId] = localPlayer });
                SetState(new NetworkGameClientState(
                    NetworkGameClientStatus.Connected,
                    accepted.SessionId,
                    accepted.PlayerId,
                    accepted.PlayerEntityId,
                    accepted.WorldId,
                    accepted.WorldSeed,
                    accepted.SpawnX,
                    accepted.SpawnY,
                    accepted.SpawnWorldLevel,
                    accepted.ServerTickRate,
                    accepted.Tick,
                    accepted.ReconnectToken,
                    null,
                    players,
                    ReadOnly(new Dictionary<ulong, EntitySnapshot>()),
                    null,
                    ReadOnly(new Dictionary<Guid, NetworkWorldObjectState>()),
                    ReadOnly(new Dictionary<NetworkWorldChunk, uint>()),
                    ReadOnly(new Dictionary<Guid, NetworkContainerState>()),
                    ReadOnly(new Dictionary<WorldChunkKey, NetworkResourceChunkState>()))
                {
                    IslandStart = accepted.IslandStart,
                    Boats = ReadOnly(new Dictionary<Guid, BoatState>()),
                    Enemies = ReadOnly(new Dictionary<Guid, EnemyState>()),
                });
                _readerTask = RunReaderAsync(stream, _connectionCancellation.Token);
                _writerTask = RunWriterAsync(stream, _outbound.Reader, _connectionCancellation.Token);
                if (_snapshotSocket is not null)
                    _snapshotReaderTask = RunSnapshotReaderAsync(
                        _snapshotSocket,
                        _connectionCancellation.Token);
                return accepted;
            }
            catch (Exception exception)
            {
                tcpClient.Dispose();
                snapshotSocket?.Dispose();
                SetState(NetworkGameClientState.Disconnected with
                {
                    Status = exception is OperationCanceledException
                        ? NetworkGameClientStatus.Disconnected
                        : NetworkGameClientStatus.Faulted,
                    LastError = exception is OperationCanceledException ? null : exception.Message,
                });
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask<ulong> SendWalkAsync(float x, float y, int worldLevel, CancellationToken cancellationToken = default) =>
        QueueCommandAsync(sequence => new WalkCommandMessage(sequence, State.ServerTick, x, y, worldLevel), cancellationToken);

    public ValueTask<ulong> SendStopAsync(CancellationToken cancellationToken = default) =>
        QueueCommandAsync(sequence => new StopCommandMessage(sequence, State.ServerTick), cancellationToken);

    public ValueTask<ulong> SendChatAsync(
        string text,
        ChatChannel channel = ChatChannel.Local,
        Guid targetPlayerId = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return QueueCommandAsync(
            sequence => new ChatCommandMessage(sequence, State.ServerTick, channel, targetPlayerId, text),
            cancellationToken);
    }

    public ValueTask<ulong> SendActionAsync(
        IActionCommandPayload payload,
        Guid commandId = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var gameplay = State.Gameplay ?? throw new InvalidOperationException(
            "The server has not supplied an authoritative player baseline.");
        if (commandId == Guid.Empty) commandId = Guid.NewGuid();
        return QueueCommandAsync(
            sequence => new ActionCommandMessage(
                sequence,
                State.ServerTick,
                commandId,
                gameplay.ActorRevision,
                gameplay.InventoryRevision,
                payload),
            cancellationToken);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? reader;
        Task? writer;
        Task? snapshotReader = null;
        try
        {
            if (State.Status == NetworkGameClientStatus.Disconnected) return;
            SetStatus(NetworkGameClientStatus.Disconnecting);
            _outbound?.Writer.TryComplete();
            _connectionCancellation?.Cancel();
            _tcpClient?.Close();
            _snapshotSocket?.Close();
            reader = _readerTask;
            writer = _writerTask;
            snapshotReader = _snapshotReaderTask;
        }
        finally
        {
            _lifecycle.Release();
        }

        await IgnoreCancellationAsync(reader).ConfigureAwait(false);
        await IgnoreCancellationAsync(writer).ConfigureAwait(false);
        await IgnoreCancellationAsync(snapshotReader).ConfigureAwait(false);
        CleanupConnection();
        SetState(NetworkGameClientState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
        await _commandAuthorship.WaitAsync().ConfigureAwait(false);
        _commandAuthorship.Release();
        _commandAuthorship.Dispose();
        _lifecycle.Dispose();
    }

    private async ValueTask<ulong> QueueCommandAsync(
        Func<ulong, IProtocolMessage> factory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        var authoredOutbound = _outbound;
        if (State.Status != NetworkGameClientStatus.Connected ||
            authoredOutbound is null)
            throw new InvalidOperationException("The client is not connected.");
        // Sequence allocation and channel publication are one ordered operation.
        // Allocating first and then concurrently awaiting WriteAsync allowed a
        // later sequence to enter the channel (and TCP stream) before an earlier
        // one. Holding this gate through admission also prevents cancellation
        // from consuming an unpublished command sequence.
        await _commandAuthorship.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State.Status != NetworkGameClientStatus.Connected ||
                !ReferenceEquals(_outbound, authoredOutbound))
                throw new InvalidOperationException(
                    "The client connection changed before the command was published.");
            var outbound = authoredOutbound;
            if (!await outbound.Writer.WaitToWriteAsync(cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidOperationException("The network command queue is closed.");

            var sequence = NextSequence();
            var message = factory(sequence);
            if (!outbound.Writer.TryWrite(message))
                throw new InvalidOperationException("The network command queue closed before publication.");
            return sequence;
        }
        finally
        {
            _commandAuthorship.Release();
        }
    }

    private async Task RunWriterAsync(
        NetworkStream stream,
        ChannelReader<IProtocolMessage> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await TcpFrameCodec.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            EndConnection(exception);
        }
    }

    private async Task RunReaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await TcpFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    EndConnection(new EndOfStreamException("The server closed the connection."));
                    return;
                }
                if (message.Sequence <= _lastInboundSequence)
                    throw new ProtocolException("Server reliable sequence did not increase monotonically.");
                _lastInboundSequence = message.Sequence;
                Consume(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            EndConnection(exception);
        }
    }

    private async Task RunSnapshotReaderAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ProtocolConstants.MaxUdpDatagramBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(
                    buffer,
                    SocketFlags.None,
                    cancellationToken).ConfigureAwait(false);
                if (received <= 0 || _snapshotReceiver is null ||
                    !_snapshotReceiver.TryDecode(
                        buffer.AsSpan(0, received),
                        out var snapshot))
                {
                    continue;
                }

                ProtocolPacketLog.WriteUdp("udp", snapshot!, received);
                ConsumeSnapshot(snapshot!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            EndConnection(exception);
        }
    }

    private void Consume(IProtocolMessage message)
    {
        switch (message)
        {
            case CommandResultMessage result:
                UpdateTick(result.Tick);
                Raise(CommandCompleted, new NetworkCommandResultEventArgs(result));
                break;
            case ActionResultMessage result:
                UpdateTick(result.Tick);
                Raise(ActionCompleted, new NetworkActionResultEventArgs(result));
                break;
            case CookingResultMessage result:
                UpdateTick(result.Tick);
                Raise(CookingCompleted,
                    new NetworkCookingResultEventArgs(result));
                break;
            case ResourceActionResultMessage result:
                UpdateTick(result.Tick);
                Raise(ResourceActionCompleted,
                    new NetworkResourceActionResultEventArgs(result));
                break;
            case BoatActionResultMessage result:
                UpdateTick(result.Tick);
                Raise(BoatActionCompleted,
                    new NetworkBoatActionResultEventArgs(result));
                break;
            case CombatActionResultMessage result:
                UpdateTick(result.Tick);
                Raise(CombatActionCompleted,
                    new NetworkCombatActionResultEventArgs(result));
                break;
            case CaveActionResultMessage result:
                UpdateTick(result.Tick);
                Raise(CaveActionCompleted,
                    new NetworkCaveActionResultEventArgs(result));
                break;
            case PlayerStateMessage playerState:
            {
                var merged = MergePlayerState(State, playerState);
                UpdateState(current => current with
                {
                    ServerTick = Math.Max(current.ServerTick, playerState.Tick),
                    Gameplay = merged,
                });
                Raise(PlayerStateChanged, new NetworkPlayerStateEventArgs(merged));
                break;
            }
            case SocialStateMessage social:
            {
                if (State.PlayerId != Guid.Empty &&
                    social.PlayerId != State.PlayerId)
                    throw new ProtocolException(
                        "The server sent social state for a different player.");
                UpdateState(current => current with
                {
                    ServerTick = Math.Max(current.ServerTick, social.Tick),
                    Social = NetworkSocialState.FromMessage(social),
                });
                break;
            }
            case PlayerJoinedMessage joined:
            {
                var player = new NetworkPlayerPresence(
                    joined.PlayerId,
                    joined.PlayerName,
                    joined.EntityId,
                    joined.Gender,
                    joined.TeamColor);
                UpdateState(current => current with { ServerTick = joined.Tick, Players = With(current.Players, player) });
                Raise(PlayerJoined, new NetworkPlayerEventArgs(player));
                break;
            }
            case PlayerLeftMessage left:
                UpdateState(current => current with { ServerTick = left.Tick, Players = Without(current.Players, left.PlayerId) });
                Raise(PlayerLeft, new NetworkPlayerLeftEventArgs(left));
                break;
            case ChatBroadcastMessage chat:
                UpdateTick(chat.Tick);
                Raise(ChatReceived, new NetworkChatEventArgs(new NetworkChatEvent(
                    chat.Tick, chat.SenderPlayerId, chat.SenderPlayerName,
                    chat.Channel, chat.TargetPlayerId, chat.Text)));
                break;
            case EntitySnapshotMessage snapshot:
                ConsumeSnapshot(snapshot);
                break;
            case WorldObjectStateMessage worldObject:
                ConsumeWorldObject(worldObject);
                break;
            case WorldObjectDeltaBatchMessage worldObjects:
                ConsumeWorldObjects(worldObjects);
                break;
            case WorldChunkRevisionBatchMessage chunks:
                ConsumeWorldChunkRevisions(chunks);
                break;
            case ContainerStateMessage container:
                ConsumeContainer(container);
                break;
            case ResourceChunkBaselineMessage resources:
                ConsumeResourceBaseline(resources);
                break;
            case ResourceNodeDeltaBatchMessage resources:
                ConsumeResourceDeltas(resources);
                break;
            case BoatBaselineMessage boats:
                ConsumeBoatBaseline(boats);
                break;
            case BoatDeltaBatchMessage boats:
                ConsumeBoatDeltas(boats);
                break;
            case EnemyBaselineMessage enemies:
                ConsumeEnemyBaseline(enemies);
                break;
            case EnemyDeltaBatchMessage enemies:
                ConsumeEnemyDeltas(enemies);
                break;
            case CombatEventBatchMessage events:
                ConsumeCombatEvents(events);
                break;
            default:
                throw new ProtocolException($"The server sent invalid reliable message kind {message.Kind} after handshake.");
        }
    }

    private void ConsumeWorldObject(WorldObjectStateMessage message)
    {
        NetworkGameClientState next;
        NetworkWorldObjectChange? accepted = null;
        lock (_stateSync)
        {
            var projected = Project(message.Object);
            var chunk = Chunk(message.Object);
            _state.WorldChunkRevisions.TryGetValue(chunk, out var knownChunk);
            var hasKnownObject = _worldObjectRevisions.TryGetValue(
                message.Object.ObjectId,
                out var knownObject);
            if (message.Object.ChunkRevision < knownChunk ||
                message.Object.ObjectRevision < knownObject)
            {
                next = _state with
                {
                    ServerTick = Math.Max(_state.ServerTick, message.Tick),
                };
            }
            else if (message.Object.ObjectRevision == knownObject &&
                     _state.WorldObjects.TryGetValue(
                         message.Object.ObjectId,
                         out var existing))
            {
                if (!EquivalentObject(existing, projected))
                    throw new ProtocolException(
                        "Equal world-object revisions contained different state.");
                next = AdvanceChunkRevision(
                    _state,
                    message.Tick,
                    chunk,
                    message.Object.ChunkRevision);
            }
            else if (message.Object.ObjectRevision == knownObject &&
                     hasKnownObject)
            {
                // Equal to a retained tombstone: never resurrect it.
                next = _state with
                {
                    ServerTick = Math.Max(_state.ServerTick, message.Tick),
                };
            }
            else
            {
                var objects = _state.WorldObjects.ToDictionary();
                objects[projected.ObjectId] = projected;
                _worldObjectRevisions[projected.ObjectId] =
                    projected.ObjectRevision;
                var chunks = _state.WorldChunkRevisions.ToDictionary();
                chunks[chunk] = Math.Max(
                    knownChunk,
                    message.Object.ChunkRevision);
                next = _state with
                {
                    ServerTick = Math.Max(_state.ServerTick, message.Tick),
                    WorldObjects = ReadOnly(objects),
                    WorldChunkRevisions = ReadOnly(chunks),
                };
                accepted = new(
                    WorldObjectDeltaKind.Upsert,
                    projected.ObjectId,
                    projected.ChunkRevision,
                    projected.ObjectRevision,
                    projected);
            }

            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        if (accepted is not null)
            Raise(
                WorldObjectsChanged,
                new NetworkWorldObjectsChangedEventArgs(
                    Array.AsReadOnly(new[] { accepted })));
    }

    private void ConsumeWorldObjects(WorldObjectDeltaBatchMessage message) =>
        ApplyWorldObjectChanges(message.Tick, message.Deltas);

    private void ConsumeWorldChunkRevisions(
        WorldChunkRevisionBatchMessage message)
    {
        NetworkGameClientState next;
        lock (_stateSync)
        {
            // Validate the full batch before publishing any revision. This is
            // deliberately repeated after wire validation so direct transport
            // adapters cannot partially mutate client state either.
            var incoming = new Dictionary<NetworkWorldChunk, uint>(
                message.Chunks.Count);
            foreach (var value in message.Chunks)
            {
                if (value.Revision == 0)
                    throw new ProtocolException(
                        "World-chunk revisions must be positive.");
                var chunk = new NetworkWorldChunk(
                    value.ChunkX,
                    value.ChunkY,
                    value.WorldLevel);
                if (incoming.TryGetValue(chunk, out var duplicate) &&
                    duplicate != value.Revision)
                {
                    throw new ProtocolException(
                        "One world-chunk batch contained conflicting duplicate entries.");
                }
                incoming[chunk] = value.Revision;
            }

            Dictionary<NetworkWorldChunk, uint>? revisions = null;
            foreach (var pair in incoming)
            {
                _state.WorldChunkRevisions.TryGetValue(
                    pair.Key,
                    out var current);
                if (pair.Value <= current) continue;
                revisions ??= _state.WorldChunkRevisions.ToDictionary();
                revisions[pair.Key] = pair.Value;
            }

            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                WorldChunkRevisions = revisions is null
                    ? _state.WorldChunkRevisions
                    : ReadOnly(revisions),
            };
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
    }

    private void ApplyWorldObjectChanges(
        ulong tick,
        IReadOnlyList<WorldObjectDelta> deltas)
    {
        NetworkGameClientState next;
        List<NetworkWorldObjectChange> accepted = [];
        lock (_stateSync)
        {
            var objects = _state.WorldObjects.ToDictionary();
            var containers = _state.Containers.ToDictionary();
            var chunks = _state.WorldChunkRevisions.ToDictionary();
            var objectRevisions = new Dictionary<Guid, uint>(
                _worldObjectRevisions);
            var seenObjects = new HashSet<Guid>();
            foreach (var group in deltas.GroupBy(static delta =>
                         Chunk(delta.Reference)))
            {
                var chunk = group.Key;
                chunks.TryGetValue(chunk, out var knownChunk);
                var expectedChunk = group.First().Reference.ExpectedChunkRevision;
                var currentChunk = group.First().CurrentChunkRevision;
                if (group.Any(delta =>
                        delta.Reference.ExpectedChunkRevision != expectedChunk ||
                        delta.CurrentChunkRevision != currentChunk))
                {
                    throw new ProtocolException(
                        "One atomic world batch contained conflicting chunk revisions.");
                }
                if (currentChunk <= knownChunk)
                    continue;
                if (expectedChunk != knownChunk)
                    throw new ProtocolException(
                        "A world-object delta does not match the current chunk revision.");
                if (currentChunk <= expectedChunk)
                {
                    throw new ProtocolException(
                        "A world-object delta did not advance its chunk revision.");
                }

                foreach (var delta in group)
                {
                    var id = delta.Reference.ObjectId;
                    if (!seenObjects.Add(id))
                        throw new ProtocolException(
                            "One atomic world batch changed the same object more than once.");
                    objectRevisions.TryGetValue(id, out var knownRevision);
                    if (delta.Reference.ExpectedObjectRevision != knownRevision &&
                        !IsFirstSeenGeneratedRemoval(delta, knownRevision))
                        throw new ProtocolException(
                            "A world-object delta does not match the current object revision.");

                    if (delta.Kind == WorldObjectDeltaKind.Upsert)
                    {
                        if (delta.State is not { } state)
                            throw new ProtocolException(
                                "A world-object upsert omitted its state.");
                        if (state.ChunkRevision != currentChunk ||
                            state.ObjectRevision <= knownRevision ||
                            Chunk(state) != chunk || state.ObjectId != id)
                        {
                            throw new ProtocolException(
                                "A world-object upsert did not match its atomic revisions.");
                        }
                        var projected = Project(state);
                        objects[id] = projected;
                        objectRevisions[id] = state.ObjectRevision;
                        accepted.Add(new(
                            WorldObjectDeltaKind.Upsert,
                            id,
                            currentChunk,
                            state.ObjectRevision,
                            projected));
                        continue;
                    }

                    if (delta.State is not null)
                        throw new ProtocolException(
                            "A world-object removal unexpectedly carried state.");
                    objects.Remove(id);
                    containers.Remove(id);
                    objectRevisions[id] = knownRevision;
                    accepted.Add(new(
                        WorldObjectDeltaKind.Remove,
                        id,
                        currentChunk,
                        knownRevision,
                        null));
                }
                chunks[chunk] = currentChunk;
            }

            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, tick),
                WorldObjects = accepted.Count == 0
                    ? _state.WorldObjects
                    : ReadOnly(objects),
                Containers = accepted.Count == 0
                    ? _state.Containers
                    : ReadOnly(containers),
                WorldChunkRevisions = accepted.Count == 0
                    ? _state.WorldChunkRevisions
                    : ReadOnly(chunks),
            };
            if (accepted.Count > 0)
            {
                _worldObjectRevisions.Clear();
                foreach (var pair in objectRevisions)
                    _worldObjectRevisions.Add(pair.Key, pair.Value);
            }
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        if (accepted.Count > 0)
            Raise(
                WorldObjectsChanged,
                new NetworkWorldObjectsChangedEventArgs(
                    Array.AsReadOnly(accepted.ToArray())));
    }

    private static NetworkGameClientState AdvanceChunkRevision(
        NetworkGameClientState state,
        ulong tick,
        NetworkWorldChunk chunk,
        uint revision)
    {
        state.WorldChunkRevisions.TryGetValue(chunk, out var known);
        if (revision <= known)
            return state with { ServerTick = Math.Max(state.ServerTick, tick) };
        var chunks = state.WorldChunkRevisions.ToDictionary();
        chunks[chunk] = revision;
        return state with
        {
            ServerTick = Math.Max(state.ServerTick, tick),
            WorldChunkRevisions = ReadOnly(chunks),
        };
    }

    private void ConsumeContainer(ContainerStateMessage message)
    {
        NetworkGameClientState next;
        NetworkContainerState? accepted = null;
        lock (_stateSync)
        {
            var containers = _state.Containers;
            containers.TryGetValue(message.Container.ObjectId, out var previous);
            if (message.IsBaseline)
            {
                if (previous is not null &&
                    message.ContainerRevision <= previous.ContainerRevision)
                {
                    if (message.ContainerRevision ==
                            previous.ContainerRevision &&
                        !EquivalentContainer(previous, message))
                    {
                        throw new ProtocolException(
                            "Equal container revisions contained different state.");
                    }
                    if (message.ContainerRevision == previous.ContainerRevision)
                    {
                        ValidateContainerReferenceProgression(
                            previous.Reference, message.Container);
                        accepted = ProjectBaseline(message);
                        next = WithContainer(_state, message.Tick, accepted);
                    }
                    else
                    {
                        next = _state with
                        {
                            ServerTick = Math.Max(_state.ServerTick, message.Tick),
                        };
                    }
                    Volatile.Write(ref _state, next);
                }
                else
                {
                    accepted = ProjectBaseline(message);
                    next = WithContainer(_state, message.Tick, accepted);
                    Volatile.Write(ref _state, next);
                }
            }
            else
            {
                // Validate the complete chain before copying or publishing any
                // slot. A malformed delta therefore leaves the old projection
                // intact and faults the transport reader.
                if (previous is null)
                    throw new ProtocolException(
                        "A container delta arrived before its baseline.");
                ValidateContainerReferenceProgression(
                    previous.Reference, message.Container);
                if (message.BaselineContainerRevision !=
                    previous.ContainerRevision)
                {
                    throw new ProtocolException(
                        "A container delta does not match the current revision.");
                }
                if (message.ContainerRevision <= previous.ContainerRevision)
                    throw new ProtocolException(
                        "A container delta did not advance its revision.");
                if (message.SlotCount != previous.Slots.Count ||
                    !string.Equals(
                        message.DefinitionId,
                        previous.DefinitionId,
                        StringComparison.Ordinal))
                {
                    throw new ProtocolException(
                        "A container delta changed its baseline shape.");
                }

                var slots = previous.Slots.ToArray();
                foreach (var slot in message.Slots) slots[slot.Slot] = slot;
                accepted = new NetworkContainerState(
                    message.Container,
                    message.ContainerRevision,
                    message.DefinitionId,
                    message.Access,
                    Array.AsReadOnly(slots));
                next = WithContainer(_state, message.Tick, accepted);
                Volatile.Write(ref _state, next);
            }
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        if (accepted is not null)
            Raise(
                ContainerStateChanged,
                new NetworkContainerStateEventArgs(accepted));
    }

    private static NetworkGameClientState WithContainer(
        NetworkGameClientState state,
        ulong tick,
        NetworkContainerState container)
    {
        var containers = state.Containers.ToDictionary();
        containers[container.ObjectId] = container;
        return state with
        {
            ServerTick = Math.Max(state.ServerTick, tick),
            Containers = ReadOnly(containers),
        };
    }

    private void ConsumeResourceBaseline(ResourceChunkBaselineMessage message)
    {
        NetworkGameClientState next;
        NetworkResourceChunkState? accepted = null;
        IReadOnlyList<NetworkResourceChange> changes = [];
        lock (_stateSync)
        {
            _state.ResourceChunks.TryGetValue(message.Chunk, out var previous);
            if (previous is not null &&
                message.ResourceChunkRevision < previous.ResourceChunkRevision)
            {
                next = _state with
                {
                    ServerTick = Math.Max(_state.ServerTick, message.Tick),
                };
            }
            else
            {
                var projected = ProjectResourceBaseline(message);
                if (previous is not null &&
                    previous.NodeRevisionHighWater.Any(pair =>
                        !projected.NodeRevisionHighWater.TryGetValue(
                            pair.Key, out var revision) ||
                        revision < pair.Value))
                {
                    throw new ProtocolException(
                        "A resource baseline regressed retained node revisions.");
                }
                if (previous is not null &&
                    message.ResourceChunkRevision == previous.ResourceChunkRevision)
                {
                    if (!EquivalentResourceChunk(previous, projected))
                    {
                        throw new ProtocolException(
                            "Equal resource chunk revisions contained different state.");
                    }
                    next = _state with
                    {
                        ServerTick = Math.Max(_state.ServerTick, message.Tick),
                    };
                }
                else
                {
                    var chunks = _state.ResourceChunks.ToDictionary();
                    chunks[message.Chunk] = projected;
                    accepted = projected;
                    changes = DescribeResourceBaselineChanges(previous, projected);
                    next = _state with
                    {
                        ServerTick = Math.Max(_state.ServerTick, message.Tick),
                        ResourceChunks = ReadOnly(chunks),
                    };
                }
            }
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        if (accepted is not null)
            Raise(ResourcesChanged, new NetworkResourcesChangedEventArgs(
                accepted.Chunk, true, changes));
    }

    private void ConsumeResourceDeltas(ResourceNodeDeltaBatchMessage message)
    {
        NetworkGameClientState next;
        var accepted = new List<(WorldChunkKey Chunk,
            IReadOnlyList<NetworkResourceChange> Changes)>();
        lock (_stateSync)
        {
            var chunks = _state.ResourceChunks.ToDictionary();
            foreach (var group in message.Deltas.GroupBy(
                         static delta => delta.Reference.Chunk))
            {
                chunks.TryGetValue(group.Key, out var previous);
                var first = group.First();
                if (group.Any(delta =>
                        delta.Reference.ExpectedResourceChunkRevision !=
                        first.Reference.ExpectedResourceChunkRevision ||
                        delta.CurrentResourceChunkRevision !=
                        first.CurrentResourceChunkRevision))
                {
                    throw new ProtocolException(
                        "One resource chunk contained conflicting atomic revisions.");
                }
                var knownChunkRevision =
                    previous?.ResourceChunkRevision ?? 0;
                if (first.CurrentResourceChunkRevision <= knownChunkRevision)
                    continue;
                if (first.Reference.ExpectedResourceChunkRevision !=
                    knownChunkRevision)
                {
                    throw new ProtocolException(
                        "A resource delta does not match the current chunk revision.");
                }

                var nodes = previous?.Nodes.ToDictionary() ??
                    new Dictionary<ResourceNodeId, ResourceNodeSparseState>();
                var highWater = previous?.NodeRevisionHighWater.ToDictionary() ??
                    new Dictionary<ResourceNodeId, uint>();
                var changes = new List<NetworkResourceChange>();
                foreach (var delta in group)
                {
                    highWater.TryGetValue(delta.Reference.Id, out var knownNode);
                    if (delta.Reference.ExpectedNodeRevision != knownNode)
                    {
                        throw new ProtocolException(
                            "A resource delta does not match the current node revision.");
                    }
                    highWater[delta.Reference.Id] = delta.CurrentNodeRevision;
                    if (delta.Kind == ResourceNodeDeltaKind.Upsert)
                        nodes[delta.Reference.Id] = delta.State!;
                    else
                        nodes.Remove(delta.Reference.Id);
                    changes.Add(new NetworkResourceChange(
                        delta.Kind,
                        delta.Reference.Id,
                        group.Key,
                        delta.CurrentNodeRevision,
                        delta.CurrentResourceChunkRevision,
                        delta.State));
                }
                chunks[group.Key] = new NetworkResourceChunkState(
                    group.Key,
                    first.CurrentResourceChunkRevision,
                    ReadOnly(nodes),
                    ReadOnly(highWater));
                accepted.Add((group.Key, Array.AsReadOnly(changes.ToArray())));
            }

            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                ResourceChunks = accepted.Count == 0
                    ? _state.ResourceChunks
                    : ReadOnly(chunks),
            };
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        foreach (var item in accepted)
            Raise(ResourcesChanged, new NetworkResourcesChangedEventArgs(
                item.Chunk, false, item.Changes));
    }

    private void ConsumeBoatBaseline(BoatBaselineMessage message)
    {
        NetworkGameClientState next;
        IReadOnlyList<NetworkBoatChange> changes;
        lock (_stateSync)
        {
            var boats = message.Boats.ToDictionary(static boat => boat.BoatId);
            var revisions = new Dictionary<Guid, uint>(_boatRevisions);
            var accepted = new List<NetworkBoatChange>();
            foreach (var pair in boats)
            {
                revisions.TryGetValue(pair.Key, out var knownRevision);
                if (pair.Value.Revision < knownRevision)
                    throw new ProtocolException(
                        "A boat baseline regressed a retained boat revision.");
                if (pair.Value.Revision == knownRevision &&
                    _state.Boats.TryGetValue(pair.Key, out var previous) &&
                    previous != pair.Value)
                    throw new ProtocolException(
                        "Equal boat revisions contained different state.");
                revisions[pair.Key] = pair.Value.Revision;
                if (!_state.Boats.TryGetValue(pair.Key, out previous) ||
                    previous != pair.Value)
                {
                    accepted.Add(new NetworkBoatChange(
                        BoatDeltaKind.Upsert,
                        pair.Key,
                        pair.Value.Revision,
                        pair.Value));
                }
            }

            foreach (var pair in _state.Boats)
            {
                if (!boats.ContainsKey(pair.Key))
                    accepted.Add(new NetworkBoatChange(
                        BoatDeltaKind.Remove,
                        pair.Key,
                        pair.Value.Revision,
                        null));
            }

            changes = Array.AsReadOnly(accepted.ToArray());
            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                Boats = ReadOnly(boats),
            };
            ReplaceBoatRevisions(revisions);
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        Raise(BoatsChanged, new NetworkBoatsChangedEventArgs(true, changes));
    }

    private void ConsumeBoatDeltas(BoatDeltaBatchMessage message)
    {
        NetworkGameClientState next;
        IReadOnlyList<NetworkBoatChange> changes;
        lock (_stateSync)
        {
            var boats = _state.Boats.ToDictionary();
            var revisions = new Dictionary<Guid, uint>(_boatRevisions);
            var accepted = new List<NetworkBoatChange>(message.Deltas.Count);
            foreach (var delta in message.Deltas)
            {
                revisions.TryGetValue(
                    delta.Reference.BoatId, out var knownRevision);
                if (delta.Reference.ExpectedRevision != knownRevision)
                    throw new ProtocolException(
                        "A boat delta does not match the retained boat revision.");

                revisions[delta.Reference.BoatId] =
                    delta.CurrentRevision;
                if (delta.Kind == BoatDeltaKind.Upsert)
                    boats[delta.Reference.BoatId] = delta.State!.Value;
                else
                    boats.Remove(delta.Reference.BoatId);
                accepted.Add(new NetworkBoatChange(
                    delta.Kind,
                    delta.Reference.BoatId,
                    delta.CurrentRevision,
                    delta.State));
            }

            changes = Array.AsReadOnly(accepted.ToArray());
            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                Boats = accepted.Count == 0 ? _state.Boats : ReadOnly(boats),
            };
            ReplaceBoatRevisions(revisions);
            Volatile.Write(ref _state, next);
        }

        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        if (changes.Count != 0)
            Raise(BoatsChanged, new NetworkBoatsChangedEventArgs(false, changes));
    }

    private void ReplaceBoatRevisions(Dictionary<Guid, uint> revisions)
    {
        _boatRevisions.Clear();
        foreach (var pair in revisions)
            _boatRevisions.Add(pair.Key, pair.Value);
    }

    private void ConsumeEnemyBaseline(EnemyBaselineMessage message)
    {
        NetworkGameClientState next;
        IReadOnlyList<NetworkEnemyChange> changes;
        lock (_stateSync)
        {
            var enemies = message.Enemies.ToDictionary(static enemy => enemy.EnemyId);
            var revisions = new Dictionary<Guid, uint>(_enemyRevisions);
            var accepted = new List<NetworkEnemyChange>();
            foreach (var pair in enemies)
            {
                revisions.TryGetValue(pair.Key, out var knownRevision);
                if (pair.Value.Revision < knownRevision)
                    throw new ProtocolException("An enemy baseline regressed retained revision high-water.");
                if (knownRevision != 0 &&
                    pair.Value.Revision == knownRevision &&
                    !_state.Enemies.ContainsKey(pair.Key))
                    throw new ProtocolException(
                        "An enemy baseline resurrected a retained tombstone.");
                if (pair.Value.Revision == knownRevision &&
                    _state.Enemies.TryGetValue(pair.Key, out var previous) &&
                    previous != pair.Value)
                    throw new ProtocolException("Equal enemy revisions contained different state.");
                revisions[pair.Key] = pair.Value.Revision;
                if (!_state.Enemies.TryGetValue(pair.Key, out previous) || previous != pair.Value)
                    accepted.Add(new NetworkEnemyChange(
                        EnemyDeltaKind.Upsert, pair.Key, pair.Value.Revision, pair.Value));
            }
            foreach (var pair in _state.Enemies)
                if (!enemies.ContainsKey(pair.Key))
                    accepted.Add(new NetworkEnemyChange(
                        EnemyDeltaKind.Remove, pair.Key, pair.Value.Revision, null));

            changes = Array.AsReadOnly(accepted.ToArray());
            ReplaceEnemyRevisions(revisions);
            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                Enemies = ReadOnly(enemies),
            };
            Volatile.Write(ref _state, next);
        }
        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        Raise(EnemiesChanged, new NetworkEnemiesChangedEventArgs(true, changes));
    }

    private void ConsumeEnemyDeltas(EnemyDeltaBatchMessage message)
    {
        NetworkGameClientState next;
        IReadOnlyList<NetworkEnemyChange> changes;
        lock (_stateSync)
        {
            // Validate and stage the full batch before publishing any state.
            var enemies = _state.Enemies.ToDictionary();
            var revisions = new Dictionary<Guid, uint>(_enemyRevisions);
            var accepted = new List<NetworkEnemyChange>(message.Deltas.Count);
            foreach (var delta in message.Deltas)
            {
                revisions.TryGetValue(delta.Reference.EnemyId, out var knownRevision);
                if (delta.Reference.ExpectedRevision != knownRevision)
                    throw new ProtocolException("An enemy delta does not match retained revision high-water.");
                revisions[delta.Reference.EnemyId] = delta.CurrentRevision;
                if (delta.Kind == EnemyDeltaKind.Upsert)
                    enemies[delta.Reference.EnemyId] = delta.State!.Value;
                else
                    enemies.Remove(delta.Reference.EnemyId);
                accepted.Add(new NetworkEnemyChange(
                    delta.Kind, delta.Reference.EnemyId, delta.CurrentRevision, delta.State));
            }
            changes = Array.AsReadOnly(accepted.ToArray());
            ReplaceEnemyRevisions(revisions);
            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
                Enemies = ReadOnly(enemies),
            };
            Volatile.Write(ref _state, next);
        }
        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        Raise(EnemiesChanged, new NetworkEnemiesChangedEventArgs(false, changes));
    }

    private void ConsumeCombatEvents(CombatEventBatchMessage message)
    {
        IReadOnlyList<CombatEvent> accepted;
        NetworkGameClientState next;
        lock (_stateSync)
        {
            var first = message.Events[0].EventOrdinal;
            if (first <= _lastCombatEventOrdinal)
                throw new ProtocolException("Combat event ordinals replayed or regressed.");
            _lastCombatEventOrdinal = message.Events[^1].EventOrdinal;
            accepted = Array.AsReadOnly(message.Events.ToArray());
            next = _state with
            {
                ServerTick = Math.Max(_state.ServerTick, message.Tick),
            };
            Volatile.Write(ref _state, next);
        }
        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
        Raise(CombatEventsReceived, new NetworkCombatEventsEventArgs(accepted));
    }

    private void ReplaceEnemyRevisions(Dictionary<Guid, uint> revisions)
    {
        _enemyRevisions.Clear();
        foreach (var pair in revisions) _enemyRevisions.Add(pair.Key, pair.Value);
    }

    private static NetworkResourceChunkState ProjectResourceBaseline(
        ResourceChunkBaselineMessage message)
    {
        var nodes = message.Nodes.ToDictionary(static value => value.Id);
        var highWater = nodes.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.NodeRevision);
        foreach (var tombstone in message.Tombstones)
            highWater[tombstone.Id] = tombstone.Revision;
        return new NetworkResourceChunkState(
            message.Chunk,
            message.ResourceChunkRevision,
            ReadOnly(nodes),
            ReadOnly(highWater));
    }

    private static bool EquivalentResourceChunk(
        NetworkResourceChunkState left,
        NetworkResourceChunkState right) =>
        left.Chunk == right.Chunk &&
        left.ResourceChunkRevision == right.ResourceChunkRevision &&
        left.Nodes.Count == right.Nodes.Count &&
        left.Nodes.All(pair =>
            right.Nodes.TryGetValue(pair.Key, out var value) &&
            pair.Value == value) &&
        left.NodeRevisionHighWater.Count == right.NodeRevisionHighWater.Count &&
        left.NodeRevisionHighWater.All(pair =>
            right.NodeRevisionHighWater.TryGetValue(pair.Key, out var value) &&
            pair.Value == value);

    private static IReadOnlyList<NetworkResourceChange>
        DescribeResourceBaselineChanges(
            NetworkResourceChunkState? previous,
            NetworkResourceChunkState current)
    {
        var result = new List<NetworkResourceChange>();
        if (previous is not null)
        {
            foreach (var pair in previous.Nodes)
            {
                if (current.Nodes.ContainsKey(pair.Key)) continue;
                current.NodeRevisionHighWater.TryGetValue(
                    pair.Key, out var revision);
                result.Add(new NetworkResourceChange(
                    ResourceNodeDeltaKind.Remove,
                    pair.Key,
                    current.Chunk,
                    revision,
                    current.ResourceChunkRevision,
                    null));
            }
        }
        foreach (var pair in current.Nodes)
        {
            if (previous is not null &&
                previous.Nodes.TryGetValue(pair.Key, out var old) &&
                old == pair.Value)
                continue;
            result.Add(new NetworkResourceChange(
                ResourceNodeDeltaKind.Upsert,
                pair.Key,
                current.Chunk,
                pair.Value.NodeRevision,
                current.ResourceChunkRevision,
                pair.Value));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static NetworkContainerState ProjectBaseline(
        ContainerStateMessage message)
    {
        var slots = new ContainerSlotState[message.SlotCount];
        foreach (var slot in message.Slots) slots[slot.Slot] = slot;
        return new NetworkContainerState(
            message.Container,
            message.ContainerRevision,
            message.DefinitionId,
            message.Access,
            Array.AsReadOnly(slots));
    }

    private static NetworkWorldObjectState Project(WorldObjectState value) =>
        new(
            value.ObjectId,
            value.ChunkX,
            value.ChunkY,
            value.WorldLevel,
            value.ChunkRevision,
            value.ObjectRevision,
            value.DefinitionId,
            value.X,
            value.Y,
            value.Rotation,
            value.Health,
            value.MaximumHealth,
            value.HasContainer,
            value.FuelItemId,
            value.LitUntilGameSeconds,
            value.GateState,
            value.LinkedObjectId);

    private static bool EquivalentObject(
        NetworkWorldObjectState left,
        NetworkWorldObjectState right) =>
        left with { ChunkRevision = right.ChunkRevision } == right;

    private static bool EquivalentContainer(
        NetworkContainerState previous,
        ContainerStateMessage message) =>
        previous.ObjectId == message.Container.ObjectId &&
        previous.ContainerRevision == message.ContainerRevision &&
        string.Equals(
            previous.DefinitionId,
            message.DefinitionId,
            StringComparison.Ordinal) &&
        previous.Access == message.Access &&
        previous.Slots.SequenceEqual(message.Slots);

    private static void ValidateContainerReferenceProgression(
        WorldObjectReference previous,
        WorldObjectReference current)
    {
        if (previous.ObjectId != current.ObjectId ||
            previous.ChunkX != current.ChunkX ||
            previous.ChunkY != current.ChunkY ||
            previous.WorldLevel != current.WorldLevel ||
            current.ExpectedObjectRevision < previous.ExpectedObjectRevision ||
            current.ExpectedChunkRevision < previous.ExpectedChunkRevision)
        {
            throw new ProtocolException(
                "An equal container baseline regressed its world-object reference.");
        }
    }

    /// <summary>
    /// Generated sticks, rocks, seeds, and coastal loot are never published
    /// as world objects. A first-seen removal is a tombstone: missing IDs
    /// are revision 0, and a stale 1 is the pre-fix command convention.
    /// </summary>
    private static bool IsFirstSeenGeneratedRemoval(
        WorldObjectDelta delta,
        uint knownRevision) =>
        knownRevision == 0 &&
        delta.Kind == WorldObjectDeltaKind.Remove &&
        delta.Reference.ExpectedObjectRevision <=
            GeneratedPortableGroundLoot.VirginCommandRevision;

    private static NetworkWorldChunk Chunk(WorldObjectState value) =>
        new(value.ChunkX, value.ChunkY, value.WorldLevel);

    private static NetworkWorldChunk Chunk(WorldObjectReference value) =>
        new(value.ChunkX, value.ChunkY, value.WorldLevel);

    internal void ConsumeSnapshot(EntitySnapshotMessage snapshot)
    {
        EntitySnapshotMessage complete;
        NetworkGameClientState next;
        bool buffered;
        lock (_snapshotSync)
        {
            if (!_snapshotReconstructor.TryReconstruct(
                    snapshot,
                    out var reconstructed))
            {
                return;
            }

            complete = reconstructed.Snapshot;
            // Both transport readers serialize reconstruction and buffer
            // publication here. Event callbacks run after the lock and may
            // arrive later than a subsequent state update, which is acceptable
            // because each callback carries its own immutable complete frame.
            buffered = reconstructed.ReplacesLatestFrame
                ? SnapshotBuffer.ReplaceLatest(complete)
                : SnapshotBuffer.Add(complete);
        }
        if (!buffered) return;

        var entities = ReadOnly(complete.Entities.ToDictionary(
            static entity => entity.EntityId));
        lock (_stateSync)
        {
            next = _state with
            {
                ServerTick = Math.Max(
                    _state.ServerTick,
                    complete.Metadata.ServerTick),
                Entities = entities,
            };
            Volatile.Write(ref _state, next);
        }

        // Never invoke external subscribers under the TCP/UDP ordering lock.
        // Production rendering consumes the serialized interpolation buffer;
        // this event is an observation hook for complete frames only.
        // Snapshots update the interpolation buffer. Do not raise StateChanged
        // here: the game already samples that buffer, and a 20 Hz copy of the
        // full client state onto the UI queue stalls the join/render thread.
        Raise(SnapshotReceived, new NetworkSnapshotEventArgs(complete));
    }

    private void EndConnection(Exception exception)
    {
        _connectionCancellation?.Cancel();
        _outbound?.Writer.TryComplete(exception);
        _tcpClient?.Close();
        if (State.Status is NetworkGameClientStatus.Disconnecting or NetworkGameClientStatus.Disconnected) return;
        UpdateState(current => current with
        {
            Status = NetworkGameClientStatus.Faulted,
            LastError = exception.Message,
        });
    }

    private static void ValidateAcceptance(ClientHandshakeOptions options, ulong nonce, HandshakeAcceptedMessage accepted)
    {
        if (accepted.ProtocolVersion != ProtocolConstants.CurrentVersion)
            throw new ProtocolException("Server accepted an incompatible protocol version.");
        if (accepted.EchoClientNonce != nonce)
            throw new ProtocolException("Server handshake nonce did not match this connection.");
        if (accepted.SessionId == Guid.Empty || accepted.PlayerId == Guid.Empty ||
            accepted.PlayerEntityId == 0 || accepted.WorldId == Guid.Empty)
            throw new ProtocolException("Server acceptance omitted required identities.");
        if (accepted.ServerTickRate == 0)
            throw new ProtocolException("Server acceptance specified an invalid tick rate.");
        if (accepted.BuildVersion != options.BuildVersion || accepted.ContentVersion != options.ContentVersion)
            throw new ProtocolException("Server acceptance described different build or content versions.");
        if (options.RequestedWorldId != Guid.Empty && accepted.WorldId != options.RequestedWorldId)
            throw new ProtocolException("Server accepted the client into a different world than requested.");
    }

    private static NetworkPlayerGameplayState MergePlayerState(
        NetworkGameClientState client,
        PlayerStateMessage message)
    {
        if (message.PlayerId != client.PlayerId ||
            message.PlayerEntityId != client.PlayerEntityId)
            throw new ProtocolException(
                "The server sent private state for a different player.");

        var baseline = message.Flags.HasFlag(PlayerStateFlags.Baseline);
        var previous = client.Gameplay;
        if (!baseline && previous is null)
            throw new ProtocolException(
                "A player-state delta arrived before its baseline.");
        if (!baseline && message.Flags.HasFlag(PlayerStateFlags.Actor) &&
            previous!.ActorRevision != message.BaselineActorRevision)
            throw new ProtocolException(
                "A player-state delta does not match the actor baseline.");
        if (!baseline && message.Flags.HasFlag(PlayerStateFlags.Inventory) &&
            previous!.InventoryRevision != message.BaselineInventoryRevision)
            throw new ProtocolException(
                "A player-state delta does not match the inventory baseline.");

        var slots = baseline
            ? new InventorySlotState[ProtocolLimits.PlayerInventorySlots]
            : previous!.InventorySlots.ToArray();
        if (baseline)
            for (var slot = 0; slot < slots.Length; slot++)
                slots[slot] = new(slot, string.Empty, 0);
        if (message.Flags.HasFlag(PlayerStateFlags.Inventory))
            foreach (var slot in message.InventorySlots)
                slots[slot.Slot] = slot;

        var actorChanged = message.Flags.HasFlag(PlayerStateFlags.Actor);
        IReadOnlyList<QuestProgressState>? quests = actorChanged
            ? null
            : previous!.Quests;
        if (actorChanged)
        {
            try
            {
                var normalized = QuestService.Normalize((message.Quests ?? []).Select(quest =>
                    new QuestProgress(
                        quest.QuestId,
                        (QuestStatus)quest.Status,
                        quest.Objectives.ToDictionary(
                            objective => objective.ObjectiveId,
                            objective => objective.Count,
                            StringComparer.Ordinal),
                        quest.CompletionTick)).ToArray());
                quests = normalized.Select(quest => new QuestProgressState(
                    quest.QuestId, (byte)quest.Status, quest.CompletionTick,
                    (quest.ObjectiveCounts ??
                     Enumerable.Empty<KeyValuePair<string, int>>()).Select(value =>
                        new QuestObjectiveState(value.Key, value.Value)).ToArray())).ToArray();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new ProtocolException("Received invalid canonical quest state.");
            }
        }

        return new NetworkPlayerGameplayState(
            actorChanged ? message.ActorRevision : previous!.ActorRevision,
            message.Flags.HasFlag(PlayerStateFlags.Inventory)
                ? message.InventoryRevision
                : previous!.InventoryRevision,
            actorChanged ? message.Health : previous!.Health,
            actorChanged ? message.Hunger : previous!.Hunger,
            actorChanged ? message.WellFedSeconds : previous!.WellFedSeconds,
            actorChanged
                ? message.CraftingExperience
                : previous!.CraftingExperience,
            actorChanged
                ? message.CookingExperience
                : previous!.CookingExperience,
            Array.AsReadOnly(slots),
            actorChanged
                ? message.WoodcuttingExperience
                : previous!.WoodcuttingExperience,
            actorChanged
                ? message.FarmingExperience
                : previous!.FarmingExperience,
            actorChanged
                ? message.MiningExperience
                : previous!.MiningExperience,
            actorChanged
                ? message.AdventureExperience
                : previous!.AdventureExperience,
            actorChanged
                ? message.DiggingExperience
                : previous!.DiggingExperience,
            actorChanged
                ? message.FishingExperience
                : previous!.FishingExperience,
            actorChanged ? message.MaximumHealth : previous!.MaximumHealth,
            actorChanged ? message.AttackExperience : previous!.AttackExperience,
            actorChanged ? message.StrengthExperience : previous!.StrengthExperience,
            actorChanged ? message.DefenceExperience : previous!.DefenceExperience,
            actorChanged ? message.CombatStance : previous!.CombatStance,
            actorChanged ? message.LifeState : previous!.LifeState,
            actorChanged ? message.RespawnTick : previous!.RespawnTick,
            actorChanged ? message.CombatStatusFlags : previous!.CombatStatusFlags,
            actorChanged
                ? message.CombatTargetEnemyId == Guid.Empty
                    ? null
                    : message.CombatTargetEnemyId
                : previous!.CombatTargetEnemyId,
            quests);
    }

    private void UpdateTick(ulong tick)
    {
        lock (_stateSync)
        {
            if (tick <= _state.ServerTick) return;
            Volatile.Write(ref _state, _state with { ServerTick = tick });
        }
    }

    private void SetStatus(NetworkGameClientStatus status) => UpdateState(current => current with { Status = status });

    private void UpdateState(Func<NetworkGameClientState, NetworkGameClientState> change)
    {
        NetworkGameClientState next;
        lock (_stateSync)
        {
            next = change(_state);
            Volatile.Write(ref _state, next);
        }
        Raise(StateChanged, new NetworkClientStateChangedEventArgs(next));
    }

    private void SetState(NetworkGameClientState state)
    {
        lock (_stateSync)
        {
            _worldObjectRevisions.Clear();
            _boatRevisions.Clear();
            _enemyRevisions.Clear();
            _lastCombatEventOrdinal = 0;
            Volatile.Write(ref _state, state);
        }
        Raise(StateChanged, new NetworkClientStateChangedEventArgs(state));
    }

    private static IReadOnlyDictionary<Guid, NetworkPlayerPresence> With(
        IReadOnlyDictionary<Guid, NetworkPlayerPresence> source,
        NetworkPlayerPresence value)
    {
        var copy = source.ToDictionary();
        copy[value.PlayerId] = value;
        return ReadOnly(copy);
    }

    private static IReadOnlyDictionary<Guid, NetworkPlayerPresence> Without(
        IReadOnlyDictionary<Guid, NetworkPlayerPresence> source,
        Guid playerId)
    {
        var copy = source.ToDictionary();
        copy.Remove(playerId);
        return ReadOnly(copy);
    }

    private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(Dictionary<TKey, TValue> values)
        where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(values);

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

    private ulong NextSequence() => unchecked((ulong)Interlocked.Increment(ref _outboundSequence));

    private static Channel<IProtocolMessage> CreateOutboundChannel() => Channel.CreateBounded<IProtocolMessage>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            // QueueCommandAsync serializes authorship and admission, allowing
            // the channel to use its lower-overhead single-writer path.
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

    private void CleanupConnection()
    {
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _tcpClient?.Dispose();
        _snapshotSocket?.Dispose();
        _snapshotSocket = null;
        _snapshotReceiver = null;
        _tcpClient = null;
        _stream = null;
        _outbound = null;
        _readerTask = null;
        _writerTask = null;
        _snapshotReaderTask = null;
        ClearSnapshotProjection();
    }

    private void ClearSnapshotProjection()
    {
        lock (_snapshotSync)
        {
            _snapshotReconstructor.Clear();
            SnapshotBuffer.Clear();
        }
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private void Raise<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { /* A UI subscriber must not terminate the transport loop. */ }
        }
    }
}
