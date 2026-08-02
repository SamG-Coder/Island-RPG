using IslandRpg.World;

namespace IslandRpg.Gameplay;

internal static class VillagerOpeningIncidentService
{
    public const double IncidentRealSeconds = 45;

    public static VillagerState ApplyShoreExposure(
        VillagerState villager, Biome biome, int damage = 1)
    {
        if (villager.Health <= 0 ||
            villager.Action != EntityAction.Hurt ||
            biome is not (Biome.Beach or Biome.ShallowWater or
                Biome.MangroveShallows) ||
            damage <= 0)
            return villager;
        var health = Math.Max(0, villager.Health - damage);
        return villager with
        {
            Health = health,
            Action = health == 0 ? EntityAction.Die : EntityAction.Hurt,
            DeathCause = health == 0
                ? "Died from wounds and exposure on the shore."
                : villager.DeathCause
        };
    }

    private enum IncidentKind : byte
    {
        Rescue,
        ScavengeDispute,
        SharedLoss,
        Abandonment
    }

    private readonly record struct RescuePair(string VictimId, string HelperId);

    public static IReadOnlyList<VillagerState> Apply(
        IReadOnlyList<VillagerState> villagers,
        long worldRunSeed,
        double gameSeconds)
    {
        if (villagers.Count < 2) return villagers.ToArray();
        var result = villagers.ToArray();
        var random = new Random(Seed(worldRunSeed, gameSeconds));
        var minimum = Math.Max(1, result.Length / 3);
        var maximum = Math.Max(minimum, result.Length / 2);
        var incidentCount = random.Next(minimum, maximum + 1);
        var injuryCreated = false;
        var rescuedVictims = new HashSet<string>(StringComparer.Ordinal);
        var protectedHelpers = new HashSet<string>(StringComparer.Ordinal);

        for (var incidentIndex = 0;
             incidentIndex < incidentCount;
             incidentIndex++)
        {
            var mustCreateInjury = !injuryCreated &&
                incidentIndex == incidentCount - 1;
            var kind = SelectIncident(random, result.Length,
                mustCreateInjury);
            if (kind == IncidentKind.Rescue)
            {
                var rescue = ApplyRescue(
                    result, random, worldRunSeed, gameSeconds, incidentIndex,
                    protectedHelpers: protectedHelpers);
                rescuedVictims.Add(rescue.VictimId);
                protectedHelpers.Add(rescue.HelperId);
                injuryCreated = true;
            }
            else
                injuryCreated |= kind switch
                {
                    IncidentKind.ScavengeDispute => ApplyDispute(
                        result, random, worldRunSeed, gameSeconds, incidentIndex),
                    IncidentKind.SharedLoss => ApplySharedLoss(
                        result, random, worldRunSeed, gameSeconds, incidentIndex),
                    _ => ApplyAbandonment(
                        result, random, worldRunSeed, gameSeconds, incidentIndex,
                        protectedHelpers)
                };
        }
        var rescueIndex = incidentCount;
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index].Health >= villagers[index].Health ||
                rescuedVictims.Contains(result[index].Id))
                continue;
            var rescue = ApplyRescue(
                result, random, worldRunSeed, gameSeconds,
                rescueIndex++, forcedInjured: index,
                applyInjury: false,
                protectedHelpers: protectedHelpers);
            rescuedVictims.Add(rescue.VictimId);
            protectedHelpers.Add(rescue.HelperId);
        }
        return result;
    }

    public static bool IsActive(
        IReadOnlyList<VillagerState> villagers,
        double gameSeconds)
    {
        var earliestAwakening = double.PositiveInfinity;
        for (var index = 0; index < villagers.Count; index++)
        {
            var villager = villagers[index];
            if (villager.Health > 0 &&
                villager.AwakenedGameSeconds < earliestAwakening)
                earliestAwakening = villager.AwakenedGameSeconds;
        }
        return !double.IsPositiveInfinity(earliestAwakening) &&
            gameSeconds < earliestAwakening +
            IncidentRealSeconds * VillagerSimulation.GameSecondsPerRealSecond;
    }

    public static IReadOnlyList<VillagerGroupConversationLine> Accounts(
        IReadOnlyList<VillagerState> villagers)
    {
        var lines = new List<VillagerGroupConversationLine>();
        foreach (var villager in villagers.Where(value => value.Health > 0))
        {
            var incident = villager.Memories?
                .Where(value => value.Kind.StartsWith(
                    "wreck_", StringComparison.Ordinal))
                .OrderByDescending(value => Math.Abs(value.Sentiment))
                .ThenByDescending(value => value.GameSeconds)
                .FirstOrDefault();
            if (incident is null) continue;
            lines.Add(new(villager.Id,
                incident.Summary ?? "There was confusion beside the wreck.",
                incident.Sentiment < 0
                    ? "incident-dispute"
                    : "incident-account",
                UseAi: true));
        }
        return lines;
    }

    private static IncidentKind SelectIncident(
        Random random, int population, bool requireInjury)
    {
        if (requireInjury)
            return random.Next(100) < 68
                ? IncidentKind.Rescue
                : IncidentKind.Abandonment;
        var roll = random.Next(100) + Math.Min(10, population);
        return roll switch
        {
            < 38 => IncidentKind.Rescue,
            < 66 => IncidentKind.ScavengeDispute,
            < 87 => IncidentKind.SharedLoss,
            _ => IncidentKind.Abandonment
        };
    }

    private static RescuePair ApplyRescue(
        VillagerState[] villagers, Random random, long seed,
        double gameSeconds, int incidentIndex,
        int? forcedInjured = null,
        bool applyInjury = true,
        IReadOnlySet<string>? protectedHelpers = null)
    {
        var injured = forcedInjured ?? PickWeighted(
            villagers, random, -1,
            _ => 1,
            value => protectedHelpers?.Contains(value.Id) != true);
        var helper = PickWeighted(villagers, random, injured,
            value => 1 + Altruism(value) * 3 + Strength(value) * 2,
            value => value.Health >= AdventureService.BaseMaximumHealth);
        var eventId = IncidentId(seed, incidentIndex, IncidentKind.Rescue,
            villagers[injured].Id, villagers[helper].Id);
        var affinity = random.Next(20, 36);
        var trust = random.Next(15, 26);
        var context = random.Next(3) switch
        {
            0 => "pulled me clear of the surf",
            1 => "freed me from beneath a broken spar",
            _ => "carried me above the rising tide"
        };
        var injuredState = applyInjury
            ? villagers[injured] with
            {
                Health = Math.Min(villagers[injured].Health,
                    random.Next(58, 82))
            }
            : villagers[injured];
        villagers[injured] = AddMemory(
            injuredState, new(eventId, "wreck_rescue", villagers[helper].Id, null,
                1, gameSeconds, affinity,
                $"{villagers[helper].Name} {context} when I was hurt."));
        villagers[helper] = AddMemory(villagers[helper],
            new(eventId, "wreck_rescue", villagers[injured].Id, null,
                1, gameSeconds, trust,
                $"I helped {villagers[injured].Name}, who was injured among the wreckage."));
        villagers[injured] = AdjustRelationship(villagers[injured],
            villagers[helper].Id, trust, affinity, respect: trust / 2,
            gratitude: affinity);
        return new(villagers[injured].Id, villagers[helper].Id);
    }

    private static bool ApplyDispute(
        VillagerState[] villagers, Random random, long seed,
        double gameSeconds, int incidentIndex)
    {
        var accused = PickWeighted(villagers, random, -1,
            value => 1 + Greed(value) * 4);
        var accuser = PickWeighted(villagers, random, accused,
            value => 1 + value.Boldness * 2 + value.Honesty);
        var eventId = IncidentId(seed, incidentIndex,
            IncidentKind.ScavengeDispute,
            villagers[accuser].Id, villagers[accused].Id);
        var affinity = -random.Next(15, 31);
        var trust = -random.Next(15, 26);
        var objectName = random.Next(3) switch
        {
            0 => "a food cask",
            1 => "a coil of sound rope",
            _ => "tools cast up by the tide"
        };
        villagers[accuser] = AddMemory(villagers[accuser],
            new(eventId, "wreck_dispute", villagers[accused].Id, null,
                .72f, gameSeconds, affinity,
                $"I believe {villagers[accused].Name} tried to keep {objectName} while others needed aid."));
        villagers[accused] = AddMemory(villagers[accused],
            new(eventId, "wreck_dispute", villagers[accuser].Id, null,
                .65f, gameSeconds, trust,
                $"{villagers[accuser].Name} accused me of withholding {objectName} during the confusion."));
        villagers[accuser] = AdjustRelationship(villagers[accuser],
            villagers[accused].Id, trust, affinity, respect: affinity / 2,
            resentment: -affinity);
        return false;
    }

    private static bool ApplySharedLoss(
        VillagerState[] villagers, Random random, long seed,
        double gameSeconds, int incidentIndex)
    {
        var first = random.Next(villagers.Length);
        var second = PickWeighted(villagers, random, first,
            value => 1 + value.Sociability + value.Honesty);
        var eventId = IncidentId(seed, incidentIndex,
            IncidentKind.SharedLoss, villagers[first].Id, villagers[second].Id);
        var empathy = random.Next(10, 21);
        var loss = random.Next(3) switch
        {
            0 => "watched our belongings vanish with the tide",
            1 => "searched the shore together for missing kin",
            _ => "kept watch together while the wreck broke apart"
        };
        villagers[first] = AddMemory(villagers[first],
            new(eventId, "wreck_shared_loss", villagers[second].Id, null,
                .9f, gameSeconds, empathy,
                $"{villagers[second].Name} and I {loss}."));
        villagers[second] = AddMemory(villagers[second],
            new(eventId, "wreck_shared_loss", villagers[first].Id, null,
                .9f, gameSeconds, empathy,
                $"{villagers[first].Name} and I {loss}."));
        villagers[first] = AdjustRelationship(villagers[first],
            villagers[second].Id, empathy / 2, empathy,
            gratitude: empathy / 2);
        villagers[second] = AdjustRelationship(villagers[second],
            villagers[first].Id, empathy / 2, empathy,
            gratitude: empathy / 2);
        return false;
    }

    private static bool ApplyAbandonment(
        VillagerState[] villagers, Random random, long seed,
        double gameSeconds, int incidentIndex,
        IReadOnlySet<string>? protectedHelpers = null)
    {
        var panicked = PickWeighted(villagers, random, -1,
            value => 1 + (1 - value.Boldness) * 3);
        var abandoned = PickWeighted(villagers, random, panicked,
            value => 1 + value.Boldness,
            value => protectedHelpers?.Contains(value.Id) != true);
        var eventId = IncidentId(seed, incidentIndex,
            IncidentKind.Abandonment,
            villagers[abandoned].Id, villagers[panicked].Id);
        villagers[abandoned] = AddMemory(
            villagers[abandoned] with
            {
                Health = Math.Min(villagers[abandoned].Health,
                    random.Next(62, 86))
            }, new(eventId, "wreck_abandonment", villagers[panicked].Id,
                null, .85f, gameSeconds, -20,
                $"{villagers[panicked].Name} fled when I called for aid beside the wreck."));
        villagers[panicked] = AddMemory(villagers[panicked],
            new(eventId, "wreck_abandonment", villagers[abandoned].Id,
                null, .72f, gameSeconds, -10,
                $"I panicked and left {villagers[abandoned].Name} calling for help near the surf."));
        villagers[abandoned] = AdjustRelationship(villagers[abandoned],
            villagers[panicked].Id, -10, -20, respect: -12,
            resentment: 20);
        return true;
    }

    private static int PickWeighted(
        IReadOnlyList<VillagerState> villagers, Random random,
        int excludedIndex, Func<VillagerState, float> weight,
        Func<VillagerState, bool>? eligible = null)
    {
        var total = 0f;
        for (var index = 0; index < villagers.Count; index++)
            if (index != excludedIndex &&
                (eligible is null || eligible(villagers[index])))
                total += Math.Max(.01f, weight(villagers[index]));
        if (total <= 0 && eligible is not null)
            return PickWeighted(
                villagers, random, excludedIndex, weight);
        var roll = random.NextSingle() * total;
        for (var index = 0; index < villagers.Count; index++)
        {
            if (index == excludedIndex ||
                eligible is not null && !eligible(villagers[index]))
                continue;
            roll -= Math.Max(.01f, weight(villagers[index]));
            if (roll <= 0) return index;
        }
        return excludedIndex == 0 ? 1 : 0;
    }

    private static float Greed(VillagerState value) =>
        (1 - value.Honesty) * .65f + value.Boldness * .35f;

    private static float Altruism(VillagerState value) =>
        value.Honesty * .6f + value.Sociability * .4f;

    private static float Strength(VillagerState value) =>
        Math.Clamp(value.Health / 100f * .6f +
            value.StrengthExperience / 1000f * .4f, 0, 1);

    private static VillagerState AddMemory(
        VillagerState villager, VillagerMemory memory)
    {
        var memories = (villager.Memories ?? [])
            .Where(value => value.EventId != memory.EventId).ToList();
        memories.Add(memory);
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(0,
                memories.Count - VillagerSimulation.MaximumMemories);
        return villager with { Memories = memories };
    }

    private static VillagerState AdjustRelationship(
        VillagerState villager, string otherId, int trust, int affection,
        int respect = 0, int gratitude = 0, int resentment = 0)
    {
        var relationships = (villager.Relationships ?? []).ToList();
        var index = relationships.FindIndex(value =>
            value.CharacterId == otherId);
        var state = index >= 0
            ? relationships[index].State
            : new RelationshipState();
        var updated = (state with
        {
            Trust = state.Trust + trust,
            Affection = state.Affection + affection,
            Respect = state.Respect + respect,
            Gratitude = state.Gratitude + gratitude,
            Resentment = state.Resentment + resentment
        }).Clamp();
        var relationship = new VillagerRelationship(otherId, updated,
            index >= 0 ? relationships[index].OwnershipOffences : 0);
        if (index >= 0) relationships[index] = relationship;
        else relationships.Add(relationship);
        return villager with { Relationships = relationships };
    }

    private static int Seed(long worldRunSeed, double gameSeconds)
    {
        unchecked
        {
            var value = (ulong)worldRunSeed ^
                        (ulong)BitConverter.DoubleToInt64Bits(gameSeconds);
            value ^= value >> 33;
            value *= 0xff51afd7ed558ccdUL;
            value ^= value >> 33;
            return (int)(value ^ (value >> 32));
        }
    }

    private static Guid IncidentId(
        long seed, int index, IncidentKind kind, string first, string second)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"wreck:{seed}:{index}:{kind}:{first}:{second}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
