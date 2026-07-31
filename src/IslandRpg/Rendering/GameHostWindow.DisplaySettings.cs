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
        var settings = _saves.LoadSettings();
        LayoutResolutionDropdown(panel, settings);
        if (_resolutionDropdown.TrySelect(
                pointer, out var selectedResolution))
        {
            settings = ApplyResolutionSelection(
                settings, selectedResolution.Id);
            _saves.SaveSettings(settings);
            ApplyDisplaySettings(settings);
            return true;
        }
        if (_settingsMenu.OptionBounds(1).Contains(pointer))
        {
            _resolutionDropdown.Toggle();
            return true;
        }
        if (_resolutionDropdown.IsOpen)
        {
            _resolutionDropdown.Close();
            return true;
        }
        for (var option = 0; option < 5; option++)
        {
            if (option == 1) continue;
            if (!_settingsMenu.ContentList.VisibleIndices.Contains(option) ||
                !_settingsMenu.OptionBounds(option).Contains(pointer))
                continue;
            settings = option switch
            {
                0 => settings with
                {
                    Fullscreen = !settings.Fullscreen
                },
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

    private IslandRpg.Persistence.GameSettings ApplyResolutionSelection(
        IslandRpg.Persistence.GameSettings settings,
        string selection)
    {
        if (selection == "native")
            return settings with
            {
                FullscreenWidth = 0,
                FullscreenHeight = 0
            };
        var parts = selection.Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var width) ||
            !int.TryParse(parts[1], out var height))
            return settings;
        return settings with
        {
            FullscreenWidth = width,
            FullscreenHeight = height
        };
    }

    internal void LayoutResolutionDropdown(
        Vector4 panel,
        IslandRpg.Persistence.GameSettings settings)
    {
        _settingsMenu.LayoutContent(panel);
        _resolutionDropdown.Layout(
            _settingsMenu.OptionBounds(1),
            FullscreenResolutionOptions(),
            SettingsMenuState.ContentBounds(panel));
    }

    internal bool ScrollResolutionDropdown(
        Vector4 panel,
        Vector2 pointer,
        float offset)
    {
        if (_settingsMenu.SelectedTab != SettingsTab.Display ||
            !_resolutionDropdown.IsOpen)
            return false;
        LayoutResolutionDropdown(panel, _saves.LoadSettings());
        return _resolutionDropdown.Scroll(pointer, offset);
    }

    private DropdownOption[] FullscreenResolutionOptions()
    {
        var native = NativeMonitorResolution();
        return
        [
            new(
                "native",
                $"Native ({native.X}x{native.Y})"),
            .. SupportedFullscreenResolutions()
                .Where(mode => mode != native)
                .Select(mode => new DropdownOption(
                    $"{mode.X}x{mode.Y}",
                    $"{mode.X}x{mode.Y}"))
        ];
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
        var settings = _saves.LoadSettings();
        if (_settingsMenu.ContentList.VisibleIndices.Contains(0) &&
            _settingsMenu.OptionBounds(0).Contains(pointer))
        {
            settings = settings with
            {
                OccludedPlayerOutline =
                    !settings.OccludedPlayerOutline
            };
        }
        else if (_settingsMenu.ContentList.VisibleIndices.Contains(1) &&
                 _settingsMenu.OptionBounds(1).Contains(pointer))
        {
            settings = settings with
            {
                UnlimitedZoom = !settings.UnlimitedZoom
            };
            _unlimitedZoomToggle.SetChecked(settings.UnlimitedZoom);
            if (!settings.UnlimitedZoom)
                _targetZoom = Math.Clamp(_targetZoom, .45f, 1.75f);
        }
        else
            return false;
        _saves.SaveSettings(settings);
        ApplyDisplaySettings(settings);
        return true;
    }
}
