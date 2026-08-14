using System.Numerics;
using IslandRpg.Boats;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class AuthoritativeSessionChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "authoritative join publishes immutable server state",
            JoinPublishesImmutableServerState);
        checks.Add(
            "movement advances at the fixed server tick rate",
            MovementUsesFixedServerTicks);
        checks.Add(
            "stale and foreign commands cannot mutate an actor",
            RejectsStaleAndForeignCommands);
        checks.Add(
            "disconnect and reconnect preserve identity securely",
            ReconnectPreservesIdentity);
        checks.Add(
            "a valid reconnect token takes over a live connection",
            ReconnectTakesOverLiveConnection);
        checks.Add(
            "disconnected identities do not consume concurrent player slots",
            DisconnectChurnReleasesConcurrentSlots);
        checks.Add(
            "bounded offline retention survives identity churn with explicit expiry",
            OfflineIdentityRetentionIsBounded);
        checks.Add(
            "island identity churn atomically reclaims provisioned boats",
            IslandIdentityChurnReclaimsBoats);
        checks.Add(
            "inbound work is bounded under pressure",
            InboundQueueIsBounded);
        checks.Add(
            "chat history remains ordered and bounded",
            ChatHistoryIsOrderedAndBounded);
        checks.Add(
            "inventory actions use authoritative revisions",
            InventoryActionsUseAuthoritativeRevisions);
        checks.Add(
            "stale gameplay revisions never mutate inventory",
            StaleGameplayRevisionsAreAtomic);
        checks.Add(
            "duplicate crafting commands replay one receipt",
            DuplicateCraftingIsIdempotent);
        checks.Add(
            "failed crafting is atomic and idempotent",
            FailedCraftingIsAtomic);
        checks.Add(
            "eating consumes one authoritative food item",
            EatingUsesAuthoritativeSurvivalState);
        checks.Add(
            "passive survival advances only connected actors at coarse authority cadence",
            PassiveSurvivalAdvancesConnectedActors);
        checks.Add(
            "stone tool sharpening is an authoritative combination",
            SharpeningUsesAuthoritativeInventoryState);
        checks.Add(
            "fire-and-forget commands reject revisioned gameplay",
            FireAndForgetRejectsGameplay);
    }

    private static void JoinPublishesImmutableServerState()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var pendingJoin = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            "  Elara  ",
            new Vector2(4, -2)));

        CheckAssert.False(
            pendingJoin.IsCompleted,
            "network workers must enqueue joins rather than mutate session state");
        CheckAssert.Equal(
            1,
            session.Drain(),
            "the owner thread must process the queued join once");

        var joined = pendingJoin.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, "a valid join must be accepted");
        CheckAssert.False(
            joined.ReconnectToken.IsEmpty,
            "joining must issue a private reconnect credential");

        var before = session.CaptureSnapshot();
        CheckAssert.Equal(1, before.Actors.Length, "the joined actor must be visible");
        CheckAssert.Equal(
            "Elara",
            before.Actors[0].DisplayName,
            "display names must be normalized by the server");
        CheckAssert.Equal(
            new Vector2(4, -2),
            before.Actors[0].Position,
            "the authority must own the spawn position");
        var gameplay = before.Actors[0].Gameplay;
        CheckAssert.Equal(1U, gameplay.ActorRevision,
            "new actor gameplay must begin at revision one");
        CheckAssert.Equal(1U, gameplay.Inventory.Revision,
            "new inventories must begin at revision one");
        CheckAssert.Equal(28, gameplay.Inventory.Capacity,
            "the authoritative carried inventory must contain 28 slots");
        CheckAssert.True(
            gameplay.Inventory.Slots.All(slot =>
                slot.ItemId is null && slot.Quantity == 0),
            "a new authoritative inventory must start empty");
        CheckAssert.Equal(100, gameplay.Health,
            "new players must start at full health");
        CheckAssert.Equal(100f, gameplay.Hunger,
            "new players must start at full hunger");
        CheckAssert.Equal(0f, gameplay.WellFedSeconds,
            "new players must not start with a well-fed timer");
        CheckAssert.Equal(0, gameplay.CraftingExperience,
            "new players must start without crafting experience");
        CheckAssert.Equal(0, gameplay.CookingExperience,
            "new players must start without cooking experience");

        var command = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new WalkIntent(new Vector2(5, -2))));
        session.Tick();
        CheckAssert.True(
            command.GetAwaiter().GetResult().Accepted,
            "a valid movement intent must enter authoritative simulation");
        CheckAssert.Equal(
            new Vector2(4, -2),
            before.Actors[0].Position,
            "published snapshots must remain immutable after later ticks");
    }

    private static void MovementUsesFixedServerTicks()
    {
        var limits = SimulationLimits.Default with
        {
            ActorMovementSpeed = 6,
            DestinationArrivalDistance = 0
        };
        var session = NewSession(limits);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Aveline", Vector2.Zero);

        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new WalkIntent(new Vector2(1, 0))));

        SessionSnapshot? published = null;
        for (var tick = 0; tick < 10; tick++)
            published = session.Tick().PublishedSnapshot ?? published;

        CheckAssert.True(
            pending.GetAwaiter().GetResult().Accepted,
            "valid movement must be accepted");
        CheckAssert.True(
            published is not null,
            "the server must publish snapshots independently of render frames");
        CheckAssert.Equal(
            10L,
            session.Clock.Tick,
            "ten calls must advance exactly ten authoritative ticks");
        CheckAssert.Equal(
            3L,
            session.Clock.SnapshotSequence,
            "20 Hz snapshots must be published from a 60 Hz simulation");

        var actor = session.CaptureSnapshot().Actors[0];
        CheckAssert.True(
            MathF.Abs(actor.Position.X - 1) < 0.0001f,
            $"movement distance must be speed multiplied by fixed server time; actual {actor.Position}");
        CheckAssert.True(
            actor.Destination is null && actor.Velocity == Vector2.Zero,
            "arrival must clear authoritative movement state");
    }

    private static void RejectsStaleAndForeignCommands()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Linnet", Vector2.Zero);

        var accepted = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new WalkIntent(new Vector2(3, 0))));
        session.Drain();
        CheckAssert.True(
            accepted.GetAwaiter().GetResult().Accepted,
            "the first command sequence must be accepted");

        var stale = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            StopIntent.Instance));
        var foreign = session.EnqueueIntentAsync(new ActorCommand(
            ClientConnectionId.New(),
            joined.Identity.PlayerId,
            2,
            StopIntent.Instance));
        session.Drain();

        CheckAssert.Equal(
            IntentStatus.StaleSequence,
            stale.GetAwaiter().GetResult().Status,
            "replayed command sequences must be rejected");
        CheckAssert.Equal(
            IntentStatus.InvalidConnection,
            foreign.GetAwaiter().GetResult().Status,
            "a different connection must not control the actor");
        CheckAssert.True(
            session.CaptureSnapshot().Actors[0].Destination is not null,
            "rejected commands must not cancel the accepted destination");
    }

    private static void ReconnectPreservesIdentity()
    {
        var session = NewSession();
        var firstConnection = ClientConnectionId.New();
        var joined = Join(session, firstConnection, "Serena", Vector2.One);

        var disconnect = session.EnqueueDisconnectAsync(new DisconnectRequest(
            firstConnection,
            joined.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(
            disconnect.GetAwaiter().GetResult().Accepted,
            "the owning connection must be able to disconnect cleanly");
        CheckAssert.False(
            session.CaptureSnapshot().Actors[0].Connected,
            "disconnect must immediately stop authoritative actor control");

        var secondConnection = ClientConnectionId.New();
        var rejected = session.EnqueueReconnectAsync(new ReconnectRequest(
            secondConnection,
            joined.Identity.PlayerId,
            new ReconnectToken("wrong-secret")));
        session.Drain();
        CheckAssert.Equal(
            ReconnectStatus.InvalidToken,
            rejected.GetAwaiter().GetResult().Status,
            "an invalid reconnect credential must be rejected");

        var accepted = session.EnqueueReconnectAsync(new ReconnectRequest(
            secondConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        session.Drain();
        var result = accepted.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "the issued reconnect token must work");
        CheckAssert.Equal(
            joined.Identity,
            result.Identity,
            "reconnect must preserve player and actor identities");
        CheckAssert.True(
            session.CaptureSnapshot().Actors[0].Connected,
            "successful reconnect must restore authoritative control");
    }

    private static void ReconnectTakesOverLiveConnection()
    {
        var session = NewSession();
        var firstConnection = ClientConnectionId.New();
        var joined = Join(session, firstConnection, "Serena", Vector2.One);
        var secondConnection = ClientConnectionId.New();
        var takeover = session.EnqueueReconnectAsync(new ReconnectRequest(
            secondConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        session.Drain();
        var result = takeover.GetAwaiter().GetResult();
        CheckAssert.True(
            result.Accepted,
            "a valid token must reclaim a still-connected actor");
        CheckAssert.Equal(
            firstConnection,
            result.EvictedConnectionId,
            "takeover must report the evicted live connection");
        CheckAssert.Equal(
            joined.Identity,
            result.Identity,
            "takeover must keep the original player identity");
        CheckAssert.True(
            session.CaptureSnapshot().Actors[0].Connected,
            "the actor must remain connected after takeover");

        var stale = session.EnqueueIntentAsync(new ActorCommand(
            firstConnection,
            joined.Identity.PlayerId,
            1,
            new WalkIntent(new Vector2(4, 0))));
        session.Drain();
        CheckAssert.Equal(
            IntentStatus.InvalidConnection,
            stale.GetAwaiter().GetResult().Status,
            "the evicted connection must no longer control the actor");
    }

    private static void DisconnectChurnReleasesConcurrentSlots()
    {
        var session = NewSession(SimulationLimits.Default with
        {
            MaximumActors = 8,
            MaximumConnectedActors = 2
        });
        var retained = new List<JoinResult>();

        // Create more durable identities than may be connected concurrently.
        // Each disconnect must release its live slot without deleting the
        // reconnect credential or actor state.
        for (var index = 0; index < 4; index++)
        {
            var connection = ClientConnectionId.New();
            var player = Join(
                session,
                connection,
                $"Retained {index}",
                new Vector2(index, 0));
            retained.Add(player);

            var pendingDisconnect = session.EnqueueDisconnectAsync(
                new DisconnectRequest(
                    connection,
                    player.Identity.PlayerId));
            session.Drain();
            CheckAssert.True(
                pendingDisconnect.GetAwaiter().GetResult().Accepted,
                "each sequential disconnect must release its concurrent slot");
        }

        Join(
            session,
            ClientConnectionId.New(),
            "Live One",
            new Vector2(10, 0));
        var secondLiveConnection = ClientConnectionId.New();
        var secondLive = Join(
            session,
            secondLiveConnection,
            "Live Two",
            new Vector2(11, 0));

        var overCapacity = session.EnqueueJoinAsync(new JoinRequest(
            ClientConnectionId.New(),
            "Live Three",
            new Vector2(12, 0)));
        session.Drain();
        CheckAssert.Equal(
            JoinStatus.SessionFull,
            overCapacity.GetAwaiter().GetResult().Status,
            "a concurrent player beyond the configured live cap must fail");
        CheckAssert.Equal(
            2,
            session.CaptureSnapshot().Actors.Count(static actor =>
                actor.Connected),
            "a rejected concurrent join must not create an actor");

        var original = retained[0];
        var fullReconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            ClientConnectionId.New(),
            original.Identity.PlayerId,
            original.ReconnectToken));
        session.Drain();
        CheckAssert.Equal(
            ReconnectStatus.SessionFull,
            fullReconnect.GetAwaiter().GetResult().Status,
            "a reconnect must obey the same concurrent player cap as a join");

        var release = session.EnqueueDisconnectAsync(new DisconnectRequest(
            secondLiveConnection,
            secondLive.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(
            release.GetAwaiter().GetResult().Accepted,
            "disconnecting a live player must make one slot available");

        var reconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            ClientConnectionId.New(),
            original.Identity.PlayerId,
            original.ReconnectToken));
        session.Drain();
        var reconnected = reconnect.GetAwaiter().GetResult();
        CheckAssert.True(
            reconnected.Accepted,
            "an old retained identity must reconnect after churn releases a slot");
        CheckAssert.Equal(
            original.Identity,
            reconnected.Identity,
            "slot reuse must not replace the retained player identity");
        CheckAssert.Equal(
            2,
            session.CaptureSnapshot().Actors.Count(static actor =>
                actor.Connected),
            "reconnect must consume exactly one available concurrent slot");
    }

    private static void OfflineIdentityRetentionIsBounded()
    {
        var limits = SimulationLimits.Default with
        {
            MaximumActors = 3,
            MaximumConnectedActors = 1,
            ExpiredPlayerTombstoneCapacity = 16
        };
        var session = NewSession(limits);
        var churned = new List<JoinResult>();
        for (var index = 0; index < 8; index++)
        {
            var connection = ClientConnectionId.New();
            var joined = Join(
                session,
                connection,
                $"Churn {index}",
                new Vector2(index, 0));
            churned.Add(joined);
            if (index == 7)
            {
                CheckAssert.True(session.TryGrantInventoryItem(
                        joined.Identity.PlayerId, "logs", 2),
                    "the newest retained identity must accept durable state");
            }
            var disconnect = session.EnqueueDisconnectAsync(
                new DisconnectRequest(
                    connection,
                    joined.Identity.PlayerId));
            session.Drain();
            CheckAssert.True(
                disconnect.GetAwaiter().GetResult().Accepted,
                "sequential churn must release the live player slot");
            CheckAssert.True(session.ActorCount <= limits.MaximumActors,
                "offline identity retention must remain hard bounded");
        }

        CheckAssert.Equal(3, session.ActorCount,
            "more than one durable-cap of churn must retain only the newest identities");
        var expired = session.EnqueueReconnectAsync(new ReconnectRequest(
            ClientConnectionId.New(),
            churned[0].Identity.PlayerId,
            churned[0].ReconnectToken));
        session.Drain();
        CheckAssert.Equal(
            ReconnectStatus.ExpiredPlayer,
            expired.GetAwaiter().GetResult().Status,
            "a recently evicted credential must report explicit bounded-history expiry");

        var retainedConnection = ClientConnectionId.New();
        var retained = session.EnqueueReconnectAsync(new ReconnectRequest(
            retainedConnection,
            churned[^1].Identity.PlayerId,
            churned[^1].ReconnectToken));
        session.Drain();
        var retainedResult = retained.GetAwaiter().GetResult();
        CheckAssert.True(retainedResult.Accepted,
            "a non-evicted reconnect credential must continue working exactly");
        CheckAssert.Equal(churned[^1].Identity, retainedResult.Identity,
            "bounded retention must preserve the retained player and actor IDs");
        CheckAssert.Equal(2, CountItem(
                retainedResult.Gameplay.Inventory, "logs"),
            "bounded retention must preserve retained authoritative gameplay state");

        var overConcurrentCap = session.EnqueueJoinAsync(new JoinRequest(
            ClientConnectionId.New(),
            "Concurrent overflow",
            Vector2.Zero));
        session.Drain();
        CheckAssert.Equal(
            JoinStatus.SessionFull,
            overConcurrentCap.GetAwaiter().GetResult().Status,
            "offline eviction must never bypass the concurrent player cap");
        CheckAssert.Equal(3, session.ActorCount,
            "a concurrent-cap rejection must not evict retained history");

        var disconnectRetained = session.EnqueueDisconnectAsync(
            new DisconnectRequest(
                retainedConnection,
                retainedResult.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(
            disconnectRetained.GetAwaiter().GetResult().Accepted,
            "the retained player must disconnect before checkpointing");
        var checkpoint = session.CaptureCheckpoint();
        var restored = NewSession(limits);
        restored.RestoreCheckpoint(checkpoint);
        var forgottenTombstone = restored.EnqueueReconnectAsync(
            new ReconnectRequest(
                ClientConnectionId.New(),
                churned[0].Identity.PlayerId,
                churned[0].ReconnectToken));
        restored.Drain();
        CheckAssert.Equal(
            ReconnectStatus.UnknownPlayer,
            forgottenTombstone.GetAwaiter().GetResult().Status,
            "non-secret expiry tombstones are deliberately transient across restart");
    }

    private static void IslandIdentityChurnReclaimsBoats()
    {
        var limits = SimulationLimits.Default with
        {
            MaximumActors = 4,
            MaximumConnectedActors = 1,
            ExpiredPlayerTombstoneCapacity = 8
        };
        var boats = new AuthoritativeBoatTransactions(
            new ShorelineBoatNavigation(),
            new AuthoritativeBoatTransactionOptions
            {
                MaximumBoats = 2
            });
        var session = new AuthoritativeWorldSession(
            limits,
            new DeterministicIdentitySource(),
            new SessionId(Guid.Parse(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            boatTransactions: boats);
        var boatDeltas = new List<BoatStateDelta>();
        session.BoatStateCommitted += boatDeltas.Add;
        var churned = new List<JoinResult>();

        for (var index = 0; index < 6; index++)
        {
            var connection = ClientConnectionId.New();
            var pending = session.EnqueueJoinAsync(new JoinRequest(
                connection,
                $"Island churn {index}",
                new Vector2(0, 1.5f),
                ProvisionBoat: true));
            session.Drain();
            var joined = pending.GetAwaiter().GetResult();
            CheckAssert.True(joined.Accepted && joined.Boat is not null,
                "every sequential island join must provision a replacement raft");
            churned.Add(joined);
            CheckAssert.True(session.CaptureBoats().Length <= 2,
                "provisioned raft retention must remain bounded with actor history");
            CheckAssert.True(session.ActorCount <= 2,
                "the smaller fleet cap must trigger owned-identity expiry before the actor cap");

            var disconnect = session.EnqueueDisconnectAsync(
                new DisconnectRequest(
                    connection,
                    joined.Identity.PlayerId));
            session.Drain();
            CheckAssert.True(
                disconnect.GetAwaiter().GetResult().Accepted,
                "the island churn fixture must release its live slot");
        }

        var retainedPlayers = churned.TakeLast(2)
            .Select(static value => value.Identity.PlayerId)
            .ToHashSet();
        var retainedBoats = session.CaptureBoats();
        CheckAssert.Equal(2, retainedBoats.Length,
            "the reduced boat cap must be fully reusable after identity churn");
        CheckAssert.True(retainedBoats.All(value =>
                retainedPlayers.Contains(value.OwnerPlayerId)),
            "expired player rafts must be removed before replacement provisioning");
        CheckAssert.Equal(4, boatDeltas.Count(value =>
                value.Kind == BoatChangeKind.Removed),
            "each actor evicted beyond the retention cap must publish one raft removal");

        var expired = session.EnqueueReconnectAsync(new ReconnectRequest(
            ClientConnectionId.New(),
            churned[0].Identity.PlayerId,
            churned[0].ReconnectToken));
        session.Drain();
        CheckAssert.Equal(ReconnectStatus.ExpiredPlayer,
            expired.GetAwaiter().GetResult().Status,
            "a reclaimed island identity must report explicit reconnect expiry");

        var checkpoint = session.CaptureCheckpoint();
        CheckAssert.Equal(2, checkpoint.Actors.Length,
            "fleet capacity must constrain island identity retention below its actor cap");
        CheckAssert.True(checkpoint.Boats is { Boats.Length: 2 } &&
                         checkpoint.Boats.Boats.All(value =>
                             retainedPlayers.Contains(value.OwnerPlayerId)),
            "island checkpoint boats must reference only retained owners");
    }

    private static void InboundQueueIsBounded()
    {
        var limits = SimulationLimits.Default with
        {
            InboundCommandCapacity = 2,
            MaximumCommandsPerTick = 2
        };
        var session = NewSession(limits);
        var first = session.EnqueueJoinAsync(new JoinRequest(
            ClientConnectionId.New(),
            "One",
            Vector2.Zero));
        var second = session.EnqueueJoinAsync(new JoinRequest(
            ClientConnectionId.New(),
            "Two",
            Vector2.One));
        var overflow = session.EnqueueJoinAsync(new JoinRequest(
            ClientConnectionId.New(),
            "Three",
            new Vector2(2)));

        CheckAssert.Equal(
            JoinStatus.QueueFull,
            overflow.GetAwaiter().GetResult().Status,
            "a full inbound queue must reject additional work without growing");
        CheckAssert.Equal(2, session.Drain(), "the bounded queue must contain two joins");
        CheckAssert.True(
            first.GetAwaiter().GetResult().Accepted &&
            second.GetAwaiter().GetResult().Accepted,
            "queued work must remain intact when overflow is rejected");
    }

    private static void ChatHistoryIsOrderedAndBounded()
    {
        var limits = SimulationLimits.Default with { ChatHistoryCapacity = 2 };
        var session = NewSession(limits);
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Valerian", Vector2.Zero);

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var chat = session.EnqueueIntentAsync(new ActorCommand(
                connection,
                joined.Identity.PlayerId,
                sequence,
                new ChatIntent($"message {sequence}")));
            session.Drain();
            CheckAssert.True(
                chat.GetAwaiter().GetResult().Accepted,
                "valid chat must be accepted by the authority");
        }

        var history = session.CaptureSnapshot().ChatHistory;
        CheckAssert.Equal(2, history.Length, "chat history must remain bounded");
        CheckAssert.SequenceEqual(
            new[] { "message 2", "message 3" },
            history.Select(static message => message.Message),
            "chat history must retain ordered newest entries");
        CheckAssert.True(
            history[0].MessageId < history[1].MessageId,
            "server-issued chat identifiers must remain monotonic");
    }

    private static void InventoryActionsUseAuthoritativeRevisions()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Tamsin", Vector2.Zero);
        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "large_rock",
                2),
            "the test world reward must seed two combinable rocks");

        var seeded = session.CaptureSnapshot().Actors[0].Gameplay;
        var swap = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new SwapInventorySlotsIntent(
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                seeded.Inventory.Revision,
                seeded.ActorRevision,
                0,
                2)));
        session.Drain();
        var swapped = swap.GetAwaiter().GetResult();
        CheckAssert.True(swapped.Accepted,
            "a revision-matched slot swap must succeed");
        CheckAssert.Equal(seeded.Inventory.Revision + 1,
            swapped.InventoryRevision,
            "a slot swap must advance only the inventory revision");
        CheckAssert.Equal(seeded.ActorRevision, swapped.ActorRevision,
            "a slot swap must not revise unchanged actor gameplay");

        var afterSwap = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal("large_rock", afterSwap.Inventory.Slots[2].ItemId,
            "the source item must move to the requested target slot");
        var combine = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            2,
            new CombineInventorySlotsIntent(
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                afterSwap.Inventory.Revision,
                afterSwap.ActorRevision,
                1,
                2)));
        session.Drain();
        CheckAssert.True(combine.GetAwaiter().GetResult().Accepted,
            "two matching inventory slots must resolve through the combination catalogue");

        var combined = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(1, CountItem(combined.Inventory, "large_rock"),
            "the combination recipe must preserve its returned striking rock");
        CheckAssert.Equal(2, CountItem(combined.Inventory, "medium_rock"),
            "the combination recipe must commit both canonical outputs");
        CheckAssert.Equal(8, combined.CraftingExperience,
            "combining items must award the canonical recipe experience once");
        CheckAssert.Equal(2, combined.AdventureExperience,
            "crafting must feed canonical Adventure XP from actual skill XP gained");
        CheckAssert.Equal(afterSwap.Inventory.Revision + 1,
            combined.Inventory.Revision,
            "a successful combination must advance the inventory revision once");
        CheckAssert.Equal(afterSwap.ActorRevision + 1,
            combined.ActorRevision,
            "awarded crafting experience must advance actor gameplay once");
    }

    private static void StaleGameplayRevisionsAreAtomic()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Orrin", Vector2.Zero);
        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "logs"),
            "the test world reward must seed a swappable item");
        var before = session.CaptureSnapshot().Actors[0].Gameplay;

        var staleInventory = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new SwapInventorySlotsIntent(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision - 1,
                before.ActorRevision,
                0,
                1)));
        session.Drain();
        CheckAssert.Equal(
            IntentStatus.StaleInventoryRevision,
            staleInventory.GetAwaiter().GetResult().Status,
            "a stale inventory revision must reject the action");

        var staleActor = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            2,
            new SwapInventorySlotsIntent(
                Guid.Parse("40000000-0000-0000-0000-000000000002"),
                before.Inventory.Revision,
                before.ActorRevision - 1,
                0,
                1)));
        session.Drain();
        CheckAssert.Equal(
            IntentStatus.StaleActorRevision,
            staleActor.GetAwaiter().GetResult().Status,
            "a stale actor revision must reject the action");

        var after = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(before.Inventory.Revision, after.Inventory.Revision,
            "stale commands must not advance the inventory revision");
        CheckAssert.Equal(before.ActorRevision, after.ActorRevision,
            "stale commands must not advance the actor revision");
        CheckAssert.SequenceEqual(
            before.Inventory.Slots.Select(slot => (slot.ItemId, slot.Quantity)),
            after.Inventory.Slots.Select(slot => (slot.ItemId, slot.Quantity)),
            "stale commands must leave every inventory slot untouched");
    }

    private static void DuplicateCraftingIsIdempotent()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Mirelle", Vector2.Zero);
        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "plant_fibres",
                3),
            "the test world reward must seed one rope recipe");
        var before = session.CaptureSnapshot().Actors[0].Gameplay;
        var intent = new CraftRecipeIntent(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            before.Inventory.Revision,
            before.ActorRevision,
            "rope");

        var first = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            intent));
        session.Drain();
        var firstResult = first.GetAwaiter().GetResult();
        CheckAssert.True(firstResult.Accepted && !firstResult.Duplicate,
            "the first stable recipe command must craft normally");
        var crafted = session.CaptureSnapshot().Actors[0].Gameplay;

        var disconnect = session.EnqueueDisconnectAsync(new DisconnectRequest(
            connection,
            joined.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(disconnect.GetAwaiter().GetResult().Accepted,
            "the crafting player must disconnect before the retry check");
        var retryConnection = ClientConnectionId.New();
        var reconnect = session.EnqueueReconnectAsync(new ReconnectRequest(
            retryConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        session.Drain();
        CheckAssert.True(reconnect.GetAwaiter().GetResult().Accepted,
            "the crafting player must reconnect with the issued credential");

        var retry = session.EnqueueIntentAsync(new ActorCommand(
            retryConnection,
            joined.Identity.PlayerId,
            2,
            intent));
        session.Drain();
        var retryResult = retry.GetAwaiter().GetResult();
        CheckAssert.True(retryResult.Accepted && retryResult.Duplicate,
            "a repeated command identifier must replay its accepted receipt after reconnect");

        var retried = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(1, CountItem(retried.Inventory, "rope"),
            "retrying a craft must never duplicate its product");
        CheckAssert.Equal(0, CountItem(retried.Inventory, "plant_fibres"),
            "retrying a craft must not consume resources again");
        CheckAssert.Equal(crafted.CraftingExperience,
            retried.CraftingExperience,
            "retrying a craft must not award experience twice");
        CheckAssert.Equal(crafted.Inventory.Revision,
            retried.Inventory.Revision,
            "retrying a craft must not advance inventory revision twice");
        CheckAssert.Equal(crafted.ActorRevision, retried.ActorRevision,
            "retrying a craft must not advance actor revision twice");
    }

    private static void FailedCraftingIsAtomic()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Galen", Vector2.Zero);
        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "plant_fibres",
                2),
            "the test world reward must seed insufficient rope resources");
        var before = session.CaptureSnapshot().Actors[0].Gameplay;
        var intent = new CraftRecipeIntent(
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            before.Inventory.Revision,
            before.ActorRevision,
            "rope");

        var failed = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            intent));
        session.Drain();
        CheckAssert.Equal(
            IntentStatus.MissingResources,
            failed.GetAwaiter().GetResult().Status,
            "an incomplete recipe must report missing resources");
        var afterFailure = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(2, CountItem(afterFailure.Inventory, "plant_fibres"),
            "a failed craft must not partially consume ingredients");
        CheckAssert.Equal(before.Inventory.Revision,
            afterFailure.Inventory.Revision,
            "a failed craft must not advance inventory revision");
        CheckAssert.Equal(before.ActorRevision, afterFailure.ActorRevision,
            "a failed craft must not advance actor revision");

        var retry = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            2,
            intent));
        session.Drain();
        var retryResult = retry.GetAwaiter().GetResult();
        CheckAssert.True(
            retryResult.Status == IntentStatus.MissingResources &&
            retryResult.Duplicate,
            "a failed command receipt must also replay idempotently");
        CheckAssert.Equal(2, CountItem(
                session.CaptureSnapshot().Actors[0].Gameplay.Inventory,
                "plant_fibres"),
            "retrying a failed craft must preserve every ingredient");
    }

    private static void EatingUsesAuthoritativeSurvivalState()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(
            session,
            connection,
            "Rowan",
            Vector2.Zero,
            initialHunger: 92f);
        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "wild_berries"),
            "the test world reward must seed one edible item");
        var before = session.CaptureSnapshot().Actors[0].Gameplay;

        var eat = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new ConsumeFoodIntent(
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision,
                before.ActorRevision,
                0)));
        session.Drain();
        CheckAssert.True(eat.GetAwaiter().GetResult().Accepted,
            "a known food item must be consumed through SurvivalService");

        var after = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(0, CountItem(after.Inventory, "wild_berries"),
            "eating must remove exactly one selected food item");
        CheckAssert.Equal(100, after.Health,
            "food healing must remain clamped to maximum health");
        CheckAssert.Equal(100f, after.Hunger,
            "food hunger restoration must remain clamped to maximum hunger");
        CheckAssert.Equal(20f, after.WellFedSeconds,
            "wild berries must apply their canonical well-fed duration");
        CheckAssert.Equal(before.Inventory.Revision + 1,
            after.Inventory.Revision,
            "eating must advance inventory revision once");
        CheckAssert.Equal(before.ActorRevision + 1, after.ActorRevision,
            "changed survival state must advance actor revision once");

        CheckAssert.True(
            session.TryGrantInventoryItem(
                joined.Identity.PlayerId,
                "wild_berries"),
            "the test world reward must seed a second edible item");
        var full = session.CaptureSnapshot().Actors[0].Gameplay;
        var rejected = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            2,
            new ConsumeFoodIntent(
                Guid.Parse("70000000-0000-0000-0000-000000000002"),
                full.Inventory.Revision,
                full.ActorRevision,
                full.Inventory.Slots.Single(slot =>
                    slot.ItemId == "wild_berries").Slot)));
        session.Drain();
        CheckAssert.Equal(IntentStatus.AlreadyFull,
            rejected.GetAwaiter().GetResult().Status,
            "full and healthy players must preserve solo-mode food rules");
        CheckAssert.Equal(1, CountItem(
                session.CaptureSnapshot().Actors[0].Gameplay.Inventory,
                "wild_berries"),
            "a rejected food action must not consume the item");
    }

    private static void PassiveSurvivalAdvancesConnectedActors()
    {
        var session = NewSession();
        var firstConnection = ClientConnectionId.New();
        var secondConnection = ClientConnectionId.New();
        var first = Join(session, firstConnection, "Mira", Vector2.Zero,
            initialHunger: 100);
        var second = Join(session, secondConnection, "Rowan", Vector2.One,
            initialHunger: 100);
        for (var tick = 0; tick < SimulationTiming.TicksPerSecond - 1; tick++)
            session.Tick();
        var beforeBoundary = session.CaptureSnapshot().Actors
            .Single(actor => actor.PlayerId == first.Identity.PlayerId).Gameplay;
        CheckAssert.Equal(100f, beforeBoundary.Hunger,
            "survival must not churn private actor revisions every fixed step");
        session.Tick();
        var afterFirstSecond = session.CaptureSnapshot().Actors
            .Single(actor => actor.PlayerId == first.Identity.PlayerId).Gameplay;
        CheckAssert.True(afterFirstSecond.Hunger < 100 &&
                         afterFirstSecond.ActorRevision > beforeBoundary.ActorRevision,
            "connected actors must lose hunger at the authoritative one-second cadence");

        var disconnect = session.EnqueueDisconnectAsync(new DisconnectRequest(
            secondConnection, second.Identity.PlayerId));
        session.Drain();
        CheckAssert.True(disconnect.GetAwaiter().GetResult().Accepted,
            "the control actor must disconnect before offline survival is checked");
        var offlineBefore = session.CaptureSnapshot().Actors
            .Single(actor => actor.PlayerId == second.Identity.PlayerId).Gameplay;
        for (var tick = 0; tick < SimulationTiming.TicksPerSecond * 3; tick++)
            session.Tick();
        var onlineAfter = session.CaptureSnapshot().Actors
            .Single(actor => actor.PlayerId == first.Identity.PlayerId).Gameplay;
        var offlineAfter = session.CaptureSnapshot().Actors
            .Single(actor => actor.PlayerId == second.Identity.PlayerId).Gameplay;
        CheckAssert.True(onlineAfter.Hunger < afterFirstSecond.Hunger,
            "connected actors must continue authoritative survival progression");
        CheckAssert.Equal(offlineBefore.Hunger, offlineAfter.Hunger,
            "offline actors must pause survival progression");
        CheckAssert.Equal(offlineBefore.WellFedSeconds, offlineAfter.WellFedSeconds,
            "offline actors must retain their exact digestion timer");
    }

    private static void SharpeningUsesAuthoritativeInventoryState()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Edwin", Vector2.Zero);
        CheckAssert.True(session.TryGrantInventoryItem(
                joined.Identity.PlayerId, "small_rocks"),
            "the test world reward must seed a sharpening stone");
        CheckAssert.True(session.TryGrantInventoryItem(
                joined.Identity.PlayerId, "blunt_stone_axe"),
            "the test world reward must seed a blunt axe");
        var before = session.CaptureSnapshot().Actors[0].Gameplay;
        var rocks = before.Inventory.Slots.Single(slot =>
            slot.ItemId == "small_rocks").Slot;
        var axe = before.Inventory.Slots.Single(slot =>
            slot.ItemId == "blunt_stone_axe").Slot;
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new CombineInventorySlotsIntent(
                Guid.Parse("71000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision,
                before.ActorRevision,
                rocks,
                axe)));
        session.Drain();
        CheckAssert.True(pending.GetAwaiter().GetResult().Accepted,
            "small rocks must sharpen a blunt stone axe through shared rules");
        var after = session.CaptureSnapshot().Actors[0].Gameplay.Inventory;
        CheckAssert.Equal(0, CountItem(after, "small_rocks"),
            "sharpening must consume exactly one small rock");
        CheckAssert.Equal(0, CountItem(after, "blunt_stone_axe"),
            "sharpening must replace the blunt tool");
        CheckAssert.Equal(1, CountItem(after, "stone_axe"),
            "sharpening must create exactly one restored axe");
    }

    private static void FireAndForgetRejectsGameplay()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Rowan", Vector2.Zero);
        var before = session.CaptureSnapshot().Actors[0].Gameplay;

        var queued = session.TryEnqueueIntent(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new SwapInventorySlotsIntent(
                Guid.Parse("72000000-0000-0000-0000-000000000001"),
                before.Inventory.Revision,
                before.ActorRevision,
                0,
                1)));

        CheckAssert.False(queued,
            "revisioned gameplay must use the acknowledged command path");
        CheckAssert.Equal(0, session.Drain(),
            "rejected fire-and-forget gameplay must not enter the authority queue");
        var after = session.CaptureSnapshot().Actors[0].Gameplay;
        CheckAssert.Equal(
            before with { Inventory = after.Inventory },
            after,
            "rejected fire-and-forget gameplay must not mutate actor state");
        CheckAssert.SequenceEqual(
            before.Inventory.Slots,
            after.Inventory.Slots,
            "rejected fire-and-forget gameplay must preserve inventory slots");
    }

    private static int CountItem(
        PlayerInventorySnapshot inventory,
        string itemId) => inventory.Slots.Sum(slot =>
            string.Equals(slot.ItemId, itemId,
                StringComparison.OrdinalIgnoreCase)
                ? slot.Quantity
                : 0);

    private static AuthoritativeWorldSession NewSession(
        SimulationLimits? limits = null) =>
        new(
            limits,
            new DeterministicIdentitySource(),
            new SessionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));

    private static JoinResult Join(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        string displayName,
        Vector2 spawn,
        float initialHunger = 100f)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            displayName,
            spawn,
            InitialHunger: initialHunger));
        session.Drain();
        var result = pending.GetAwaiter().GetResult();
        CheckAssert.True(result.Accepted, "test actor must join the session");
        return result;
    }

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        private int _next;

        public PlayerIdentity CreatePlayerIdentity()
        {
            var index = ++_next;
            return new PlayerIdentity(
                new PlayerId(Guid.Parse($"10000000-0000-0000-0000-{index:D12}")),
                new ActorId(Guid.Parse($"20000000-0000-0000-0000-{index:D12}")));
        }

        public ReconnectToken CreateReconnectToken() =>
            new($"deterministic-secret-{_next}");
    }

    private sealed class ShorelineBoatNavigation : IBoatNavigationQuery
    {
        public bool IsNavigable(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.Y is >= 0 and < 1;

        public bool IsLanding(Vector2 point) =>
            float.IsFinite(point.X) && float.IsFinite(point.Y) &&
            point.Y is >= 1 and < 2;

        public bool IsInitialMooring(Vector2 point) => IsNavigable(point);
    }
}
