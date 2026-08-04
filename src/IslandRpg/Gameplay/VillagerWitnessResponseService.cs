namespace IslandRpg.Gameplay;

internal enum VillagerWitnessIntent : byte
{
    Ignore,
    BackAway,
    Warn,
    SeekHelp,
    Protect
}

internal readonly record struct VillagerWitnessDecision(
    VillagerWitnessIntent Intent,
    string Thought,
    int Priority);

/// <summary>
/// Converts witnessed violence into a personality and relationship driven
/// response. World movement and combat remain owned by the NPC controller.
/// </summary>
internal static class VillagerWitnessResponseService
{
    public static VillagerWitnessDecision Decide(
        VillagerState witness,
        VillagerState victim,
        string attackerId,
        bool attackerArmed)
    {
        if (witness.Health <= 0 || victim.Health <= 0 ||
            witness.Id == victim.Id)
            return new(VillagerWitnessIntent.Ignore,
                "I cannot respond to this attack.", 0);

        var victimRelationship = Relationship(witness, victim.Id);
        var attackerRelationship = Relationship(witness, attackerId);
        var victimIsLeader = victim.Id == witness.RecognizedLeaderId;
        var willProtect = VillagerRelationshipClassifier.WillDefend(
            victimRelationship, victimIsLeader);
        var threatened = attackerArmed || witness.Health <= 35 ||
                         attackerRelationship.Fear >= 20;

        if (willProtect && !threatened && witness.Boldness >= .55f)
            return new(VillagerWitnessIntent.Protect,
                $"{victim.Name} matters to me. I will put myself between them.",
                100);
        if (willProtect &&
            (witness.Sociability >= .45f || threatened))
            return new(VillagerWitnessIntent.SeekHelp,
                $"{victim.Name} needs help. I should call the others.", 85);
        if (witness.Boldness < .3f || threatened)
            return new(VillagerWitnessIntent.BackAway,
                "This could turn on me. I should get clear.", 35);
        if (witness.Honesty >= .6f || witness.Boldness >= .5f ||
            witness.Sociability >= .7f)
            return new(VillagerWitnessIntent.Warn,
                "I should object before someone is killed.", 60);
        return new(VillagerWitnessIntent.Ignore,
            "Intervening would put me at risk for someone I barely know.", 10);
    }

    private static RelationshipState Relationship(
        VillagerState witness,
        string characterId) =>
        witness.Relationships?.FirstOrDefault(value =>
            value.CharacterId == characterId)?.State ?? default;
}
