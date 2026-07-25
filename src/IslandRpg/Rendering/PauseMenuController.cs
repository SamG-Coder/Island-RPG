using IslandRpg.Rendering.Ui;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal enum PausePage
{
    Main,
    Settings
}

internal sealed partial class GameHostWindow
{
    private sealed class PauseMenuController(GameHostWindow window)
    {
        private bool _leftWasDown;

        public bool IsPaused { get; private set; }
        public PausePage Page { get; private set; }

        public void HandleEscapeKey()
        {
            if (IsPaused && Page != PausePage.Main)
                Page = PausePage.Main;
            else
                SetPaused(!IsPaused);
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            if (paused)
                window._modalScreen.Open(ModalScreenKind.Pause);
            else
                window._modalScreen.Close(ModalScreenKind.Pause);
            Page = PausePage.Main;
            _leftWasDown =
                window.MouseState.IsButtonDown(MouseButton.Left);
            if (paused)
            {
                window._chatUi.BlurInput();
                window._inventoryContext.Close();
                window.UseDefaultGameCursor();
            }
            else if (window._defaultNativeCursor is not null)
                window.Cursor = window._defaultNativeCursor;
        }

        public void Update()
        {
            var leftDown =
                window.MouseState.IsButtonDown(MouseButton.Left);
            var clicked = leftDown && !_leftWasDown;
            _leftWasDown = leftDown;
            if (!clicked) return;

            var pointer = window.MouseState.Position;
            if (Page != PausePage.Main &&
                window.PauseCloseButtonBounds().Contains(pointer))
            {
                Page = PausePage.Main;
                return;
            }

            if (Page == PausePage.Main)
            {
                if (window.PauseButton(0).Contains(pointer))
                    SetPaused(false);
                else if (window.PauseButton(1).Contains(pointer))
                {
                    Page = PausePage.Settings;
                    window._settingsMenu.EnsureVisible();
                }
                else if (window.PauseButton(2).Contains(pointer))
                    window.ReturnToMainMenu();
                else if (window.PauseButton(3).Contains(pointer))
                    window.Close();
                return;
            }

            var panel = window.PauseSubmenuPanel();
            if (window._settingsMenu.SelectAt(panel, pointer))
                return;
            if (window._settingsMenu.SelectedTab == SettingsTab.Display &&
                SettingsMenuState.OptionBounds(
                    panel, 0).Contains(pointer))
            {
                var settings = window._saves.LoadSettings();
                var fullscreen = !settings.Fullscreen;
                window._saves.SaveSettings(
                    settings with { Fullscreen = fullscreen });
                window.WindowState = fullscreen
                    ? WindowState.Fullscreen
                    : WindowState.Normal;
            }
            else if (window._settingsMenu.SelectedTab == SettingsTab.Dev &&
                     window.UpdateDeveloperSettings(pointer, panel))
                return;
            else if (SettingsMenuState.BackButtonBounds(
                         panel).Contains(pointer))
                Page = PausePage.Main;
        }
    }
}
