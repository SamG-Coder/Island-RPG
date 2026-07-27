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
        switch (_settingsMenu.SelectedTab)
        {
            case SettingsTab.Display:
                var settings = _saves.LoadSettings();
                DrawMenuButton(
                    SettingsMenuState.OptionBounds(panel, 0),
                    $"Fullscreen: {(settings.Fullscreen ? "On" : "Off")}");
                DrawMenuButton(
                    SettingsMenuState.OptionBounds(panel, 1),
                    $"VSync: {settings.VSyncMode}");
                DrawMenuButton(
                    SettingsMenuState.OptionBounds(panel, 2),
                    "Frame limit: " +
                    DisplaySettingsController.FrameRateLabel(
                        settings.FrameRateLimit));
                DrawMenuButton(
                    SettingsMenuState.OptionBounds(panel, 3),
                    "Performance metrics: " +
                    $"{(settings.PerformanceMetrics ? "On" : "Off")}");
                break;
            case SettingsTab.Game:
                DrawCenteredUiText(
                    "Gameplay settings will appear here.",
                    content, new(174, 164, 134, 255));
                break;
            case SettingsTab.Sound:
                DrawCenteredUiText(
                    "Sound settings will appear here.",
                    content, new(174, 164, 134, 255));
                break;
            case SettingsTab.Dev:
                RenderDeveloperSettings(panel);
                break;
        }
    }

    private void RenderDeveloperSettings(Vector4 panel)
    {
        if (!_settingsMenu.DeveloperModeEnabled) return;
        DrawMenuButton(
            DeveloperSettingsController.MultiplierBounds(panel),
            $"XP multiplier: x{_developerSettings.ExperienceMultiplier}");
        if (_activePlayer is not null)
            DrawMenuButton(
                DeveloperSettingsController.MapToolBounds(panel),
                "Map tool");
        if (_activeWorld is not null)
        {
            DrawMenuButton(
                DeveloperSettingsController.AdvanceTimeBounds(panel),
                "+12 Hours");
            DrawMenuButton(
                DeveloperSettingsController.WorldLevelBounds(panel),
                _activeWorldLevel == (int)WorldLevel.Overworld
                    ? "Enter underground"
                    : "Return overworld");
        }
        foreach (var skill in Enum.GetValues<SkillType>())
        {
            var row = DeveloperSettingsController.SkillRowBounds(
                panel, skill);
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
                DeveloperSettingsController.GrantBounds(panel, skill),
                $"+{_developerSettings.ExperienceGrant}");
            DrawMenuButton(
                DeveloperSettingsController.MaxBounds(panel, skill),
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
