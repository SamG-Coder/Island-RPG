using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal enum VillagerNeed : byte
{
    Safe,
    Food,
    Social,
    Explore,
    Idle
}

internal enum VillagerSimulationTier : byte
{
    Nearby,
    Regional,
    Distant
}

internal enum VillagerActivity : byte
{
    Idle,
    Conversing,
    Reflecting,
    Following,
    Socializing,
    SeekingResource,
    Exploring,
    Resting,
    Blocked
}

internal enum VillagerConflictIntent : byte
{
    None,
    Warn,
    Flee,
    Surrender,
    Defend,
    CallForHelp,
    Retaliate,
    Forgive,
    Deescalate
}

internal sealed record VillagerMemory(
    Guid EventId,
    string Kind,
    string SubjectId,
    Guid? ItemInstanceId,
    float Confidence,
    double GameSeconds,
    int Sentiment = 0,
    string? Summary = null,
    string? ItemId = null,
    float? ObservedValue = null);

internal sealed record VillagerFailedTarget(
    Guid TargetId,
    double RetryAfterGameSeconds);

internal sealed record VillagerRelationship(
    string CharacterId,
    RelationshipState State,
    int OwnershipOffences = 0);

internal enum AcquaintanceStage : byte
{
    Unknown,
    Seen,
    Introduced,
    ExchangedOrigins,
    Cooperative,
    DiscussedSkills
}

internal sealed record VillagerKnownPerson(
    string CharacterId,
    AcquaintanceStage Stage,
    string? StatedName,
    double LastConversationGameSeconds,
    int ConversationCount = 0);

internal sealed record VillagerPersona(
    string BackgroundStory,
    string Personality,
    string PriorTrade,
    IReadOnlyList<string> KnownToolIds,
    string ArrivalMemory,
    string SocialDrive);

internal sealed record VillagerConversationTurn(
    string SpeakerId,
    string SpeakerName,
    string Text,
    double GameSeconds);

internal sealed record VillagerDeliberationTrace(
    string PrivateThought,
    string Decision,
    string Action,
    int Willingness,
    int EstimatedCost,
    int Risk,
    int Priority,
    double GameSeconds,
    string ItemId = "");

internal sealed record VillagerState(
    string Id,
    string Name,
    EntityGender Gender,
    int SkinTone,
    int TeamColor,
    float PositionX,
    float PositionY,
    int WorldLevel,
    int Health,
    float Hunger,
    string?[] Inventory,
    IReadOnlyList<VillagerRelationship>? Relationships = null,
    IReadOnlyList<VillagerMemory>? Memories = null,
    VillagerNeed Need = VillagerNeed.Idle,
    double NextDecisionGameSeconds = 0,
    double LastSimulatedGameSeconds = 0,
    float Sociability = .5f,
    float Honesty = .5f,
    float Boldness = .5f,
    Guid? GoalObjectId = null,
    EntityAction Action = EntityAction.Idle,
    float FacingX = 1,
    float FacingY = 1,
    float? TargetX = null,
    float? TargetY = null,
    IReadOnlyList<VillagerLongTermGoal>? Goals = null,
    IReadOnlyList<VillagerPromise>? Promises = null,
    double ActionTime = 0,
    IReadOnlyList<VillagerKnownPerson>? KnownPeople = null,
    double NextSocialGameSeconds = 0,
    string? FollowingActorId = null,
    VillagerPersona? Persona = null,
    double AwakenedGameSeconds = 8 * 60 * 60,
    VillagerActivity Activity = VillagerActivity.Idle,
    double ActivityUntilGameSeconds = 0,
    string? ConversationPartnerId = null,
    int BlockedMoveAttempts = 0,
    IReadOnlyList<VillagerConversationTurn>? ConversationHistory = null,
    VillagerDeliberationTrace? LastDeliberation = null,
    int SurvivalTimeScaleVersion = 0,
    float WellFedSeconds = 0,
    int WoodcuttingExperience = 0,
    int FarmingExperience = 0,
    int CraftingExperience = 0,
    int FishingExperience = 0,
    int CookingExperience = 0,
    int FiremakingExperience = 0,
    int DiggingExperience = 0,
    int MiningExperience = 0,
    int AttackExperience = 0,
    int StrengthExperience = 0,
    int DefenceExperience = 0,
    VillagerWorkRole WorkRole = VillagerWorkRole.Unassigned,
    string? ConflictTargetId = null,
    VillagerConflictIntent ConflictIntent = VillagerConflictIntent.None,
    string? ConflictMotive = null,
    double ConflictExpiresGameSeconds = 0,
    float StarvationDamageRemainder = 0,
    IReadOnlyList<VillagerFailedTarget>? FailedTargets = null,
    IReadOnlyList<Guid>? ObservedOwnershipIncidentIds = null,
    float Energy = VillagerFatigueService.MaximumEnergy,
    double? LastEnergyGameSeconds = null,
    IReadOnlyList<VillagerLocationMemory>? LocationMemories = null);

internal readonly record struct VillagerDecision(
    VillagerNeed Need,
    Vector2? MoveTarget,
    int ConsumeSlot = -1,
    string? Speech = null);

internal enum VillagerWorldActionKind : byte
{
    None,
    ApproachItem,
    TakeItem,
    ApproachStorage,
    DepositItems
}

internal readonly record struct VillagerWorldObject(
    Guid Id,
    string ItemId,
    Vector2 Position,
    string? OwnerId,
    bool IsStorage);

internal readonly record struct VillagerWorldAction(
    VillagerWorldActionKind Kind,
    Guid? ObjectId = null,
    Vector2? Target = null);

internal enum VillagerSocialIntent : byte
{
    None,
    Introduce,
    AskOrigin,
    AskSurvival,
    AskTools,
    SeekCompany,
    RequestFood,
    OfferFood
}

internal readonly record struct SocialActorObservation(
    string Id,
    string Name,
    Vector2 Position,
    int WorldLevel,
    float Hunger,
    int FoodCount,
    IReadOnlyList<string>? VisibleToolIds = null);

internal readonly record struct VillagerSocialGoal(
    VillagerSocialIntent Intent,
    string? OtherActorId = null,
    Vector2? Target = null,
    string? Speech = null);

internal static class VillagerSimulation
{
    public const int InitialPopulation = 3;
    public const float NearbyRadius = 28;
    public const float RegionalRadius = 128;
    public const double NearbyDecisionSeconds =
        8 * GameSecondsPerRealSecond;
    public const double RegionalDecisionSeconds =
        30 * GameSecondsPerRealSecond;
    public const double DistantDecisionSeconds =
        120 * GameSecondsPerRealSecond;
    public const int MaximumMemories = 64;
    public const int MaximumConversationTurns = 12;
    public const int MaximumObservedOwnershipIncidents = 256;
    public const float InteractionRange = 1.5f;
    public const float ResourceSearchRadius = 24;
    public const int StorageDepositThreshold = 8;
    public const float FootBoxWidth = .46f;
    public const float FootBoxDepth = .34f;
    public const double GatherPauseSeconds =
        45 * GameSecondsPerRealSecond;
    public const double SocialCooldownRealSeconds = 120;
    public const double IntroductionCooldownRealSeconds = 45;
    public const double RelationshipCheckInSeconds = 6 * 60 * 60;
    public const float SocialRange = 8;
    public const float FollowStopDistance = 1.8f;
    public const float FollowResumeDistance = 2.4f;
    public const float FollowRetargetDistance = .6f;
    public const double GameSecondsPerRealSecond = 60;
    public const double MinimumReflectionRealSeconds = .35;
    public const double MaximumReflectionRealSeconds = 1.2;

    private static readonly string[] Names = ["Mira", "Tomas", "Rowan"];

    public static IReadOnlyList<string> NamesForPopulation(
        int population) =>
        Names.Take(Math.Clamp(
            population, 0, InitialPopulation)).ToArray();

    public static VillagerState[] CreateInitial(
        long worldSeed,
        Vector2 origin,
        Func<Vector2, bool>? canStand = null,
        double gameSeconds = 0,
        int population = InitialPopulation,
        IReadOnlyList<VillagerPersona>? personas = null,
        IReadOnlyList<NewWorldSurvivorSetup>? setups = null)
    {
        population = Math.Clamp(population, 0, InitialPopulation);
        var result = new VillagerState[population];
        for (var index = 0; index < result.Length; index++)
        {
            var position = FindSpawn(
                worldSeed, index, origin, canStand);
            var id = StableId(worldSeed, index);
            var setup = setups is not null && index < setups.Count
                ? setups[index]
                : null;
            var inventory = PlayerInventory.CreateStartingInventory();
            if (setup is not null)
                for (var slot = 0; slot < setup.StartingItems.Count &&
                                   slot < inventory.Length; slot++)
                    inventory[slot] = setup.StartingItems[slot];
            result[index] = new(
                id,
                setup?.Name ?? Names[index],
                index == 0 ? EntityGender.Female : EntityGender.Male,
                PositiveMod(Hash(worldSeed, index, 17), 5),
                index + 1,
                position.X,
                position.Y,
                WorldLevel: 0,
                Health: AdventureService.BaseMaximumHealth,
                Hunger: SurvivalService.MaximumHunger,
                Inventory: inventory,
                NextDecisionGameSeconds: ScheduleNextDecision(
                    id, gameSeconds, NearbyDecisionSeconds),
                LastSimulatedGameSeconds: gameSeconds,
                Sociability: Unit(Hash(worldSeed, index, 31)),
                Honesty: Unit(Hash(worldSeed, index, 47)),
                Boldness: Unit(Hash(worldSeed, index, 61)),
                Persona: setup?.Persona ?? (personas is not null &&
                         index < personas.Count
                    ? personas[index]
                    : DefaultPersona(index)),
                AwakenedGameSeconds: gameSeconds,
                Goals: VillagerCommitmentService.InitialGoals(
                    id, gameSeconds),
                SurvivalTimeScaleVersion: 1,
                LastEnergyGameSeconds: gameSeconds);
        }
        return result;
    }

    public static VillagerPersona DefaultPersona(int index) =>
        (index % InitialPopulation) switch
        {
            0 => new(
                "A practical village carpenter who remembers repairing homes before the wreck.",
                "Observant, guarded, and quietly helpful.",
                "Carpenter",
                [ItemIds.StoneAxe, ItemIds.StoneHammer],
                "Woke on the beach with salt in her eyes and no memory of the wreck itself.",
                "Wants to learn who can be trusted and build a safe camp together."),
            1 => new(
                "A travelling fisher accustomed to storms, ropes, and feeding small crews.",
                "Patient, sociable, and cautious around promises.",
                "Fisher",
                [ItemIds.StoneKnife],
                "Remembers rough water, then waking alone near the tide line.",
                "Wants companions, shared meals, and reliable information."),
            _ => new(
                "A quarry labourer who learned to identify useful stone and maintain hand tools.",
                "Direct, resilient, and slow to trust.",
                "Quarry worker",
                [ItemIds.StonePickaxe, ItemIds.StoneHammer],
                "Remembers a loud impact before waking among scattered supplies.",
                "Wants to understand the island and find dependable partners.")
        };

    public static double HoursOnIsland(
        VillagerState state,
        double gameSeconds) =>
        Math.Max(
            0,
            gameSeconds - state.AwakenedGameSeconds) /
        (60 * 60);

    public static VillagerSimulationTier Tier(
        in Vector2 villager,
        in Vector2 player)
    {
        var distanceSquared = Vector2.DistanceSquared(villager, player);
        if (distanceSquared <= NearbyRadius * NearbyRadius)
            return VillagerSimulationTier.Nearby;
        return distanceSquared <= RegionalRadius * RegionalRadius
            ? VillagerSimulationTier.Regional
            : VillagerSimulationTier.Distant;
    }

    public static double DecisionInterval(VillagerSimulationTier tier) =>
        tier switch
        {
            VillagerSimulationTier.Nearby => NearbyDecisionSeconds,
            VillagerSimulationTier.Regional => RegionalDecisionSeconds,
            _ => DistantDecisionSeconds
        };

    public static VillagerState CatchUp(
        VillagerState state,
        double gameSeconds,
        float hungerLossMultiplier = 1)
    {
        if (state.SurvivalTimeScaleVersion < 1)
        {
            var violenceCausedDefeat =
                state.Health <= 0 &&
                state.Memories?.Any(memory =>
                    memory.Kind == "violence") == true;
            state = state with
            {
                Health = state.Health <= 0 &&
                         !violenceCausedDefeat
                    ? AdventureService.BaseMaximumHealth
                    : state.Health,
                Hunger = !violenceCausedDefeat
                    ? Math.Max(25, state.Hunger)
                    : state.Hunger,
                SurvivalTimeScaleVersion = 1
            };
        }
        state = VillagerFatigueService.Advance(state, gameSeconds);
        var elapsed = Math.Max(
            0, gameSeconds - state.LastSimulatedGameSeconds);
        if (elapsed <= 0) return state;
        const double maximumChunkGameSeconds = 24 * 60 * 60;
        var simulated = state;
        var remaining = elapsed;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, maximumChunkGameSeconds);
            var survival = SurvivalService.Advance(
                simulated.Hunger,
                simulated.WellFedSeconds,
                simulated.Health,
                (float)(chunk / GameSecondsPerRealSecond),
                hungerLossMultiplier,
                simulated.StarvationDamageRemainder);
            simulated = simulated with
            {
                Hunger = survival.Hunger,
                Health = survival.Health,
                WellFedSeconds = survival.WellFedSeconds,
                StarvationDamageRemainder =
                    survival.StarvationDamageRemainder,
                LastSimulatedGameSeconds =
                    simulated.LastSimulatedGameSeconds + chunk
            };
            remaining -= chunk;
        }
        return simulated;
    }

    public static VillagerDecision Decide(
        VillagerState state,
        Vector2 playerPosition,
        double gameSeconds)
    {
        if (state.Health <= 0) return default;
        if (state.Health <= 20)
            return new(VillagerNeed.Safe, AwayFrom(
                new(state.PositionX, state.PositionY),
                playerPosition, 4));
        if (state.Hunger <= 35)
        {
            var inventory = state.Inventory;
            for (var slot = 0; slot < inventory.Length; slot++)
                if (inventory[slot] is { } item &&
                    SurvivalService.TryFoodEffect(item, out _))
                    return new(VillagerNeed.Food, null, slot);
            return new(VillagerNeed.Food, null);
        }
        if (VillagerFatigueService.ShouldRest(state))
            return new(VillagerNeed.Idle, null);
        var socialRoll = Unit(Hash(
            StableHash(state.Id),
            (int)Math.Floor(gameSeconds / 10),
            83));
        if (Vector2.DistanceSquared(
                new(state.PositionX, state.PositionY),
                playerPosition) < 8 * 8 &&
            socialRoll < state.Sociability * .03f)
            return new(
                VillagerNeed.Social,
                null);
        var activityRoll = Unit(Hash(
            StableHash(state.Id),
            (int)Math.Floor(gameSeconds / 12),
            91));
        if (activityRoll > .25f)
            return new(VillagerNeed.Idle, null);
        var angle = Unit(Hash(
            StableHash(state.Id),
            (int)Math.Floor(gameSeconds / 5),
            97)) * MathF.Tau;
        return new(
            VillagerNeed.Explore,
            new Vector2(state.PositionX, state.PositionY) +
            new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 2);
    }

    public static VillagerWorldAction SelectWorldAction(
        VillagerState state,
        ReadOnlySpan<VillagerWorldObject> objects,
        double gameSeconds = 0)
    {
        if (state.Health <= 0) return default;
        if (VillagerFatigueService.ShouldRest(state)) return default;
        var position = new Vector2(
            state.PositionX, state.PositionY);
        var carried = PlayerInventory.Count(state.Inventory);
        var bestScore = float.MaxValue;
        VillagerWorldObject best = default;
        var found = false;

        if (carried >= StorageDepositThreshold)
        {
            for (var index = 0; index < objects.Length; index++)
            {
                ref readonly var candidate = ref objects[index];
                if (IsFailedTarget(state, candidate.Id, gameSeconds))
                    continue;
                if (!candidate.IsStorage ||
                    !string.Equals(
                        candidate.OwnerId, state.Id,
                        StringComparison.Ordinal))
                    continue;
                var distanceSquared = Vector2.DistanceSquared(
                    position, candidate.Position);
                if (!VillagerLocationMemoryService.CanVisit(
                        state, candidate.Position, gameSeconds))
                    continue;
                if (distanceSquared >= bestScore) continue;
                bestScore = distanceSquared;
                best = candidate;
                found = true;
            }
            if (found)
                return bestScore <=
                       InteractionRange * InteractionRange
                    ? new(
                        VillagerWorldActionKind.DepositItems,
                        best.Id,
                        best.Position)
                    : new(
                        VillagerWorldActionKind.ApproachStorage,
                        best.Id,
                        StepToward(position, best.Position));
        }
        if (PlayerInventory.IsFull(state.Inventory))
        {
            var storage = VillagerLocationMemoryService.SelectUsefulLocation(
                state, gameSeconds, storageOnly: true);
            return storage is null
                ? default
                : new(
                    VillagerWorldActionKind.ApproachStorage,
                    Target: StepToward(
                        position,
                        new(storage.PositionX, storage.PositionY)));
        }

        bestScore = ResourceSearchRadius * ResourceSearchRadius;
        found = false;
        for (var index = 0; index < objects.Length; index++)
        {
            ref readonly var candidate = ref objects[index];
            if (IsFailedTarget(state, candidate.Id, gameSeconds))
                continue;
            if (candidate.IsStorage ||
                candidate.OwnerId is { Length: > 0 } owner &&
                !string.Equals(
                    owner, state.Id,
                    StringComparison.Ordinal) ||
                !ItemCatalog.TryGet(candidate.ItemId, out var item) ||
                item.HasTag(ItemTag.PlaceableObject) ||
                !ShouldGather(state, item))
                continue;
            var distanceSquared = Vector2.DistanceSquared(
                position, candidate.Position);
            if (!VillagerLocationMemoryService.CanVisit(
                    state, candidate.Position, gameSeconds))
                continue;
            if (distanceSquared >
                ResourceSearchRadius * ResourceSearchRadius)
                continue;
            var priority = VillagerResourcePriority.Score(
                state, candidate.ItemId);
            var score = distanceSquared -
                        priority * 8 -
                        (candidate.Id == state.GoalObjectId
                            ? 32
                            : 0);
            if (found && score >= bestScore) continue;
            bestScore = score;
            best = candidate;
            found = true;
        }
        if (!found)
        {
            var remembered =
                VillagerLocationMemoryService.SelectUsefulLocation(
                    state, gameSeconds);
            if (remembered is null) return default;
            return new(
                remembered.Type == VillagerLocationType.Storage
                    ? VillagerWorldActionKind.ApproachStorage
                    : VillagerWorldActionKind.ApproachItem,
                Target: StepToward(
                    position,
                    new(remembered.PositionX, remembered.PositionY)));
        }
        var inRange = Vector2.DistanceSquared(
            position, best.Position) <=
            InteractionRange * InteractionRange;
        return inRange
            ? new(
                VillagerWorldActionKind.TakeItem,
                best.Id,
                best.Position)
             : new(
                VillagerWorldActionKind.ApproachItem,
                best.Id,
                 StepToward(position, best.Position));
    }

    private static bool IsFailedTarget(
        VillagerState state,
        Guid targetId,
        double gameSeconds) =>
        state.FailedTargets?.Any(failed =>
            failed.TargetId == targetId &&
            failed.RetryAfterGameSeconds > gameSeconds) == true;

    public static VillagerSocialGoal SelectSocialGoal(
        VillagerState state,
        ReadOnlySpan<SocialActorObservation> actors,
        double gameSeconds = 0)
    {
        if (state.Health <= 0) return default;
        var projectedFoodNeed =
            state.Hunger <= 35 ||
            VillagerNeedPatternMemory.NeedsFoodSoon(
                state, state.Id, gameSeconds);
        if (gameSeconds < state.NextSocialGameSeconds &&
            !projectedFoodNeed)
            return default;
        var position = new Vector2(
            state.PositionX, state.PositionY);
        var ownFood = CountFood(state.Inventory);
        var bestDistance = SocialRange * SocialRange;
        var bestScore = float.MaxValue;
        SocialActorObservation best = default;
        var found = false;
        for (var index = 0; index < actors.Length; index++)
        {
            ref readonly var actor = ref actors[index];
            if (actor.Id == state.Id ||
                actor.WorldLevel != state.WorldLevel)
                continue;
            var distance = Vector2.DistanceSquared(
                position, actor.Position);
            if (distance > SocialRange * SocialRange) continue;
            var known = KnownPerson(state, actor.Id);
            var needsIntroduction =
                known?.Stage is null or
                    AcquaintanceStage.Unknown or
                    AcquaintanceStage.Seen;
            var needsOrigin =
                known?.Stage == AcquaintanceStage.Introduced;
            var needsSurvival =
                known?.Stage == AcquaintanceStage.ExchangedOrigins;
            var needsTools =
                known?.Stage == AcquaintanceStage.Cooperative;
            var hasKnownTool =
                actor.VisibleToolIds is { Count: > 0 } ||
                VillagerCapabilityMemory.KnownTools(
                    state, actor.Id).Count > 0;
            var relationshipCheckIn =
                state.Need == VillagerNeed.Social &&
                (known is null ||
                 known.Stage < AcquaintanceStage.DiscussedSkills ||
                 gameSeconds -
                 known.LastConversationGameSeconds >=
                 RelationshipCheckInSeconds);
            var actorNeedsFoodSoon =
                actor.Hunger <= 35 ||
                VillagerNeedPatternMemory.NeedsFoodSoon(
                    state, actor.Id, gameSeconds);
            var canHelpHungryVillager =
                projectedFoodNeed && ownFood == 0 &&
                actor.FoodCount > 1;
            var needsOurSurplus =
                ownFood > 1 && actorNeedsFoodSoon &&
                actor.FoodCount == 0;
            if (!canHelpHungryVillager &&
                !needsOurSurplus &&
                !needsIntroduction &&
                !needsOrigin &&
                !needsSurvival &&
                !needsTools &&
                !relationshipCheckIn)
                continue;
            var score = distance -
                (canHelpHungryVillager ||
                 needsOurSurplus
                    ? 1024
                    : 0) -
                (hasKnownTool ? 16 : 0);
            if (score >= bestScore) continue;
            bestScore = score;
            bestDistance = distance;
            best = actor;
            found = true;
        }
        if (!found) return default;
        var inConversationRange =
            bestDistance <= InteractionRange * InteractionRange;
        var ownNeedsFoodSoon =
            projectedFoodNeed;
        var bestNeedsFoodSoon =
            best.Hunger <= 35 ||
            VillagerNeedPatternMemory.NeedsFoodSoon(
                state, best.Id, gameSeconds);
        if (ownNeedsFoodSoon &&
            ownFood == 0 &&
            best.FoodCount > 1)
            return new(
                VillagerSocialIntent.RequestFood,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? state.Hunger <= 35
                        ? $"{best.Name}, could you spare some food?"
                        : $"{best.Name}, I'm running low. Could we plan to share some food?"
                    : null);
        if (!ownNeedsFoodSoon &&
            ownFood > 1 &&
            bestNeedsFoodSoon &&
            best.FoodCount == 0)
            return new(
                VillagerSocialIntent.OfferFood,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? best.Hunger <= 35
                        ? $"{best.Name}, take this. You need it."
                        : $"{best.Name}, you'll need food soon. Take this before it becomes urgent."
                    : null);
        var acquaintance = KnownPerson(state, best.Id);
        if (acquaintance?.Stage is null or
                AcquaintanceStage.Unknown or
                AcquaintanceStage.Seen)
            return new(
                VillagerSocialIntent.Introduce,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? $"I'm {state.Name}. What's your name?"
                    : null);
        if (acquaintance.Stage == AcquaintanceStage.Introduced)
            return new(
                VillagerSocialIntent.AskOrigin,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? "Do you remember how we ended up here?"
                    : null);
        if (acquaintance.Stage ==
            AcquaintanceStage.ExchangedOrigins)
            return new(
                VillagerSocialIntent.AskSurvival,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? "What have you learned about surviving here?"
                    : null);
        if (acquaintance.Stage ==
            AcquaintanceStage.Cooperative)
        {
            var knownToolId = best.VisibleToolIds?.FirstOrDefault() ??
                VillagerCapabilityMemory.KnownTools(
                    state, best.Id).FirstOrDefault();
            var toolQuestion = knownToolId is not null &&
                               ItemCatalog.TryGet(
                                   knownToolId, out var knownTool)
                ? $"I noticed your {knownTool.Name}. Could you show me how you use it?"
                : state.Persona is { } persona
                    ? $"I used to work as a {persona.PriorTrade}. What tools do you know?"
                    : "What sort of work and tools do you know?";
            return new(
                VillagerSocialIntent.AskTools,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? toolQuestion
                    : null);
        }
        return new(
            VillagerSocialIntent.SeekCompany,
            best.Id,
            inConversationRange
                ? null
                : StepToward(position, best.Position),
            inConversationRange
                ? $"How are you holding up, {best.Name}?"
                : null);
    }

    public static VillagerKnownPerson? KnownPerson(
        VillagerState state,
        string characterId) =>
        state.KnownPeople?.FirstOrDefault(value =>
            string.Equals(
                value.CharacterId,
                characterId,
                StringComparison.Ordinal));

    public static string PerceivedName(
        VillagerState observer,
        string characterId,
        string unknownName = "the stranger")
    {
        var known = KnownPerson(observer, characterId);
        return known?.Stage >= AcquaintanceStage.Introduced &&
               !string.IsNullOrWhiteSpace(known.StatedName)
            ? known.StatedName!
            : unknownName;
    }

    public static bool TryExtractIntroducedName(
        string speech,
        out string statedName)
    {
        statedName = "";
        if (string.IsNullOrWhiteSpace(speech))
            return false;
        var markers = new[]
        {
            "my name is ",
            "call me ",
            "i'm ",
            "i am "
        };
        foreach (var marker in markers)
        {
            var markerIndex = speech.IndexOf(
                marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var claim = speech[(markerIndex + marker.Length)..]
                .TrimStart();
            var end = claim.IndexOfAny(['.', ',', '!', '?', ';', ':', '\r', '\n']);
            if (end >= 0) claim = claim[..end];
            claim = claim.Trim().Trim('\'', '"');
            if (claim.Length is < 1 or > 32 ||
                claim.Split(
                    ' ', StringSplitOptions.RemoveEmptyEntries).Length > 4 ||
                claim.Any(character =>
                    !char.IsLetter(character) &&
                    !char.IsWhiteSpace(character) &&
                    character is not ('\'' or '-')))
                continue;
            if (marker is "i'm " or "i am " &&
                !char.IsUpper(claim[0]))
                continue;
            statedName = claim;
            return true;
        }
        return false;
    }

    public static VillagerState RecordIntroductionResponse(
        VillagerState state,
        string characterId,
        string? statedName,
        double gameSeconds) =>
        string.IsNullOrWhiteSpace(statedName)
            ? state with
            {
                NextSocialGameSeconds = gameSeconds +
                    SocialRealCooldown(
                        state, VillagerSocialIntent.Introduce) *
                    GameSecondsPerRealSecond
            }
            : RecordConversation(
                state,
                characterId,
                statedName,
                VillagerSocialIntent.Introduce,
                gameSeconds);

    public static VillagerState RecordConversation(
        VillagerState state,
        string characterId,
        string? statedName,
        VillagerSocialIntent intent,
        double gameSeconds)
    {
        var people = state.KnownPeople?.ToList() ?? [];
        var index = people.FindIndex(value =>
            string.Equals(
                value.CharacterId,
                characterId,
                StringComparison.Ordinal));
        var existing = index >= 0
            ? people[index]
            : new(
                characterId,
                AcquaintanceStage.Unknown,
                null,
                0);
        var stage = intent switch
        {
            VillagerSocialIntent.Introduce =>
                AcquaintanceStage.Introduced,
            VillagerSocialIntent.AskOrigin =>
                AcquaintanceStage.ExchangedOrigins,
            VillagerSocialIntent.AskSurvival =>
                AcquaintanceStage.Cooperative,
            VillagerSocialIntent.AskTools =>
                AcquaintanceStage.DiscussedSkills,
            _ => existing.Stage
        };
        var updated = existing with
        {
            Stage = stage > existing.Stage
                ? stage
                : existing.Stage,
            StatedName = string.IsNullOrWhiteSpace(statedName)
                ? existing.StatedName
                : statedName,
            LastConversationGameSeconds = gameSeconds,
            ConversationCount = existing.ConversationCount + 1
        };
        if (index >= 0) people[index] = updated;
        else people.Add(updated);
        var memories = state.Memories?.ToList() ?? [];
        var summary = stage > existing.Stage
            ? intent switch
        {
            VillagerSocialIntent.Introduce =>
                $"Met {statedName ?? "another survivor"}.",
            VillagerSocialIntent.AskOrigin =>
                $"Asked {statedName ?? "another survivor"} what they remember about arriving.",
            VillagerSocialIntent.AskSurvival =>
                $"Compared island survival information with {statedName ?? "another survivor"}.",
            VillagerSocialIntent.AskTools =>
                $"Discussed former work and tool knowledge with {statedName ?? "another survivor"}.",
            _ => null
        }
            : null;
        if (summary is not null)
        {
            memories.Add(new(
                Guid.NewGuid(),
                "social-knowledge",
                characterId,
                null,
                1,
                gameSeconds,
                5,
                summary));
            if (memories.Count > MaximumMemories)
                memories.RemoveRange(
                    0, memories.Count - MaximumMemories);
        }
        var relationships = state.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            string.Equals(
                value.CharacterId,
                characterId,
                StringComparison.Ordinal));
        var relationship = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(characterId, default);
        if (stage > existing.Stage)
            relationship = relationship with
            {
                State = (relationship.State with
                {
                    Trust = relationship.State.Trust + .5f,
                    Affection =
                        relationship.State.Affection + .25f
                }).Clamp()
            };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = relationship;
        else
            relationships.Add(relationship);
        return state with
        {
            KnownPeople = people,
            Memories = memories,
            Relationships = relationships,
            NextSocialGameSeconds = gameSeconds +
                SocialRealCooldown(state, intent) *
                GameSecondsPerRealSecond
        };
    }

    public static VillagerState RecordDialogueTurn(
        VillagerState state,
        string speakerId,
        string speakerName,
        string text,
        double gameSeconds)
    {
        text = text.Trim();
        if (text.Length == 0) return state;
        if (text.Length > 160) text = text[..160];
        var history = state.ConversationHistory?.ToList() ?? [];
        history.Add(new(
            speakerId,
            speakerName.Trim(),
            text,
            gameSeconds));
        var memories = state.Memories?.ToList() ?? [];
        if (history.Count > MaximumConversationTurns)
        {
            var displacedCount =
                history.Count - MaximumConversationTurns;
            for (var index = 0;
                 index < displacedCount;
                 index++)
                ConsolidateDialogueMemory(
                    memories, history[index]);
            history.RemoveRange(
                0, displacedCount);
        }
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - MaximumMemories);
        return state with
        {
            ConversationHistory = history,
            Memories = memories
        };
    }

    public static VillagerState ApplyDismissal(
        VillagerState villager,
        string actorId,
        string actorName,
        string actorSpeech,
        string reply,
        int sentiment,
        double gameSeconds)
    {
        var relationships =
            villager.Relationships?.ToList() ?? [];
        var index = relationships.FindIndex(value =>
            value.CharacterId == actorId);
        var existing = index >= 0
            ? relationships[index]
            : new VillagerRelationship(actorId, default);
        var amount = MathF.Abs(sentiment) / 20f;
        var updated = existing with
        {
            State = (existing.State with
            {
                Trust = existing.State.Trust - amount,
                Resentment =
                    existing.State.Resentment + amount
            }).Clamp()
        };
        if (index >= 0)
            relationships[index] = updated;
        else
            relationships.Add(updated);
        var memories = villager.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(),
            "social-conflict",
            actorId,
            null,
            .9f,
            gameSeconds,
            sentiment,
            $"{actorName} dismissed or insulted {villager.Name}."));
        if (memories.Count > MaximumMemories)
            memories.RemoveAt(0);
        villager = villager with
        {
            Relationships = relationships,
            Memories = memories,
            FollowingActorId = null,
            Need = VillagerNeed.Idle,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds =
                gameSeconds + NearbyDecisionSeconds
        };
        villager = RecordDialogueTurn(
            villager,
            actorId,
            actorName,
            actorSpeech,
            gameSeconds);
        return RecordDialogueTurn(
            villager,
            villager.Id,
            villager.Name,
            reply,
            gameSeconds);
    }

    public static VillagerState RecordGift(
        VillagerState villager,
        string giverId,
        string giverName,
        Guid itemInstanceId,
        string itemId,
        double gameSeconds)
    {
        var relationships =
            villager.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            value.CharacterId == giverId);
        var relationship = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(giverId, default);
        relationship = relationship with
        {
            State = (relationship.State with
            {
                Trust = relationship.State.Trust + .5f,
                Affection = relationship.State.Affection + .75f
            }).Clamp()
        };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = relationship;
        else
            relationships.Add(relationship);

        var memories = villager.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(),
            "gift-received",
            giverId,
            itemInstanceId,
            1,
            gameSeconds,
            Math.Clamp(ItemValueForMemory(itemId), 2, 30),
            $"{giverName} gave {villager.Name} " +
            $"{ItemCatalog.Get(itemId).Name}."));
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - MaximumMemories);
        return villager with
        {
            Relationships = relationships,
            Memories = memories
        };
    }

    public static VillagerState RecordAttack(
        VillagerState villager,
        string attackerId,
        string attackerName,
        int damage,
        double gameSeconds)
    {
        if (damage <= 0 || villager.Health <= 0)
            return villager;
        var relationships =
            villager.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            value.CharacterId == attackerId);
        var relationship = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(attackerId, default);
        relationship = relationship with
        {
            State = (relationship.State with
            {
                Trust = relationship.State.Trust - 4,
                Affection = relationship.State.Affection - 3,
                Resentment = relationship.State.Resentment + 5
            }).Clamp()
        };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = relationship;
        else
            relationships.Add(relationship);
        var memories = villager.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(),
            "violence",
            attackerId,
            null,
            1,
            gameSeconds,
            -Math.Max(20, damage * 4),
            $"{attackerName} attacked {villager.Name}."));
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - MaximumMemories);
        var attacked = villager with
        {
            Health = Math.Max(0, villager.Health - damage),
            Relationships = relationships,
            Memories = memories,
            FollowingActorId = null,
            Need = VillagerNeed.Safe,
            Action = villager.Health - damage <= 0
                ? EntityAction.Die
                : EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = gameSeconds
        };
        return VillagerLocationMemoryService.Remember(
            attacked,
            VillagerLocationType.Danger,
            new(villager.PositionX, villager.PositionY),
            villager.WorldLevel,
            gameSeconds,
            confidence: 1);
    }

    public static VillagerState RecordWitnessedAttack(
        VillagerState witness,
        string attackerId,
        string attackerName,
        string victimId,
        string victimName,
        double gameSeconds)
    {
        if (witness.Health <= 0) return DeadState(witness);
        var relationships =
            witness.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            value.CharacterId == attackerId);
        var relationship = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(attackerId, default);
        relationship = relationship with
        {
            State = (relationship.State with
            {
                Trust = relationship.State.Trust - 2,
                Resentment = relationship.State.Resentment + 3
            }).Clamp()
        };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = relationship;
        else
            relationships.Add(relationship);
        var memories = witness.Memories?.ToList() ?? [];
        memories.Add(new(
            Guid.NewGuid(),
            "witnessed-violence",
            attackerId,
            null,
            1,
            gameSeconds,
            -30,
            $"{witness.Name} saw {attackerName} attack {victimName}."));
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - MaximumMemories);
        return witness with
        {
            Relationships = relationships,
            Memories = memories,
            Need = VillagerNeed.Safe,
            NextDecisionGameSeconds = gameSeconds
        };
    }

    private static int ItemValueForMemory(string itemId)
    {
        var item = ItemCatalog.Get(itemId);
        if (item.HasTag(ItemTag.MetalToolSprite)) return 30;
        if (item.HasTag(ItemTag.Tool)) return 15;
        if (item.HasTag(ItemTag.PlaceableObject)) return 20;
        if (item.HasTag(ItemTag.CookedFood)) return 5;
        return 2;
    }

    public static IReadOnlyList<VillagerMemory> RecallMemories(
        VillagerState state,
        string? partnerId,
        string query,
        double gameSeconds,
        int maximum = 8)
    {
        if (state.Memories is not { Count: > 0 } ||
            maximum <= 0)
            return [];
        var queryWords = SignificantWords(query);
        return state.Memories
            .Select(memory => (
                Memory: memory,
                Score: RecallScore(
                    memory,
                    partnerId,
                    queryWords,
                    gameSeconds)))
            .OrderByDescending(value => value.Score)
            .ThenByDescending(value =>
                value.Memory.GameSeconds)
            .Take(maximum)
            .Select(value => value.Memory)
            .ToArray();
    }

    private static void ConsolidateDialogueMemory(
        List<VillagerMemory> memories,
        VillagerConversationTurn turn)
    {
        var summary = $"{turn.SpeakerName} said: {turn.Text}";
        var existingIndex = memories.FindIndex(memory =>
            memory.Kind == "conversation-heard" &&
            string.Equals(
                memory.SubjectId,
                turn.SpeakerId,
                StringComparison.Ordinal) &&
            string.Equals(
                memory.Summary,
                summary,
                StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = memories[existingIndex];
            memories[existingIndex] = existing with
            {
                Confidence = Math.Min(
                    1, existing.Confidence + .08f),
                GameSeconds = Math.Max(
                    existing.GameSeconds,
                    turn.GameSeconds)
            };
            return;
        }
        memories.Add(new(
            Guid.NewGuid(),
            "conversation-heard",
            turn.SpeakerId,
            null,
            .72f,
            turn.GameSeconds,
            Summary: summary));
    }

    private static float RecallScore(
        VillagerMemory memory,
        string? partnerId,
        HashSet<string> queryWords,
        double gameSeconds)
    {
        var score = Math.Clamp(memory.Confidence, 0, 1) * 40;
        if (!string.IsNullOrWhiteSpace(partnerId) &&
            string.Equals(
                memory.SubjectId,
                partnerId,
                StringComparison.Ordinal))
            score += 32;
        score += Math.Min(30, Math.Abs(memory.Sentiment) * .3f);
        var ageDays = Math.Max(
            0, gameSeconds - memory.GameSeconds) /
            WorldTime.GameSecondsPerDay;
        score += 18 / (1 + (float)ageDays);
        if (memory.Summary is { } summary &&
            queryWords.Count > 0)
        {
            var memoryWords = SignificantWords(summary);
            score += queryWords.Count(memoryWords.Contains) * 18;
        }
        return score;
    }

    private static HashSet<string> SignificantWords(string value)
    {
        var ignored = new HashSet<string>(
            [
                "the", "and", "that", "this", "with", "from",
                "have", "there", "what", "when", "where", "who",
                "you", "your", "was", "were", "are", "for"
            ],
            StringComparer.Ordinal);
        return value
            .ToLowerInvariant()
            .Split(
                [' ', '.', ',', '!', '?', ';', ':', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(word => word.Length >= 3 &&
                           !ignored.Contains(word))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static double SocialCooldown(
        VillagerState state,
        VillagerSocialIntent intent)
    {
        var seconds = intent switch
        {
            VillagerSocialIntent.RequestFood => 30,
            VillagerSocialIntent.OfferFood => 50,
            VillagerSocialIntent.Introduce =>
                IntroductionCooldownRealSeconds,
            VillagerSocialIntent.AskOrigin => 75,
            VillagerSocialIntent.AskSurvival => 100,
            VillagerSocialIntent.AskTools => 115,
            _ => SocialCooldownRealSeconds
        };
        if (state.Need == VillagerNeed.Social)
            seconds *= .65;
        if (state.Hunger <= 20 &&
            intent == VillagerSocialIntent.RequestFood)
            return 15;

        var activePromises = state.Promises?.Count(value =>
            value.Status == CommitmentStatus.Active) ?? 0;
        var unfinishedGoals = state.Goals?.Count(value =>
            value.Status == CommitmentStatus.Active) ?? 0;
        seconds += Math.Min(
            90,
            activePromises * 25 +
            unfinishedGoals * 12);

        var sociabilityScale = 1.2 -
            Math.Clamp(state.Sociability, 0, 1) * .45;
        return Math.Clamp(
            seconds * sociabilityScale,
            15,
            240);
    }

    public static double SocialRealCooldown(
        VillagerState state,
        VillagerSocialIntent intent) =>
        Math.Clamp(
            SocialCooldown(state, intent) * .25,
            12,
            60);

    public static int CountFood(string?[] inventory)
    {
        var count = 0;
        for (var slot = 0; slot < inventory.Length; slot++)
            if (inventory[slot] is { } item &&
                SurvivalService.TryFoodEffect(item, out _))
                count++;
        return count;
    }

    public static VillagerState ApplyDecision(
        VillagerState state,
        in VillagerDecision decision,
        VillagerSimulationTier tier,
        double gameSeconds)
    {
        if (state.Health <= 0)
            return DeadState(state);
        var inventory = state.Inventory;
        var hunger = state.Hunger;
        var health = state.Health;
        var wellFedSeconds = state.WellFedSeconds;
        var ate = false;
        if ((uint)decision.ConsumeSlot < (uint)inventory.Length &&
            inventory[decision.ConsumeSlot] is { } food &&
            SurvivalService.TryFoodEffect(food, out var effect))
        {
            inventory = (string?[])inventory.Clone();
            inventory[decision.ConsumeSlot] = null;
            var eaten = SurvivalService.Eat(
                effect,
                hunger,
                wellFedSeconds,
                health,
                AdventureService.BaseMaximumHealth);
            hunger = eaten.Hunger;
            health = eaten.Health;
            wellFedSeconds = eaten.WellFedSeconds;
            ate = true;
        }
        var previous = new Vector2(
            state.PositionX, state.PositionY);
        var target = decision.MoveTarget ?? previous;
        var direction = target - previous;
        if (direction.LengthSquared > .0001f)
            direction = direction.Normalized();
        else
            direction = new(state.FacingX, state.FacingY);
        var action = decision.MoveTarget is null
            ? EntityAction.Idle
            : EntityAction.Move;
        var activity = decision.MoveTarget is null
            ? VillagerActivity.Idle
            : decision.Need switch
            {
                VillagerNeed.Social => VillagerActivity.Socializing,
                VillagerNeed.Food => VillagerActivity.SeekingResource,
                VillagerNeed.Explore => VillagerActivity.Exploring,
                _ => VillagerActivity.Exploring
            };
        var applied = state with
        {
            Inventory = inventory,
            Hunger = hunger,
            Health = health,
            WellFedSeconds = wellFedSeconds,
            Need = decision.Need,
            Action = action,
            ActionTime = state.Action == action
                ? state.ActionTime
                : 0,
            FacingX = direction.X,
            FacingY = direction.Y,
            TargetX = decision.MoveTarget?.X,
            TargetY = decision.MoveTarget?.Y,
            Activity = activity,
            ActivityUntilGameSeconds = 0,
            ConversationPartnerId = null,
            NextDecisionGameSeconds = ScheduleNextDecision(
                state.Id, gameSeconds, DecisionInterval(tier)),
            LastSimulatedGameSeconds = gameSeconds
        };
        return ate
            ? VillagerNeedPatternMemory.RecordMeal(applied, gameSeconds)
            : applied;
    }

    public static VillagerState RetargetFollowing(
        VillagerState state,
        Vector2 target,
        double gameSeconds)
    {
        if (state.Health <= 0) return DeadState(state);
        var position = new Vector2(
            state.PositionX, state.PositionY);
        var direction = target - position;
        if (direction.LengthSquared > .0001f)
            direction = direction.Normalized();
        else
            direction = new(state.FacingX, state.FacingY);
        return state with
        {
            Need = VillagerNeed.Social,
            Activity = VillagerActivity.Following,
            Action = EntityAction.Move,
            ActionTime = state.Action == EntityAction.Move
                ? state.ActionTime
                : 0,
            FacingX = direction.X,
            FacingY = direction.Y,
            TargetX = target.X,
            TargetY = target.Y,
            ActivityUntilGameSeconds = 0,
            ConversationPartnerId = null,
            LastSimulatedGameSeconds = gameSeconds
        };
    }

    public static Vector2 FollowTarget(
        in Vector2 follower,
        in Vector2 leader)
    {
        var away = follower - leader;
        if (away.LengthSquared <= .0001f)
            away = Vector2.UnitX;
        else
            away = away.Normalized();
        return leader + away * FollowStopDistance;
    }

    public static bool NeedsFollowRetarget(
        VillagerState state,
        in Vector2 target) =>
        state.Action != EntityAction.Move ||
        state.TargetX is not { } targetX ||
        state.TargetY is not { } targetY ||
        Vector2.DistanceSquared(
            new(targetX, targetY), target) >=
        FollowRetargetDistance * FollowRetargetDistance;

    public static (
        VillagerState Speaker,
        VillagerState Listener) RecordSharedDialogueLine(
        VillagerState speaker,
        VillagerState listener,
        string text,
        double gameSeconds) =>
        (
            RecordDialogueTurn(
                speaker,
                speaker.Id,
                speaker.Name,
                text,
                gameSeconds),
            RecordDialogueTurn(
                listener,
                speaker.Id,
                speaker.Name,
                text,
                gameSeconds)
        );

    public static VillagerState AdvanceMovement(
        VillagerState state,
        float elapsed,
        float terrainSpeedMultiplier = 1,
        Func<Vector2, bool>? canOccupy = null,
        double gameSeconds = 0)
    {
        if (state.Health <= 0) return DeadState(state);
        if (elapsed <= 0)
            return state;
        if (state.Action != EntityAction.Move ||
            state.TargetX is not { } targetX ||
            state.TargetY is not { } targetY)
            return state with
            {
                ActionTime = state.ActionTime + elapsed
            };
        var position = new Vector2(
            state.PositionX, state.PositionY);
        var target = new Vector2(targetX, targetY);
        var displacement = target - position;
        var distance = displacement.Length;
        var maximumStep =
            ActorMovementService.BaseMoveSpeed *
            VillagerFatigueService.MovementEffectiveness(state.Energy) *
            Math.Clamp(terrainSpeedMultiplier, .35f, 1f) *
            elapsed;
        if (distance <= Math.Max(.03f, maximumStep))
        {
            if (canOccupy is not null && !canOccupy(target))
                return BlockMovement(state, gameSeconds);
            return state with
            {
                PositionX = target.X,
                PositionY = target.Y,
                Action = EntityAction.Idle,
                ActionTime = 0,
                TargetX = null,
                TargetY = null,
                Activity = VillagerActivity.Idle,
                BlockedMoveAttempts = 0
            };
        }
        var facing = displacement / distance;
        var nextPosition = position + facing * maximumStep;
        if (canOccupy is not null && !canOccupy(nextPosition))
            return BlockMovement(state, gameSeconds);
        return state with
        {
            PositionX = nextPosition.X,
            PositionY = nextPosition.Y,
            ActionTime = state.ActionTime + elapsed,
            FacingX = facing.X,
            FacingY = facing.Y
        };
    }

    public static VillagerState CompleteAction(VillagerState state)
    {
        if (state.Health <= 0) return DeadState(state);
        return state with
        {
            Action = EntityAction.Idle,
            ActionTime = 0,
            Activity = state.Activity is
                VillagerActivity.Conversing or
                VillagerActivity.Reflecting or
                VillagerActivity.Following
                ? state.Activity
                : VillagerActivity.Idle,
            TargetX = null,
            TargetY = null
        };
    }

    public static double ScheduleNextDecision(
        string actorId,
        double gameSeconds,
        double baseDelay)
    {
        uint hash = 2166136261;
        foreach (var character in actorId)
        {
            hash ^= character;
            hash *= 16777619;
        }
        var cadence = .82 + (hash % 1000) / 1000.0 * .36;
        return gameSeconds + Math.Max(1, baseDelay * cadence);
    }

    public static VillagerState BeginConversation(
        VillagerState state,
        string? partnerId,
        double gameSeconds,
        double realSeconds)
    {
        if (state.Health <= 0) return DeadState(state);
        var holdRealSeconds = double.IsFinite(realSeconds)
            ? Math.Max(0, realSeconds)
            : double.MaxValue;
        var until = holdRealSeconds == double.MaxValue
            ? double.MaxValue
            : gameSeconds +
              holdRealSeconds * GameSecondsPerRealSecond;
        return state with
        {
            Activity = VillagerActivity.Conversing,
            ActivityUntilGameSeconds = until,
            ConversationPartnerId = partnerId,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = until
        };
    }

    public static VillagerState CompleteConversation(
        VillagerState state,
        double gameSeconds)
    {
        if (state.Health <= 0) return DeadState(state);
        var reflectionSeconds = MathHelper.Lerp(
            (float)MaximumReflectionRealSeconds,
            (float)MinimumReflectionRealSeconds,
            Math.Clamp(state.Boldness * .6f + state.Sociability * .4f, 0, 1));
        var until =
            gameSeconds + reflectionSeconds * GameSecondsPerRealSecond;
        return state with
        {
            Activity = VillagerActivity.Reflecting,
            ActivityUntilGameSeconds = until,
            ConversationPartnerId = null,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = until
        };
    }

    public static VillagerState ResumeAfterConversation(
        VillagerState state,
        double gameSeconds) => state.Health <= 0
        ? DeadState(state)
        : state with
        {
            Activity = VillagerActivity.Idle,
            ActivityUntilGameSeconds = 0,
            ConversationPartnerId = null,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            NextDecisionGameSeconds = gameSeconds + 1
        };

    public static VillagerState CompleteReflection(
        VillagerState state,
        double gameSeconds) =>
        state.Activity == VillagerActivity.Reflecting &&
        gameSeconds >= state.ActivityUntilGameSeconds
            ? state with
            {
                Activity = VillagerActivity.Idle,
                ActivityUntilGameSeconds = 0,
                NextDecisionGameSeconds = gameSeconds
            }
            : state;

    public static VillagerState BlockMovement(
        VillagerState state,
        double gameSeconds,
        Guid? failedTargetId = null)
    {
        if (state.Health <= 0) return DeadState(state);
        var attempts = Math.Min(8, state.BlockedMoveAttempts + 1);
        var failedTargets = (state.FailedTargets ?? [])
            .Where(value => value.RetryAfterGameSeconds > gameSeconds &&
                            value.TargetId != (failedTargetId ?? state.GoalObjectId))
            .ToList();
        var failedId = failedTargetId ?? state.GoalObjectId;
        if (failedId is { } targetId)
            failedTargets.Add(new(
                targetId,
                gameSeconds + Math.Min(10, 2 + attempts) * 60));
        return state with
        {
            Activity = VillagerActivity.Blocked,
            ActivityUntilGameSeconds =
                gameSeconds + (1.5 + attempts * .5) *
                GameSecondsPerRealSecond,
            Action = EntityAction.Idle,
            ActionTime = 0,
            TargetX = null,
            TargetY = null,
            GoalObjectId = null,
            BlockedMoveAttempts = attempts,
            FailedTargets = failedTargets,
            NextDecisionGameSeconds =
                gameSeconds + (1.5 + attempts * .5) *
                GameSecondsPerRealSecond
        };
    }

    private static VillagerState DeadState(VillagerState state) =>
        state with
        {
            Health = 0,
            Action = EntityAction.Die,
            ActionTime = 0,
            Activity = VillagerActivity.Idle,
            ActivityUntilGameSeconds = 0,
            TargetX = null,
            TargetY = null,
            FollowingActorId = null,
            ConversationPartnerId = null,
            ConflictTargetId = null,
            ConflictIntent = VillagerConflictIntent.None,
            ConflictMotive = null,
            ConflictExpiresGameSeconds = 0
        };

    public static bool FootBoxesOverlap(
        in Vector2 first,
        in Vector2 second) =>
        MathF.Abs(first.X - second.X) < FootBoxWidth &&
        MathF.Abs(first.Y - second.Y) < FootBoxDepth;

    public static bool TryCollisionSidestep(
        in Vector2 previous,
        in Vector2 attempted,
        in Vector2 target,
        in Vector2 obstacle,
        Func<Vector2, bool> canOccupy,
        out Vector2 resolved)
    {
        resolved = previous;
        var step = Vector2.Distance(previous, attempted);
        var forward = target - previous;
        if (step <= .0001f || forward.LengthSquared <= .0001f)
            return false;
        forward = forward.Normalized();
        var perpendicular = new Vector2(-forward.Y, forward.X);
        if (Vector2.Dot(perpendicular, previous - obstacle) < 0)
            perpendicular = -perpendicular;
        for (var side = 0; side < 2; side++)
        {
            var offset = (forward * .25f + perpendicular * .97f)
                .Normalized() * step;
            var candidate = previous + offset;
            if (!FootBoxesOverlap(candidate, obstacle) &&
                canOccupy(candidate))
            {
                resolved = candidate;
                return true;
            }
            perpendicular = -perpendicular;
        }
        return false;
    }

    public static VillagerState ObserveUnauthorizedTaking(
        VillagerState observer,
        Guid incidentId,
        string itemType,
        string ownerId,
        string suspectId,
        double gameSeconds,
        float confidence,
        int itemValue,
        out OwnershipReaction reaction)
    {
        if (observer.Health <= 0)
        {
            reaction = OwnershipReaction.None;
            return DeadState(observer);
        }
        if (observer.ObservedOwnershipIncidentIds?.Contains(incidentId) == true)
        {
            reaction = OwnershipReaction.None;
            return observer;
        }
        var relationships =
            observer.Relationships?.ToList() ?? [];
        var relationshipIndex = relationships.FindIndex(value =>
            string.Equals(
                value.CharacterId, suspectId,
                StringComparison.Ordinal));
        var existing = relationshipIndex >= 0
            ? relationships[relationshipIndex]
            : new VillagerRelationship(suspectId, default);
        var incident = new OwnershipIncident(
            incidentId, itemType, ownerId, suspectId,
            Math.Clamp(confidence, 0, 1),
            itemValue,
            existing.OwnershipOffences,
            Returned: false,
            WasEmergency: false);
        var updated = existing with
        {
            State = ItemOwnershipService.ApplyIncident(
                existing.State, incident),
            OwnershipOffences = existing.OwnershipOffences + 1
        };
        if (relationshipIndex >= 0)
            relationships[relationshipIndex] = updated;
        else
            relationships.Add(updated);
        reaction = ItemOwnershipService.Assess(
            incident, updated.State);
        var memories = observer.Memories?.ToList() ?? [];
        memories.Add(new(
            incidentId,
            "unauthorized-item-taken",
            suspectId,
            incidentId,
            confidence,
            gameSeconds));
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(0, memories.Count - MaximumMemories);
        var observedIncidents =
            observer.ObservedOwnershipIncidentIds?.ToList() ?? [];
        observedIncidents.Add(incidentId);
        if (observedIncidents.Count > MaximumObservedOwnershipIncidents)
            observedIncidents.RemoveRange(
                0,
                observedIncidents.Count -
                MaximumObservedOwnershipIncidents);
        return observer with
        {
            Relationships = relationships,
            Memories = memories,
            ObservedOwnershipIncidentIds = observedIncidents
        };
    }

    public static string ReactionSpeech(
        string villagerName,
        string itemName,
        OwnershipReaction reaction) =>
        reaction switch
        {
            OwnershipReaction.Question =>
                $"Did you take my {itemName}?",
            OwnershipReaction.DemandReturn =>
                $"Please return my {itemName}.",
            OwnershipReaction.DemandCompensation =>
                $"That {itemName} is mine. Return it now.",
            OwnershipReaction.RefuseAccess =>
                $"You took my {itemName}. Stay away from my things.",
            OwnershipReaction.WarnCommunity =>
                $"Everyone should know you stole my {itemName}.",
            OwnershipReaction.Hostile =>
                $"Return my {itemName}, or this ends badly.",
            _ => $"{villagerName} notices the {itemName} is missing."
        };

    private static Vector2 FindSpawn(
        long seed,
        int index,
        Vector2 origin,
        Func<Vector2, bool>? canStand)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var angle = Unit(Hash(seed, index, attempt * 2)) * MathF.Tau;
            var radius = 3 + Unit(
                Hash(seed, index, attempt * 2 + 1)) * 7;
            var candidate = origin +
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            if (canStand?.Invoke(candidate) != false)
                return candidate;
        }
        return origin + new Vector2(index + 2, index + 2);
    }

    private static Vector2 AwayFrom(
        Vector2 origin,
        Vector2 threat,
        float distance)
    {
        var away = origin - threat;
        if (away.LengthSquared < .001f) away = Vector2.UnitX;
        return origin + away.Normalized() * distance;
    }

    private static Vector2 StepToward(
        Vector2 origin,
        Vector2 target)
    {
        var displacement = target - origin;
        var distance = displacement.Length;
        if (distance <= InteractionRange)
            return origin;
        var travel = MathF.Min(
            2,
            distance - InteractionRange * .9f);
        return origin + displacement / distance * travel;
    }

    private static bool ShouldGather(
        VillagerState state,
        ItemDefinition item) =>
        VillagerResourcePriority.Score(state, item.Id) > 0;

    private static bool IsPromisedItem(
        VillagerState state,
        string itemId)
    {
        if (state.Promises is null) return false;
        for (var index = 0;
             index < state.Promises.Count;
             index++)
        {
            var promise = state.Promises[index];
            if (promise.Status == CommitmentStatus.Active &&
                promise.Kind ==
                    VillagerPromiseKind.GatherItem &&
                promise.Progress < promise.TargetQuantity &&
                string.Equals(
                    promise.ItemId, itemId,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int CountMatching(
        string?[] inventory,
        Func<ItemDefinition, bool> predicate)
    {
        var count = 0;
        for (var slot = 0; slot < inventory.Length; slot++)
            if (inventory[slot] is { } itemId &&
                ItemCatalog.TryGet(itemId, out var item) &&
                predicate(item))
                count++;
        return count;
    }

    private static string StableId(long seed, int index) =>
        $"villager-{unchecked((uint)Hash(seed, index, 211)):x8}-{index}";

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619;
            return (int)hash;
        }
    }

    private static int Hash(long seed, int a, int b)
    {
        unchecked
        {
            ulong value = (ulong)seed;
            value ^= (uint)a + 0x9e3779b9u + (value << 6) + (value >> 2);
            value ^= (uint)b + 0x85ebca6bu + (value << 6) + (value >> 2);
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            return (int)(value ^ (value >> 31));
        }
    }

    private static float Unit(int value) =>
        (value & 0x7fffffff) / (float)int.MaxValue;

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
