using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Caves;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused cave-authority checks. Registration is intentionally left to the
/// parent milestone so this shared-worktree patch does not edit Program.cs.
/// </summary>
internal static class CaveAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add("cave authority rejects generated-id collisions atomically",
            GeneratedIdentityFailureIsAtomic);
        checks.Add("cave authority coalesces same-chunk completion mutations",
            SameChunkCompletionHasOneRevisionDelta);
        checks.Add("cave authority links ropes traverses and fills atomically",
            LinkedPortalLifecycleIsAuthoritative);
        checks.Add("cave traversal is session authoritative and restart durable",
            SessionTraversalIsDurableAndIdempotent);
        checks.Add("cave placement follows sparse standing-tree authority",
            StandingTreeBlocksUntilFelled);
    }

    private static void GeneratedIdentityFailureIsAtomic()
    {
        var surfaceId = Guid.Parse(
            "ca000000-0000-0000-0000-000000000001");
        var ids = new Queue<Guid>([surfaceId, surfaceId]);
        var authority = new AuthoritativeWorldTransactions(
            () => ids.Dequeue(), new FixedEnvironment(caveBelow: true));
        var actor = Actor([(ItemIds.StoneShovel, 1)]);
        var start = authority.Execute(actor, new StartExcavationTransaction(
            Context(actor), new(.5f, .5f), 0, 0, 0, 0));
        CheckAssert.True(start.Accepted, "the site should be created");
        var site = start.ObjectDeltas.Single().Object!;
        actor = actor with { Gameplay = start.Gameplay!.Value };

        WorldTransactionResult result = null!;
        for (var strike = 0; strike < 7; strike++)
        {
            var before = authority.CaptureObject(surfaceId);
            result = authority.Execute(actor, new WorkExcavationTransaction(
                Context(actor), Handle(authority, before), 0, strike + 1));
            if (!result.Accepted) break;
            actor = actor with { Gameplay = result.Gameplay!.Value };
        }
        CheckAssert.Equal(WorldTransactionStatus.InvalidCommand, result.Status,
            "a duplicate portal ID should reject completion");
        var after = authority.CaptureObject(surfaceId);
        CheckAssert.Equal(ItemIds.DigSite, after.DefinitionId,
            "a rejected completion must keep the dig site");
        CheckAssert.Equal(2, after.Health,
            "a rejected completion must not apply the final strike");
        CheckAssert.Equal(7u, after.ObjectRevision,
            "a rejected completion must not advance object revision");
        CheckAssert.True(after.LinkedObjectId is null,
            "a rejected completion must not leave a partial portal link");
    }

    private static void SameChunkCompletionHasOneRevisionDelta()
    {
        var ids = new Queue<Guid>([
            Guid.Parse("ca100000-0000-0000-0000-000000000001"),
            Guid.Parse("ca100000-0000-0000-0000-000000000002")]);
        var authority = new AuthoritativeWorldTransactions(
            () => ids.Dequeue(), new FixedEnvironment(caveBelow: false));
        var actor = Actor(Enumerable.Repeat((ItemIds.LargeRock, 1), 27)
            .Prepend((ItemIds.StoneShovel, 1)).ToArray());
        var start = authority.Execute(actor, new StartExcavationTransaction(
            Context(actor), new(.5f, .5f), 0, 0, 0, 0));
        actor = actor with { Gameplay = start.Gameplay!.Value };
        WorldTransactionResult result = null!;
        for (var strike = 0; strike < 7; strike++)
        {
            var site = authority.CaptureObject(
                start.ObjectDeltas.Single().ObjectId);
            result = authority.Execute(actor, new WorkExcavationTransaction(
                Context(actor), Handle(authority, site), 0, strike + 1));
            CheckAssert.True(result.Accepted, "each excavation strike should work");
            actor = actor with { Gameplay = result.Gameplay!.Value };
        }
        CheckAssert.Equal(2, result.ObjectDeltas.Length,
            "completion with a full bag should update the site and add a drop");
        CheckAssert.Equal(1, result.ChunkDeltas.Length,
            "same-chunk site and reward mutations must share one revision");
    }

    private static void LinkedPortalLifecycleIsAuthoritative()
    {
        var ids = new Queue<Guid>([
            Guid.Parse("ca200000-0000-0000-0000-000000000001"),
            Guid.Parse("ca200000-0000-0000-0000-000000000002")]);
        var authority = new AuthoritativeWorldTransactions(
            () => ids.Dequeue(), new FixedEnvironment(caveBelow: true));
        var actor = Actor([
            (ItemIds.StoneShovel, 1),
            (ItemIds.Rope, 1),
            (ItemIds.Dirt, 1)]);
        var start = authority.Execute(actor, new StartExcavationTransaction(
            Context(actor), new(.5f, .5f), 0, 0, 0, 0));
        actor = actor with { Gameplay = start.Gameplay!.Value };
        WorldTransactionResult work = null!;
        for (var strike = 0; strike < 7; strike++)
        {
            var site = authority.CaptureObject(
                start.ObjectDeltas.Single().ObjectId);
            work = authority.Execute(actor, new WorkExcavationTransaction(
                Context(actor), Handle(authority, site), 0, strike + 1));
            CheckAssert.True(work.Accepted, "cave work should complete");
            actor = actor with { Gameplay = work.Gameplay!.Value };
        }
        var surface = work.ObjectDeltas
            .Select(value => value.Object)
            .Single(value => value?.Chunk.WorldLevel == 0)!;
        var underground = work.ObjectDeltas
            .Select(value => value.Object)
            .Single(value => value?.Chunk.WorldLevel == -1)!;
        CheckAssert.Equal(underground.ObjectId, surface.LinkedObjectId!.Value,
            "surface should link to the underground endpoint");
        CheckAssert.Equal(surface.ObjectId, underground.LinkedObjectId!.Value,
            "underground should link back to the surface endpoint");

        var ropeSlot = Slot(actor.Gameplay, ItemIds.Rope);
        var install = authority.Execute(actor, new InstallCaveRopeTransaction(
            Context(actor), Handle(authority, surface), ropeSlot));
        CheckAssert.True(install.Accepted, "rope installation should succeed");
        CheckAssert.Equal(2, install.ObjectDeltas.Length,
            "both endpoints should update atomically");
        actor = actor with { Gameplay = install.Gameplay!.Value };
        surface = install.ObjectDeltas.Select(value => value.Object)
            .Single(value => value?.Chunk.WorldLevel == 0)!;

        var traverse = authority.Execute(actor, new TraverseCaveTransaction(
            Context(actor), Handle(authority, surface)));
        CheckAssert.True(traverse.Accepted,
            "a fully linked roped entrance should be traversable");
        CheckAssert.Equal(-1, traverse.ActorTransition!.Value.WorldLevel,
            "surface traversal should target underground");

        actor = actor with
        {
            WorldLevel = -1,
            Gameplay = traverse.Gameplay!.Value
        };
        underground = authority.CaptureObject(underground.ObjectId);
        var returnTrip = authority.Execute(actor, new TraverseCaveTransaction(
            Context(actor), Handle(authority, underground)));
        CheckAssert.True(returnTrip.Accepted,
            "the linked underground endpoint should return to the surface");
        CheckAssert.Equal(0, returnTrip.ActorTransition!.Value.WorldLevel,
            "underground traversal should target the surface");

        actor = actor with
        {
            WorldLevel = 0,
            Gameplay = returnTrip.Gameplay!.Value
        };
        surface = authority.CaptureObject(surface.ObjectId);
        var take = authority.Execute(actor, new TakeCaveRopeTransaction(
            Context(actor), Handle(authority, surface)));
        CheckAssert.True(take.Accepted,
            "the surface owner should recover the rope");
        CheckAssert.Equal(2, take.ObjectDeltas.Length,
            "rope recovery should update both endpoints");
        actor = actor with { Gameplay = take.Gameplay!.Value };
        surface = authority.CaptureObject(surface.ObjectId);
        var materialSlot = Slot(actor.Gameplay, ItemIds.Dirt);
        var fill = authority.Execute(actor, new FillExcavationTransaction(
            Context(actor), Handle(authority, surface), materialSlot));
        CheckAssert.True(fill.Accepted,
            "an open shaft should accept its original fill material");
        CheckAssert.Equal(2, fill.ObjectDeltas.Length,
            "filling should remove both linked endpoints atomically");
        CheckAssert.True(fill.ObjectDeltas.All(value =>
                value.Kind == WorldObjectChangeKind.Removed),
            "filling should emit only linked endpoint removals");
    }

    private static void SessionTraversalIsDurableAndIdempotent()
    {
        var sessionId = new SessionId(Guid.Parse(
            "ca300000-0000-0000-0000-000000000001"));
        var surfaceId = Guid.Parse(
            "ca300000-0000-0000-0000-000000000002");
        var undergroundId = Guid.Parse(
            "ca300000-0000-0000-0000-000000000003");
        var position = new Vector2(.5f, .5f);
        var session = Session(sessionId);
        var surface = session.SeedWorldObject(new(
            surfaceId,
            CaveExcavationRules.RopedEntranceItemId,
            position,
            CaveExcavationRules.SurfaceWorldLevel,
            Health: 0,
            MaximumHealth: 50,
            LinkedObjectId: undergroundId));
        session.SeedWorldObject(new(
            undergroundId,
            CaveExcavationRules.RopedEntranceItemId,
            position,
            CaveExcavationRules.UndergroundWorldLevel,
            Health: 0,
            MaximumHealth: 50,
            LinkedObjectId: surfaceId));
        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, position);

        var walkPending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new WalkIntent(new Vector2(4, 0))));
        session.Drain();
        CheckAssert.True(walkPending.GetAwaiter().GetResult().Accepted,
            "the fixture route should be active before traversal");
        CheckAssert.True(session.CaptureSnapshot().Actors.Single()
                .Destination is not null,
            "the fixture must prove that traversal clears an existing route");

        var commandId = Guid.Parse(
            "ca300000-0000-0000-0000-000000000004");
        var intent = new TraverseCaveIntent(
            commandId,
            joined.Gameplay.Inventory.Revision,
            joined.Gameplay.ActorRevision,
            new(surface.ObjectId,
                surface.Chunk,
                surface.ObjectRevision,
                session.CaptureWorldChunkRevision(surface.Chunk)));
        var traversePending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            2,
            intent));
        session.Drain();
        var traversed = traversePending.GetAwaiter().GetResult();
        CheckAssert.True(traversed.Accepted && !traversed.Duplicate,
            "session traversal should commit once");
        CheckAssert.Equal(joined.Gameplay.ActorRevision + 1,
            traversed.ActorRevision,
            "traversal should advance the actor exactly once");
        var actor = session.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(CaveExcavationRules.UndergroundWorldLevel,
            actor.WorldLevel,
            "the session snapshot should publish the destination level");
        CheckAssert.Equal(position, actor.Position,
            "the session snapshot should publish the linked endpoint position");
        CheckAssert.True(actor.Destination is null &&
                         actor.Velocity == Vector2.Zero,
            "traversal should clear every queued waypoint and velocity");
        CheckAssert.Equal(traversed.ActorRevision,
            actor.Gameplay.ActorRevision,
            "the result and actor snapshot must expose one gameplay revision");
        CheckAssert.Equal(traversed.Gameplay.DiggingExperience,
            actor.Gameplay.DiggingExperience,
            "the result and actor snapshot must expose the same digging state");

        var checkpoint = session.CaptureCheckpoint();
        var durableActor = checkpoint.Actors.Single();
        CheckAssert.Equal(actor.WorldLevel, durableActor.WorldLevel,
            "the checkpoint should preserve the traversed world level");
        CheckAssert.Equal(actor.Position, durableActor.Position,
            "the checkpoint should preserve the linked endpoint position");
        CheckAssert.Equal(actor.Gameplay.ActorRevision,
            durableActor.Gameplay.ActorRevision,
            "the checkpoint should preserve the same gameplay revision");

        var restored = Session(sessionId);
        restored.RestoreCheckpoint(checkpoint);
        var restoredConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(
            new ReconnectRequest(
                restoredConnection,
                joined.Identity.PlayerId,
                joined.ReconnectToken));
        restored.Drain();
        CheckAssert.True(reconnectPending.GetAwaiter().GetResult().Accepted,
            "the traversed actor should reconnect after restart");
        var reconnected = restored.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(CaveExcavationRules.UndergroundWorldLevel,
            reconnected.WorldLevel,
            "restart and reconnect must retain the underground level");
        CheckAssert.Equal(position, reconnected.Position,
            "restart and reconnect must retain the portal destination");

        var replayPending = restored.EnqueueIntentAsync(new ActorCommand(
            restoredConnection,
            joined.Identity.PlayerId,
            3,
            intent));
        restored.Drain();
        var replay = replayPending.GetAwaiter().GetResult();
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "a restored traversal receipt should replay without traversing again");
        var afterReplay = restored.CaptureSnapshot().Actors.Single();
        CheckAssert.Equal(CaveExcavationRules.UndergroundWorldLevel,
            afterReplay.WorldLevel,
            "a replayed traversal must not return the actor to the surface");
        CheckAssert.Equal(actor.Gameplay.ActorRevision,
            afterReplay.Gameplay.ActorRevision,
            "a replayed traversal must not advance actor state twice");
    }

    private static void StandingTreeBlocksUntilFelled()
    {
        var position = new Vector2(.5f, .5f);
        var chunk = WorldChunkKey.At(position, 0);
        var descriptor = new ResourceNodeDescriptor(
            new ResourceNodeId(Guid.Parse(
                "ca400000-0000-0000-0000-000000000001")),
            ResourceNodeKind.Tree,
            chunk,
            position,
            Variant: 0,
            InitialHealth: 1,
            MaximumHealth: 1);
        var catalog = new FixedResourceCatalog(descriptor);
        var standingResources = new AuthoritativeResourceTransactions(
            41, catalog);
        var blockedSession = Session(
            new SessionId(Guid.Parse(
                "ca400000-0000-0000-0000-000000000002")),
            standingResources);
        var blockedConnection = ClientConnectionId.New();
        var blockedJoin = JoinWithShovel(
            blockedSession, blockedConnection, position);
        var blocked = StartExcavation(
            blockedSession, blockedConnection, blockedJoin, position);
        CheckAssert.Equal(IntentStatus.InvalidPlacement, blocked.Status,
            "a procedural standing tree should block excavation placement");

        var felledResources = new AuthoritativeResourceTransactions(
            41, catalog);
        felledResources.RestoreCheckpoint(new(
            [new ResourceChunkSparseState(
                chunk,
                ResourceChunkRevision: 1,
                [new ResourceNodeSparseState(
                    descriptor.Id,
                    ResourceNodeKind.Tree,
                    chunk,
                    NodeRevision: 1,
                    Health: 0,
                    Remaining: 0,
                    ReadyAtGameSeconds: 0,
                    Depleted: true)])],
            []));
        var clearSession = Session(
            new SessionId(Guid.Parse(
                "ca400000-0000-0000-0000-000000000003")),
            felledResources);
        var clearConnection = ClientConnectionId.New();
        var clearJoin = JoinWithShovel(
            clearSession, clearConnection, position);
        var accepted = StartExcavation(
            clearSession, clearConnection, clearJoin, position);
        CheckAssert.True(accepted.Accepted,
            "the same catalog tree should release its tile when sparse state marks it felled");
    }

    private static WorldTransactionActorInput Actor(
        IReadOnlyList<(string ItemId, int Quantity)> items)
    {
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(
            PlayerInventory.Capacity);
        var expanded = items.SelectMany(value =>
            Enumerable.Repeat(value.ItemId, value.Quantity)).ToArray();
        for (var slot = 0; slot < PlayerInventory.Capacity; slot++)
            slots.Add(slot < expanded.Length
                ? new(slot, expanded[slot], 1)
                : new(slot, null, 0));
        var gameplay = new PlayerGameplaySnapshot(
            1, 100, 100, 0, 0, 0,
            new(1, slots.MoveToImmutable()));
        return new(new ActorId(Guid.Parse(
            "ca900000-0000-0000-0000-000000000001")),
            new(.5f, .5f), 0, gameplay);
    }

    private static WorldTransactionContext Context(
        WorldTransactionActorInput actor) => new(
            Guid.NewGuid(), actor.ActorId,
            actor.Gameplay.ActorRevision,
            actor.Gameplay.Inventory.Revision);

    private static WorldObjectHandle Handle(
        AuthoritativeWorldTransactions authority,
        AuthoritativeWorldObjectSnapshot value) => new(
            value.ObjectId, value.Chunk, value.ObjectRevision,
            authority.CaptureChunkRevision(value.Chunk));

    private static int Slot(PlayerGameplaySnapshot gameplay, string itemId) =>
        gameplay.Inventory.Slots.First(value => value.ItemId == itemId).Slot;

    private static AuthoritativeWorldSession Session(
        SessionId sessionId,
        AuthoritativeResourceTransactions? resources = null) =>
        new(
            identitySource: new DeterministicIdentitySource(),
            sessionId: sessionId,
            worldTransactions: new AuthoritativeWorldTransactions(
                caves: new FixedEnvironment(caveBelow: true)),
            resourceTransactions: resources);

    private static JoinResult Join(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        Vector2 position)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection, "Cave tester", position));
        session.Drain();
        var joined = pending.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, "the cave test actor should join");
        return joined;
    }

    private static JoinResult JoinWithShovel(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        Vector2 position)
    {
        var pending = session.EnqueueJoinAsync(new JoinRequest(
            connection,
            "Tree tester",
            position,
            [new InitialInventoryItem(ItemIds.StoneShovel)]));
        session.Drain();
        var joined = pending.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, "the tree test actor should join");
        return joined;
    }

    private static IntentResult StartExcavation(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        JoinResult joined,
        Vector2 position)
    {
        var pending = session.EnqueueIntentAsync(new ActorCommand(
            connection,
            joined.Identity.PlayerId,
            1,
            new StartExcavationIntent(
                Guid.NewGuid(),
                joined.Gameplay.Inventory.Revision,
                joined.Gameplay.ActorRevision,
                position,
                CaveExcavationRules.SurfaceWorldLevel,
                0,
                ExpectedChunkRevision: 0)));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "ca300000-0000-0000-0000-000000000005")),
            new ActorId(Guid.Parse(
                "ca300000-0000-0000-0000-000000000006")));

        public ReconnectToken CreateReconnectToken() =>
            new("cave-authority-reconnect-token");
    }

    private sealed class FixedResourceCatalog(
        ResourceNodeDescriptor descriptor) : IResourceDescriptorResolver
    {
        public bool TryResolve(
            long worldSeed,
            ResourceNodeReference reference,
            out ResourceNodeDescriptor resolved)
        {
            if (reference.Id == descriptor.Id &&
                reference.Chunk == descriptor.Chunk)
            {
                resolved = descriptor;
                return true;
            }
            resolved = null!;
            return false;
        }

        public IReadOnlyList<ResourceNodeDescriptor> DescribeChunk(
            long worldSeed,
            WorldChunkKey chunk) =>
            chunk == descriptor.Chunk ? [descriptor] : [];
    }

    private sealed class FixedEnvironment(bool caveBelow) :
        ICaveExcavationEnvironment
    {
        public Vector2 Snap(Vector2 position) =>
            CaveExcavationRules.Snap(position);

        public ExcavationTerrain TerrainAt(Vector2 position) =>
            CaveExcavationRules.Terrain(ExcavationTerrainKind.Soil);

        public bool IsSurfaceDiggable(Vector2 position) => true;

        public bool IsCaveBelow(Vector2 position) => caveBelow;
    }
}
