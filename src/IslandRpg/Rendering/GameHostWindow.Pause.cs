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
                var soundSettings = _saves.LoadSettings();
                DrawMenuButton(
                    _settingsMenu.OptionBounds(0),
                    "Music: " +
                    (soundSettings.MusicEnabled ? "On" : "Off"));
                RenderMusicVolumeSlider(soundSettings.MasterVolume);
                break;
            case SettingsTab.Dev:
                RenderDeveloperSettings();
                break;
        }
        RenderListScrollbar(_settingsMenu.ContentList);
        if (_settingsMenu.SelectedTab == SettingsTab.Display)
            RenderResolutionDropdownMenu();
    }

    private void RenderMusicVolumeSlider(float persistedVolume)
    {
        if (!_musicVolumeSlider.Pressed)
            _musicVolumeSlider.SetValue(persistedVolume);
        _musicVolumeSlider.Layout(_settingsMenu.OptionBounds(1));
        var row = _musicVolumeSlider.Bounds;
        DrawUiColor(row, new(.055f, .049f, .036f, .96f));
        DrawPanelOutline(row, 0, new(.24f, .20f, .12f, 1));
        DrawUiText(
            $"Music volume  {MathF.Round(_musicVolumeSlider.Value * 100)}%",
            new(row.X + 12, row.Y + 13),
            new(211, 199, 160, 255));
        DrawUiColor(
            new(
                _musicVolumeSlider.TrackBounds.X,
                _musicVolumeSlider.TrackBounds.Y - 2,
                _musicVolumeSlider.TrackBounds.Z,
                _musicVolumeSlider.TrackBounds.W + 4),
            new(.022f, .021f, .018f, 1));
        DrawUiColor(
            _musicVolumeSlider.FillBounds,
            new(.52f, .39f, .12f, 1));
        DrawPanelOutline(
            _musicVolumeSlider.ThumbBounds,
            0,
            _musicVolumeSlider.Pressed ||
            _musicVolumeSlider.Hovered
                ? new(.82f, .66f, .27f, 1)
                : new(.51f, .42f, .22f, 1));
        DrawUiColor(
            new(
                _musicVolumeSlider.ThumbBounds.X + 2,
                _musicVolumeSlider.ThumbBounds.Y + 2,
                _musicVolumeSlider.ThumbBounds.Z - 4,
                _musicVolumeSlider.ThumbBounds.W - 4),
            _musicVolumeSlider.Pressed
                ? new(.58f, .43f, .13f, 1)
                : new(.30f, .26f, .16f, 1));
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
        RenderDeveloperSection(
            list,
            DeveloperSettingsController.ToolsHeaderIndex,
            "WORLD TOOLS",
            "Inspect and manipulate the active test world.");
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.PrimaryToolsIndex))
        {
            if (_activePlayer is not null)
                DrawMenuButton(
                    DeveloperSettingsController.MapToolBounds(list),
                    "Open map tool");
            DrawMenuButton(
                DeveloperSettingsController.ItemBankBounds(list),
                "All-items bank");
        }
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.WorldToolsIndex) &&
            _activeWorld is not null)
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
        RenderDeveloperSection(
            list,
            DeveloperSettingsController.DiagnosticsHeaderIndex,
            "DIAGNOSTICS",
            "Optional overlays for validating world systems.");
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.NavigationBlocksIndex))
        {
            _navigationBlocksToggle.Layout(
                DeveloperSettingsController.NavigationBlocksBounds(list),
                horizontalInset: 0);
            _navigationBlocksToggle.Hovered =
                _navigationBlocksToggle.HitTest(MouseState.Position);
            DrawToggleControl(_navigationBlocksToggle);
        }
        RenderDeveloperSection(
            list,
            DeveloperSettingsController.ProgressionHeaderIndex,
            "PROGRESSION",
            "Grant experience for focused skill testing.");
        foreach (var skill in DeveloperSettingsController.Skills)
        {
            if (!list.VisibleIndices.Contains(
                    DeveloperSettingsController.SkillStartIndex +
                    (int)skill))
                continue;
            var row = DeveloperSettingsController.SkillRowBounds(
                list, skill);
            DrawUiColor(row, new(.055f, .048f, .034f, .96f));
            DrawPanelOutline(row, 1, new(.28f, .23f, .13f, 1));
            DrawSkillIcon(
                skill,
                new(row.X + 8, row.Y + 9, 32, 32));
            var level = DeveloperSettingsController.Level(
                _activePlayer, skill);
            var experience = DeveloperSettingsController.Experience(
                _activePlayer, skill);
            var toNext =
                DeveloperSettingsController.ExperienceToNextLevel(
                    _activePlayer, skill);
            DrawUiText(
                $"{skill}  Lv {level}/20",
                new(row.X + 49, row.Y + 7),
                new(224, 210, 168, 255));
            DrawUiText(
                toNext == 0
                    ? $"{experience} XP  (max level)"
                    : $"{experience} XP  |  {toNext} to next",
                new(row.X + 49, row.Y + 28),
                new(174, 164, 134, 255));
            DrawMenuButton(
                DeveloperSettingsController.MaxBounds(list, skill),
                "Max");
        }
    }

    private void RenderDeveloperSection(
        ListControlState list,
        int index,
        string title,
        string description)
    {
        if (!list.VisibleIndices.Contains(index)) return;
        var row = list.RowBounds(index);
        DrawUiColor(row, new(.047f, .041f, .029f, .72f));
        DrawUiColor(
            new(row.X, row.Y + 5, 3, row.W - 10),
            new(.57f, .43f, .17f, .95f));
        DrawUiText(
            title,
            new(row.X + 13, row.Y + 5),
            new(231, 209, 154, 255));
        DrawUiText(
            description,
            new(row.X + 13, row.Y + 26),
            new(159, 151, 127, 255));
    }

    private void DrawToggleControl(ToggleControlState toggle)
    {
        var bounds = toggle.Bounds;
        DrawRoundedUiColor(
            bounds,
            4,
            toggle.Hovered
                ? new(.090f, .076f, .046f, .98f)
                : new(.059f, .052f, .037f, .98f));
        DrawPanelOutline(
            bounds,
            1,
            toggle.Hovered
                ? new(.48f, .37f, .15f, 1)
                : new(.25f, .22f, .14f, 1));
        DrawUiText(
            toggle.Label,
            new(
                bounds.X + 12,
                bounds.Y + (bounds.W >= 54 ? 8 : 13)),
            new(226, 213, 174, 255));
        if (bounds.W >= 54 &&
            !string.IsNullOrWhiteSpace(toggle.Description))
            DrawUiText(
                toggle.Description,
                new(bounds.X + 12, bounds.Y + 31),
                new(154, 148, 129, 255));

        const float trackWidth = 42;
        const float trackHeight = 20;
        var track = new Vector4(
            bounds.X + bounds.Z - trackWidth - 12,
            bounds.Y + (bounds.W - trackHeight) * .5f,
            trackWidth,
            trackHeight);
        DrawRoundedUiColor(
            track,
            trackHeight * .5f,
            toggle.IsChecked
                ? new(.32f, .38f, .15f, 1)
                : new(.12f, .11f, .09f, 1));
        DrawPanelOutline(track, 1, new(.16f, .13f, .075f, 1));
        var thumbX = toggle.IsChecked
            ? track.X + track.Z - trackHeight * .5f
            : track.X + trackHeight * .5f;
        DrawUiCircle(
            thumbX,
            track.Y + track.W * .5f,
            7,
            toggle.IsChecked
                ? new(.79f, .69f, .36f, 1)
                : new(.45f, .42f, .34f, 1));
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
