using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal static class UiGeometry
{
    public static bool Contains(this Vector4 bounds, Vector2 point) =>
        point.X >= bounds.X && point.X < bounds.X + bounds.Z &&
        point.Y >= bounds.Y && point.Y < bounds.Y + bounds.W;
}
