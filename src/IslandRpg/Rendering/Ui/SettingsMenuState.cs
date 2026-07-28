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
    private static readonly SettingsTab[] StandardTabs =
        [SettingsTab.Display, SettingsTab.Game, SettingsTab.Sound];
    private static readonly SettingsTab[] AllTabs =
        Enum.GetValues<SettingsTab>();
    private static readonly string[] DisplayItems =
        ["fullscreen", "vsync", "frame-limit", "metrics"];
    private static readonly string[] GameItems = ["game-placeholder"];
    private static readonly string[] SoundItems = ["sound-placeholder"];
    private static readonly string[] DeveloperItems =
    [
        "developer-tools-primary",
        "developer-tools-world",
        "developer-tools-items",
        .. DeveloperSettingsController.Skills
            .Select(skill => $"skill-{skill}")
    ];

    public SettingsTab SelectedTab { get; private set; } =
        SettingsTab.Display;
    public ListControlState ContentList { get; } = new();

    public bool DeveloperModeEnabled =>
        _developerModeEnabled || Debugger.IsAttached;

    public IReadOnlyList<SettingsTab> VisibleTabs =>
        DeveloperModeEnabled
            ? AllTabs
            : StandardTabs;

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
            if (SelectedTab != tabs[index])
                ContentList.ScrollToIndex(0);
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

    public void LayoutContent(Vector4 panel)
    {
        var content = ContentBounds(panel);
        var items = SelectedTab switch
        {
            SettingsTab.Display => DisplayItems,
            SettingsTab.Game => GameItems,
            SettingsTab.Sound => SoundItems,
            _ => DeveloperItems
        };
        ContentList.Layout(
            new(
                content.X + 14,
                content.Y + 14,
                content.Z - 28,
                content.W - 28),
            items,
            rowHeight: SelectedTab == SettingsTab.Dev ? 62 : 44,
            rowGap: SelectedTab == SettingsTab.Dev ? 8 : 14,
            deleteWidth: 0,
            actionGap: 0);
    }

    public static Vector4 BackButtonBounds(Vector4 panel) =>
        new(
            panel.X + panel.Z - PanelPadding - BackButtonWidth,
            panel.Y + panel.W - PanelPadding - BackButtonHeight,
            BackButtonWidth,
            BackButtonHeight);

    public Vector4 OptionBounds(int index) =>
        ContentList.RowBounds(index);
}
