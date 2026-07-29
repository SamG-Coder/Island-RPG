using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal static class SceneCoordinateMapper
{
    public static Vector2 ClientToScene(
        Vector2 clientPosition,
        Vector2i clientSize,
        Vector2i framebufferSize,
        Vector2i logicalSceneSize)
    {
        var clientWidth = Math.Max(1, clientSize.X);
        var clientHeight = Math.Max(1, clientSize.Y);
        var framebufferWidth = Math.Max(1, framebufferSize.X);
        var framebufferHeight = Math.Max(1, framebufferSize.Y);
        var logicalWidth = Math.Max(1, logicalSceneSize.X);
        var logicalHeight = Math.Max(1, logicalSceneSize.Y);

        var framebufferPosition = new Vector2(
            clientPosition.X * framebufferWidth / clientWidth,
            clientPosition.Y * framebufferHeight / clientHeight);
        var scale = Math.Min(
            framebufferWidth / (float)logicalWidth,
            framebufferHeight / (float)logicalHeight);
        var outputWidth = logicalWidth * scale;
        var outputHeight = logicalHeight * scale;
        var left = (framebufferWidth - outputWidth) * .5f;
        var top = (framebufferHeight - outputHeight) * .5f;
        return new(
            (framebufferPosition.X - left) /
            Math.Max(scale, .001f),
            (framebufferPosition.Y - top) /
            Math.Max(scale, .001f));
    }
}
