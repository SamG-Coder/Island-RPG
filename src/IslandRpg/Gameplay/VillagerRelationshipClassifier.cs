namespace IslandRpg.Gameplay;

internal enum VillagerRelationshipKind : byte
{
    Neutral,
    Acquaintance,
    Respected,
    Fond,
    Friend,
    CloseBond,
    Rival,
    Enemy,
    FearedEnemy
}

internal enum VillagerAttractionLevel : byte
{
    None,
    Interest,
    Attracted,
    Devoted
}

internal readonly record struct VillagerRelationshipSummary(
    int Friends,
    int CloseBonds,
    int Rivals,
    int Enemies);

internal readonly record struct VillagerRelationshipTransition(
    VillagerRelationshipKind PreviousKind,
    VillagerRelationshipKind CurrentKind,
    VillagerAttractionLevel PreviousAttraction,
    VillagerAttractionLevel CurrentAttraction)
{
    public string? PlayerMessage(string villagerName)
    {
        if (CurrentAttraction != PreviousAttraction)
            return CurrentAttraction switch
            {
                VillagerAttractionLevel.Interest =>
                    $"{villagerName} seems interested in you.",
                VillagerAttractionLevel.Attracted =>
                    $"{villagerName} is attracted to you.",
                VillagerAttractionLevel.Devoted =>
                    $"{villagerName} has become deeply attached to you.",
                VillagerAttractionLevel.None when
                    PreviousAttraction != VillagerAttractionLevel.None =>
                    $"{villagerName}'s attraction to you has faded.",
                _ => null
            };
        if (CurrentKind == PreviousKind) return null;
        return CurrentKind switch
        {
            VillagerRelationshipKind.Friend =>
                $"{villagerName} now considers you a friend.",
            VillagerRelationshipKind.CloseBond =>
                $"You and {villagerName} have formed a close bond.",
            VillagerRelationshipKind.Rival =>
                $"{villagerName} now considers you a rival.",
            VillagerRelationshipKind.Enemy =>
                $"{villagerName} now considers you an enemy.",
            VillagerRelationshipKind.FearedEnemy =>
                $"{villagerName} now fears you as an enemy.",
            _ => null
        };
    }
}

internal static class VillagerRelationshipClassifier
{
    public static VillagerRelationshipKind Classify(
        in RelationshipState state,
        bool subjectIsLeader = false)
    {
        if (state.Fear >= 35 &&
            (state.Trust <= -10 || state.Resentment >= 20))
            return VillagerRelationshipKind.FearedEnemy;
        if (state.Trust <= -35 || state.Resentment >= 45)
            return VillagerRelationshipKind.Enemy;
        if (state.Trust <= -15 || state.Resentment >= 20)
            return VillagerRelationshipKind.Rival;
        if (state.Trust >= 45 && state.Affection >= 35)
            return VillagerRelationshipKind.CloseBond;
        if (state.Trust >= 20 && state.Affection >= 12)
            return VillagerRelationshipKind.Friend;
        if (state.Affection >= 15 && state.Trust >= 5)
            return VillagerRelationshipKind.Fond;
        if (state.Respect >= 15 &&
            (subjectIsLeader || state.Trust >= 5))
            return VillagerRelationshipKind.Respected;
        if (state.Trust > 0 || state.Affection > 0 ||
            state.Respect > 0 || state.Gratitude > 0)
            return VillagerRelationshipKind.Acquaintance;
        return VillagerRelationshipKind.Neutral;
    }

    public static VillagerRelationshipSummary Summarize(
        IEnumerable<VillagerRelationship>? relationships,
        string? leaderId = null)
    {
        var friends = 0;
        var closeBonds = 0;
        var rivals = 0;
        var enemies = 0;
        foreach (var relationship in relationships ?? [])
            switch (Classify(
                        relationship.State,
                        relationship.CharacterId == leaderId))
            {
                case VillagerRelationshipKind.Friend:
                    friends++;
                    break;
                case VillagerRelationshipKind.CloseBond:
                    closeBonds++;
                    break;
                case VillagerRelationshipKind.Rival:
                    rivals++;
                    break;
                case VillagerRelationshipKind.Enemy:
                case VillagerRelationshipKind.FearedEnemy:
                    enemies++;
                    break;
            }
        return new(friends, closeBonds, rivals, enemies);
    }

    public static float SocialPreferenceAdjustment(
        VillagerRelationshipKind kind) => kind switch
    {
        VillagerRelationshipKind.CloseBond => -256,
        VillagerRelationshipKind.Friend => -160,
        VillagerRelationshipKind.Fond => -80,
        VillagerRelationshipKind.Respected => -32,
        VillagerRelationshipKind.Rival => 160,
        VillagerRelationshipKind.Enemy => 512,
        VillagerRelationshipKind.FearedEnemy => 768,
        _ => 0
    };

    public static bool WillDefend(
        in RelationshipState state,
        bool subjectIsLeader = false)
    {
        var kind = Classify(state, subjectIsLeader);
        return kind is VillagerRelationshipKind.Friend or
                       VillagerRelationshipKind.CloseBond ||
               kind == VillagerRelationshipKind.Respected &&
               state.Respect >= 20 ||
               state.Gratitude >= 25 && state.Trust >= 5;
    }

    public static string PromptDescription(
        in RelationshipState state,
        bool subjectIsLeader = false) =>
        Classify(state, subjectIsLeader) switch
        {
            VillagerRelationshipKind.CloseBond => "shares a close bond with",
            VillagerRelationshipKind.Friend => "considers a friend",
            VillagerRelationshipKind.Fond => "feels warmly toward",
            VillagerRelationshipKind.Respected => "respects",
            VillagerRelationshipKind.Rival => "considers a rival",
            VillagerRelationshipKind.Enemy => "considers an enemy",
            VillagerRelationshipKind.FearedEnemy => "fears as an enemy",
            VillagerRelationshipKind.Acquaintance => "knows as an acquaintance",
            _ => "is neutral toward"
        };

    public static VillagerAttractionLevel Attraction(
        string observerId,
        EntityGender observerGender,
        string subjectId,
        EntityGender subjectGender,
        in RelationshipState state)
    {
        if (observerGender == subjectGender ||
            state.Trust < 0 || state.Affection < 5 ||
            state.Resentment >= 15 || state.Fear >= 20)
            return VillagerAttractionLevel.None;
        var score = state.Affection * .6f + state.Trust * .3f +
                    state.Respect * .3f + state.Gratitude * .1f -
                    state.Resentment * .8f - state.Fear * .5f +
                    (Chemistry(observerId, subjectId) - 50) * .25f;
        if (score >= 80) return VillagerAttractionLevel.Devoted;
        if (score >= 50) return VillagerAttractionLevel.Attracted;
        return score >= 25
            ? VillagerAttractionLevel.Interest
            : VillagerAttractionLevel.None;
    }

    public static float AttractionPreferenceAdjustment(
        VillagerAttractionLevel attraction) => attraction switch
    {
        VillagerAttractionLevel.Interest => -24,
        VillagerAttractionLevel.Attracted => -64,
        VillagerAttractionLevel.Devoted => -96,
        _ => 0
    };

    public static VillagerRelationshipTransition Transition(
        string observerId,
        EntityGender observerGender,
        string subjectId,
        EntityGender subjectGender,
        in RelationshipState before,
        in RelationshipState after,
        bool subjectIsLeader = false) =>
        new(
            Classify(before, subjectIsLeader),
            Classify(after, subjectIsLeader),
            Attraction(observerId, observerGender,
                subjectId, subjectGender, before),
            Attraction(observerId, observerGender,
                subjectId, subjectGender, after));

    private static int Chemistry(string observerId, string subjectId)
    {
        uint hash = 2166136261;
        foreach (var character in observerId)
        {
            hash ^= character;
            hash *= 16777619;
        }
        hash ^= '>';
        hash *= 16777619;
        foreach (var character in subjectId)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return (int)(hash % 101);
    }
}
