using System.Buffers.Binary;
using System.Collections.ObjectModel;
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
    private NetworkGameClientState _state = NetworkGameClientState.Disconnected;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private Channel<IProtocolMessage>? _outbound;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _readerTask;
    private Task? _writerTask;
    private long _outboundSequence;
    private ulong _lastInboundSequence;
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
            try
            {
                await tcpClient.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                var stream = tcpClient.GetStream();
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
                    options.ClientSnapshotPort,
                    options.Capabilities,
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
                    null));
                _readerTask = RunReaderAsync(stream, _connectionCancellation.Token);
                _writerTask = RunWriterAsync(stream, _outbound.Reader, _connectionCancellation.Token);
                return accepted;
            }
            catch (Exception exception)
            {
                tcpClient.Dispose();
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
        try
        {
            if (State.Status == NetworkGameClientStatus.Disconnected) return;
            SetStatus(NetworkGameClientStatus.Disconnecting);
            _outbound?.Writer.TryComplete();
            _connectionCancellation?.Cancel();
            _tcpClient?.Close();
            reader = _readerTask;
            writer = _writerTask;
        }
        finally
        {
            _lifecycle.Release();
        }

        await IgnoreCancellationAsync(reader).ConfigureAwait(false);
        await IgnoreCancellationAsync(writer).ConfigureAwait(false);
        CleanupConnection();
        SetState(NetworkGameClientState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async ValueTask<ulong> QueueCommandAsync(
        Func<ulong, IProtocolMessage> factory,
        CancellationToken cancellationToken)
    {
        if (State.Status != NetworkGameClientStatus.Connected || _outbound is null)
            throw new InvalidOperationException("The client is not connected.");
        var sequence = NextSequence();
        var message = factory(sequence);
        await _outbound.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        return sequence;
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
            {
                var entities = snapshot.Entities.ToDictionary(static entity => entity.EntityId);
                SnapshotBuffer.Add(snapshot);
                UpdateState(current => current with
                {
                    ServerTick = snapshot.Metadata.ServerTick,
                    Entities = ReadOnly(entities),
                });
                Raise(SnapshotReceived, new NetworkSnapshotEventArgs(snapshot with
                {
                    Entities = Array.AsReadOnly(snapshot.Entities.ToArray()),
                }));
                break;
            }
            default:
                throw new ProtocolException($"The server sent invalid reliable message kind {message.Kind} after handshake.");
        }
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
        lock (_stateSync) Volatile.Write(ref _state, state);
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

    private ulong NextSequence() => unchecked((ulong)Interlocked.Increment(ref _outboundSequence));

    private static Channel<IProtocolMessage> CreateOutboundChannel() => Channel.CreateBounded<IProtocolMessage>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

    private void CleanupConnection()
    {
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _stream = null;
        _outbound = null;
        _readerTask = null;
        _writerTask = null;
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
