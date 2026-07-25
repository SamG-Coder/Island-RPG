using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class WorldFishAnimation
{
    // All six fish graphics use this authored rate in the AoE DAT.
    internal const double SecondsPerFrame = .13;

    public static int FrameAt(WorldFish fish, double realSeconds)
    {
        var frameCount = WorldFishGenerator.FrameCount(
            fish.GraphicName);
        var elapsedFrames = (long)Math.Floor(
            realSeconds / SecondsPerFrame);
        return (int)((elapsedFrames + fish.AnimationOffset) % frameCount);
    }

    public static string AtlasKey(
        WorldFish fish, double realSeconds) =>
        $"{fish.GraphicName}#{FrameAt(fish, realSeconds)}";

    public static Vector2 WorldAt(long seed, WorldFish fish)
    {
        var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
            seed, fish.X, fish.Y);
        return new(
            (fish.X - fish.Y) * 48,
            (fish.X + fish.Y) * 24 - elevation * 20);
    }
}
