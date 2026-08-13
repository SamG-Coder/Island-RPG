using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using IslandRpg.Protocol;

namespace IslandRpg.Client;

/// <summary>
/// Rendering-independent reliable game client. Event callbacks run on transport
/// tasks and should be marshalled by UI consumers rather than blocking the reader.
/// </summary>
public sealed class NetworkGameClient : IAsyncDisposable
{
    private const int OutboundCapacity = 256;
    private readonly object _stateSync = new();
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
    public event EventHandler<NetworkWorldObjectsChangedEventArgs>? WorldObjectsChanged;
    public event EventHandler<NetworkContainerStateEventArgs>? ContainerStateChanged;

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
            SnapshotBuffer.Clear();
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
                    options.ReconnectToken);
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
                var localPlayer = new NetworkPlayerPresence(accepted.PlayerId, options.PlayerName);
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
                    ReadOnly(new Dictionary<Guid, NetworkContainerState>())));
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
            case PlayerJoinedMessage joined:
            {
                var player = new NetworkPlayerPresence(joined.PlayerId, joined.PlayerName);
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
                    if (delta.Reference.ExpectedObjectRevision != knownRevision)
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
            value.GateState);

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

    private static NetworkWorldChunk Chunk(WorldObjectState value) =>
        new(value.ChunkX, value.ChunkY, value.WorldLevel);

    private static NetworkWorldChunk Chunk(WorldObjectReference value) =>
        new(value.ChunkX, value.ChunkY, value.WorldLevel);

    private void ConsumeSnapshot(EntitySnapshotMessage snapshot)
    {
        // The interpolation buffer owns chronological rejection across both
        // UDP publications and reliable recovery keyframes.
        if (!SnapshotBuffer.Add(snapshot))
            return;
        UpdateState(current => current with
        {
            ServerTick = Math.Max(current.ServerTick, snapshot.Metadata.ServerTick),
            Entities = MergeEntities(current.Entities, snapshot),
        });
        Raise(SnapshotReceived, new NetworkSnapshotEventArgs(snapshot with
        {
            Entities = Array.AsReadOnly(snapshot.Entities.ToArray()),
        }));
    }

    private static IReadOnlyDictionary<ulong, EntitySnapshot> MergeEntities(
        IReadOnlyDictionary<ulong, EntitySnapshot> previous,
        EntitySnapshotMessage snapshot)
    {
        var entities = snapshot.Metadata.Flags.HasFlag(SnapshotFlags.Delta)
            ? previous.ToDictionary()
            : new Dictionary<ulong, EntitySnapshot>();
        foreach (var entity in snapshot.Entities)
            entities[entity.EntityId] = entity;
        return ReadOnly(entities);
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
            Array.AsReadOnly(slots));
    }

    private void UpdateTick(ulong tick) => UpdateState(current => current with { ServerTick = Math.Max(current.ServerTick, tick) });

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
        SnapshotBuffer.Clear();
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
