using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

internal static class CropWorldTransactionChecks
{
    private const string GrainSeed = "wild_grain_seeds";
    private const string GrainCrop = "wild_grain_crop";
    private const string Grain = "wild_grain";
    private const string Basket = "gathering_basket";
    private const string Rock = "large_rock";
    private const double GrowthSeconds = 4 * 60 * 60;

    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "crop world transactions plant harvest and checkpoint atomically",
            PlantHarvestAndCheckpointAreAtomic);
        checks.Add(
            "crop world transactions validate tile access revisions and capacity",
            TileAccessRevisionsAndCapacityAreValidated);
        checks.Add(
            "lit campfire query enables canonical twenty-times regeneration",
            LitCampfireEnablesTwentyTimesRegeneration);
    }

    private static void PlantHarvestAndCheckpointAreAtomic()
    {
        var actorId = new ActorId(Guid.Parse(
            "41000000-0000-0000-0000-000000000001"));
        var commandId = Guid.Parse(
            "42000000-0000-0000-0000-000000000001");
        var cropId = AuthoritativeWorldTransactions.DeriveCropObjectId(
            actorId, commandId, 1);
        CheckAssert.Equal(cropId,
            AuthoritativeWorldTransactions.DeriveCropObjectId(
                actorId, commandId, 1),
            "crop object identity should be deterministic");
        CheckAssert.True(cropId !=
            AuthoritativeWorldTransactions.DeriveCropObjectId(
                actorId, commandId, 2),
            "reusing a command at a later actor revision must use a fresh crop identity");

        var authority = new AuthoritativeWorldTransactions();
        var actor = Actor(actorId, [GrainSeed, Basket]);
        var position = new Vector2(1.5f, .5f);
        var chunk = WorldChunkKey.At(position, 0);
        var plantCommand = new PlantCropTransaction(
            Context(actor, commandId), cropId, 0, position, 0,
            authority.CaptureChunkRevision(chunk), 100);

        var planted = authority.Execute(actor, plantCommand);
        CheckAssert.True(planted.Accepted, "crop planting should succeed");
        CheckAssert.Equal(2u, planted.ActorRevision,
            "planting should advance actor revision once");
        CheckAssert.Equal(2u, planted.InventoryRevision,
            "planting should advance inventory revision once");
        CheckAssert.Equal(0, Count(planted.Gameplay!.Value, GrainSeed),
            "planting should consume the exact seed slot");
        CheckAssert.Equal(25, planted.Gameplay.Value.FarmingExperience,
            "planting should atomically award Farming XP");
        CheckAssert.Equal(7, planted.Gameplay.Value.AdventureExperience,
            "planting should atomically award Adventure XP");
        var crop = planted.ObjectDeltas.Single().Object!;
        CheckAssert.Equal(cropId, crop.ObjectId,
            "planting should use the authority-supplied crop identity");
        CheckAssert.Equal(GrainCrop, crop.DefinitionId,
            "seed kind should select the canonical crop definition");
        CheckAssert.Equal(Grain, crop.FuelItemId,
            "crop state should persist its canonical harvest item");
        CheckAssert.Equal(100 + GrowthSeconds,
            crop.LitUntilGameSeconds,
            "crop state should persist an absolute readiness deadline");
        CheckAssert.Equal(actorId.ToString(), crop.OwnerId,
            "planted crops should remain private to their owner");

        var replay = authority.Execute(actor, plantCommand);
        CheckAssert.True(ReferenceEquals(planted, replay),
            "plant replay should return the original immutable receipt");
        CheckAssert.Equal(1,
            authority.CaptureCheckpoint().Objects.Length,
            "plant replay must not create a duplicate crop");

        actor = actor with { Gameplay = planted.Gameplay.Value };
        var cropChunkRevision = planted.ChunkDeltas.Single().CurrentRevision;
        var handle = Handle(crop, cropChunkRevision);
        var premature = authority.Execute(actor,
            new HarvestCropTransaction(
                Context(actor), handle,
                crop.LitUntilGameSeconds - .001));
        CheckAssert.Equal(WorldTransactionStatus.CropNotReady,
            premature.Status,
            "harvest should use the absolute readiness deadline");
        CheckAssert.Equal(crop,
            authority.CaptureObject(crop.ObjectId),
            "premature harvest must leave the crop untouched");

        var pickup = authority.Execute(actor,
            new PickUpWorldObjectTransaction(Context(actor), handle));
        CheckAssert.Equal(WorldTransactionStatus.NotPortable, pickup.Status,
            "generic pickup must not bypass crop harvesting rules");

        var checkpoint = authority.CaptureCheckpoint();
        var restored = new AuthoritativeWorldTransactions();
        restored.RestoreCheckpoint(checkpoint);
        CheckAssert.Equal(crop,
            restored.CaptureObject(crop.ObjectId),
            "checkpoint restore should retain crop output and deadline");
        CheckAssert.Equal(cropChunkRevision,
            restored.CaptureChunkRevision(crop.Chunk),
            "checkpoint restore should retain crop chunk revision");

        var harvested = restored.Execute(actor,
            new HarvestCropTransaction(
                Context(actor), handle, crop.LitUntilGameSeconds));
        CheckAssert.True(harvested.Accepted,
            "crop should harvest exactly at its readiness deadline");
        CheckAssert.Equal(3, Count(harvested.Gameplay!.Value, Grain),
            "a gathering basket should increase the canonical yield to three");
        CheckAssert.Equal(100, harvested.Gameplay.Value.FarmingExperience,
            "harvest yield should scale Farming XP atomically");
        CheckAssert.Equal(26, harvested.Gameplay.Value.AdventureExperience,
            "harvest should award derived Adventure XP atomically");
        CheckAssert.Equal(3u, harvested.ActorRevision,
            "harvest should advance actor revision once");
        CheckAssert.Equal(3u, harvested.InventoryRevision,
            "harvest should advance inventory revision once");
        CheckAssert.Equal(WorldObjectChangeKind.Removed,
            harvested.ObjectDeltas.Single().Kind,
            "harvest should emit a crop removal delta");
        CheckAssert.Equal(cropChunkRevision + 1,
            harvested.ChunkDeltas.Single().CurrentRevision,
            "harvest should advance the crop chunk once");
        CheckAssert.Throws<KeyNotFoundException>(
            () => restored.CaptureObject(crop.ObjectId),
            "harvest should remove the durable crop object");
    }

    private static void TileAccessRevisionsAndCapacityAreValidated()
    {
        var authority = new AuthoritativeWorldTransactions();
        var actor = Actor(new ActorId(Guid.NewGuid()), [GrainSeed]);
        var position = new Vector2(1.5f, .5f);
        var chunk = WorldChunkKey.At(position, 0);
        authority.AddObject(new(
            Guid.NewGuid(), Rock, new Vector2(1.1f, .1f)));
        var occupiedRevision = authority.CaptureChunkRevision(chunk);
        var occupied = authority.Execute(actor, new PlantCropTransaction(
            Context(actor), Guid.NewGuid(), 0, position, 0,
            occupiedRevision, 0));
        CheckAssert.Equal(WorldTransactionStatus.InvalidPlacement,
            occupied.Status,
            "planting should reject a tile occupied by another world object");

        var stale = authority.Execute(actor, new PlantCropTransaction(
            Context(actor), Guid.NewGuid(), 0, new(2.5f, .5f), 0,
            occupiedRevision - 1, 0));
        CheckAssert.Equal(WorldTransactionStatus.StaleChunkRevision,
            stale.Status,
            "planting should validate the exact target chunk revision");

        var wrongSlot = authority.Execute(actor,
            new PlantCropTransaction(
                Context(actor), Guid.NewGuid(), 1, new(2.5f, .5f), 0,
                occupiedRevision, 0));
        CheckAssert.Equal(WorldTransactionStatus.ItemUnavailable,
            wrongSlot.Status,
            "planting should consume only the requested seed slot");

        var cropId = Guid.NewGuid();
        var crop = authority.AddObject(new(
            cropId, GrainCrop, new Vector2(2.5f, .5f),
            FuelItemId: Grain,
            LitUntilGameSeconds: GrowthSeconds,
            OwnerId: "another_actor"));
        var cropRevision = authority.CaptureChunkRevision(crop.Chunk);
        var denied = authority.Execute(actor,
            new HarvestCropTransaction(
                Context(actor), Handle(crop, cropRevision), GrowthSeconds));
        CheckAssert.Equal(WorldTransactionStatus.AccessDenied, denied.Status,
            "harvest should enforce crop ownership");

        var fullAuthority = new AuthoritativeWorldTransactions();
        var fullActor = Actor(
            new ActorId(Guid.NewGuid()),
            Enumerable.Repeat(Rock, 27).Append(Basket).ToArray());
        var fullCrop = fullAuthority.AddObject(new(
            Guid.NewGuid(), GrainCrop, new Vector2(1, 0),
            FuelItemId: Grain,
            LitUntilGameSeconds: GrowthSeconds,
            OwnerId: fullActor.ActorId.ToString()));
        var fullChunk = fullAuthority.CaptureChunkRevision(fullCrop.Chunk);
        var noCapacity = fullAuthority.Execute(fullActor,
            new HarvestCropTransaction(
                Context(fullActor), Handle(fullCrop, fullChunk),
                GrowthSeconds));
        CheckAssert.Equal(WorldTransactionStatus.InventoryFull,
            noCapacity.Status,
            "harvest should require capacity for the complete basket yield");
        CheckAssert.Equal(fullCrop,
            fullAuthority.CaptureObject(fullCrop.ObjectId),
            "failed harvest must leave the crop untouched");
        CheckAssert.Equal(fullChunk,
            fullAuthority.CaptureChunkRevision(fullCrop.Chunk),
            "failed harvest must not advance the chunk");
    }

    private static void LitCampfireEnablesTwentyTimesRegeneration()
    {
        var authority = new AuthoritativeWorldTransactions();
        var now = AuthoritativeWorldTime.FromElapsedRealSeconds(10);
        authority.AddObject(new(
            Guid.NewGuid(), "campfire", new Vector2(1, 0),
            FuelItemId: "logs",
            LitUntilGameSeconds: now + 60));
        for (var index = 0; index < 1_024; index++)
        {
            authority.AddObject(new(
                Guid.NewGuid(), Rock,
                new Vector2(100 + index * 17, 100 + index * 11)));
        }
        authority.AddObject(new(
            Guid.NewGuid(), "campfire", new Vector2(1, 0),
            WorldLevel: -1,
            FuelItemId: "logs",
            LitUntilGameSeconds: now + 60));
        CheckAssert.True(authority.HasLitCampfireWithin(
                Vector2.Zero, 0, now,
                EntityHealthRegenerationService.LitCampfireRange),
            "lit campfire should be found using authoritative world time");
        CheckAssert.False(authority.HasLitCampfireWithin(
                Vector2.Zero, 0, now + 60,
                EntityHealthRegenerationService.LitCampfireRange),
            "campfire should stop boosting regeneration at its deadline");

        var restored = new AuthoritativeWorldTransactions();
        restored.RestoreCheckpoint(authority.CaptureCheckpoint());
        CheckAssert.True(restored.HasLitCampfireWithin(
                Vector2.Zero, 0, now,
                EntityHealthRegenerationService.LitCampfireRange),
            "checkpoint restore should rebuild the bounded campfire chunk index");
        CheckAssert.True(restored.HasLitCampfireWithin(
                Vector2.Zero, -1, now,
                EntityHealthRegenerationService.LitCampfireRange),
            "the campfire index should preserve world-level isolation");

        var ordinary = EntityHealthRegenerationService.Advance(
            50, 100, 1);
        var besideFire = EntityHealthRegenerationService.Advance(
            50, 100, 1,
            EntityHealthRegenerationService.LitCampfireHumanMultiplier);
        CheckAssert.Equal(0, ordinary.Health - 50,
            "ordinary one-second regeneration should remain fractional");
        CheckAssert.Equal(1, besideFire.Health - 50,
            "the canonical twenty-times fire multiplier should heal one point per second");
    }

    private static WorldTransactionActorInput Actor(
        ActorId id, IReadOnlyList<string> items)
    {
        if (items.Count > 28)
            throw new ArgumentOutOfRangeException(nameof(items));
        var slots = ImmutableArray.CreateBuilder<InventorySlotSnapshot>(28);
        for (var slot = 0; slot < 28; slot++)
            slots.Add(slot < items.Count
                ? new(slot, items[slot], 1)
                : new(slot, null, 0));
        return new(id, Vector2.Zero, 0,
            new PlayerGameplaySnapshot(
                1, 100, 100, 0, 0, 0,
                new(1, slots.MoveToImmutable())));
    }

    private static WorldTransactionContext Context(
        WorldTransactionActorInput actor, Guid? commandId = null) =>
        new(commandId ?? Guid.NewGuid(), actor.ActorId,
            actor.Gameplay.ActorRevision,
            actor.Gameplay.Inventory.Revision);

    private static WorldObjectHandle Handle(
        AuthoritativeWorldObjectSnapshot value, uint chunkRevision) =>
        new(value.ObjectId, value.Chunk,
            value.ObjectRevision, chunkRevision,
            value.ContainerRevision);

    private static int Count(
        PlayerGameplaySnapshot gameplay, string itemId) =>
        gameplay.Inventory.Slots.Sum(value =>
            value.ItemId == itemId ? value.Quantity : 0);
}
