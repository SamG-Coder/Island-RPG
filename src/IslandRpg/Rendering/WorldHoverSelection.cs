namespace IslandRpg.Rendering;

internal static class WorldHoverSelection
{
    public static bool Prefer(float candidateDepth, ref float selectedDepth)
    {
        if (candidateDepth <= selectedDepth)
            return false;
        selectedDepth = candidateDepth;
        return true;
    }

    public static long TileKey(int x, int y) =>
        ((long)x << 32) | (uint)y;
}
