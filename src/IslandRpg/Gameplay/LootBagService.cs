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
        var random = new Random(HashCode.Combine(
            worldSeed, enemy.Id, enemy.Kind, enemy.PowerLevel));
        var drops = new List<LootStack>(3);
        Add(drops, ItemIds.SlimeGel, 1 + random.Next(2));
        if (random.NextDouble() < .38)
            Add(drops, BiomeReagent(enemy.Kind), 1);
        if (random.NextDouble() < .08 + enemy.PowerLevel * .01)
            Add(drops, ItemIds.SlimeCore, 1);
        return drops;
    }

    public static WorldGroundObject Create(
        Guid id, Vector2 position, IReadOnlyList<LootStack> loot)
    {
        var builder = new ItemContainerState(Definition(
            id, ItemContainerAccess.DepositAndWithdraw));
        foreach (var stack in loot)
            builder.TryAdd(stack.ItemId, stack.Quantity);
        var saved = builder.Save();
        return new(
            id, ItemIds.LootBag, position.X, position.Y,
            Container: new(saved.Items, saved.Quantities, saved.OwnerIds));
    }

    public static ItemContainerState Open(WorldGroundObject bag)
    {
        if (!IsLootBag(bag.ItemId))
            throw new ArgumentException("This object is not a loot bag.", nameof(bag));
        var contents = bag.Container ?? new(
            new string?[Capacity], new int[Capacity]);
        return new(Definition(bag.Id, ItemContainerAccess.WithdrawOnly), new(
            bag.Id, contents.Items, contents.Quantities, contents.OwnerIds));
    }

    public static WorldGroundObject Save(
        WorldGroundObject bag, ItemContainerState container)
    {
        if (!IsLootBag(bag.ItemId) || bag.Id != container.Definition.Id)
            throw new ArgumentException(
                "The container does not belong to this loot bag.", nameof(container));
        var saved = container.Save();
        return bag with
        {
            Container = new(saved.Items, saved.Quantities, saved.OwnerIds)
        };
    }

    public static float FadeOpacity(double fadeStartedAt, double now) =>
        1f - Math.Clamp((float)((now - fadeStartedAt) / FadeSeconds), 0f, 1f);

    public static bool FadeFinished(double fadeStartedAt, double now) =>
        now - fadeStartedAt >= FadeSeconds;

    private static ItemContainerDefinition Definition(
        Guid id, ItemContainerAccess access) => new(
        id, "Loot", 4, 3,
        ShowPlayerInventory: true,
        AllowStacking: true,
        ShowTransferAllButton: false,
        Access: access);

    private static void Add(List<LootStack> loot, string itemId, int quantity)
    {
        if (quantity > 0 && ItemCatalog.TryGet(itemId, out _))
            loot.Add(new(itemId, quantity));
    }

    public static string BiomeReagent(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => ItemIds.SaltCrystals,
        EnemyKind.GrassSlime => ItemIds.MedicinalHerbs,
        EnemyKind.SandSlime => ItemIds.SaltCrystals,
        EnemyKind.CaveSlime => ItemIds.Coal,
        _ => ItemIds.SlimeGel
    };

}

internal static class WorldItemContainerService
{
    public static bool IsContainer(string itemId) =>
        StorageContainerService.IsStorage(itemId) ||
        LootBagService.IsLootBag(itemId);

    public static ItemContainerState Open(WorldGroundObject value) =>
        LootBagService.IsLootBag(value.ItemId)
            ? LootBagService.Open(value)
            : StorageContainerService.Open(value);

    public static WorldGroundObject Save(
        WorldGroundObject value, ItemContainerState container) =>
        LootBagService.IsLootBag(value.ItemId)
            ? LootBagService.Save(value, container)
            : StorageContainerService.Save(value, container);
}
