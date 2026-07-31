namespace IslandRpg.Gameplay;

internal enum ItemOwnerKind : byte
{
    Unclaimed,
    Character,
    Household,
    Group,
    Settlement
}

internal enum ItemAccess : byte
{
    Private,
    Borrowed,
    Shared,
    Public
}

internal enum OwnershipAcquisition : byte
{
    Unknown,
    Gathered,
    Crafted,
    Gifted,
    Traded,
    Found,
    Stolen
}

internal readonly record struct ItemOwner(ItemOwnerKind Kind, string Id)
{
    public static ItemOwner Unclaimed => new(ItemOwnerKind.Unclaimed, "");
    public bool IsUnclaimed => Kind == ItemOwnerKind.Unclaimed;
    public static ItemOwner Character(string id) =>
        new(ItemOwnerKind.Character, id);
}

internal readonly record struct ItemOwnership(
    ItemOwner Owner,
    ItemAccess Access = ItemAccess.Private,
    OwnershipAcquisition AcquiredBy = OwnershipAcquisition.Unknown,
    ItemOwner PreviousOwner = default,
    double LastTransferGameSeconds = 0)
{
    public static ItemOwnership Unclaimed => new(ItemOwner.Unclaimed);
}

internal enum OwnershipAction : byte
{
    Take,
    Use,
    Deposit,
    Transfer
}

internal enum OwnershipEvidenceKind : byte
{
    Missing,
    Circumstantial,
    Testimony,
    Possession,
    Admission,
    Witnessed
}

internal readonly record struct OwnershipEvidence(
    Guid ItemInstanceId,
    string ObserverId,
    string SuspectId,
    string OwnerId,
    OwnershipEvidenceKind Kind,
    float Confidence,
    double GameSeconds);

internal readonly record struct OwnershipBelief(
    Guid ItemInstanceId,
    string BelievedOwnerId,
    string SuspectedHolderId,
    float Confidence,
    OwnershipEvidenceKind StrongestEvidence,
    double LastUpdatedGameSeconds);

internal readonly record struct RelationshipState(
    float Trust = 0,
    float Affection = 0,
    float Respect = 0,
    float Fear = 0,
    float Gratitude = 0,
    float Resentment = 0)
{
    public RelationshipState Clamp() => new(
        Math.Clamp(Trust, -100, 100),
        Math.Clamp(Affection, -100, 100),
        Math.Clamp(Respect, -100, 100),
        Math.Clamp(Fear, 0, 100),
        Math.Clamp(Gratitude, 0, 100),
        Math.Clamp(Resentment, 0, 100));
}

internal enum OwnershipReaction : byte
{
    None,
    NoticeMissing,
    Question,
    DemandReturn,
    DemandCompensation,
    RefuseAccess,
    WarnCommunity,
    Hostile
}

internal readonly record struct OwnershipIncident(
    Guid ItemInstanceId,
    string ItemId,
    string OwnerId,
    string SuspectId,
    float EvidenceConfidence,
    int ItemValue,
    int PriorOffences,
    bool Returned,
    bool WasEmergency);

internal static class ItemOwnershipService
{
    public static bool IsAuthorized(
        in ItemOwnership ownership,
        string actorId,
        OwnershipAction action,
        ReadOnlySpan<string> sharedMemberships = default)
    {
        if (ownership.Owner.IsUnclaimed ||
            ownership.Access == ItemAccess.Public)
            return true;
        if (ownership.Owner.Kind == ItemOwnerKind.Character)
            return string.Equals(
                       ownership.Owner.Id, actorId,
                       StringComparison.Ordinal) ||
                   ownership.Access == ItemAccess.Borrowed &&
                   action != OwnershipAction.Transfer;
        if (ownership.Access != ItemAccess.Shared) return false;
        for (var index = 0; index < sharedMemberships.Length; index++)
            if (string.Equals(
                    ownership.Owner.Id,
                    sharedMemberships[index],
                    StringComparison.Ordinal))
                return true;
        return false;
    }

    public static ItemOwnership Transfer(
        in ItemOwnership ownership,
        ItemOwner nextOwner,
        OwnershipAcquisition acquisition,
        double gameSeconds,
        ItemAccess access = ItemAccess.Private) =>
        new(
            nextOwner,
            access,
            acquisition,
            ownership.Owner,
            gameSeconds);

    public static OwnershipReaction Assess(
        in OwnershipIncident incident,
        in RelationshipState relationship)
    {
        if (incident.EvidenceConfidence < .25f)
            return OwnershipReaction.NoticeMissing;
        var severity = Math.Max(1, incident.ItemValue) *
                       Math.Clamp(incident.EvidenceConfidence, 0, 1);
        severity += incident.PriorOffences * 12;
        severity += relationship.Resentment * .25f;
        severity -= Math.Max(0, relationship.Trust) * .2f;
        if (incident.Returned) severity *= .3f;
        if (incident.WasEmergency) severity *= .55f;
        return severity switch
        {
            < 5 => OwnershipReaction.Question,
            < 15 => OwnershipReaction.DemandReturn,
            < 30 => OwnershipReaction.DemandCompensation,
            < 50 => OwnershipReaction.RefuseAccess,
            < 75 => OwnershipReaction.WarnCommunity,
            _ => OwnershipReaction.Hostile
        };
    }

    public static RelationshipState ApplyIncident(
        in RelationshipState relationship,
        in OwnershipIncident incident)
    {
        var weight = Math.Clamp(
            incident.EvidenceConfidence *
            (1 + MathF.Log2(Math.Max(1, incident.ItemValue))),
            0, 12);
        if (incident.Returned) weight *= .35f;
        if (incident.WasEmergency) weight *= .6f;
        return (relationship with
        {
            Trust = relationship.Trust - weight * 1.5f,
            Affection = relationship.Affection - weight * .5f,
            Respect = relationship.Respect - weight * .4f,
            Resentment = relationship.Resentment + weight
        }).Clamp();
    }
}

internal sealed class OwnershipKnowledge
{
    private readonly Dictionary<Guid, OwnershipBelief> _beliefs = [];

    public int Count => _beliefs.Count;

    public bool TryGet(Guid itemInstanceId, out OwnershipBelief belief) =>
        _beliefs.TryGetValue(itemInstanceId, out belief);

    public void Observe(in OwnershipEvidence evidence)
    {
        if (evidence.Confidence <= 0) return;
        var confidence = Math.Clamp(evidence.Confidence, 0, 1);
        if (_beliefs.TryGetValue(evidence.ItemInstanceId, out var current) &&
            current.Confidence > confidence &&
            current.LastUpdatedGameSeconds >= evidence.GameSeconds)
            return;
        _beliefs[evidence.ItemInstanceId] = new(
            evidence.ItemInstanceId,
            evidence.OwnerId,
            evidence.SuspectId,
            confidence,
            evidence.Kind,
            evidence.GameSeconds);
    }
}

internal sealed class RelationshipLedger
{
    private readonly Dictionary<(string Observer, string Subject),
        RelationshipState> _states = [];

    public RelationshipState Get(string observerId, string subjectId) =>
        _states.GetValueOrDefault((observerId, subjectId));

    public void Set(
        string observerId,
        string subjectId,
        in RelationshipState state) =>
        _states[(observerId, subjectId)] = state.Clamp();

    public RelationshipState Apply(
        string observerId,
        string subjectId,
        in OwnershipIncident incident)
    {
        var updated = ItemOwnershipService.ApplyIncident(
            Get(observerId, subjectId), incident);
        Set(observerId, subjectId, updated);
        return updated;
    }
}
