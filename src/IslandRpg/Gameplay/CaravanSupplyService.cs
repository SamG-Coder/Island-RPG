namespace IslandRpg.Gameplay;

internal readonly record struct CaravanSupplyStack(
    string ItemId,
    int Quantity);

internal static class CaravanSupplyService
{
    public static IReadOnlyList<IReadOnlyList<CaravanSupplyStack>> Barrels { get; } =
    [
        [
            new(ItemIds.StoneAxe, 2),
            new(ItemIds.StoneKnife, 2),
            new(ItemIds.StoneHammer, 1),
            new(ItemIds.StonePickaxe, 1),
            new(ItemIds.StoneShovel, 1)
        ],
        [
            new(ItemIds.Logs, 5),
            new(ItemIds.Sticks, 8),
            new(ItemIds.PlantFibres, 8),
            new(ItemIds.LargeRock, 6),
            new(ItemIds.Rope, 2)
        ],
        [
            new(ItemIds.CookedMinnows, 8),
            new(ItemIds.CookedRiverPerch, 4),
            new(ItemIds.CookedSilverHerring, 3)
        ]
    ];
}
