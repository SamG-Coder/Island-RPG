using IslandRpg.World;

namespace IslandRpg.Rendering;

internal static class WorldLighting
{
    public static float Darkness(float daylight, int worldLevel) =>
        worldLevel == (int)WorldLevel.Underground
            ? 1f
            : Math.Clamp(1f - daylight, 0f, 1f);
}
