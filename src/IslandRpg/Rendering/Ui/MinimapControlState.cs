using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class MinimapControlState : ControlState
{
    public const int Diameter = 160;

    public void Layout(Vector4 viewport)
    {
        Bounds = new(
            Math.Max(viewport.X, viewport.X + viewport.Z - Diameter - 12),
            viewport.Y + 12,
            Diameter,
            Diameter);
    }
}
