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
        for (var option = 0; option < 5; option++)
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
                1 => CycleFullscreenResolution(settings),
                2 => DisplaySettingsController.CycleVSync(settings),
                3 => DisplaySettingsController.CycleFrameRateLimit(settings),
                4 => settings with
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
        if (settings.Fullscreen)
        {
            var resolution = ResolveFullscreenResolution(settings);
            WindowState =
                OpenTK.Windowing.Common.WindowState.Fullscreen;
            if (ClientSize != resolution)
                ClientSize = resolution;
        }
        else
        {
            WindowState = OpenTK.Windowing.Common.WindowState.Normal;
        }
        DisplaySettingsController.Apply(this, settings);
    }

    internal string FullscreenResolutionLabel(
        IslandRpg.Persistence.GameSettings settings)
    {
        var native = NativeMonitorResolution();
        if (settings.FullscreenWidth <= 0 ||
            settings.FullscreenHeight <= 0 ||
            settings.FullscreenWidth == native.X &&
            settings.FullscreenHeight == native.Y)
            return $"Native ({native.X}x{native.Y})";
        return $"{settings.FullscreenWidth}x{settings.FullscreenHeight}";
    }

    private IslandRpg.Persistence.GameSettings
        CycleFullscreenResolution(
            IslandRpg.Persistence.GameSettings settings)
    {
        var modes = SupportedFullscreenResolutions();
        if (modes.Length == 0) return settings;
        var current = settings.FullscreenWidth <= 0 ||
                      settings.FullscreenHeight <= 0
            ? NativeMonitorResolution()
            : new Vector2i(
                settings.FullscreenWidth,
                settings.FullscreenHeight);
        var index = Array.FindIndex(
            modes, mode => mode == current);
        var next = modes[(Math.Max(-1, index) + 1) % modes.Length];
        return settings with
        {
            FullscreenWidth = next.X,
            FullscreenHeight = next.Y
        };
    }

    private Vector2i ResolveFullscreenResolution(
        IslandRpg.Persistence.GameSettings settings)
    {
        var requested = new Vector2i(
            settings.FullscreenWidth,
            settings.FullscreenHeight);
        return SupportedFullscreenResolutions().Contains(requested)
            ? requested
            : NativeMonitorResolution();
    }

    private Vector2i NativeMonitorResolution()
    {
        var monitor = Monitors.GetMonitorFromWindow(this);
        return new(
            monitor.HorizontalResolution,
            monitor.VerticalResolution);
    }

    private Vector2i[] SupportedFullscreenResolutions()
    {
        var monitor = Monitors.GetMonitorFromWindow(this);
        var native = new Vector2i(
            monitor.HorizontalResolution,
            monitor.VerticalResolution);
        return monitor.SupportedVideoModes
            .Select(mode => new Vector2i(mode.Width, mode.Height))
            .Where(mode =>
                mode.X >= ReferenceWidth &&
                mode.Y >= ReferenceHeight)
            .Append(native)
            .Distinct()
            .OrderBy(mode => (long)mode.X * mode.Y)
            .ThenBy(mode => mode.X)
            .ToArray();
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
