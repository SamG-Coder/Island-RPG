using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class ObserveScenarioService
{
    public const string Default = "default";
    public const string DesertSurplus = "desert-surplus";

    public static bool IsSupported(string value) =>
        value is Default or DesertSurplus;

    public static IReadOnlyList<VillagerState> Configure(
        string scenario,
        long seed,
        IReadOnlyList<VillagerState> villagers,
        int startingFoodCount = 20)
    {
        if (scenario == Default) return villagers;
        if (scenario != DesertSurplus || villagers.Count != 2)
            throw new InvalidOperationException(
                $"Observe scenario '{scenario}' requires exactly two villagers.");

        var positions = FindDesertPair(seed);
        var food = PlayerInventory.CreateStartingInventory();
        startingFoodCount = Math.Clamp(
            startingFoodCount, 0, PlayerInventory.Capacity);
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
                Inventory = food
            },
            villagers[1] with
            {
                PositionX = positions.KnifeHolder.X,
                PositionY = positions.KnifeHolder.Y,
                Inventory = knife,
                Hunger = 70
            }
        ];
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
}
