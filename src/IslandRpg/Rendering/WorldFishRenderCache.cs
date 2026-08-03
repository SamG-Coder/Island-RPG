using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal readonly record struct WorldFishRenderItem(
    WorldFish Fish, Vector2 World)
{
    // Fish positions drive navigation and range checks in world-grid space.
    // World is the isometric render anchor and must never be mixed with it.
    public Vector2 Grid => new(Fish.X, Fish.Y);
}

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
