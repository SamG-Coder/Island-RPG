using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IslandRpg.Protocol;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

internal sealed class ClientConnection : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpClient _client;
    private readonly DedicatedServer _server;
    private readonly CancellationTokenSource _lifetime;
    private readonly Channel<IProtocolMessage> _outbound;
    private readonly EntitySnapshot[] _snapshotSelection =
        new EntitySnapshot[UdpSnapshotCodec.MaxEntitiesPerDatagram];
    private long _nextOutboundSequence;
    private int _disposed;

    public ClientConnection(
        ClientConnectionId id,
        TcpClient client,
        DedicatedServer server,
        CancellationToken serverCancellation)
    {
        Id = id;
        _client = client;
        _server = server;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        _outbound = Channel.CreateBounded<IProtocolMessage>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        Completion = Task.CompletedTask;
    }

    public ClientConnectionId Id { get; }

    public bool Authenticated { get; private set; }

    public Guid PlayerId { get; private set; }

    public EndPoint RemoteEndPoint => _client.Client.RemoteEndPoint ??
        throw new InvalidOperationException("Connection has no remote endpoint.");

    public ulong PlayerEntityId { get; private set; }

    public IPEndPoint? SnapshotEndpoint { get; private set; }

    public ulong DatagramToken { get; private set; }

    public bool UdpSnapshotsEnabled => SnapshotEndpoint is not null && DatagramToken != 0;

    public bool DeltaSnapshotsEnabled { get; private set; }

    public Span<EntitySnapshot> SnapshotSelectionBuffer => _snapshotSelection;

    public Task Completion { get; private set; }

    public async Task RunAsync()
    {
        Completion = RunCoreAsync();
        await Completion.ConfigureAwait(false);
    }

    public ulong NextOutboundSequence() =>
        checked((ulong)Interlocked.Increment(ref _nextOutboundSequence));

    public ushort NextSnapshotSequence() =>
        unchecked((ushort)Interlocked.Increment(ref _nextSnapshotSequence));

    public int NextInterestOffset(int entityCount, int selectedCount)
    {
        var overflow = entityCount - selectedCount;
        if (overflow <= 0)
            return 0;
        return (int)((uint)Interlocked.Add(
            ref _interestCursor,
            selectedCount) % (uint)entityCount);
    }

    private int _nextSnapshotSequence;
    private int _interestCursor;

    public void ConfigureSnapshotTransport(
        IPEndPoint? endpoint,
        ulong datagramToken,
        ulong playerEntityId,
        bool deltaSnapshotsEnabled)
    {
        SnapshotEndpoint = endpoint;
        DatagramToken = endpoint is null ? 0 : datagramToken;
        PlayerEntityId = playerEntityId;
        DeltaSnapshotsEnabled = endpoint is not null && deltaSnapshotsEnabled;
    }

    public bool TryQueue(IProtocolMessage message) =>
        !_lifetime.IsCancellationRequested && _outbound.Writer.TryWrite(message);

    public void Stop() => _lifetime.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _outbound.Writer.TryComplete();
        _client.Dispose();
        _lifetime.Dispose();
        await Task.CompletedTask;
    }

    private async Task RunCoreAsync()
    {
        AuthenticatedPlayer? player = null;
        var stream = _client.GetStream();
        var writer = WriteLoopAsync(stream, _lifetime.Token);
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            handshakeTimeout.CancelAfter(HandshakeTimeout);
            var first = await TcpFrameCodec.ReadAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
            if (first is not HandshakeRequestMessage handshake)
            {
                throw new HandshakeFailure(
                    HandshakeRejectionCode.Unknown,
                    "The first reliable message must be a handshake request.");
            }

            try
            {
                player = await _server.AuthenticateAsync(this, handshake).ConfigureAwait(false);
            }
            catch (HandshakeFailure failure)
            {
                await TcpFrameCodec.WriteAsync(
                    stream,
                    _server.CreateHandshakeRejected(this, failure),
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

            if (!TryQueue(_server.CreateHandshakeAccepted(this, handshake, player.Value)))
            {
                return;
            }
            if (!TryQueue(_server.CreatePlayerStateBaseline(this, player.Value)))
            {
                return;
            }

            Authenticated = true;
            PlayerId = player.Value.Identity.PlayerId.Value;
            _server.AnnouncePlayerJoined(
                this,
                player.Value.Identity.PlayerId.Value,
                handshake.PlayerName);
            Console.WriteLine(
                $"Player {handshake.PlayerName} ({player.Value.Identity.PlayerId}) " +
                (player.Value.Reconnected ? "reconnected." : "joined."));

            while (!_lifetime.IsCancellationRequested)
            {
                var message = await TcpFrameCodec.ReadAsync(stream, _lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                IntentResult result;
                CommandRejectionCode rejection;
                string detail;
                try
                {
                    result = await _server.ProcessCommandAsync(this, player.Value, message)
                        .ConfigureAwait(false);
                    rejection = DedicatedServer.MapRejection(result.Status);
                    detail = result.Error ?? string.Empty;
                }
                catch (CommandFailure failure)
                {
                    result = default;
                    rejection = failure.Code;
                    detail = failure.Message;
                }

                var accepted = rejection == CommandRejectionCode.None;
                if (message is ActionCommandMessage actionCommand)
                {
                    _server.QueueActionOutcome(
                        this, player.Value, actionCommand, result);
                    continue;
                }
                if (!TryQueue(new CommandResultMessage(
                    NextOutboundSequence(),
                    checked((ulong)_server.CurrentTick),
                    message.Sequence,
                    accepted,
                    rejection,
                    detail)))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Authenticated = false;
            if (player is { } authenticated)
            {
                await _server.DisconnectAsync(this, authenticated).ConfigureAwait(false);
                _server.ReleaseClientId(authenticated.ClientId);
                _server.BroadcastPlayerLeft(
                    authenticated.Identity.PlayerId.Value,
                    PlayerLeaveReason.Disconnected,
                    "Connection closed.");
            }

            _outbound.Writer.TryComplete();
            _lifetime.Cancel();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
            {
            }
        }
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await TcpFrameCodec.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        }
    }
}
