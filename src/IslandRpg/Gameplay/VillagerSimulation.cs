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

internal sealed record VillagerMemory(
    Guid EventId,
    string Kind,
    string SubjectId,
    Guid? ItemInstanceId,
    float Confidence,
    double GameSeconds,
    int Sentiment = 0,
    string? Summary = null);

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
    double AwakenedGameSeconds = 8 * 60 * 60);

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
    int FoodCount);

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
    public const double NearbyDecisionSeconds = 8;
    public const double RegionalDecisionSeconds = 30;
    public const double DistantDecisionSeconds = 120;
    public const int MaximumMemories = 64;
    public const float InteractionRange = 1.35f;
    public const float ResourceSearchRadius = 24;
    public const int StorageDepositThreshold = 8;
    public const float FootBoxWidth = .46f;
    public const float FootBoxDepth = .34f;
    public const double GatherPauseSeconds = 45;
    public const double SocialCooldownSeconds = 120;
    public const double IntroductionCooldownSeconds = 45;
    public const double RelationshipCheckInSeconds = 6 * 60 * 60;
    public const float SocialRange = 8;

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
        IReadOnlyList<VillagerPersona>? personas = null)
    {
        population = Math.Clamp(population, 0, InitialPopulation);
        var result = new VillagerState[population];
        for (var index = 0; index < result.Length; index++)
        {
            var position = FindSpawn(
                worldSeed, index, origin, canStand);
            var id = StableId(worldSeed, index);
            result[index] = new(
                id,
                Names[index],
                index == 0 ? EntityGender.Female : EntityGender.Male,
                PositiveMod(Hash(worldSeed, index, 17), 5),
                index + 1,
                position.X,
                position.Y,
                WorldLevel: 0,
                Health: AdventureService.BaseMaximumHealth,
                Hunger: SurvivalService.MaximumHunger,
                Inventory: PlayerInventory.CreateStartingInventory(),
                NextDecisionGameSeconds: gameSeconds,
                LastSimulatedGameSeconds: gameSeconds,
                Sociability: Unit(Hash(worldSeed, index, 31)),
                Honesty: Unit(Hash(worldSeed, index, 47)),
                Boldness: Unit(Hash(worldSeed, index, 61)),
                Persona: personas is not null &&
                         index < personas.Count
                    ? personas[index]
                    : DefaultPersona(index),
                AwakenedGameSeconds: gameSeconds,
                Goals: VillagerCommitmentService.InitialGoals(
                    id, gameSeconds));
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
        double gameSeconds)
    {
        var elapsed = Math.Clamp(
            gameSeconds - state.LastSimulatedGameSeconds,
            0,
            24 * 60 * 60);
        if (elapsed <= 0) return state;
        var survival = SurvivalService.Advance(
            state.Hunger, 0, state.Health, (float)elapsed);
        return state with
        {
            Hunger = survival.Hunger,
            Health = survival.Health,
            LastSimulatedGameSeconds = gameSeconds
        };
    }

    public static VillagerDecision Decide(
        VillagerState state,
        Vector2 playerPosition,
        double gameSeconds)
    {
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
        ReadOnlySpan<VillagerWorldObject> objects)
    {
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
                if (!candidate.IsStorage ||
                    !string.Equals(
                        candidate.OwnerId, state.Id,
                        StringComparison.Ordinal))
                    continue;
                var distanceSquared = Vector2.DistanceSquared(
                    position, candidate.Position);
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
            return default;

        bestScore = ResourceSearchRadius * ResourceSearchRadius;
        found = false;
        for (var index = 0; index < objects.Length; index++)
        {
            ref readonly var candidate = ref objects[index];
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
            if (distanceSquared >
                ResourceSearchRadius * ResourceSearchRadius)
                continue;
            var usefulFood =
                state.Hunger <= 55 &&
                SurvivalService.TryFoodEffect(
                    candidate.ItemId, out _);
            var score = distanceSquared -
                        (usefulFood ? 256 : 0) -
                        (IsPromisedItem(
                            state, candidate.ItemId)
                            ? 512
                            : 0) -
                        (candidate.Id == state.GoalObjectId
                            ? 32
                            : 0);
            if (found && score >= bestScore) continue;
            bestScore = score;
            best = candidate;
            found = true;
        }
        if (!found) return default;
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

    public static VillagerSocialGoal SelectSocialGoal(
        VillagerState state,
        ReadOnlySpan<SocialActorObservation> actors,
        double gameSeconds = 0)
    {
        if (gameSeconds < state.NextSocialGameSeconds)
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
            var relationshipCheckIn =
                state.Need == VillagerNeed.Social &&
                (known is null ||
                 known.Stage < AcquaintanceStage.DiscussedSkills ||
                 gameSeconds -
                 known.LastConversationGameSeconds >=
                 RelationshipCheckInSeconds);
            var canHelpHungryVillager =
                state.Hunger <= 35 && ownFood == 0 &&
                actor.FoodCount > 1;
            var needsOurSurplus =
                ownFood > 1 && actor.Hunger <= 35 &&
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
                    : 0);
            if (score >= bestScore) continue;
            bestScore = score;
            bestDistance = distance;
            best = actor;
            found = true;
        }
        if (!found) return default;
        var inConversationRange =
            bestDistance <= InteractionRange * InteractionRange;
        if (state.Hunger <= 35 &&
            ownFood == 0 &&
            best.FoodCount > 1)
            return new(
                VillagerSocialIntent.RequestFood,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? $"{best.Name}, could you spare some food?"
                    : null);
        if (ownFood > 1 &&
            best.Hunger <= 35 &&
            best.FoodCount == 0)
            return new(
                VillagerSocialIntent.OfferFood,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? $"{best.Name}, take this. You need it."
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
            return new(
                VillagerSocialIntent.AskTools,
                best.Id,
                inConversationRange
                    ? null
                    : StepToward(position, best.Position),
                inConversationRange
                    ? state.Persona is { } persona
                        ? $"I used to work as a {persona.PriorTrade}. What tools do you know?"
                        : "What sort of work and tools do you know?"
                    : null);
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
                SocialCooldown(state, intent)
        };
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
                IntroductionCooldownSeconds,
            VillagerSocialIntent.AskOrigin => 75,
            VillagerSocialIntent.AskSurvival => 100,
            VillagerSocialIntent.AskTools => 115,
            _ => SocialCooldownSeconds
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
        var inventory = state.Inventory;
        var hunger = state.Hunger;
        if ((uint)decision.ConsumeSlot < (uint)inventory.Length &&
            inventory[decision.ConsumeSlot] is { } food &&
            SurvivalService.TryFoodEffect(food, out var effect))
        {
            inventory = (string?[])inventory.Clone();
            inventory[decision.ConsumeSlot] = null;
            hunger = Math.Min(
                SurvivalService.MaximumHunger,
                hunger + effect.HungerRestored);
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
        return state with
        {
            Inventory = inventory,
            Hunger = hunger,
            Need = decision.Need,
            Action = action,
            ActionTime = state.Action == action
                ? state.ActionTime
                : 0,
            FacingX = direction.X,
            FacingY = direction.Y,
            TargetX = decision.MoveTarget?.X,
            TargetY = decision.MoveTarget?.Y,
            NextDecisionGameSeconds =
                gameSeconds + DecisionInterval(tier),
            LastSimulatedGameSeconds = gameSeconds
        };
    }

    public static VillagerState AdvanceMovement(
        VillagerState state,
        float elapsed,
        float terrainSpeedMultiplier = 1)
    {
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
            Math.Clamp(terrainSpeedMultiplier, .35f, 1f) *
            elapsed;
        if (distance <= Math.Max(.03f, maximumStep))
            return state with
            {
                PositionX = target.X,
                PositionY = target.Y,
                Action = EntityAction.Idle,
                ActionTime = 0,
                TargetX = null,
                TargetY = null
            };
        var facing = displacement / distance;
        position += facing * maximumStep;
        return state with
        {
            PositionX = position.X,
            PositionY = position.Y,
            ActionTime = state.ActionTime + elapsed,
            FacingX = facing.X,
            FacingY = facing.Y
        };
    }

    public static bool FootBoxesOverlap(
        in Vector2 first,
        in Vector2 second) =>
        MathF.Abs(first.X - second.X) < FootBoxWidth &&
        MathF.Abs(first.Y - second.Y) < FootBoxDepth;

    public static VillagerState ObserveUnauthorizedTaking(
        VillagerState observer,
        Guid itemId,
        string itemType,
        string ownerId,
        string suspectId,
        double gameSeconds,
        float confidence,
        int itemValue,
        out OwnershipReaction reaction)
    {
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
            itemId, itemType, ownerId, suspectId,
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
            itemId,
            "unauthorized-item-taken",
            suspectId,
            itemId,
            confidence,
            gameSeconds));
        if (memories.Count > MaximumMemories)
            memories.RemoveRange(0, memories.Count - MaximumMemories);
        return observer with
        {
            Relationships = relationships,
            Memories = memories
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
        return distance <= 2
            ? target
            : origin + displacement / distance * 2;
    }

    private static bool ShouldGather(
        VillagerState state,
        ItemDefinition item)
    {
        if (IsPromisedItem(state, item.Id))
            return true;
        if (SurvivalService.TryFoodEffect(item.Id, out _))
            return state.Hunger <= 65 &&
                   CountMatching(
                       state.Inventory,
                       candidate =>
                           SurvivalService.TryFoodEffect(
                               candidate.Id, out _)) < 2;
        if (item.HasTag(ItemTag.Log))
            return CountMatching(
                state.Inventory,
                candidate => candidate.HasTag(ItemTag.Log)) < 2;
        if (item.HasTag(ItemTag.NaturalMaterial) ||
            item.HasTag(ItemTag.WoodcuttingMaterial) ||
            item.HasTag(ItemTag.MiningMaterial) ||
            item.HasTag(ItemTag.Mineral))
            return CountMatching(
                state.Inventory,
                candidate =>
                    candidate.HasTag(ItemTag.NaturalMaterial) ||
                    candidate.HasTag(
                        ItemTag.WoodcuttingMaterial) ||
                    candidate.HasTag(ItemTag.MiningMaterial) ||
                    candidate.HasTag(ItemTag.Mineral)) < 3;
        return false;
    }

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
