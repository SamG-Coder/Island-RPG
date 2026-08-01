namespace IslandRpg.Gameplay;

/// <summary>
/// Converts actor-specific capability memories into a threat assessment.
/// NPCs never inspect another actor's inventory through this service.
/// </summary>
internal static class VillagerWeaponAwareness
{
    public static ItemDefinition? BestKnownKnife(
        VillagerState observer, string subjectId) =>
        VillagerCapabilityMemory.KnownTools(observer, subjectId)
            .Select(itemId => ItemCatalog.TryGet(itemId, out var item)
                ? item : null)
            .Where(item => item is not null &&
                           item.HasTag(ItemTag.Weapon) &&
                           item.HasTag(ItemTag.Knife))
            .OrderByDescending(item => item!.KnifePower)
            .FirstOrDefault();

    public static int KnownKnifePower(
        VillagerState observer, string subjectId) =>
        BestKnownKnife(observer, subjectId)?.KnifePower ?? 0;

    public static int RiskBonus(
        VillagerState observer, string subjectId) =>
        Math.Min(30, KnownKnifePower(observer, subjectId) * 10);
}
