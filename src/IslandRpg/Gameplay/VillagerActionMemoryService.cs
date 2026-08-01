namespace IslandRpg.Gameplay;

internal static class VillagerActionMemoryService
{
    public const string SkillActionKind = "skill-action";

    public static VillagerState RecordResourceStrike(
        VillagerState villager,
        string skill,
        string targetId,
        string targetName,
        ResourceStrikeResult strike,
        double gameSeconds)
    {
        if (villager.Health <= 0) return villager;
        var memories = villager.Memories?.ToList() ?? [];
        var subjectId = $"{skill}:{targetId}";
        var index = memories.FindIndex(memory =>
            memory.Kind == SkillActionKind &&
            memory.SubjectId.Equals(subjectId, StringComparison.Ordinal));
        var skillName = char.ToUpperInvariant(skill[0]) + skill[1..];
        var summary = strike.Hit
            ? $"I used {skillName.ToLowerInvariant()} on {targetName}; " +
              $"dealt {strike.Damage} damage, gained " +
              $"{strike.Experience.Gained} XP, and reached " +
              $"level {strike.Experience.Level}." +
              (strike.Depleted ? " The resource was depleted." : "")
            : $"I missed {targetName} while using " +
              $"{skillName.ToLowerInvariant()} at level " +
              $"{strike.Experience.Level}.";
        var memory = new VillagerMemory(
            index >= 0 ? memories[index].EventId : Guid.NewGuid(),
            SkillActionKind,
            subjectId,
            null,
            strike.Hit ? 1 : .85f,
            gameSeconds,
            strike.Hit ? 2 : -1,
            summary,
            ObservedValue: strike.Experience.Experience);
        if (index >= 0) memories[index] = memory;
        else memories.Add(memory);
        if (memories.Count > VillagerSimulation.MaximumMemories)
            memories.RemoveRange(
                0, memories.Count - VillagerSimulation.MaximumMemories);
        return villager with { Memories = memories };
    }
}
