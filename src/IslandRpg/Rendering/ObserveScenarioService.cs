using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class ObserveScenarioService
{
    public const string Default = "default";
    public const string DesertSurplus = "desert-surplus";
    public const string DesertKnifeConflict = "desert-knife-conflict";
    public const string IslandResourceTrio = "island-resource-trio";
    public const string IslandFuturesTrio = "island-futures-trio";

    public static bool IsSupported(string value) =>
        value is Default or DesertSurplus or DesertKnifeConflict or
            IslandResourceTrio or IslandFuturesTrio;

    public static int RequiredVillagerCount(string scenario) =>
        scenario switch
        {
            IslandResourceTrio => 3,
            IslandFuturesTrio => 3,
            _ => 2
        };

    public static IReadOnlyList<VillagerState> Configure(
        string scenario,
        long seed,
        IReadOnlyList<VillagerState> villagers,
        int startingFoodCount = 20)
    {
        if (villagers.Count != RequiredVillagerCount(scenario))
            throw new InvalidOperationException(
                $"Observe scenario '{scenario}' requires " +
                $"{RequiredVillagerCount(scenario)} villagers.");
        if (scenario == Default) return villagers;
        if (scenario == IslandResourceTrio)
            return ConfigureIslandResourceTrio(seed, villagers);
        if (scenario == IslandFuturesTrio)
            return ConfigureIslandFuturesTrio(seed, villagers);
        if (scenario is not (DesertSurplus or DesertKnifeConflict) ||
            villagers.Count != 2)
            throw new InvalidOperationException(
                $"Observe scenario '{scenario}' requires exactly two villagers.");

        var positions = FindDesertPair(seed);
        var food = PlayerInventory.CreateStartingInventory();
        startingFoodCount = scenario == DesertKnifeConflict
            ? 2
            : Math.Clamp(startingFoodCount, 0, PlayerInventory.Capacity);
        for (var slot = 0; slot < startingFoodCount; slot++)
            food[slot] = ItemIds.CookedMinnows;
        var knife = PlayerInventory.CreateStartingInventory();
        knife[0] = ItemIds.StoneKnife;
        return
        [
            villagers[0] with
            {
                PositionX = positions.FoodHolder.X,
                PositionY = positions.FoodHolder.Y,
                Inventory = food,
                Hunger = scenario == DesertKnifeConflict
                    ? 35 : villagers[0].Hunger,
                Boldness = scenario == DesertKnifeConflict
                    ? .62f : villagers[0].Boldness
            },
            villagers[1] with
            {
                PositionX = positions.KnifeHolder.X,
                PositionY = positions.KnifeHolder.Y,
                Inventory = knife,
                Hunger = scenario == DesertKnifeConflict ? 8 : 70,
                Boldness = scenario == DesertKnifeConflict
                    ? .82f : villagers[1].Boldness
            }
        ];
    }

    private static IReadOnlyList<VillagerState> ConfigureIslandFuturesTrio(
        long seed,
        IReadOnlyList<VillagerState> villagers)
    {
        var positions = FindIslandTrio(seed);
        var farmer = PlayerInventory.CreateStartingInventory();
        farmer[0] = ItemIds.GatheringBasket;
        farmer[1] = ItemIds.WildGrainSeeds;
        farmer[2] = ItemIds.BeanSeeds;
        farmer[3] = ItemIds.RootSeeds;
        farmer[4] = ItemIds.CookedMinnows;
        farmer[5] = ItemIds.CookedMinnows;
        var woodworker = PlayerInventory.CreateStartingInventory();
        woodworker[0] = ItemIds.StoneAxe;
        var crafter = PlayerInventory.CreateStartingInventory();
        crafter[0] = ItemIds.StoneKnife;
        crafter[1] = ItemIds.StoneHammer;
        crafter[2] = ItemIds.StonePickaxe;
        crafter[3] = ItemIds.StoneShovel;
        crafter[4] = ItemIds.PortableTorch;
        return
        [
            villagers[0] with
            {
                PositionX = positions.FoodHolder.X,
                PositionY = positions.FoodHolder.Y,
                Inventory = farmer,
                Hunger = 52,
                Boldness = .35f
            },
            villagers[1] with
            {
                PositionX = positions.AxeHolder.X,
                PositionY = positions.AxeHolder.Y,
                Inventory = woodworker,
                Hunger = 47,
                Boldness = .55f
            },
            villagers[2] with
            {
                PositionX = positions.KnifeHolder.X,
                PositionY = positions.KnifeHolder.Y,
                Inventory = crafter,
                Hunger = 42,
                Boldness = .7f
            }
        ];
    }

    private static IReadOnlyList<VillagerState> ConfigureIslandResourceTrio(
        long seed,
        IReadOnlyList<VillagerState> villagers)
    {
        var positions = FindIslandTrio(seed);
        var food = PlayerInventory.CreateStartingInventory();
        food[0] = ItemIds.CookedMinnows;
        food[1] = ItemIds.CookedMinnows;
        var axe = PlayerInventory.CreateStartingInventory();
        axe[0] = ItemIds.StoneAxe;
        var knife = PlayerInventory.CreateStartingInventory();
        knife[0] = ItemIds.StoneKnife;
        return
        [
            villagers[0] with
            {
                PositionX = positions.FoodHolder.X,
                PositionY = positions.FoodHolder.Y,
                Inventory = food
            },
            villagers[1] with
            {
                PositionX = positions.AxeHolder.X,
                PositionY = positions.AxeHolder.Y,
                Inventory = axe
            },
            villagers[2] with
            {
                PositionX = positions.KnifeHolder.X,
                PositionY = positions.KnifeHolder.Y,
                Inventory = knife
            }
        ];
    }

    public static (Vector2 FoodHolder, Vector2 AxeHolder, Vector2 KnifeHolder)
        FindIslandTrio(long seed, int maximumRadius = 2048)
    {
        for (var radius = 0; radius <= maximumRadius; radius += 8)
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius) continue;
            if (!IsIslandLand(seed, x, y) ||
                !IsIslandLand(seed, x + 1, y) ||
                !IsIslandLand(seed, x, y + 1))
                continue;
            return (
                new(x + .5f, y + .5f),
                new(x + 1.5f, y + .5f),
                new(x + .5f, y + 1.5f));
        }
        throw new InvalidOperationException(
            $"No walkable island trio was found within {maximumRadius} tiles.");
    }


    public static (Vector2 FoodHolder, Vector2 KnifeHolder)
        FindDesertPair(long seed, int maximumRadius = 2048)
    {
        const int sampleStep = 8;
        for (var radius = 0; radius <= maximumRadius; radius += sampleStep)
        for (var y = -radius; y <= radius; y += sampleStep)
        for (var x = -radius; x <= radius; x += sampleStep)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                continue;
            var sample = new Vector2(x, y);
            if (!IsDesert(seed, sample)) continue;
            for (var localY = -sampleStep; localY <= sampleStep; localY++)
            for (var localX = -sampleStep; localX <= sampleStep; localX++)
            {
                var tile = sample + new Vector2(localX, localY);
                if (!IsDesert(seed, tile)) continue;
                foreach (var offset in new[]
                         {
                             Vector2.UnitX, -Vector2.UnitX,
                             Vector2.UnitY, -Vector2.UnitY
                         })
                {
                    var other = tile + offset;
                    if (IsDesert(seed, other))
                        return (tile + new Vector2(.5f),
                            other + new Vector2(.5f));
                }
            }
        }
        throw new InvalidOperationException(
            $"No adjacent desert spawn was found within {maximumRadius} tiles.");
    }

    private static bool IsDesert(long seed, Vector2 position) =>
        InfiniteWorldGenerator.BiomeAt(
            seed,
            (int)position.X,
            (int)position.Y) is Biome.DesertSand or Biome.CrackedEarth;

    private static bool IsIslandLand(long seed, int x, int y) =>
        WorldLevelNavigation.IsWalkable(
            seed, x, y, (int)WorldLevel.Overworld);
}
