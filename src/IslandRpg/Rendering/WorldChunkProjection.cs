using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class WorldChunkProjection
{
    public static Vector4 TerrainBounds(
        IReadOnlyList<float> vertices, int stride)
    {
        if (stride < 2 || vertices.Count < 2)
            return Vector4.Zero;
        var minimum = new Vector2(float.MaxValue);
        var maximum = new Vector2(float.MinValue);
        for (var offset = 0;
             offset + 1 < vertices.Count;
             offset += stride)
        {
            var projected = new Vector2(
                vertices[offset], vertices[offset + 1]);
            minimum = Vector2.ComponentMin(minimum, projected);
            maximum = Vector2.ComponentMax(maximum, projected);
        }
        return new(
            minimum.X,
            minimum.Y,
            maximum.X - minimum.X,
            maximum.Y - minimum.Y);
    }

    public static bool IsVisible(
        Vector4 projectedBounds,
        Vector2 camera,
        float zoom,
        Vector2 viewport,
        float padding = 96)
    {
        var left = viewport.X * .5f + camera.X +
                   projectedBounds.X * zoom - padding;
        var top = viewport.Y * .5f + camera.Y +
                  projectedBounds.Y * zoom - padding;
        var right = viewport.X * .5f + camera.X +
                    (projectedBounds.X + projectedBounds.Z) * zoom +
                    padding;
        var bottom = viewport.Y * .5f + camera.Y +
                     (projectedBounds.Y + projectedBounds.W) * zoom +
                     padding;
        return right >= 0 && left <= viewport.X &&
               bottom >= 0 && top <= viewport.Y;
    }
}
