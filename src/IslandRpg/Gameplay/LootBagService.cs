using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal readonly record struct LootStack(string ItemId, int Quantity);

internal static class LootBagService
{
    public const int Capacity = 12;
    public const double FadeSeconds = 1.25;

    public static bool IsLootBag(string itemId) => itemId == ItemIds.LootBag;

    public static IReadOnlyList<LootStack> Roll(EnemyState enemy, int worldSeed)
    {
        return SlimeCombatRules.RollLoot(new(
                worldSeed,
                enemy.Id,
                enemy.Kind,
                enemy.PowerLevel))
            .Select(drop => new LootStack(drop.ItemId, drop.Quantity))
            .ToArray();
    }

    public static WorldGroundObject Create(
        Guid id, Vector2 position, IReadOnlyList<LootStack> loot)
    {
        var bag = new WorldGroundObject(
            id, ItemIds.LootBag, position.X, position.Y);
        var builder = WorldItemContainerService.OpenForSeeding(bag);
        foreach (var stack in loot)
            builder.TryAdd(stack.ItemId, stack.Quantity);
        return WorldItemContainerService.Save(bag, builder);
    }

    public static ItemContainerState Open(WorldGroundObject bag)
    {
        if (!IsLootBag(bag.ItemId))
            throw new ArgumentException("This object is not a loot bag.", nameof(bag));
        return WorldItemContainerService.Open(bag);
    }

    public static WorldGroundObject Save(
        WorldGroundObject bag, ItemContainerState container)
    {
        if (!IsLootBag(bag.ItemId) || bag.Id != container.Definition.Id)
            throw new ArgumentException(
                "The container does not belong to this loot bag.", nameof(container));
        return WorldItemContainerService.Save(bag, container);
    }

    public static float FadeOpacity(double fadeStartedAt, double now) =>
        1f - Math.Clamp((float)((now - fadeStartedAt) / FadeSeconds), 0f, 1f);

    public static bool FadeFinished(double fadeStartedAt, double now) =>
        now - fadeStartedAt >= FadeSeconds;

    private static void Add(List<LootStack> loot, string itemId, int quantity)
    {
        if (quantity > 0 && ItemCatalog.TryGet(itemId, out _))
            loot.Add(new(itemId, quantity));
    }

    public static string BiomeReagent(EnemyKind kind) =>
        SlimeCombatRules.BiomeReagent(kind);

}
