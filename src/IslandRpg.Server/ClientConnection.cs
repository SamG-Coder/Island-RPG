using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IslandRpg.Protocol;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Server;

internal sealed class ClientConnection : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpClient _client;
    private readonly DedicatedServer _server;
    private readonly CancellationTokenSource _lifetime;
    private readonly Channel<OutboundPublication> _outbound;
    private readonly EntitySnapshot[] _snapshotSelection =
        new EntitySnapshot[UdpSnapshotCodec.MaxEntitiesPerDatagram];
    private readonly object _outboundSync = new();
    private Dictionary<WorldChunkKey, uint>? _publicWorldRevisions;
    private Dictionary<WorldChunkKey, uint>? _publicResourceRevisions;
    private Dictionary<Guid, uint>? _publicBoatRevisions;
    private Dictionary<Guid, uint>? _publicEnemyRevisions;
    private readonly PrivatePlayerStateHighWater _privatePlayerState = new();
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
        _outbound = Channel.CreateBounded<OutboundPublication>(new BoundedChannelOptions(128)
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

    public ulong NextOutboundSequence()
    {
        lock (_outboundSync)
        {
            return checked((ulong)++_nextOutboundSequence);
        }
    }

    /// <summary>
    /// Allocates a reliable sequence and publishes its message under the same
    /// lock. Concurrent broadcasts can therefore never enter the channel in a
    /// different order from their wire sequence numbers.
    /// </summary>
    public bool TryQueueSequenced(Func<ulong, IProtocolMessage> createMessage)
    {
        ArgumentNullException.ThrowIfNull(createMessage);
        lock (_outboundSync)
        {
            if (_lifetime.IsCancellationRequested) return false;
            var sequence = checked((ulong)_nextOutboundSequence + 1);
            var message = createMessage(sequence);
            if (message is PlayerStateMessage)
                throw new InvalidOperationException(
                    "Private player state must use its monotonic publication path.");
            if (!_outbound.Writer.TryWrite(OutboundPublication.Single(message)))
                return false;
            _nextOutboundSequence = checked((long)sequence);
            return true;
        }
    }

    /// <summary>
    /// Atomically publishes only the player-state sections newer than those
    /// already queued for this client. A command result captured before an
    /// autonomous combat mutation can therefore still publish its newer
    /// inventory section without rewinding the actor section. The projected
    /// delta is rebased to this connection's exact queued high-water.
    /// </summary>
    public bool TryQueuePrivateStateSequenced(
        Func<ulong, PlayerStateMessage> createMessage)
    {
        ArgumentNullException.ThrowIfNull(createMessage);
        lock (_outboundSync)
        {
            if (_lifetime.IsCancellationRequested) return false;
            var sequence = checked((ulong)_nextOutboundSequence + 1);
            var publication = _privatePlayerState.Project(
                createMessage(sequence));
            if (publication is null) return true;
            if (!_outbound.Writer.TryWrite(
                    OutboundPublication.Single(publication)))
                return false;
            _nextOutboundSequence = checked((long)sequence);
            _privatePlayerState.Observe(publication);
            return true;
        }
    }

    public uint PrivateActorRevisionHighWater
    {
        get
        {
            lock (_outboundSync) return _privatePlayerState.ActorRevision;
        }
    }

    public uint PrivateInventoryRevisionHighWater
    {
        get
        {
            lock (_outboundSync) return _privatePlayerState.InventoryRevision;
        }
    }

    /// <summary>
    /// Queues one public message while atomically allocating its wire
    /// sequence. A stale post-bootstrap mutation consumes no sequence, so
    /// filtering cannot create a reliable-stream hole.
    /// </summary>
    public bool TryQueuePublicSequenced(
        Func<ulong, IProtocolMessage> createMessage)
    {
        ArgumentNullException.ThrowIfNull(createMessage);
        lock (_outboundSync)
        {
            if (_lifetime.IsCancellationRequested) return false;
            var sequence = checked((ulong)_nextOutboundSequence + 1);
            var message = createMessage(sequence);
            var updates = ValidatePublicMessage(message);
            if (updates.IsStale)
                return true;
            if (!_outbound.Writer.TryWrite(OutboundPublication.Single(message)))
                return false;
            _nextOutboundSequence = checked((long)sequence);
            updates.Apply(
                _publicWorldRevisions,
                _publicResourceRevisions,
                _publicBoatRevisions,
                _publicEnemyRevisions);
            return true;
        }
    }

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

    public bool TryQueue(IProtocolMessage message)
    {
        lock (_outboundSync)
        {
            if (message is PlayerStateMessage)
                throw new InvalidOperationException(
                    "Private player state must use its monotonic publication path.");
            if (_lifetime.IsCancellationRequested ||
                !_outbound.Writer.TryWrite(OutboundPublication.Single(message)))
                return false;
            return true;
        }
    }

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
            if (!TryQueuePrivateStateSequenced(sequence =>
                    _server.CreatePlayerStateBaseline(sequence, player.Value)))
            {
                return;
            }
            if (!TryQueueSequenced(sequence =>
                    _server.CreateSocialStateBaseline(sequence, player.Value)))
            {
                return;
            }
            PlayerId = player.Value.Identity.PlayerId.Value;
            if (!_server.ActivateAndQueuePublicBaselines(this))
            {
                return;
            }
            _server.AnnouncePlayerJoined(
                this,
                player.Value.Identity.PlayerId.Value,
                handshake.PlayerName,
                handshake.Gender,
                handshake.TeamColor);
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

                if (message is WalkCommandMessage or StopCommandMessage or
                    PresentSkillCommandMessage)
                {
                    _ = ProcessMovementCommandAsync(player.Value, message);
                    continue;
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
                if (!TryQueueSequenced(sequence => new CommandResultMessage(
                    sequence,
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

    private async Task ProcessMovementCommandAsync(
        AuthenticatedPlayer player,
        IProtocolMessage message)
    {
        try
        {
            var result = await _server.ProcessCommandAsync(this, player, message)
                .ConfigureAwait(false);
            _server.PublishSocialFromIntent(result);
            var rejection = DedicatedServer.MapRejection(result.Status);
            var accepted = rejection == CommandRejectionCode.None;
            if (!TryQueueSequenced(sequence => new CommandResultMessage(
                    sequence,
                    checked((ulong)_server.CurrentTick),
                    message.Sequence,
                    accepted,
                    rejection,
                    result.Error ?? string.Empty)))
            {
                Stop();
            }
        }
        catch (CommandFailure failure)
        {
            if (!TryQueueSequenced(sequence => new CommandResultMessage(
                    sequence,
                    checked((ulong)_server.CurrentTick),
                    message.Sequence,
                    false,
                    failure.Code,
                    failure.Message)))
            {
                Stop();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            Stop();
        }
    }

    /// <summary>
    /// Queues the complete public bootstrap as one bounded-channel
    /// publication and atomically enters public replication. The publication
    /// reserves one contiguous reliable-sequence range, but creates and
    /// writes its protocol messages lazily. A large bootstrap therefore
    /// cannot consume every per-connection queue slot while the server holds
    /// its global bootstrap/broadcast barrier.
    /// </summary>
    internal bool TryQueuePublicBootstrapAndActivate(
        IEnumerable<WorldChunkRevisionState> worldChunks,
        IEnumerable<ResourceChunkSparseState> resourceChunks,
        IEnumerable<AuthoritativeBoatSnapshot> boats,
        IEnumerable<AuthoritativeEnemySnapshot> enemies,
        int messageCount,
        Func<ulong, IEnumerable<IProtocolMessage>> createMessages)
    {
        ArgumentNullException.ThrowIfNull(worldChunks);
        ArgumentNullException.ThrowIfNull(resourceChunks);
        ArgumentNullException.ThrowIfNull(boats);
        ArgumentNullException.ThrowIfNull(enemies);
        ArgumentNullException.ThrowIfNull(createMessages);
        if (messageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(messageCount));

        var worldRevisions = worldChunks.ToDictionary(
            static value => new WorldChunkKey(
                value.ChunkX, value.ChunkY, value.WorldLevel),
            static value => value.Revision);
        var resourceRevisions = resourceChunks.ToDictionary(
            static value => value.Chunk,
            static value => value.ResourceChunkRevision);
        var boatRevisions = boats.ToDictionary(
            static value => value.BoatId.Value,
            static value => value.Revision);
        var enemyRevisions = enemies.ToDictionary(
            static value => value.EnemyId.Value,
            static value => value.Revision);

        lock (_outboundSync)
        {
            if (_lifetime.IsCancellationRequested) return false;
            var firstSequence = checked((ulong)_nextOutboundSequence + 1);
            var lastSequence = checked(
                firstSequence + checked((ulong)messageCount - 1));
            if (lastSequence > long.MaxValue)
                throw new OverflowException(
                    "The public bootstrap exhausted reliable sequences.");
            var publication = OutboundPublication.Batch(
                firstSequence,
                messageCount,
                createMessages);
            if (!_outbound.Writer.TryWrite(publication)) return false;

            _nextOutboundSequence = checked((long)lastSequence);
            _publicWorldRevisions = worldRevisions;
            _publicResourceRevisions = resourceRevisions;
            _publicBoatRevisions = boatRevisions;
            _publicEnemyRevisions = enemyRevisions;
            Authenticated = true;
            return true;
        }
    }

    private PublicRevisionUpdates ValidatePublicMessage(
        IProtocolMessage message)
    {
        return message switch
        {
            WorldObjectDeltaBatchMessage world =>
                ValidateWorld(world, _publicWorldRevisions),
            ResourceNodeDeltaBatchMessage resources =>
                ValidateResources(resources, _publicResourceRevisions),
            BoatDeltaBatchMessage boats =>
                ValidateBoats(boats, _publicBoatRevisions),
            EnemyDeltaBatchMessage enemies =>
                ValidateEnemies(enemies, _publicEnemyRevisions),
            _ => PublicRevisionUpdates.None
        };
    }

    private static PublicRevisionUpdates ValidateWorld(
        WorldObjectDeltaBatchMessage message,
        Dictionary<WorldChunkKey, uint>? revisions)
    {
        if (revisions is null)
            throw new InvalidOperationException(
                "Public world high-water was not initialized.");
        var groups = message.Deltas.GroupBy(static value => new WorldChunkKey(
                value.Reference.ChunkX,
                value.Reference.ChunkY,
                value.Reference.WorldLevel))
            .ToArray();
        var next = new List<(WorldChunkKey Chunk, uint Revision)>();
        var stale = false;
        foreach (var group in groups)
        {
            revisions.TryGetValue(group.Key, out var known);
            var first = group.First();
            if (group.Any(value =>
                    value.Reference.ExpectedChunkRevision !=
                    first.Reference.ExpectedChunkRevision ||
                    value.CurrentChunkRevision !=
                    first.CurrentChunkRevision))
                throw new InvalidOperationException(
                    "A public world batch contained conflicting chunk revisions.");
            if (first.CurrentChunkRevision <=
                first.Reference.ExpectedChunkRevision)
                throw new InvalidOperationException(
                    "A public world delta did not advance its chunk revision.");
            if (first.CurrentChunkRevision <= known)
            {
                stale = true;
                continue;
            }
            if (first.Reference.ExpectedChunkRevision != known)
                throw new InvalidOperationException(
                    "A public world delta lost its per-connection revision chain.");
            next.Add((group.Key, first.CurrentChunkRevision));
        }
        if (stale && next.Count != 0)
            throw new InvalidOperationException(
                "A public world batch straddled the retained bootstrap revision.");
        return next.Count == 0
            ? PublicRevisionUpdates.Stale
            : new PublicRevisionUpdates(World: next);
    }

    private static PublicRevisionUpdates ValidateResources(
        ResourceNodeDeltaBatchMessage message,
        Dictionary<WorldChunkKey, uint>? revisions)
    {
        if (revisions is null)
            throw new InvalidOperationException(
                "Public resource high-water was not initialized.");
        var next = new List<(WorldChunkKey Chunk, uint Revision)>();
        var stale = false;
        foreach (var group in message.Deltas.GroupBy(
                     static value => value.Reference.Chunk))
        {
            revisions.TryGetValue(group.Key, out var known);
            var first = group.First();
            if (group.Any(value =>
                    value.Reference.ExpectedResourceChunkRevision !=
                    first.Reference.ExpectedResourceChunkRevision ||
                    value.CurrentResourceChunkRevision !=
                    first.CurrentResourceChunkRevision))
                throw new InvalidOperationException(
                    "A public resource batch contained conflicting chunk revisions.");
            if (first.CurrentResourceChunkRevision <=
                first.Reference.ExpectedResourceChunkRevision)
                throw new InvalidOperationException(
                    "A public resource delta did not advance its chunk revision.");
            if (first.CurrentResourceChunkRevision <= known)
            {
                stale = true;
                continue;
            }
            if (first.Reference.ExpectedResourceChunkRevision != known)
                throw new InvalidOperationException(
                    "A public resource delta lost its per-connection revision chain.");
            next.Add((group.Key, first.CurrentResourceChunkRevision));
        }
        if (stale && next.Count != 0)
            throw new InvalidOperationException(
                "A public resource batch straddled the retained bootstrap revision.");
        return next.Count == 0
            ? PublicRevisionUpdates.Stale
            : new PublicRevisionUpdates(Resources: next);
    }

    private static PublicRevisionUpdates ValidateBoats(
        BoatDeltaBatchMessage message,
        Dictionary<Guid, uint>? revisions)
    {
        if (revisions is null)
            throw new InvalidOperationException(
                "Public boat high-water was not initialized.");
        var next = new List<(Guid BoatId, uint Revision)>();
        var stale = false;
        var seen = new HashSet<Guid>();
        foreach (var delta in message.Deltas)
        {
            if (!seen.Add(delta.Reference.BoatId))
                throw new InvalidOperationException(
                    "A public boat batch changed one boat more than once.");
            if (delta.CurrentRevision <= delta.Reference.ExpectedRevision)
                throw new InvalidOperationException(
                    "A public boat delta did not advance its revision.");
            var retained = revisions.TryGetValue(
                delta.Reference.BoatId, out var known);
            if (delta.CurrentRevision <= known)
            {
                stale = true;
                continue;
            }
            // A removal can race after activation captured a baseline in
            // which the boat was already absent. That baseline is the newer
            // complete state, so this queued removal is stale for this one
            // connection and must not consume a reliable sequence. Upserts
            // still require an exact retained revision chain below.
            if (!retained && delta.Kind == BoatDeltaKind.Remove)
            {
                stale = true;
                continue;
            }
            if (delta.Reference.ExpectedRevision != known)
                throw new InvalidOperationException(
                    "A public boat delta lost its per-connection revision chain.");
            next.Add((delta.Reference.BoatId, delta.CurrentRevision));
        }
        if (stale && next.Count != 0)
            throw new InvalidOperationException(
                "A public boat batch straddled the retained bootstrap revision.");
        return next.Count == 0
            ? PublicRevisionUpdates.Stale
            : new PublicRevisionUpdates(Boats: next);
    }

    private static PublicRevisionUpdates ValidateEnemies(
        EnemyDeltaBatchMessage message,
        Dictionary<Guid, uint>? revisions)
    {
        if (revisions is null)
            throw new InvalidOperationException(
                "Public enemy high-water was not initialized.");
        var next = new List<(Guid EnemyId, uint Revision)>();
        var stale = false;
        var seen = new HashSet<Guid>();
        foreach (var delta in message.Deltas)
        {
            if (!seen.Add(delta.Reference.EnemyId))
                throw new InvalidOperationException(
                    "A public enemy batch changed one enemy more than once.");
            if (delta.CurrentRevision <= delta.Reference.ExpectedRevision)
                throw new InvalidOperationException(
                    "A public enemy delta did not advance its revision.");
            revisions.TryGetValue(delta.Reference.EnemyId, out var known);
            if (delta.CurrentRevision <= known)
            {
                stale = true;
                continue;
            }
            if (delta.Reference.ExpectedRevision != known)
                throw new InvalidOperationException(
                    "A public enemy delta lost its per-connection revision chain.");
            next.Add((delta.Reference.EnemyId, delta.CurrentRevision));
        }
        if (stale && next.Count != 0)
            throw new InvalidOperationException(
                "A public enemy batch straddled the retained bootstrap revision.");
        return next.Count == 0
            ? PublicRevisionUpdates.Stale
            : new PublicRevisionUpdates(Enemies: next);
    }

    private sealed record PublicRevisionUpdates(
        IReadOnlyList<(WorldChunkKey Chunk, uint Revision)>? World = null,
        IReadOnlyList<(WorldChunkKey Chunk, uint Revision)>? Resources = null,
        IReadOnlyList<(Guid BoatId, uint Revision)>? Boats = null,
        IReadOnlyList<(Guid EnemyId, uint Revision)>? Enemies = null,
        bool IsStale = false)
    {
        public static PublicRevisionUpdates None { get; } = new();
        public static PublicRevisionUpdates Stale { get; } = new(IsStale: true);

        public void Apply(
            Dictionary<WorldChunkKey, uint>? world,
            Dictionary<WorldChunkKey, uint>? resources,
            Dictionary<Guid, uint>? boats,
            Dictionary<Guid, uint>? enemies)
        {
            if (World is not null)
                foreach (var value in World)
                    world![value.Chunk] = value.Revision;
            if (Resources is not null)
                foreach (var value in Resources)
                    resources![value.Chunk] = value.Revision;
            if (Boats is not null)
                foreach (var value in Boats)
                    boats![value.BoatId] = value.Revision;
            if (Enemies is not null)
                foreach (var value in Enemies)
                    enemies![value.EnemyId] = value.Revision;
        }
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await foreach (var publication in
                       _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await WritePublicationAsync(
                    stream, publication, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WritePublicationAsync(
        NetworkStream stream,
        OutboundPublication publication,
        CancellationToken cancellationToken)
    {
        var timeout = _server.OutboundPublicationWriteTimeout;
        if (timeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "The outbound publication write timeout must be positive.");
        using var inactivityDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        try
        {
            // Queue residence has its own hard bound: do not expand a lazy
            // publication that could no longer represent a responsive peer.
            // Once expansion starts, every completed frame is observable
            // progress and renews the inactivity deadline below.
            if (Stopwatch.GetElapsedTime(publication.EnqueuedTimestamp) >=
                timeout)
            {
                inactivityDeadline.Cancel();
                inactivityDeadline.Token.ThrowIfCancellationRequested();
            }
            inactivityDeadline.CancelAfter(timeout);
            if (publication.SingleMessage is { } single)
            {
                await WriteMessageAsync(
                        stream, single, inactivityDeadline.Token)
                    .ConfigureAwait(false);
                return;
            }

            var expectedSequence = publication.FirstSequence;
            var written = 0;
            foreach (var message in publication.CreateMessages())
            {
                if (written >= publication.MessageCount)
                    throw new InvalidOperationException(
                        "An outbound publication produced too many messages.");
                if (message.Sequence != expectedSequence)
                    throw new InvalidOperationException(
                        "An outbound publication broke its reserved sequence range.");
                inactivityDeadline.Token.ThrowIfCancellationRequested();
                await WriteMessageAsync(
                        stream, message, inactivityDeadline.Token)
                    .ConfigureAwait(false);
                written++;
                expectedSequence = checked(expectedSequence + 1);
                inactivityDeadline.CancelAfter(timeout);
            }
            if (written != publication.MessageCount)
                throw new InvalidOperationException(
                    "An outbound publication produced too few messages.");
        }
        catch (OperationCanceledException) when (
            inactivityDeadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            // A queued lazy batch retains the captured bootstrap generation.
            // Cancelling the connection after queue starvation or a full
            // frame of inactivity unwinds this writer and releases that
            // closure instead of allowing one stalled peer to pin it forever.
            Stop();
            throw;
        }
    }

    private async ValueTask WriteMessageAsync(
        NetworkStream stream,
        IProtocolMessage message,
        CancellationToken cancellationToken)
    {
        var beforeWrite = _server.BeforeOutboundWriteForTest;
        if (beforeWrite is not null)
            await beforeWrite(this, message, cancellationToken)
                .ConfigureAwait(false);
        await TcpFrameCodec.WriteAsync(stream, message, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class OutboundPublication
    {
        private readonly Func<ulong, IEnumerable<IProtocolMessage>>?
            _createMessages;

        private OutboundPublication(IProtocolMessage message)
        {
            EnqueuedTimestamp = Stopwatch.GetTimestamp();
            FirstSequence = message.Sequence;
            MessageCount = 1;
            SingleMessage = message;
        }

        private OutboundPublication(
            ulong firstSequence,
            int messageCount,
            Func<ulong, IEnumerable<IProtocolMessage>> createMessages)
        {
            EnqueuedTimestamp = Stopwatch.GetTimestamp();
            FirstSequence = firstSequence;
            MessageCount = messageCount;
            _createMessages = createMessages;
        }

        public long EnqueuedTimestamp { get; }

        public ulong FirstSequence { get; }

        public int MessageCount { get; }

        public IProtocolMessage? SingleMessage { get; }

        public IEnumerable<IProtocolMessage> CreateMessages() =>
            (_createMessages ?? throw new InvalidOperationException(
                "A single-message publication cannot be expanded as a batch."))(
                FirstSequence);

        public static OutboundPublication Single(IProtocolMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            return new OutboundPublication(message);
        }

        public static OutboundPublication Batch(
            ulong firstSequence,
            int messageCount,
            Func<ulong, IEnumerable<IProtocolMessage>> createMessages) =>
            new(firstSequence, messageCount, createMessages);
    }
}

/// <summary>
/// Per-connection high-water for the independently revisioned private actor
/// and inventory sections. All calls are serialized by ClientConnection's
/// outbound lock; this type is separate only to keep projection deterministic
/// and directly regression-testable.
/// </summary>
internal sealed class PrivatePlayerStateHighWater
{
    public uint ActorRevision { get; private set; }

    public uint InventoryRevision { get; private set; }

    public void Observe(PlayerStateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Flags.HasFlag(PlayerStateFlags.Actor))
            ActorRevision = Math.Max(ActorRevision, message.ActorRevision);
        if (message.Flags.HasFlag(PlayerStateFlags.Inventory))
            InventoryRevision = Math.Max(
                InventoryRevision, message.InventoryRevision);
    }

    public PlayerStateMessage? Project(PlayerStateMessage candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (ActorRevision == 0 && InventoryRevision == 0 &&
            candidate.Flags.HasFlag(PlayerStateFlags.Baseline))
            return candidate;
        var publishActor =
            candidate.Flags.HasFlag(PlayerStateFlags.Actor) &&
            candidate.ActorRevision > ActorRevision;
        var publishInventory =
            candidate.Flags.HasFlag(PlayerStateFlags.Inventory) &&
            candidate.InventoryRevision > InventoryRevision;
        if (!publishActor && !publishInventory)
            return null;

        var flags = PlayerStateFlags.None;
        if (publishActor) flags |= PlayerStateFlags.Actor;
        if (publishInventory) flags |= PlayerStateFlags.Inventory;
        return candidate with
        {
            Flags = flags,
            BaselineActorRevision = ActorRevision,
            BaselineInventoryRevision = InventoryRevision,
            ActorRevision = publishActor
                ? candidate.ActorRevision
                : ActorRevision,
            InventoryRevision = publishInventory
                ? candidate.InventoryRevision
                : InventoryRevision,
            InventorySlots = publishInventory
                ? candidate.InventorySlots
                : Array.Empty<InventorySlotState>(),
            CombatTargetEnemyId = publishActor
                ? candidate.CombatTargetEnemyId
                : Guid.Empty,
            Quests = publishActor
                ? candidate.Quests
                : Array.Empty<QuestProgressState>()
        };
    }
}
