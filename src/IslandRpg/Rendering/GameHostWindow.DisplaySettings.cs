using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    internal bool UpdateDisplaySettings(
        Vector2 pointer, Vector4 panel)
    {
        for (var option = 0; option < 4; option++)
        {
            if (!SettingsMenuState.OptionBounds(
                    panel, option).Contains(pointer))
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
        WindowState = settings.Fullscreen
            ? OpenTK.Windowing.Common.WindowState.Fullscreen
            : OpenTK.Windowing.Common.WindowState.Normal;
        DisplaySettingsController.Apply(this, settings);
    }
}
