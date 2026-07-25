using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal static class SkillPanelLayout
{
    public static Vector4 ListItemBounds(Vector4 panel, int index) =>
        new(panel.X + 12, panel.Y + 48 + index * 64, panel.Z - 24, 54);

    public static Vector4 BackButtonBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 48, 48, 27);

    public static Vector4 TitleBounds(Vector4 panel) =>
        new(panel.X + 66, panel.Y + 48, panel.Z - 78, 27);

    public static Vector4 LevelCardBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 84, panel.Z - 24, 38);

    public static Vector4 ProgressBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 132, panel.Z - 24, 18);

    public static Vector4 InformationBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 160, panel.Z - 24, 58);

    public static Vector4 ActionButtonBounds(Vector4 panel) =>
        new(panel.X + 12, panel.Y + 230, panel.Z - 24, 38);
}
