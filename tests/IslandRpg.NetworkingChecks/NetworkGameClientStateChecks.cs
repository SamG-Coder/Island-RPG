using System.Net;
using System.Net.Sockets;
using IslandRpg.Client;
using IslandRpg.Protocol;

namespace IslandRpg.NetworkingChecks;

internal static class NetworkGameClientStateChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string BuildVersion = "client-state-checks";
    private const string ContentVersion = "client-state-content";

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "network client applies a full player baseline and merges deltas",
            AppliesBaselineAndDeltaAsync);
        checks.Add(
            "network client faults on player-state revision mismatches",
            FaultsOnRevisionMismatchAsync);
        checks.Add(
            "network client actions require a baseline and encode its revisions",
            RequiresBaselineAndEncodesActionRevisionsAsync);
        checks.Add(
            "network client publishes concurrent commands in sequence order",
            PublishesConcurrentCommandsInSequenceOrderAsync);
        checks.Add(
            "network client publishes typed cave action outcomes",
            PublishesTypedCaveOutcomeAsync);
        checks.Add(
            "network client rejects boat batches atomically",
            RejectsBoatBatchAtomicallyAsync);
    }

    private static async ValueTask AppliesBaselineAndDeltaAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client,
            cancellationToken);
        var slots = CreateFullInventory();
        var baseline = CreateBaseline(peer, 2, 500, 7, 11, slots);

        await peer.SendAsync(baseline, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.InventoryRevision == 11,
            "the client did not apply the player baseline",
            cancellationToken);

        var initial = client.State.Gameplay!;
        CheckAssert.Equal(ProtocolLimits.PlayerInventorySlots,
            initial.InventorySlots.Count,
            "the baseline must retain every fixed inventory slot");
        CheckAssert.SequenceEqual(slots, initial.InventorySlots,
            "the full 28-slot baseline must be applied without reordering");
        CheckAssert.Equal(7u, initial.ActorRevision,
            "the baseline actor revision must be applied");
        CheckAssert.Equal(11u, initial.InventoryRevision,
            "the baseline inventory revision must be applied");
        CheckAssert.Equal(86, initial.Health,
            "the baseline health must be applied");
        CheckAssert.Equal(62.5f, initial.Hunger,
            "the baseline hunger must be applied");
        CheckAssert.Equal(12.25f, initial.WellFedSeconds,
            "the baseline survival effect must be applied");
        CheckAssert.Equal(320, initial.CraftingExperience,
            "the baseline crafting experience must be applied");
        CheckAssert.Equal(210, initial.CookingExperience,
            "the baseline cooking experience must be applied");
        CheckAssert.Equal(610, initial.WoodcuttingExperience,
            "the baseline woodcutting experience must be applied");
        CheckAssert.Equal(720, initial.FarmingExperience,
            "the baseline farming experience must be applied");
        CheckAssert.Equal(830, initial.MiningExperience,
            "the baseline mining experience must be applied");
        CheckAssert.Equal(940, initial.AdventureExperience,
            "the baseline adventure experience must be applied");
        CheckAssert.Equal(1050, initial.DiggingExperience,
            "the baseline digging experience must be applied");

        var deltaSlots = new[]
        {
            new InventorySlotState(0, string.Empty, 0),
            new InventorySlotState(14, "delta_rope", 3),
            new InventorySlotState(27, "delta_berries", 9),
        };
        var delta = new PlayerStateMessage(
            3,
            525,
            peer.PlayerId,
            peer.PlayerEntityId,
            PlayerStateFlags.Actor | PlayerStateFlags.Inventory,
            7,
            11,
            8,
            12,
            91,
            48.75f,
            30.5f,
            400,
            275,
            deltaSlots,
            611,
            721,
            831,
            941,
            1051);

        await peer.SendAsync(delta, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.InventoryRevision == 12,
            "the client did not apply the valid player-state delta",
            cancellationToken);

        var merged = client.State.Gameplay!;
        var expectedSlots = slots.ToArray();
        foreach (var slot in deltaSlots) expectedSlots[slot.Slot] = slot;
        CheckAssert.SequenceEqual(expectedSlots, merged.InventorySlots,
            "inventory deltas must replace only their indexed slots");
        CheckAssert.Equal(8u, merged.ActorRevision,
            "an actor delta must advance the actor revision");
        CheckAssert.Equal(12u, merged.InventoryRevision,
            "an inventory delta must advance the inventory revision");
        CheckAssert.Equal(91, merged.Health,
            "an actor delta must replace health");
        CheckAssert.Equal(48.75f, merged.Hunger,
            "an actor delta must replace hunger");
        CheckAssert.Equal(30.5f, merged.WellFedSeconds,
            "an actor delta must replace the survival effect");
        CheckAssert.Equal(400, merged.CraftingExperience,
            "an actor delta must replace crafting experience");
        CheckAssert.Equal(275, merged.CookingExperience,
            "an actor delta must replace cooking experience");
        CheckAssert.Equal(721, merged.FarmingExperience,
            "an actor delta must replace farming experience");
        CheckAssert.Equal(831, merged.MiningExperience,
            "an actor delta must replace mining experience");
        CheckAssert.Equal(941, merged.AdventureExperience,
            "an actor delta must replace adventure experience");
        CheckAssert.Equal(1051, merged.DiggingExperience,
            "an actor delta must replace digging experience");
        CheckAssert.Equal(525ul, client.State.ServerTick,
            "player-state application must advance the observed server tick");

        await client.DisconnectAsync(cancellationToken);
    }

    private static async ValueTask FaultsOnRevisionMismatchAsync(
        CancellationToken cancellationToken)
    {
        await AssertRevisionMismatchFaultsAsync(
            PlayerStateFlags.Actor,
            "actor baseline",
            cancellationToken);
        await AssertRevisionMismatchFaultsAsync(
            PlayerStateFlags.Inventory,
            "inventory baseline",
            cancellationToken);
    }

    private static async Task AssertRevisionMismatchFaultsAsync(
        PlayerStateFlags changedSection,
        string expectedError,
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client,
            cancellationToken);
        var stateEvents = 0;
        client.PlayerStateChanged += (_, _) => Interlocked.Increment(ref stateEvents);
        var baseline = CreateBaseline(
            peer,
            2,
            600,
            17,
            23,
            CreateFullInventory());
        await peer.SendAsync(baseline, cancellationToken);
        await EventuallyAsync(
            () => Volatile.Read(ref stateEvents) == 1,
            "the mismatch check did not first receive its baseline",
            cancellationToken);
        var acceptedState = client.State.Gameplay;

        var actorChanged = changedSection == PlayerStateFlags.Actor;
        var mismatch = new PlayerStateMessage(
            3,
            601,
            peer.PlayerId,
            peer.PlayerEntityId,
            changedSection,
            actorChanged ? 999u : 17u,
            actorChanged ? 23u : 999u,
            actorChanged ? 18u : 17u,
            actorChanged ? 23u : 24u,
            70,
            40,
            5,
            330,
            220,
            actorChanged
                ? Array.Empty<InventorySlotState>()
                : [new InventorySlotState(5, "mismatched_delta", 1)]);

        await peer.SendAsync(mismatch, cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            $"the client did not fault on the {expectedError} mismatch",
            cancellationToken);

        CheckAssert.True(
            client.State.LastError?.Contains(
                expectedError,
                StringComparison.OrdinalIgnoreCase) == true,
            $"the client fault must identify the {expectedError} mismatch");
        CheckAssert.Equal(1, Volatile.Read(ref stateEvents),
            "a rejected delta must not raise a player-state event");
        CheckAssert.True(ReferenceEquals(acceptedState, client.State.Gameplay),
            "a rejected delta must leave the last accepted gameplay state intact");
    }

    private static async ValueTask RequiresBaselineAndEncodesActionRevisionsAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client,
            cancellationToken);

        var missingBaseline = CheckAssert.Throws<InvalidOperationException>(
            () =>
            {
                _ = client.SendActionAsync(new ConsumeItemAction(4));
            },
            "actions must be rejected until private player state arrives");
        CheckAssert.True(
            missingBaseline.Message.Contains(
                "baseline",
                StringComparison.OrdinalIgnoreCase),
            "the pre-baseline action failure must explain what is missing");

        var baseline = CreateBaseline(
            peer,
            2,
            725,
            41,
            73,
            CreateFullInventory());
        await peer.SendAsync(baseline, cancellationToken);
        await EventuallyAsync(
            () => client.State.Gameplay?.ActorRevision == 41,
            "the action check did not receive its player baseline",
            cancellationToken);

        var commandId = Guid.NewGuid();
        var payload = new InventorySwapAction(3, 19);
        var sequence = await client.SendActionAsync(
            payload,
            commandId,
            cancellationToken);
        var outbound = await peer.ReceiveAsync(cancellationToken);
        CheckAssert.True(outbound is ActionCommandMessage,
            "SendActionAsync must emit an action-command frame");
        var action = (ActionCommandMessage)outbound;
        CheckAssert.Equal(ScriptedPeer.FirstCommandSequence, sequence,
            "a rejected pre-baseline action must not consume a command sequence");
        CheckAssert.Equal(sequence, action.Sequence,
            "the encoded action must use the sequence returned to its caller");
        CheckAssert.Equal(725ul, action.Tick,
            "the encoded action must use the latest authoritative tick");
        CheckAssert.Equal(commandId, action.CommandId,
            "the encoded action must preserve its correlation id");
        CheckAssert.Equal(41u, action.ActorRevision,
            "the encoded action must target the applied actor revision");
        CheckAssert.Equal(73u, action.InventoryRevision,
            "the encoded action must target the applied inventory revision");
        CheckAssert.Equal(payload, action.Payload,
            "the encoded action payload must round trip through the real stream");

        await client.DisconnectAsync(cancellationToken);
    }

    private static async ValueTask PublishesConcurrentCommandsInSequenceOrderAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client,
            cancellationToken);

        const int commandCount = 384;
        using var start = new ManualResetEventSlim(false);
        var sends = Enumerable.Range(0, commandCount)
            .Select(index => Task.Run(async () =>
            {
                start.Wait(cancellationToken);
                return await client.SendChatAsync(
                    $"ordered-{index}",
                    cancellationToken: cancellationToken);
            }, cancellationToken))
            .ToArray();
        start.Set();

        var received = new List<ulong>(commandCount);
        for (var index = 0; index < commandCount; index++)
        {
            var message = await peer.ReceiveAsync(cancellationToken);
            CheckAssert.True(message is ChatCommandMessage,
                "every concurrent send must publish a chat command");
            received.Add(message.Sequence);
        }
        var returned = await Task.WhenAll(sends).WaitAsync(
            Timeout, cancellationToken);

        CheckAssert.SequenceEqual(
            Enumerable.Range(0, commandCount)
                .Select(index => ScriptedPeer.FirstCommandSequence +
                    checked((ulong)index)),
            received,
            "TCP publication order must exactly follow assigned command sequences");
        CheckAssert.SequenceEqual(
            received.Order(),
            returned.Order(),
            "every sequence returned to concurrent callers must be published once");
    }

    private static async ValueTask PublishesTypedCaveOutcomeAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client, cancellationToken);
        var commandId = Guid.Parse(
            "cacacaca-caca-caca-caca-cacacacacaca");
        var expected = new CaveActionResultMessage(
            2, 777, commandId, CaveActionKind.WorkExcavation,
            true, CommandRejectionCode.None, "cave_discovered", 8, 12,
            false, 0, 0, 0, 9, true);
        var completion = new TaskCompletionSource<CaveActionResultMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.CaveActionCompleted += (_, args) =>
            completion.TrySetResult(args.Result);

        await peer.SendAsync(expected, cancellationToken);
        var actual = await completion.Task.WaitAsync(Timeout, cancellationToken);
        CheckAssert.Equal(expected, actual,
            "the client event must expose the full typed cave receipt");
        await EventuallyAsync(
            () => client.State.ServerTick == expected.Tick,
            "the cave receipt did not advance the observed server tick",
            cancellationToken);
    }

    private static async ValueTask RejectsBoatBatchAtomicallyAsync(
        CancellationToken cancellationToken)
    {
        await using var client = new NetworkGameClient(TimeSpan.Zero);
        await using var peer = await ScriptedPeer.ConnectAsync(
            client, cancellationToken);
        var firstId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse(
            "b0a70000-0000-0000-0000-000000000002");
        var first = Boat(firstId, 1, peer.PlayerId, 0x8000_0000_0000_0001);
        var second = Boat(secondId, 1, peer.PlayerId, 0x8000_0000_0000_0002);
        await peer.SendAsync(new BoatBaselineMessage(
            2, 500, [first, second]), cancellationToken);
        await EventuallyAsync(
            () => client.State.Boats.Count == 2,
            "the boat atomicity check did not receive its baseline",
            cancellationToken);

        var poisoned = new BoatDeltaBatchMessage(
            3,
            501,
            [
                new BoatDelta(
                    BoatDeltaKind.Upsert,
                    new BoatReference(firstId, 1),
                    2,
                    first with { Revision = 2, X = 1 }),
                new BoatDelta(
                    BoatDeltaKind.Upsert,
                    new BoatReference(secondId, 0),
                    1,
                    second)
            ]);
        await peer.SendAsync(poisoned, cancellationToken);
        await EventuallyAsync(
            () => client.State.Status == NetworkGameClientStatus.Faulted,
            "the client did not fault on the malformed second boat delta",
            cancellationToken);

        CheckAssert.Equal(first, client.State.Boats[firstId],
            "a malformed later delta must not partially apply earlier state");
        CheckAssert.Equal(second, client.State.Boats[secondId],
            "a rejected boat batch must preserve every visible boat");
        CheckAssert.Equal(new BoatReference(firstId, 1),
            client.GetBoatReference(firstId),
            "a rejected boat batch must not poison retained revision high-water");
        CheckAssert.Equal(new BoatReference(secondId, 1),
            client.GetBoatReference(secondId),
            "a rejected boat batch must preserve later boat revision state");
    }

    private static BoatState Boat(
        Guid id,
        uint revision,
        Guid owner,
        ulong entityId) => new(
        id,
        entityId,
        revision,
        owner,
        string.Empty,
        Guid.Empty,
        0,
        0,
        0,
        0,
        1,
        0,
        false);

    private static InventorySlotState[] CreateFullInventory() =>
        Enumerable.Range(0, ProtocolLimits.PlayerInventorySlots)
            .Select(static slot => new InventorySlotState(
                slot,
                $"baseline_item_{slot}",
                slot + 1))
            .ToArray();

    private static PlayerStateMessage CreateBaseline(
        ScriptedPeer peer,
        ulong sequence,
        ulong tick,
        uint actorRevision,
        uint inventoryRevision,
        IReadOnlyList<InventorySlotState> slots) => new(
            sequence,
            tick,
            peer.PlayerId,
            peer.PlayerEntityId,
            PlayerStateFlags.Baseline |
            PlayerStateFlags.Actor |
            PlayerStateFlags.Inventory,
            0,
            0,
            actorRevision,
            inventoryRevision,
            86,
            62.5f,
            12.25f,
            320,
            210,
            slots,
            610,
            720,
            830,
            940,
            1050);

    private static async Task EventuallyAsync(
        Func<bool> condition,
        string failure,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(failure);
    }

    private sealed class ScriptedPeer : IAsyncDisposable
    {
        public const ulong FirstCommandSequence = 400;
        private readonly TcpListener _listener;
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;

        private ScriptedPeer(
            TcpListener listener,
            TcpClient tcpClient,
            NetworkStream stream,
            Guid playerId,
            ulong playerEntityId)
        {
            _listener = listener;
            _tcpClient = tcpClient;
            _stream = stream;
            PlayerId = playerId;
            PlayerEntityId = playerEntityId;
        }

        public Guid PlayerId { get; }
        public ulong PlayerEntityId { get; }

        public static async Task<ScriptedPeer> ConnectAsync(
            NetworkGameClient client,
            CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient? tcpClient = null;
            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var worldId = Guid.NewGuid();
                var connect = client.ConnectAsync(
                    endpoint.Address.ToString(),
                    endpoint.Port,
                    new ClientHandshakeOptions(
                        BuildVersion,
                        ContentVersion,
                        Guid.NewGuid(),
                        "Scripted Client",
                        worldId),
                    cancellationToken);
                tcpClient = await listener.AcceptTcpClientAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                tcpClient.NoDelay = true;
                var stream = tcpClient.GetStream();
                var requestMessage = await TcpFrameCodec.ReadAsync(
                        stream,
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                if (requestMessage is not HandshakeRequestMessage request)
                    throw new InvalidOperationException(
                        "the scripted peer did not receive a client handshake");

                var playerId = Guid.NewGuid();
                const ulong playerEntityId = 707;
                var acceptance = new HandshakeAcceptedMessage(
                    1,
                    450,
                    ProtocolConstants.CurrentVersion,
                    BuildVersion,
                    ContentVersion,
                    Guid.NewGuid(),
                    playerId,
                    playerEntityId,
                    worldId,
                    123456,
                    4.5f,
                    -2.25f,
                    0,
                    9090,
                    request.ClientNonce,
                    FirstCommandSequence,
                    "scripted-reconnect-token",
                    0,
                    20,
                    ServerCapabilities.None);
                await TcpFrameCodec.WriteAsync(
                        stream,
                        acceptance,
                        cancellationToken)
                    .AsTask()
                    .WaitAsync(Timeout, cancellationToken);
                await connect.WaitAsync(Timeout, cancellationToken);
                return new ScriptedPeer(
                    listener,
                    tcpClient,
                    stream,
                    playerId,
                    playerEntityId);
            }
            catch
            {
                tcpClient?.Dispose();
                listener.Stop();
                throw;
            }
        }

        public async ValueTask SendAsync(
            IProtocolMessage message,
            CancellationToken cancellationToken) =>
            await TcpFrameCodec.WriteAsync(_stream, message, cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);

        public async ValueTask<IProtocolMessage> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            var message = await TcpFrameCodec.ReadAsync(_stream, cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken);
            return message ?? throw new EndOfStreamException(
                "the client closed before sending the expected frame");
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _tcpClient.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
