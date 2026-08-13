using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class WorldTransactionChecks
{
    private const string Log = "logs";
    private const string Rock = "large_rock";
    private const string Hammer = "stone_hammer";
    private const string SmallRocks = "small_rocks";
    private const string Knife = "stone_knife";
    private const string Campfire = "campfire";
    private const string Chest = "storage_chest";
    private const string Wall = "wooden_wall";
    private const string LootBag = "loot_bag";
    private const string SlimeGel = "slime_gel";

    public static void Register(CheckRunner checks)
    {
        checks.Add("world transactions pickup/drop are atomic and revisioned",
            PickupDropAtomicAndRevisioned);
        checks.Add("world transactions reject stale revisions and replay duplicates",
            StaleRevisionsAndDuplicateReplay);
        checks.Add("world container transfers are atomic and private",
            ContainerTransfersAreAtomic);
        checks.Add("world loot bags are authoritative withdraw-only containers",
            LootBagsAreAuthoritativeWithdrawOnlyContainers);
        checks.Add("world transactions enforce access range and world level",
            AccessRangeAndLevelAreValidated);
        checks.Add("world campfire transactions use shared fire rules",
            CampfireTransactionsUseCoreRules);
        checks.Add("world cooking cleanup refunds dead actors",
            CookingCleanupRefundsDeadActors);
        checks.Add("world construction place build demolish is authoritative",
            ConstructionLifecycleIsAuthoritative);
    }

    private static void PickupDropAtomicAndRevisioned()
    {
        var actorId = new ActorId(Guid.Parse(
            "10000000-0000-0000-0000-000000000001"));
        var droppedId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var authority = new AuthoritativeWorldTransactions(() => droppedId);
        var actor = Actor(actorId, [(Rock, 1)]);
        var objectId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var worldObject = authority.AddObject(new(
            objectId, Log, new(1, 0)));
        var beforeChunk = authority.CaptureChunkRevision(worldObject.Chunk);

        var pick = authority.Execute(actor, new PickUpWorldObjectTransaction(
            Context(actor), Handle(worldObject, beforeChunk)));
        CheckAssert.True(pick.Accepted, "pickup should succeed");
        CheckAssert.Equal(2u, pick.InventoryRevision,
            "pickup should advance inventory revision once");
        CheckAssert.Equal(2u, pick.ActorRevision,
            "pickup should advance actor revision once");
        CheckAssert.Equal(WorldObjectChangeKind.Removed,
            pick.ObjectDeltas.Single().Kind,
            "pickup should emit one removal");
        CheckAssert.Equal(beforeChunk + 1,
            pick.ChunkDeltas.Single().CurrentRevision,
            "pickup should advance its chunk once");
        CheckAssert.Equal(1, Count(pick.Gameplay!.Value, Log),
            "pickup should add the object item");

        actor = actor with { Gameplay = pick.Gameplay!.Value };
        var rockSlot = Slot(actor.Gameplay, Rock);
        var chunkRevision = authority.CaptureChunkRevision(worldObject.Chunk);
        var drop = authority.Execute(actor, new DropInventoryItemTransaction(
            Context(actor), rockSlot, 1, new(.5f, .5f), 0,
            chunkRevision));
        CheckAssert.True(drop.Accepted, "drop should succeed");
        CheckAssert.Equal(droppedId, drop.ObjectDeltas.Single().ObjectId,
            "drop should use a stable authority-issued ID");
        CheckAssert.Equal(0, Count(drop.Gameplay!.Value, Rock),
            "drop should remove the exact carried item");
        CheckAssert.Equal(chunkRevision + 1,
            drop.ChunkDeltas.Single().CurrentRevision,
            "drop should increment the target chunk once");

        var full = Actor(new ActorId(Guid.NewGuid()), FullInventory());
        var fullAuthority = new AuthoritativeWorldTransactions();
        var fullObject = fullAuthority.AddObject(new(
            Guid.NewGuid(), Log, new(1, 0)));
        var fullChunk = fullAuthority.CaptureChunkRevision(fullObject.Chunk);
        var failed = fullAuthority.Execute(full,
            new PickUpWorldObjectTransaction(
                Context(full), Handle(fullObject, fullChunk)));
        CheckAssert.Equal(WorldTransactionStatus.InventoryFull, failed.Status,
            "pickup into a full bag should fail");
        CheckAssert.Equal(fullChunk,
            fullAuthority.CaptureChunkRevision(fullObject.Chunk),
            "failed pickup must not advance the chunk");
        CheckAssert.Equal(fullObject,
            fullAuthority.CaptureObject(fullObject.ObjectId),
            "failed pickup must leave the object untouched");
        AssertGameplayEqual(full.Gameplay, failed.Gameplay!.Value,
            "failed pickup must leave gameplay untouched");
    }

    private static void StaleRevisionsAndDuplicateReplay()
    {
        var actorId = new ActorId(Guid.NewGuid());
        var actor = Actor(actorId, [(Rock, 1)]);
        var generated = Guid.NewGuid();
        var authority = new AuthoritativeWorldTransactions(() => generated);
        var chunk = WorldChunkKey.At(Vector2.Zero, 0);
        var commandId = Guid.NewGuid();
        var command = new DropInventoryItemTransaction(
            Context(actor, commandId), Slot(actor.Gameplay, Rock), 1,
            Vector2.Zero, 0, authority.CaptureChunkRevision(chunk));
        var accepted = authority.Execute(actor, command);
        var replayed = authority.Execute(actor, command);
        CheckAssert.True(ReferenceEquals(accepted, replayed),
            "duplicate command replay should return the original immutable receipt");
        CheckAssert.Equal(1, accepted.ObjectDeltas.Length,
            "duplicate replay must not create a second object");
        CheckAssert.Equal(accepted.ChunkDeltas.Single().CurrentRevision,
            authority.CaptureChunkRevision(chunk),
            "duplicate replay must not advance the chunk twice");
        var conflicting = authority.Execute(actor, command with
        {
            Quantity = 2
        });
        CheckAssert.Equal(WorldTransactionStatus.CommandIdConflict,
            conflicting.Status,
            "reusing a command ID for another payload must reject");

        var staleActor = actor with
        {
            Gameplay = actor.Gameplay with { ActorRevision = 2 }
        };
        var staleActorResult = authority.Execute(staleActor,
            command with { Context = Context(staleActor) with
            {
                ExpectedActorRevision = 1
            }});
        CheckAssert.Equal(WorldTransactionStatus.StaleActorRevision,
            staleActorResult.Status, "stale actor revisions must reject");

        var worldObject = authority.AddObject(new(
            Guid.NewGuid(), Log, new(1, 0)));
        var currentChunk = authority.CaptureChunkRevision(worldObject.Chunk);
        var staleObjectResult = authority.Execute(actor,
            new PickUpWorldObjectTransaction(
            Context(actor), Handle(worldObject, currentChunk) with
            {
                ExpectedObjectRevision = worldObject.ObjectRevision - 1
            }));
        CheckAssert.Equal(WorldTransactionStatus.StaleObjectRevision,
            staleObjectResult.Status, "stale object revisions must reject");
        var staleChunkResult = authority.Execute(actor,
            new PickUpWorldObjectTransaction(
                Context(actor), Handle(worldObject, currentChunk - 1)));
        CheckAssert.Equal(WorldTransactionStatus.StaleChunkRevision,
            staleChunkResult.Status, "stale chunk revisions must reject");
    }

    private static void ContainerTransfersAreAtomic()
    {
        var actor = Actor(new ActorId(Guid.NewGuid()), [(Log, 2)]);
        var authority = new AuthoritativeWorldTransactions();
        var chest = authority.AddObject(new(
            Guid.NewGuid(), Chest, new(1, 0), OwnerId: actor.ActorId.ToString()));
        var chunkRevision = authority.CaptureChunkRevision(chest.Chunk);
        var open = authority.Execute(actor, new OpenWorldContainerTransaction(
            Context(actor), Handle(chest, chunkRevision,
                chest.ContainerRevision)));
        CheckAssert.True(open.Accepted && open.Container is not null,
            "owner should receive a private container baseline");
        CheckAssert.Equal(48, open.Container!.Slots.Length,
            "wooden chest should use shared storage capacity");
        CheckAssert.Equal(0, open.ObjectDeltas.Length,
            "opening a container must not mutate public world state");

        var deposit = authority.Execute(actor,
            new TransferWorldContainerTransaction(
                Context(actor), Handle(chest, chunkRevision,
                    chest.ContainerRevision),
                WorldContainerTransferDirection.Deposit,
                Slot(actor.Gameplay, Log), 0, 1));
        CheckAssert.True(deposit.Accepted, "deposit should succeed atomically");
        CheckAssert.Equal(1, Count(deposit.Gameplay!.Value, Log),
            "deposit should remove the exact inventory quantity");
        CheckAssert.Equal(1,
            deposit.Container!.Slots.Sum(value =>
                value.ItemId == Log ? value.Quantity : 0),
            "deposit should add the exact container quantity");

        actor = actor with { Gameplay = deposit.Gameplay!.Value };
        chest = deposit.ObjectDeltas.Single().Object!;
        chunkRevision = deposit.ChunkDeltas.Single().CurrentRevision;
        var withdraw = authority.Execute(actor,
            new TransferWorldContainerTransaction(
                Context(actor), Handle(chest, chunkRevision,
                    chest.ContainerRevision),
                WorldContainerTransferDirection.Withdraw,
                0, Slot(deposit.Container!, Log), 1));
        CheckAssert.True(withdraw.Accepted, "withdraw should succeed atomically");
        CheckAssert.Equal(2, Count(withdraw.Gameplay!.Value, Log),
            "withdraw should add exactly one item");
        CheckAssert.Equal(chest.ContainerRevision + 1,
            withdraw.Container!.ContainerRevision,
            "container mutation should increment its private revision");

        var beforeObject = authority.CaptureObject(chest.ObjectId);
        var beforeChunk = authority.CaptureChunkRevision(chest.Chunk);
        var staleContainer = authority.Execute(
            actor with { Gameplay = withdraw.Gameplay!.Value },
            new TransferWorldContainerTransaction(
                Context(actor with { Gameplay = withdraw.Gameplay!.Value }),
                Handle(beforeObject, beforeChunk,
                    beforeObject.ContainerRevision - 1),
                WorldContainerTransferDirection.Withdraw,
                0, 0, 1));
        CheckAssert.Equal(WorldTransactionStatus.StaleContainerRevision,
            staleContainer.Status, "stale private container state must reject");
        CheckAssert.Equal(beforeObject,
            authority.CaptureObject(chest.ObjectId),
            "rejected container transfer must not mutate the object");
        CheckAssert.Equal(beforeChunk,
            authority.CaptureChunkRevision(chest.Chunk),
            "rejected container transfer must not advance its chunk");

        var full = Actor(new ActorId(Guid.NewGuid()), FullInventory());
        var fullAuthority = new AuthoritativeWorldTransactions();
        var fullChest = fullAuthority.AddObject(new(
            Guid.NewGuid(), Chest, new(1, 0),
            ContainerItems: [(Log, 1, null)]));
        var fullRevision = fullAuthority.CaptureChunkRevision(fullChest.Chunk);
        var noSpace = fullAuthority.Execute(full,
            new TransferWorldContainerTransaction(
                Context(full), Handle(fullChest, fullRevision,
                    fullChest.ContainerRevision),
                WorldContainerTransferDirection.Withdraw, 0, 0, 1));
        CheckAssert.Equal(WorldTransactionStatus.InventoryFull, noSpace.Status,
            "withdraw without bag space must fail");
        AssertGameplayEqual(full.Gameplay, noSpace.Gameplay!.Value,
            "failed withdraw must not mutate inventory");
        CheckAssert.Equal(fullChest,
            fullAuthority.CaptureObject(fullChest.ObjectId),
            "failed withdraw must not mutate storage");
    }

    private static void LootBagsAreAuthoritativeWithdrawOnlyContainers()
    {
        var actor = Actor(new ActorId(Guid.NewGuid()), [(Log, 1)]);
        var authority = new AuthoritativeWorldTransactions();
        var commandId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var committed = authority.AddObjectCommitted(commandId, new(
            objectId,
            LootBag,
            new(1, 0),
            ContainerItems: [(SlimeGel, 2, null)]));
        CheckAssert.True(committed.Accepted,
            "trusted combat loot creation should commit");
        CheckAssert.Equal(commandId, committed.CommandId,
            "the autonomous mutation must retain its stable identity");
        CheckAssert.Equal(WorldObjectChangeKind.Added,
            committed.ObjectDeltas.Single().Kind,
            "loot creation should publish one exact object addition");
        CheckAssert.Equal(1u,
            committed.ChunkDeltas.Single().CurrentRevision,
            "loot creation should advance its chunk exactly once");

        var bag = committed.ObjectDeltas.Single().Object!;
        var chunkRevision = committed.ChunkDeltas.Single().CurrentRevision;
        var opened = authority.Execute(actor,
            new OpenWorldContainerTransaction(
                Context(actor), Handle(bag, chunkRevision,
                    bag.ContainerRevision)));
        CheckAssert.True(opened.Accepted && opened.Container is not null,
            "a nearby loot bag should expose its private contents");
        var container = opened.Container!;
        CheckAssert.False(container.AllowsDeposit,
            "loot bags must never accept player deposits");
        CheckAssert.Equal(12, container.Slots.Length,
            "loot bags should use their bounded shared capacity");
        CheckAssert.Equal(2,
            container.Slots.Sum(value =>
                value.ItemId == SlimeGel ? value.Quantity : 0),
            "trusted seeding must materialize the exact deterministic loot");

        var rejectedDeposit = authority.Execute(actor,
            new TransferWorldContainerTransaction(
                Context(actor), Handle(bag, chunkRevision,
                    bag.ContainerRevision),
                WorldContainerTransferDirection.Deposit,
                Slot(actor.Gameplay, Log), 0, 1));
        CheckAssert.Equal(WorldTransactionStatus.ContainerDepositDenied,
            rejectedDeposit.Status,
            "a client must not disguise arbitrary items as enemy loot");
        CheckAssert.Equal(chunkRevision,
            authority.CaptureChunkRevision(bag.Chunk),
            "a rejected deposit must not advance public state");

        var withdrawn = authority.Execute(actor,
            new TransferWorldContainerTransaction(
                Context(actor), Handle(bag, chunkRevision,
                    bag.ContainerRevision),
                WorldContainerTransferDirection.Withdraw,
                SlotOrEmpty(actor.Gameplay), Slot(container, SlimeGel), 1));
        CheckAssert.True(withdrawn.Accepted,
            "a valid loot withdrawal should commit atomically");
        CheckAssert.Equal(1, Count(withdrawn.Gameplay!.Value, SlimeGel),
            "withdrawal should grant exactly one authoritative item");
        var emptied = authority.Execute(
            actor with { Gameplay = withdrawn.Gameplay!.Value },
            new TransferWorldContainerTransaction(
                Context(actor with { Gameplay = withdrawn.Gameplay!.Value }),
                new WorldObjectHandle(
                    bag.ObjectId,
                    bag.Chunk,
                    withdrawn.Container!.ObjectRevision,
                    withdrawn.ChunkDeltas.Single().CurrentRevision,
                    withdrawn.Container.ContainerRevision),
                WorldContainerTransferDirection.Withdraw,
                SlotOrEmpty(withdrawn.Gameplay.Value),
                Slot(withdrawn.Container, SlimeGel),
                1));
        CheckAssert.True(emptied.Accepted,
            "the final valid withdrawal should commit");
        CheckAssert.Equal(0,
            emptied.Container!.Slots.Sum(value =>
                value.ItemId == SlimeGel ? value.Quantity : 0),
            "the final withdrawal should return an empty private projection");
        CheckAssert.Equal(WorldObjectChangeKind.Removed,
            emptied.ObjectDeltas.Single().Kind,
            "an emptied loot bag must be removed in the same transaction");
        CheckAssert.Equal(2, Count(emptied.Gameplay!.Value, SlimeGel),
            "both deterministic items should be granted exactly once");
        CheckAssert.Throws<KeyNotFoundException>(
            () => authority.CaptureObject(objectId),
            "an emptied loot bag must no longer exist authoritatively");

        var checkpoint = authority.CaptureCheckpoint();
        var restored = new AuthoritativeWorldTransactions();
        restored.RestoreCheckpoint(checkpoint);
        CheckAssert.Throws<KeyNotFoundException>(
            () => restored.CaptureObject(objectId),
            "restart must not resurrect an emptied loot bag");
        CheckAssert.Equal(emptied.ChunkDeltas.Single().CurrentRevision,
            restored.CaptureChunkRevision(bag.Chunk),
            "restart must preserve the removal's exact public chunk revision");
    }

    private static void AccessRangeAndLevelAreValidated()
    {
        var actor = Actor(new ActorId(Guid.NewGuid()), []);
        var authority = new AuthoritativeWorldTransactions();
        var other = new ActorId(Guid.NewGuid());
        var owned = authority.AddObject(new(
            Guid.NewGuid(), Log, new(1, 0), OwnerId: other.ToString()));
        var chunk = authority.CaptureChunkRevision(owned.Chunk);
        var denied = authority.Execute(actor,
            new PickUpWorldObjectTransaction(
                Context(actor), Handle(owned, chunk)));
        CheckAssert.Equal(WorldTransactionStatus.AccessDenied, denied.Status,
            "foreign ownership should deny pickup");

        var far = authority.AddObject(new(
            Guid.NewGuid(), Log, new(20, 20)));
        var farChunk = authority.CaptureChunkRevision(far.Chunk);
        var outOfRange = authority.Execute(actor,
            new PickUpWorldObjectTransaction(
                Context(actor), Handle(far, farChunk)));
        CheckAssert.Equal(WorldTransactionStatus.OutOfRange, outOfRange.Status,
            "remote objects should reject interaction");

        var underground = authority.AddObject(new(
            Guid.NewGuid(), Log, new(1, 0), WorldLevel: -1));
        var undergroundChunk = authority.CaptureChunkRevision(underground.Chunk);
        var wrongLevel = authority.Execute(actor,
            new PickUpWorldObjectTransaction(
                Context(actor), Handle(underground, undergroundChunk)));
        CheckAssert.Equal(WorldTransactionStatus.WrongWorldLevel,
            wrongLevel.Status, "cross-level interaction should reject");
    }

    private static void CampfireTransactionsUseCoreRules()
    {
        var actor = Actor(new ActorId(Guid.NewGuid()),
            [(Log, 1), (SmallRocks, 1), (Knife, 1)]);
        var authority = new AuthoritativeWorldTransactions();
        var fire = authority.AddObject(new(
            Guid.NewGuid(), Campfire, new(1, 0),
            OwnerId: actor.ActorId.ToString()));
        var revision = authority.CaptureChunkRevision(fire.Chunk);
        var fuel = authority.Execute(actor, new AddCampfireFuelTransaction(
            Context(actor), Handle(fire, revision),
            Slot(actor.Gameplay, Log), 100));
        CheckAssert.True(fuel.Accepted, "valid log fuel should be accepted");
        CheckAssert.Equal(0, Count(fuel.Gameplay!.Value, Log),
            "adding fuel should consume one log");

        actor = actor with { Gameplay = fuel.Gameplay!.Value };
        fire = fuel.ObjectDeltas.Single().Object!;
        revision = fuel.ChunkDeltas.Single().CurrentRevision;
        var lit = authority.Execute(actor, new LightCampfireTransaction(
            Context(actor), Handle(fire, revision), 100));
        CheckAssert.True(lit.Accepted, "shared lighting prerequisites should pass");
        CheckAssert.True(lit.ObjectDeltas.Single().Object!.LitUntilGameSeconds > 100,
            "lighting should apply shared burn duration");

        var missing = Actor(new ActorId(Guid.NewGuid()), [(Log, 1)]);
        var missingAuthority = new AuthoritativeWorldTransactions();
        var missingFire = missingAuthority.AddObject(new(
            Guid.NewGuid(), Campfire, new(1, 0), FuelItemId: Log));
        var missingRevision = missingAuthority.CaptureChunkRevision(
            missingFire.Chunk);
        var failed = missingAuthority.Execute(missing,
            new LightCampfireTransaction(Context(missing),
                Handle(missingFire, missingRevision), 100));
        CheckAssert.Equal(
            WorldTransactionStatus.CampfireLightingRequirementsMissing,
            failed.Status, "missing spark tools should reject lighting");
        CheckAssert.Equal(missingRevision,
            missingAuthority.CaptureChunkRevision(missingFire.Chunk),
            "failed lighting must not advance the chunk");
    }

    private static void CookingCleanupRefundsDeadActors()
    {
        const string raw = "raw_minnows";
        const string cooked = "cooked_minnows";
        var actor = Actor(new ActorId(Guid.NewGuid()), [(raw, 1)]);
        var authority = new AuthoritativeWorldTransactions();
        var fire = authority.AddObject(new(
            Guid.NewGuid(), Campfire, new(1, 0),
            FuelItemId: Log, LitUntilGameSeconds: 300));
        var begun = authority.Execute(actor,
            new BeginCampfireCookingTransaction(
                Context(actor),
                Handle(fire, authority.CaptureChunkRevision(fire.Chunk)),
                Slot(actor.Gameplay, raw),
                GameSeconds: 100));
        CheckAssert.True(begun.Accepted,
            "a living actor should reserve one valid raw item");

        var deadGameplay = begun.Gameplay!.Value with { Health = 0 };
        var dead = actor with { Gameplay = deadGameplay };
        var completed = authority.CompleteCooking(dead,
            new CompleteCampfireCookingTransaction(
                Guid.NewGuid(),
                fire.ObjectId,
                fire.Chunk,
                fire.Position,
                SlotOrEmpty(deadGameplay),
                raw,
                cooked,
                Experience: 8,
                Burnt: false,
                Guid.NewGuid(),
                GameSeconds: 101));

        CheckAssert.True(completed.Accepted,
            "death must not prevent cleanup of a reserved cooking item");
        CheckAssert.Equal("cooking_interrupted", completed.Detail,
            "death must interrupt rather than complete cooking");
        CheckAssert.Equal(1, Count(completed.Gameplay!.Value, raw),
            "the reserved raw item must be returned exactly once");
        CheckAssert.Equal(0, Count(completed.Gameplay!.Value, cooked),
            "a dead actor must not receive cooked output");
        CheckAssert.Equal(deadGameplay.CookingExperience,
            completed.Gameplay!.Value.CookingExperience,
            "a dead actor must not gain cooking experience");
    }

    private static void ConstructionLifecycleIsAuthoritative()
    {
        var actor = Actor(new ActorId(Guid.NewGuid()), [(Log, 1), (Hammer, 1)]);
        var generated = Guid.NewGuid();
        var authority = new AuthoritativeWorldTransactions(() => generated);
        var chunk = WorldChunkKey.At(new(1, 0), 0);
        var placed = authority.Execute(actor,
            new PlaceConstructionTransaction(
                Context(actor), Wall, new(1, 0), 0, 3,
                authority.CaptureChunkRevision(chunk)));
        CheckAssert.True(placed.Accepted, "wall placement should succeed");
        CheckAssert.Equal(generated, placed.ObjectDeltas.Single().ObjectId,
            "construction should have a stable authority-issued ID");
        CheckAssert.Equal(1, placed.ObjectDeltas.Single().Object!.Health,
            "shared construction rule should begin at one health");
        CheckAssert.Equal(0, Count(placed.Gameplay!.Value, Log),
            "placement should atomically consume the recipe resources");

        actor = actor with { Gameplay = placed.Gameplay!.Value };
        var site = placed.ObjectDeltas.Single().Object!;
        var revision = placed.ChunkDeltas.Single().CurrentRevision;
        var built = authority.Execute(actor,
            new BuildConstructionTransaction(
                Context(actor), Handle(site, revision)));
        CheckAssert.True(built.Accepted, "hammer work should succeed");
        CheckAssert.True(built.ObjectDeltas.Single().Object!.Health > site.Health,
            "shared construction work formula should add health");
        CheckAssert.True(built.Gameplay!.Value.CraftingExperience >
            actor.Gameplay.CraftingExperience,
            "construction should award crafting experience");

        actor = actor with { Gameplay = built.Gameplay!.Value };
        site = built.ObjectDeltas.Single().Object!;
        revision = built.ChunkDeltas.Single().CurrentRevision;
        var demolished = authority.Execute(actor,
            new DemolishWorldObjectTransaction(
                Context(actor), Handle(site, revision)));
        CheckAssert.True(demolished.Accepted,
            "owner should demolish unfinished construction");
        CheckAssert.Equal(WorldObjectChangeKind.Removed,
            demolished.ObjectDeltas.Single().Kind,
            "demolition should emit removal");
        CheckAssert.Equal(1, Count(demolished.Gameplay!.Value, Log),
            "demolition should apply the shared refund item");
    }

    private static WorldTransactionActorInput Actor(
        ActorId id,
        IReadOnlyList<(string ItemId, int Quantity)> items,
        Vector2? position = null,
        int worldLevel = 0)
    {
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(28);
        var itemIndex = 0;
        var quantityRemaining = items.Count > 0 ? items[0].Quantity : 0;
        for (var slot = 0; slot < 28; slot++)
        {
            if (itemIndex >= items.Count)
            {
                slots.Add(new(slot, null, 0));
                continue;
            }
            var item = items[itemIndex];
            slots.Add(new(slot, item.ItemId, 1));
            quantityRemaining--;
            if (quantityRemaining <= 0)
            {
                itemIndex++;
                quantityRemaining = itemIndex < items.Count
                    ? items[itemIndex].Quantity
                    : 0;
            }
        }
        if (itemIndex < items.Count)
            throw new InvalidOperationException("Test inventory exceeds capacity.");
        return new(id, position ?? Vector2.Zero, worldLevel,
            new(1, 100, 100, 0, 0, 0,
                new(1, slots.MoveToImmutable())));
    }

    private static IReadOnlyList<(string ItemId, int Quantity)> FullInventory() =>
        Enumerable.Range(0, 28).Select(_ => (Rock, 1)).ToArray();

    private static WorldTransactionContext Context(
        WorldTransactionActorInput actor, Guid? commandId = null) =>
        new(commandId ?? Guid.NewGuid(), actor.ActorId,
            actor.Gameplay.ActorRevision, actor.Gameplay.Inventory.Revision);

    private static WorldObjectHandle Handle(
        AuthoritativeWorldObjectSnapshot value,
        uint chunkRevision,
        uint? containerRevision = null) =>
        new(value.ObjectId, value.Chunk, value.ObjectRevision, chunkRevision,
            containerRevision ?? value.ContainerRevision);

    private static int Count(PlayerGameplaySnapshot gameplay, string itemId) =>
        gameplay.Inventory.Slots.Sum(value =>
            value.ItemId == itemId ? value.Quantity : 0);

    private static int Slot(PlayerGameplaySnapshot gameplay, string itemId) =>
        gameplay.Inventory.Slots.First(value => value.ItemId == itemId).Slot;

    private static int SlotOrEmpty(PlayerGameplaySnapshot gameplay) =>
        gameplay.Inventory.Slots.First(value => value.ItemId is null).Slot;

    private static int Slot(WorldContainerSnapshot container, string itemId) =>
        container.Slots.First(value => value.ItemId == itemId).Slot;

    private static void AssertGameplayEqual(
        PlayerGameplaySnapshot expected,
        PlayerGameplaySnapshot actual,
        string message)
    {
        CheckAssert.Equal(expected.ActorRevision, actual.ActorRevision, message);
        CheckAssert.Equal(expected.Health, actual.Health, message);
        CheckAssert.Equal(expected.Hunger, actual.Hunger, message);
        CheckAssert.Equal(expected.WellFedSeconds, actual.WellFedSeconds, message);
        CheckAssert.Equal(expected.CraftingExperience,
            actual.CraftingExperience, message);
        CheckAssert.Equal(expected.CookingExperience,
            actual.CookingExperience, message);
        CheckAssert.Equal(expected.Inventory.Revision,
            actual.Inventory.Revision, message);
        CheckAssert.SequenceEqual(expected.Inventory.Slots,
            actual.Inventory.Slots, message);
    }
}
