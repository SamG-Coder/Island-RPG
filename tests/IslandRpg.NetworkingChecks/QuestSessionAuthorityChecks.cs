using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Caves;
using IslandRpg.Fishing;
using IslandRpg.Gameplay;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.NetworkingChecks;

/// <summary>
/// Focused checks for authoritative quest/session integration.
/// </summary>
internal static class QuestSessionAuthorityChecks
{
    public static void Register(CheckRunner checks)
    {
        checks.Add(
            "session quests progress once survive reconnect and raise maximum health",
            ProgressionReplayCheckpointAndCompletion);
        checks.Add(
            "non-quest action adventure XP reconciles maximum health",
            ActionExperienceReconcilesMaximumHealth);
        checks.Add(
            "committed quest event sources use exact authoritative outcomes",
            CommittedEventSourcesAreExact);
        checks.Add(
            "newly unlocked quests reconcile server-owned inventory once",
            NewlyUnlockedQuestReconcilesHeldInventoryOnce);
        checks.Add(
            "session restore rejects terminal revisions and over-cap adventure XP",
            RestoreRejectsTerminalRevisionAndOverCapExperience);
    }

    private static void RestoreRejectsTerminalRevisionAndOverCapExperience()
    {
        var source = NewSession();
        var connection = ClientConnectionId.New();
        Join(source, connection, "Restore guard", Vector2.Zero);
        var checkpoint = source.CaptureCheckpoint();
        var actor = checkpoint.Actors.Single();

        var terminalRevision = checkpoint with
        {
            Actors = [actor with
            {
                Gameplay = actor.Gameplay with
                {
                    ActorRevision = uint.MaxValue
                }
            }]
        };
        var terminalTarget = NewSession();
        CheckAssert.Throws<InvalidOperationException>(
            () => terminalTarget.RestoreCheckpoint(terminalRevision),
            "restore must reject a terminal actor revision before any aggregate commits");
        CheckAssert.Equal(0, terminalTarget.ActorCount,
            "a rejected terminal revision must leave the session pristine");

        var overCapExperience = AdventureService.ExperienceForLevel(
            AdventureService.MaximumLevel) + 1;
        var overCap = checkpoint with
        {
            Actors = [actor with
            {
                Gameplay = actor.Gameplay with
                {
                    AdventureExperience = overCapExperience,
                    MaximumHealth = AdventureService.MaximumHealth(
                        overCapExperience)
                }
            }]
        };
        var overCapTarget = NewSession();
        CheckAssert.Throws<InvalidOperationException>(
            () => overCapTarget.RestoreCheckpoint(overCap),
            "restore must reject Adventure XP above the gameplay cap before quest transactions");
        CheckAssert.Equal(0, overCapTarget.ActorCount,
            "rejected over-cap Adventure XP must leave the session pristine");
    }

    private static void ProgressionReplayCheckpointAndCompletion()
    {
        var session = NewSession();
        var materials = new List<AuthoritativeWorldObjectSnapshot>();
        Add(ItemIds.LargeRock, 5);
        Add(ItemIds.Sticks, 2);
        Add(ItemIds.PlantFibres, 2);

        var connection = ClientConnectionId.New();
        var joined = Join(session, connection, "Questor", Vector2.Zero);
        CheckAssert.Equal(QuestService.Definitions.Count,
            joined.Gameplay.Quests.Length,
            "fresh authority joins must materialize canonical quest state");
        CheckAssert.Equal(QuestStatus.InProgress,
            joined.Gameplay.Quests[0].Status,
            "the first quest must be active on a fresh join");

        var sequence = 1L;
        var firstIntent = PickupIntent(session, joined.Gameplay, materials[0]);
        var first = Send(
            session, connection, joined.Identity.PlayerId,
            sequence++, firstIntent);
        CheckAssert.True(first.Accepted,
            "an authoritative material pickup must commit");
        CheckAssert.Equal(1,
            Objective(first.Gameplay, 0, "large-rocks"),
            "the accepted pickup must advance its exact quest objective");
        CheckAssert.Equal(first.ActorRevision,
            first.WorldTransaction!.ActorRevision,
            "the requester world receipt must contain final quest revision");
        CheckAssert.Equal(
            Objective(first.Gameplay, 0, "large-rocks"),
            Objective(first.WorldTransaction.Gameplay!.Value, 0, "large-rocks"),
            "the requester world receipt must contain final quest gameplay");

        var replay = Send(
            session, connection, joined.Identity.PlayerId,
            sequence++, firstIntent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "an identical command must replay its exact session receipt");
        CheckAssert.Equal(first.ActorRevision, replay.ActorRevision,
            "receipt replay must not apply the quest event twice");
        CheckAssert.Equal(1,
            Objective(replay.Gameplay, 0, "large-rocks"),
            "receipt replay must preserve the original objective count");

        for (var index = 1; index < 4; index++)
        {
            var gameplay = Actor(session, joined.Identity.PlayerId).Gameplay;
            var result = Send(
                session, connection, joined.Identity.PlayerId,
                sequence++, PickupIntent(session, gameplay, materials[index]));
            CheckAssert.True(result.Accepted,
                "pre-checkpoint quest pickups must commit");
        }

        var checkpoint = session.CaptureCheckpoint();
        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var reconnectConnection = ClientConnectionId.New();
        var reconnectPending = restored.EnqueueReconnectAsync(new(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        var reconnected = reconnectPending.GetAwaiter().GetResult();
        CheckAssert.True(reconnected.Accepted,
            "the checkpointed quest owner must reconnect with its token");
        CheckAssert.Equal(4,
            Objective(reconnected.Gameplay, 0, "large-rocks"),
            "partial canonical quest progress must survive checkpoint restore");
        sequence = reconnected.NextCommandSequence;

        var restoredReplay = Send(
            restored, reconnectConnection, joined.Identity.PlayerId,
            sequence++, firstIntent);
        CheckAssert.True(restoredReplay.Accepted && restoredReplay.Duplicate,
            "a durable receipt must intercept a retry after restart");
        CheckAssert.Equal(4,
            Objective(restoredReplay.Gameplay, 0, "large-rocks"),
            "a restored receipt must not emit its quest event again");

        for (var index = 4; index < materials.Count; index++)
        {
            var gameplay = Actor(restored, joined.Identity.PlayerId).Gameplay;
            var result = Send(
                restored, reconnectConnection, joined.Identity.PlayerId,
                sequence++, PickupIntent(restored, gameplay, materials[index]));
            CheckAssert.True(result.Accepted,
                "post-reconnect quest pickups must commit");
        }

        var gathered = Actor(restored, joined.Identity.PlayerId).Gameplay;
        CheckAssert.Equal(QuestStatus.Complete, gathered.Quests[0].Status,
            "the exact gathered material totals must complete the first quest");
        CheckAssert.Equal(QuestStatus.InProgress, gathered.Quests[1].Status,
            "quest completion must unlock exactly its direct successor");
        CheckAssert.Equal(50, gathered.AdventureExperience,
            "the first quest reward must be awarded exactly once");

        foreach (var recipe in new[]
                 {
                     "medium-rock", "medium-rock", "medium-rock", "medium-rock",
                     "sharpened-rock", "sharpened-rock",
                     "stone-knife", "stone-axe",
                     "small-rocks", "small-rocks"
                 })
        {
            var gameplay = Actor(restored, joined.Identity.PlayerId).Gameplay;
            var intent = new CraftRecipeIntent(
                Guid.NewGuid(),
                gameplay.Inventory.Revision,
                gameplay.ActorRevision,
                recipe);
            var result = Send(
                restored, reconnectConnection, joined.Identity.PlayerId,
                sequence++, intent);
            CheckAssert.True(result.Accepted,
                $"authoritative recipe '{recipe}' must commit");
            if (recipe != "small-rocks" ||
                result.Gameplay.Quests[1].Status != QuestStatus.Complete)
                continue;

            CheckAssert.Equal(285, result.Gameplay.AdventureExperience,
                "quest rewards and authoritative crafting actions must award exact Adventure XP");
            CheckAssert.Equal(104, result.Gameplay.MaximumHealth,
                "quest reward level crossings must raise maximum health");
            CheckAssert.Equal(104, result.Gameplay.Health,
                "maximum-health gains must heal by the gained maximum only");
            var duplicate = Send(
                restored, reconnectConnection, joined.Identity.PlayerId,
                sequence++, intent);
            CheckAssert.True(duplicate.Accepted && duplicate.Duplicate,
                "the completing craft must replay without a second reward");
            CheckAssert.Equal(285, duplicate.Gameplay.AdventureExperience,
                "completion receipt replay must not duplicate quest XP");
            CheckAssert.Equal(result.ActorRevision, duplicate.ActorRevision,
                "completion receipt replay must not advance actor revision");
            return;
        }

        throw new InvalidOperationException(
            "The authoritative crafting sequence did not complete its quest.");

        void Add(string itemId, int quantity)
        {
            for (var index = 0; index < quantity; index++)
                materials.Add(session.SeedWorldObject(new(
                    Guid.NewGuid(), itemId, new Vector2(1, 0))));
        }
    }

    private static void ActionExperienceReconcilesMaximumHealth()
    {
        var session = NewSession();
        var connection = ClientConnectionId.New();
        var joined = Join(
            session,
            connection,
            "Farmer",
            new Vector2(.5f, .5f),
            [new InitialInventoryItem(ItemIds.WildGrainSeeds)]);
        var checkpoint = session.CaptureCheckpoint();
        var actor = checkpoint.Actors.Single();
        checkpoint = checkpoint with
        {
            Actors = [actor with
            {
                Gameplay = actor.Gameplay with
                {
                    AdventureExperience = 74,
                    MaximumHealth = 100,
                    Health = 100
                }
            }]
        };

        var restored = NewSession();
        restored.RestoreCheckpoint(checkpoint);
        var reconnectConnection = ClientConnectionId.New();
        var pending = restored.EnqueueReconnectAsync(new(
            reconnectConnection,
            joined.Identity.PlayerId,
            joined.ReconnectToken));
        restored.Drain();
        var reconnected = pending.GetAwaiter().GetResult();
        var seedSlot = reconnected.Gameplay.Inventory.Slots.Single(value =>
            value.ItemId == ItemIds.WildGrainSeeds).Slot;
        var intent = new PlantCropIntent(
            Guid.NewGuid(),
            reconnected.Gameplay.Inventory.Revision,
            reconnected.Gameplay.ActorRevision,
            seedSlot,
            new Vector2(1.5f, .5f),
            0,
            0);
        var planted = Send(
            restored,
            reconnectConnection,
            joined.Identity.PlayerId,
            reconnected.NextCommandSequence,
            intent);

        CheckAssert.True(planted.Accepted,
            "a valid clear surface crop tile must accept planting");
        CheckAssert.Equal(81, planted.Gameplay.AdventureExperience,
            "planting XP must feed canonical Adventure XP");
        CheckAssert.Equal(102, planted.Gameplay.MaximumHealth,
            "action XP must reconcile a level crossing without quest progress");
        CheckAssert.Equal(102, planted.Gameplay.Health,
            "action XP maximum-health gain must heal by the exact delta");
        CheckAssert.Equal(0,
            Objective(planted.Gameplay, 0, "large-rocks"),
            "an irrelevant accepted action must not mutate quest objectives");
        CheckAssert.Equal(planted.ActorRevision,
            planted.WorldTransaction!.ActorRevision,
            "the crop receipt must be rebased to reconciled actor state");
    }

    private static void NewlyUnlockedQuestReconcilesHeldInventoryOnce()
    {
        var session = NewSession();
        var materials = new List<AuthoritativeWorldObjectSnapshot>();
        Add(ItemIds.LargeRock, 5);
        Add(ItemIds.Sticks, 2);
        Add(ItemIds.PlantFibres, 2);

        var connection = ClientConnectionId.New();
        var joined = Join(
            session,
            connection,
            "Prepared",
            Vector2.Zero,
            [new InitialInventoryItem(ItemIds.MediumRock, 8)]);
        var sequence = 1L;
        IntentResult completed = default;
        PickUpWorldObjectIntent completingIntent = null!;
        foreach (var material in materials)
        {
            var gameplay = Actor(session, joined.Identity.PlayerId).Gameplay;
            completingIntent = PickupIntent(session, gameplay, material);
            completed = Send(
                session,
                connection,
                joined.Identity.PlayerId,
                sequence++,
                completingIntent);
            CheckAssert.True(completed.Accepted,
                "the fixture material pickup must commit");
        }

        CheckAssert.Equal(QuestStatus.Complete,
            completed.Gameplay.Quests[0].Status,
            "the final authoritative pickup must complete the first quest");
        CheckAssert.Equal(QuestStatus.InProgress,
            completed.Gameplay.Quests[1].Status,
            "completion must unlock the crafting quest");
        CheckAssert.Equal(8,
            Objective(completed.Gameplay, 1, "medium-rocks"),
            "the unlocked quest must credit qualifying items already held");
        CheckAssert.Equal(50, completed.Gameplay.AdventureExperience,
            "inventory reconciliation must not award an incomplete quest");
        var committedRevision = completed.ActorRevision;

        var replay = Send(
            session,
            connection,
            joined.Identity.PlayerId,
            sequence,
            completingIntent);
        CheckAssert.True(replay.Accepted && replay.Duplicate,
            "the unlock command must replay from its session receipt");
        CheckAssert.Equal(8,
            Objective(replay.Gameplay, 1, "medium-rocks"),
            "replay must not credit held inventory twice");
        CheckAssert.Equal(50, replay.Gameplay.AdventureExperience,
            "replay must not duplicate a quest reward");
        CheckAssert.Equal(committedRevision, replay.ActorRevision,
            "replay must not advance the reconciled actor revision");

        void Add(string itemId, int quantity)
        {
            for (var index = 0; index < quantity; index++)
                materials.Add(session.SeedWorldObject(new(
                    Guid.NewGuid(), itemId, new Vector2(1, 0))));
        }
    }

    private static void CommittedEventSourcesAreExact()
    {
        var gameplay = EmptyGameplay();
        var chunk = new WorldChunkKey(0, 0, 0);
        var handle = new WorldObjectHandle(Guid.NewGuid(), chunk, 1, 1);
        var target = new AuthoritativeWorldObjectSnapshot(
            handle.ObjectId,
            ItemIds.LargeRock,
            Vector2.Zero,
            chunk,
            1,
            1,
            0,
            0,
            0,
            null,
            null,
            false,
            null,
            0);
        var accepted = new WorldTransactionResult(
            Guid.NewGuid(),
            WorldTransactionStatus.Accepted,
            gameplay.ActorRevision,
            gameplay.Inventory.Revision,
            [],
            [],
            gameplay,
            null);

        AssertSingle(
            AuthoritativeWorldSession.CommittedWorldQuestEvents(
                new PickUpWorldObjectIntent(
                    Guid.NewGuid(), 1, 1, handle),
                target,
                accepted,
                gameplay,
                gameplay),
            QuestEventType.GatherItem,
            ItemIds.LargeRock,
            1,
            "pickup");
        AssertSingle(
            AuthoritativeWorldSession.CommittedWorldQuestEvents(
                new LightCampfireIntent(
                    Guid.NewGuid(), 1, 1, handle),
                target,
                accepted,
                gameplay,
                gameplay),
            QuestEventType.LightCampfire,
            null,
            1,
            "campfire lighting");
        AssertSingle(
            AuthoritativeWorldSession.CommittedWorldQuestEvents(
                new PlaceConstructionIntent(
                    Guid.NewGuid(), 1, 1,
                    ItemIds.Workbench, Vector2.Zero, 0, 0, 0),
                null,
                accepted,
                gameplay,
                gameplay),
            QuestEventType.BuildObject,
            ItemIds.Workbench,
            1,
            "construction placement");
        AssertSingle(
            AuthoritativeWorldSession.CommittedWorldQuestEvents(
                new TraverseCaveIntent(
                    Guid.NewGuid(), 1, 1, handle),
                target,
                accepted with
                {
                    ActorTransition = new(
                        Vector2.Zero,
                        CaveExcavationRules.UndergroundWorldLevel)
                },
                gameplay,
                gameplay),
            QuestEventType.EnterCave,
            null,
            1,
            "cave descent");

        var cropTarget = target with
        {
            DefinitionId = ItemIds.WildGrainCrop,
            FuelItemId = ItemIds.WildGrain
        };
        var harvested = WithItem(gameplay, ItemIds.WildGrain, 3);
        AssertSingle(
            AuthoritativeWorldSession.CommittedWorldQuestEvents(
                new HarvestCropIntent(
                    Guid.NewGuid(), 1, 1, handle),
                cropTarget,
                accepted,
                gameplay,
                harvested),
            QuestEventType.GatherItem,
            ItemIds.WildGrain,
            3,
            "crop harvest");

        var reference = new ResourceNodeReference(
            new ResourceNodeId(Guid.NewGuid()), chunk, 0, 0);
        var gathered = ResourceResult(
            gameplay,
            [new ResourceItemReward(ItemIds.PlantFibres, 2)]);
        AssertSingle(
            AuthoritativeWorldSession.CommittedResourceQuestEvents(
                new GatherFibreIntent(
                    Guid.NewGuid(), 1, 1, reference),
                gathered),
            QuestEventType.GatherItem,
            ItemIds.PlantFibres,
            2,
            "resource gathering");
        AssertSingle(
            AuthoritativeWorldSession.CommittedResourceQuestEvents(
                new MineResourceIntent(
                    Guid.NewGuid(), 1, 1, reference, 0),
                ResourceResult(
                    gameplay,
                    [new ResourceItemReward(ItemIds.TinOre, 1)])),
            QuestEventType.MineOre,
            ItemIds.TinOre,
            1,
            "mining completion reward");
        AssertSingle(
            AuthoritativeWorldSession.CommittedResourceQuestEvents(
                new CatchFishIntent(
                    Guid.NewGuid(), 1, 1, reference, 0),
                ResourceResult(
                    gameplay,
                    [new ResourceItemReward(ItemIds.RawMinnows, 1)]) with
                {
                    FishingOutcome = new(FishSpecies.ShoreMinnows, true, .5f)
                }),
            QuestEventType.CatchFish,
            ItemIds.RawMinnows,
            1,
            "successful fishing catch");
        CheckAssert.Equal(0,
            AuthoritativeWorldSession.CommittedResourceQuestEvents(
                new CatchFishIntent(
                    Guid.NewGuid(), 1, 1, reference, 0),
                ResourceResult(gameplay, []) with
                {
                    FishingOutcome = new(FishSpecies.ShoreMinnows, false, .5f)
                }).Length,
            "a fishing miss must not emit quest progress");

        AssertSingle(
            AuthoritativeWorldSession.CommittedCookingQuestEvents(
                interrupted: false,
                burnt: false,
                ItemIds.CookedMinnows),
            QuestEventType.CookFood,
            ItemIds.CookedMinnows,
            1,
            "successful cooking");
        CheckAssert.Equal(0,
            AuthoritativeWorldSession.CommittedCookingQuestEvents(
                interrupted: false,
                burnt: true,
                ItemIds.BurntMinnows).Length,
            "burnt cooking must not progress the quest");
        CheckAssert.Equal(0,
            AuthoritativeWorldSession.CommittedCookingQuestEvents(
                interrupted: true,
                burnt: false,
                ItemIds.CookedMinnows).Length,
            "interrupted cooking must not progress the quest");
    }

    private static ResourceTransactionResult ResourceResult(
        PlayerGameplaySnapshot gameplay,
        ImmutableArray<ResourceItemReward> rewards) => new(
        Guid.NewGuid(),
        ResourceTransactionStatus.Accepted,
        gameplay.ActorRevision,
        gameplay.Inventory.Revision,
        gameplay,
        null,
        null,
        rewards);

    private static void AssertSingle(
        ImmutableArray<QuestEvent> events,
        QuestEventType type,
        string? targetId,
        int amount,
        string source)
    {
        CheckAssert.Equal(1, events.Length,
            $"{source} must emit exactly one committed quest fact");
        CheckAssert.Equal(type, events[0].Type,
            $"{source} must emit the correct quest event type");
        CheckAssert.Equal(targetId, events[0].TargetId,
            $"{source} must emit the exact authoritative target");
        CheckAssert.Equal(amount, events[0].Amount,
            $"{source} must emit the exact authoritative amount");
    }

    private static PlayerGameplaySnapshot EmptyGameplay() => new(
        1,
        100,
        100,
        0,
        0,
        0,
        new PlayerInventorySnapshot(
            1,
            Enumerable.Range(0, PlayerInventory.Capacity)
                .Select(static slot => new InventorySlotSnapshot(
                    slot, null, 0))
                .ToImmutableArray()),
        Quests: QuestService.Normalize(null));

    private static PlayerGameplaySnapshot WithItem(
        PlayerGameplaySnapshot gameplay,
        string itemId,
        int quantity) => gameplay with
    {
        Inventory = gameplay.Inventory with
        {
            Slots = gameplay.Inventory.Slots.SetItem(
                0, new(0, itemId, quantity))
        }
    };

    private static int Objective(
        PlayerGameplaySnapshot gameplay,
        int questIndex,
        string objectiveId) => gameplay.Quests[questIndex]
        .ObjectiveCounts?.GetValueOrDefault(objectiveId) ?? 0;

    private static PickUpWorldObjectIntent PickupIntent(
        AuthoritativeWorldSession session,
        PlayerGameplaySnapshot gameplay,
        AuthoritativeWorldObjectSnapshot value) => new(
        Guid.NewGuid(),
        gameplay.Inventory.Revision,
        gameplay.ActorRevision,
        new WorldObjectHandle(
            value.ObjectId,
            value.Chunk,
            value.ObjectRevision,
            session.CaptureWorldChunkRevision(value.Chunk),
            value.ContainerRevision));

    private static AuthoritativeWorldSession NewSession() => new(
        identitySource: new DeterministicIdentitySource(),
        sessionId: new SessionId(Guid.Parse(
            "d1000000-0000-0000-0000-000000000001")));

    private static JoinResult Join(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        string name,
        Vector2 position,
        IReadOnlyList<InitialInventoryItem>? inventory = null)
    {
        var pending = session.EnqueueJoinAsync(new(
            connection, name, position, inventory));
        session.Drain();
        var joined = pending.GetAwaiter().GetResult();
        CheckAssert.True(joined.Accepted, "the quest test actor must join");
        return joined;
    }

    private static IntentResult Send(
        AuthoritativeWorldSession session,
        ClientConnectionId connection,
        PlayerId playerId,
        long sequence,
        SessionIntent intent)
    {
        var pending = session.EnqueueIntentAsync(new(
            connection, playerId, sequence, intent));
        session.Drain();
        return pending.GetAwaiter().GetResult();
    }

    private static ActorSnapshot Actor(
        AuthoritativeWorldSession session,
        PlayerId playerId) => session.CaptureSnapshot().Actors.Single(value =>
        value.PlayerId == playerId);

    private sealed class DeterministicIdentitySource : ISessionIdentitySource
    {
        public PlayerIdentity CreatePlayerIdentity() => new(
            new PlayerId(Guid.Parse(
                "d2000000-0000-0000-0000-000000000001")),
            new ActorId(Guid.Parse(
                "d3000000-0000-0000-0000-000000000001")));

        public ReconnectToken CreateReconnectToken() =>
            new("quest-session-authority-secret");
    }
}
