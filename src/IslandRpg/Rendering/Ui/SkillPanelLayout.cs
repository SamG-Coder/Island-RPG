using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal static class SkillPanelLayout
{
    public static Vector4 ListBounds(Vector4 panel) =>
        new(
            panel.X + 12,
            panel.Y + 48,
            panel.Z - 24,
            panel.W - 58);

    public static Vector4 BackButtonBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 48, 64, 27);

    public static Vector4 TitleBounds(Vector4 panel) =>
        new(panel.X + 82, panel.Y + 48, panel.Z - 94, 27);

    public static Vector4 LevelCardBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 82, panel.Z - 24, 61);

    public static Vector4 ProgressBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 150, panel.Z - 24, 35);

    public static Vector4 InformationBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 192, panel.Z - 24, 61);

    public static Vector4 ActionButtonBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 260, panel.Z - 24, 29);
}
