using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class IsometricTerrainProjection
{
    internal const float TileHalfWidth = 48f;
    internal const float TileHalfHeight = 24f;
    internal const float HeightScale = 20f;

    public static Vector2 Project(float x, float y, float height) =>
        new(
            (x - y) * TileHalfWidth,
            (x + y) * TileHalfHeight - height * HeightScale);

    public static Vector2 Unproject(
        Vector2 projected,
        Func<Vector2, float> sampleHeight,
        int iterations = 3)
    {
        var map = FlatUnproject(projected);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var height = sampleHeight(map);
            map = FlatUnproject(
                new(projected.X, projected.Y + height * HeightScale));
        }
        return map;
    }

    public static Vector2 FlatUnproject(Vector2 projected) => new(
        (projected.Y / TileHalfHeight +
         projected.X / TileHalfWidth) * .5f,
        (projected.Y / TileHalfHeight -
         projected.X / TileHalfWidth) * .5f);

    public static Vector2 UnprojectAtHeight(
        Vector2 projected, float height) =>
        FlatUnproject(
            new(projected.X, projected.Y + height * HeightScale));
}
