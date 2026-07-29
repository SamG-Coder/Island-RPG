using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void RenderPauseMenu()
    {
        switch (_pauseMenu.Page)
        {
            case PausePage.Main:
                var panel = PausePanel();
                DrawAoEPanelBorder(panel);
                DrawCenteredUiText(
                    "GAME PAUSED",
                    new(panel.X, panel.Y + 28, panel.Z, 42),
                    new(232, 217, 166, 255));
                var captions = new[]
                {
                    "Resume", "Settings", "Main Menu", "Quit"
                };
                for (var index = 0; index < captions.Length; index++)
                    DrawMenuButton(PauseButton(index), captions[index]);
                break;
            case PausePage.Settings:
                RenderPauseSettings();
                break;
        }
        if (_pauseMenu.Page != PausePage.Main)
            DrawMenuButton(PauseCloseButtonBounds(), "X");
    }

    private void BlurComposedFrame()
    {
        var width = Math.Max(1, FramebufferSize.X);
        var height = Math.Max(1, FramebufferSize.Y);
        if (_pauseBlurTexture == 0)
        {
            _pauseBlurTexture = GL.GenTexture();
            _pauseBlurIntermediate = GL.GenTexture();
            _pauseBlurFramebuffer = GL.GenFramebuffer();
            ConfigureBlurTexture(_pauseBlurTexture);
            ConfigureBlurTexture(_pauseBlurIntermediate);
        }

        if (_pauseBlurSize.X != width || _pauseBlurSize.Y != height)
        {
            AllocateBlurTexture(_pauseBlurTexture, width, height);
            AllocateBlurTexture(_pauseBlurIntermediate, width, height);
            _pauseBlurSize = new(width, height);
        }

        GL.BindTexture(TextureTarget.Texture2D, _pauseBlurTexture);
        GL.CopyTexSubImage2D(
            TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);

        GL.BindFramebuffer(
            FramebufferTarget.Framebuffer, _pauseBlurFramebuffer);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _pauseBlurIntermediate,
            0);
        GL.Viewport(0, 0, width, height);
        DrawBlurPass(_pauseBlurTexture, new(1f / width, 0));

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, width, height);
        DrawBlurPass(_pauseBlurIntermediate, new(0, 1f / height));

        static void ConfigureBlurTexture(int texture)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
        }

        static void AllocateBlurTexture(int texture, int width, int height)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(
                TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                width, height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
        }

        void DrawBlurPass(int texture, Vector2 direction)
        {
            GL.UseProgram(_pauseBlurProgram);
            GL.Uniform1(
                GL.GetUniformLocation(_pauseBlurProgram, "image"), 0);
            GL.Uniform2(
                GL.GetUniformLocation(_pauseBlurProgram, "direction"),
                direction);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            Draw([
                -1, 1, 0, 1,
                -1,-1, 0, 0,
                 1,-1, 1, 0,
                 1, 1, 1, 1
            ]);
        }
    }

    private void RenderPauseSettings()
    {
        var panel = PauseSubmenuPanel();
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "SETTINGS", new(panel.X, panel.Y + 24, panel.Z, 38),
            new(232, 217, 166, 255));
        RenderSettingsTabs(panel);
        RenderSelectedSettingsTab(panel);
        DrawMenuButton(
            SettingsMenuState.BackButtonBounds(panel), "Back");
    }

    private void RenderSettingsTabs(Vector4 panel)
    {
        var tabs = _settingsMenu.VisibleTabs;
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            var bounds = SettingsMenuState.TabBounds(
                panel, index, tabs.Count);
            DrawMenuButton(bounds, tab.ToString());
            if (tab == _settingsMenu.SelectedTab)
                DrawPanelOutline(bounds, 3, new(.72f, .53f, .19f, 1));
        }
    }

    private void RenderSelectedSettingsTab(Vector4 panel)
    {
        var content = SettingsMenuState.ContentBounds(panel);
        DrawAoEPanelBorder(content);
        _settingsMenu.LayoutContent(panel);
        if (_settingsMenu.SelectedTab != SettingsTab.Display)
            _resolutionDropdown.Close();
        switch (_settingsMenu.SelectedTab)
        {
            case SettingsTab.Display:
                var settings = _saves.LoadSettings();
                LayoutResolutionDropdown(panel, settings);
                foreach (var option in _settingsMenu.ContentList.VisibleIndices)
                {
                    if (option == 1)
                    {
                        RenderResolutionDropdownField(settings);
                        continue;
                    }
                    var caption = option switch
                    {
                        0 => $"Fullscreen: {(settings.Fullscreen ? "On" : "Off")}",
                        2 => $"VSync: {settings.VSyncMode}",
                        3 => "Frame limit: " +
                             DisplaySettingsController.FrameRateLabel(
                                 settings.FrameRateLimit),
                        _ => "Performance metrics: " +
                             $"{(settings.PerformanceMetrics ? "On" : "Off")}"
                    };
                    DrawMenuButton(
                        _settingsMenu.OptionBounds(option), caption);
                }
                break;
            case SettingsTab.Game:
                var gameSettings = _saves.LoadSettings();
                DrawMenuButton(
                    _settingsMenu.OptionBounds(0),
                    "Player outline behind objects: " +
                    (gameSettings.OccludedPlayerOutline
                        ? "On"
                        : "Off"));
                break;
            case SettingsTab.Sound:
                DrawCenteredUiText(
                    "Sound settings will appear here.",
                    _settingsMenu.OptionBounds(0),
                    new(174, 164, 134, 255));
                break;
            case SettingsTab.Dev:
                RenderDeveloperSettings();
                break;
        }
        RenderListScrollbar(_settingsMenu.ContentList);
        if (_settingsMenu.SelectedTab == SettingsTab.Display)
            RenderResolutionDropdownMenu();
    }

    private void RenderResolutionDropdownField(
        IslandRpg.Persistence.GameSettings settings)
    {
        var bounds = _resolutionDropdown.Bounds;
        DrawMenuButton(
            bounds,
            "Resolution: " +
            FullscreenResolutionLabel(settings));
        DrawUiText(
            _resolutionDropdown.IsOpen ? "▲" : "▼",
            new(bounds.X + bounds.Z - 22, bounds.Y + 12),
            new(206, 192, 151, 255));
    }

    private void RenderResolutionDropdownMenu()
    {
        if (!_resolutionDropdown.IsOpen) return;
        var menu = _resolutionDropdown.MenuBounds;
        DrawUiColor(menu, new(.030f, .027f, .021f, .995f));
        DrawPanelOutline(menu, 2, new(.48f, .37f, .16f, 1));
        for (var row = 0;
             row < _resolutionDropdown.VisibleCount;
             row++)
        {
            var index = _resolutionDropdown.FirstVisibleIndex + row;
            var bounds = _resolutionDropdown.OptionBounds(row);
            var hovered = bounds.Contains(MouseState.Position);
            if (hovered)
                DrawUiColor(
                    bounds,
                    new(.19f, .145f, .055f, .99f));
            if (row > 0)
                DrawUiColor(
                    new(bounds.X + 5, bounds.Y,
                        bounds.Z - 10, 1),
                    new(.19f, .16f, .10f, 1));
            DrawCenteredUiText(
                _resolutionDropdown.Options[index].Label,
                bounds,
                hovered
                    ? new(244, 225, 171, 255)
                    : new(205, 195, 160, 255));
        }
    }

    private void RenderDeveloperSettings()
    {
        if (!_settingsMenu.DeveloperModeEnabled) return;
        var list = _settingsMenu.ContentList;
        if (list.VisibleIndices.Contains(0))
        {
            DrawMenuButton(
                DeveloperSettingsController.MultiplierBounds(list),
                $"XP multiplier: x{_developerSettings.ExperienceMultiplier}");
            if (_activePlayer is not null)
                DrawMenuButton(
                    DeveloperSettingsController.MapToolBounds(list),
                    "Open map tool");
        }
        if (list.VisibleIndices.Contains(1) && _activeWorld is not null)
        {
            DrawMenuButton(
                DeveloperSettingsController.AdvanceTimeBounds(list),
                "Advance 12 hours");
            DrawMenuButton(
                DeveloperSettingsController.WorldLevelBounds(list),
                _activeWorldLevel == (int)WorldLevel.Overworld
                    ? "Enter underground"
                    : "Return overworld");
        }
        if (list.VisibleIndices.Contains(2))
            DrawMenuButton(
                DeveloperSettingsController.ItemBankBounds(list),
                "Open all-items bank");
        foreach (var skill in DeveloperSettingsController.Skills)
        {
            if (!list.VisibleIndices.Contains(3 + (int)skill))
                continue;
            var row = DeveloperSettingsController.SkillRowBounds(
                list, skill);
            DrawUiColor(row, new(.055f, .048f, .034f, .96f));
            DrawPanelOutline(row, 1, new(.28f, .23f, .13f, 1));
            var level = DeveloperSettingsController.Level(
                _activePlayer, skill);
            var experience = DeveloperSettingsController.Experience(
                _activePlayer, skill);
            var toNext =
                DeveloperSettingsController.ExperienceToNextLevel(
                    _activePlayer, skill);
            DrawUiText(
                $"{skill}  Lv {level}/20",
                new(row.X + 10, row.Y + 10),
                new(224, 210, 168, 255));
            DrawUiText(
                toNext == 0
                    ? $"{experience} XP  (max level)"
                    : $"{experience} XP  |  {toNext} to next",
                new(row.X + 10, row.Y + 34),
                new(174, 164, 134, 255));
            DrawMenuButton(
                DeveloperSettingsController.GrantBounds(list, skill),
                $"+{_developerSettings.ExperienceGrant}");
            DrawMenuButton(
                DeveloperSettingsController.MaxBounds(list, skill),
                "Max");
        }
    }

    private Vector4 PausePanel() => FrontendPanel(400, 470);

    private Vector4 PauseSubmenuPanel() => SettingsPanel();

    private Vector4 PauseButton(int index)
    {
        var panel = PausePanel();
        return new(panel.X + 48, panel.Y + 98 + index * 62, panel.Z - 96, 48);
    }

    private Vector4 PauseCloseButtonBounds()
    {
        var panel = PauseSubmenuPanel();
        return new(panel.X + panel.Z - 40, panel.Y + 10, 28, 28);
    }

    private Vector4 PauseBackButtonBounds()
    {
        var panel = PauseSubmenuPanel();
        return new(panel.X + panel.Z - 156, panel.Y + panel.W - 92, 108, 48);
    }
}
