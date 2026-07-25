using IslandRpg.Rendering.Ui;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void SetPaused(bool paused)
    {
        _paused = paused;
        _pausePage = PausePage.Main;
        _pauseLeftWasDown = MouseState.IsButtonDown(MouseButton.Left);
        if (paused)
        {
            _chatUi.BlurInput();
            _inventoryContext.Close();
            Cursor = MouseCursor.Default;
            _gameCursorKind = GameCursorKind.Default;
        }
        else if (_defaultNativeCursor is not null)
            Cursor = _defaultNativeCursor;
    }

    private void UpdatePauseMenu()
    {
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        var clicked = leftDown && !_pauseLeftWasDown;
        _pauseLeftWasDown = leftDown;
        if (!clicked) return;

        var pointer = MouseState.Position;
        if (_pausePage != PausePage.Main &&
            PauseCloseButtonBounds().Contains(pointer))
        {
            _pausePage = PausePage.Main;
            return;
        }

        switch (_pausePage)
        {
            case PausePage.Main:
                if (PauseButton(0).Contains(pointer))
                    SetPaused(false);
                else if (PauseButton(1).Contains(pointer))
                    _pausePage = PausePage.Settings;
                else if (PauseButton(2).Contains(pointer))
                    _pausePage = PausePage.Debug;
                else if (PauseButton(3).Contains(pointer))
                    ReturnToMainMenu();
                else if (PauseButton(4).Contains(pointer))
                    Close();
                break;
            case PausePage.Settings:
                if (PauseSettingsToggleBounds().Contains(pointer))
                {
                    var settings = _saves.LoadSettings();
                    var fullscreen = !settings.Fullscreen;
                    _saves.SaveSettings(settings with { Fullscreen = fullscreen });
                    WindowState = fullscreen
                        ? WindowState.Fullscreen
                        : WindowState.Normal;
                }
                else if (PauseBackButtonBounds().Contains(pointer))
                    _pausePage = PausePage.Main;
                break;
            case PausePage.Debug:
                if (PauseBackButtonBounds().Contains(pointer))
                    _pausePage = PausePage.Main;
                break;
        }
    }

    private void RenderPauseMenu()
    {
        switch (_pausePage)
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
                    "Resume", "Settings", "Debug Menu", "Main Menu", "Quit"
                };
                for (var index = 0; index < captions.Length; index++)
                    DrawMenuButton(PauseButton(index), captions[index]);
                break;
            case PausePage.Settings:
                RenderPauseSettings();
                break;
            case PausePage.Debug:
                RenderDebugMenu();
                break;
        }
        if (_pausePage != PausePage.Main)
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
        var fullscreen = _saves.LoadSettings().Fullscreen;
        DrawMenuButton(
            PauseSettingsToggleBounds(),
            $"Fullscreen: {(fullscreen ? "On" : "Off")}");
        DrawCenteredUiText(
            "The game remains paused while settings are open.",
            new(panel.X + 20, panel.Y + 190, panel.Z - 40, 28),
            new(174, 164, 134, 255));
        DrawMenuButton(PauseBackButtonBounds(), "Back");
    }

    private void RenderDebugMenu()
    {
        var panel = PauseSubmenuPanel();
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "DEBUG MENU", new(panel.X, panel.Y + 24, panel.Z, 38),
            new(232, 217, 166, 255));
        var position = _player?.Position ?? Vector2.Zero;
        var lines = new[]
        {
            $"World seed: {_worldSeed}",
            $"Player: {position.X:0.00}, {position.Y:0.00}",
            $"Loaded chunks: {_worldChunks.Count}",
            $"Path job: {(_pendingPathTask is null ? "idle" : "active")}"
        };
        for (var index = 0; index < lines.Length; index++)
            DrawUiText(
                lines[index],
                new(panel.X + 54, panel.Y + 100 + index * 32),
                new(204, 190, 150, 255));
        DrawMenuButton(PauseBackButtonBounds(), "Back");
    }

    private Vector4 PausePanel() => FrontendPanel(400, 470);

    private Vector4 PauseSubmenuPanel() => FrontendPanel(480, 360);

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

    private Vector4 PauseSettingsToggleBounds()
    {
        var panel = PauseSubmenuPanel();
        return new(panel.X + 60, panel.Y + 104, panel.Z - 120, 50);
    }

    private Vector4 PauseBackButtonBounds()
    {
        var panel = PauseSubmenuPanel();
        return new(panel.X + panel.Z - 156, panel.Y + panel.W - 92, 108, 48);
    }
}
