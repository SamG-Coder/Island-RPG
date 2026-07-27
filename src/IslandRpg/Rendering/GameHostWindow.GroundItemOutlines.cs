using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private static readonly Vector3 GroundItemOutlineColor =
        Vector3.One;

    private bool ShouldRenderGroundItemOutlines() =>
        KeyboardState.IsKeyDown(Keys.LeftAlt) ||
        KeyboardState.IsKeyDown(Keys.RightAlt);

    private void AddGroundItemOutline(
        string atlasKey,
        Vector2 world,
        float opacity,
        List<float> vertices) =>
        AddAtlasQuad(atlasKey, world, opacity, vertices);

    private void DrawGroundItemOutlines(List<float> vertices) =>
        DrawTreeOutlineBatch(vertices, GroundItemOutlineColor);
}
