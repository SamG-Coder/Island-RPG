using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal readonly record struct WorldFishRenderItem(
    WorldFish Fish, Vector2 World);

internal static class WorldFishRenderCache
{
    public static WorldFishRenderItem[] Build(
        long seed, IReadOnlyList<WorldFish> fish)
    {
        var result = new WorldFishRenderItem[fish.Count];
        for (var index = 0; index < fish.Count; index++)
            result[index] = new(
                fish[index],
                WorldFishAnimation.WorldAt(seed, fish[index]));
        return result;
    }
}
