using System.Diagnostics;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal enum SettingsTab
{
    Display,
    Game,
    Sound,
    Dev
}

internal sealed class SettingsMenuState
{
    private const float PanelPadding = 24;
    private const float BackButtonWidth = 108;
    private const float BackButtonHeight = 40;
    private bool _developerModeEnabled;

    public SettingsTab SelectedTab { get; private set; } =
        SettingsTab.Display;

    public bool DeveloperModeEnabled =>
        _developerModeEnabled || Debugger.IsAttached;

    public IReadOnlyList<SettingsTab> VisibleTabs =>
        DeveloperModeEnabled
            ? Enum.GetValues<SettingsTab>()
            : [SettingsTab.Display, SettingsTab.Game, SettingsTab.Sound];

    public void EnableDeveloperMode() =>
        _developerModeEnabled = true;

    public void EnsureVisible()
    {
        if (!VisibleTabs.Contains(SelectedTab))
            SelectedTab = SettingsTab.Display;
    }

    public bool SelectAt(Vector4 panel, Vector2 pointer)
    {
        var tabs = VisibleTabs;
        for (var index = 0; index < tabs.Count; index++)
        {
            if (!TabBounds(panel, index, tabs.Count).Contains(pointer))
                continue;
            SelectedTab = tabs[index];
            return true;
        }
        return false;
    }

    public static Vector4 TabBounds(
        Vector4 panel, int index, int tabCount)
    {
        const float gap = 6;
        var width = (panel.Z - 48 - gap * (tabCount - 1)) / tabCount;
        return new(
            panel.X + 24 + index * (width + gap),
            panel.Y + 70,
            width,
            36);
    }

    public static Vector4 ContentBounds(Vector4 panel) =>
        new(panel.X + 24, panel.Y + 118, panel.Z - 48, panel.W - 202);

    public static Vector4 BackButtonBounds(Vector4 panel) =>
        new(
            panel.X + panel.Z - PanelPadding - BackButtonWidth,
            panel.Y + panel.W - PanelPadding - BackButtonHeight,
            BackButtonWidth,
            BackButtonHeight);

    public static Vector4 OptionBounds(Vector4 panel, int index)
    {
        var content = ContentBounds(panel);
        return new(
            content.X + 24,
            content.Y + 22 + index * 58,
            content.Z - 48,
            44);
    }
}
