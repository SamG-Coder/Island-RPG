using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool _occludedPlayerOutlineEnabled = true;

    internal bool UpdateDisplaySettings(
        Vector2 pointer, Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        for (var option = 0; option < 4; option++)
        {
            if (!_settingsMenu.ContentList.VisibleIndices.Contains(option) ||
                !_settingsMenu.OptionBounds(option).Contains(pointer))
                continue;
            var settings = _saves.LoadSettings();
            settings = option switch
            {
                0 => settings with
                {
                    Fullscreen = !settings.Fullscreen
                },
                1 => DisplaySettingsController.CycleVSync(settings),
                2 => DisplaySettingsController.CycleFrameRateLimit(settings),
                3 => settings with
                {
                    PerformanceMetrics = !settings.PerformanceMetrics
                },
                _ => settings
            };
            _saves.SaveSettings(settings);
            ApplyDisplaySettings(settings);
            return true;
        }
        return false;
    }

    private void ApplyDisplaySettings(
        IslandRpg.Persistence.GameSettings settings)
    {
        _performanceMetricsEnabled = settings.PerformanceMetrics;
        _occludedPlayerOutlineEnabled =
            settings.OccludedPlayerOutline;
        WindowState = settings.Fullscreen
            ? OpenTK.Windowing.Common.WindowState.Fullscreen
            : OpenTK.Windowing.Common.WindowState.Normal;
        DisplaySettingsController.Apply(this, settings);
    }

    internal bool UpdateGameSettings(Vector2 pointer, Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        if (!_settingsMenu.ContentList.VisibleIndices.Contains(0) ||
            !_settingsMenu.OptionBounds(0).Contains(pointer))
            return false;
        var settings = _saves.LoadSettings();
        settings = settings with
        {
            OccludedPlayerOutline =
                !settings.OccludedPlayerOutline
        };
        _saves.SaveSettings(settings);
        ApplyDisplaySettings(settings);
        return true;
    }
}
