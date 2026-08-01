using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void UpdateObserveUi()
    {
        var scene = SceneClientBounds();
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        UpdateCommandHints();
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        _chatUi.UpdatePointer(MouseState.Position, leftDown);
        if (_commandHints.UpdatePointer(
                MouseState.Position, leftDown) is { } hint)
            CompleteCommandHint(hint);
        UpdateChatCommandInput();
    }

    private void UpdateChatCommandInput()
    {
        if (KeyboardState.IsKeyPressed(Keys.Enter))
        {
            if (_chatUi.Input.Focused)
                _chatUi.Submit();
            else
                _chatUi.FocusInput();
        }
        if (_chatUi.Input.Focused &&
            KeyboardState.IsKeyPressed(Keys.Backspace))
            _chatUi.Backspace();
        if (!_chatUi.Input.Focused || !_commandHints.Visible) return;
        if (KeyboardState.IsKeyPressed(Keys.Up))
            _commandHints.MoveSelection(-1);
        else if (KeyboardState.IsKeyPressed(Keys.Down))
            _commandHints.MoveSelection(1);
        if (KeyboardState.IsKeyPressed(Keys.Tab) &&
            _commandHints.Selected() is { } selected)
            CompleteCommandHint(selected);
    }

    private bool IsPointerOverObserveUi(Vector2 pointer) =>
        _chatUi.BlocksWorldInput(pointer) ||
        _commandHints.HitTest(pointer) ||
        _minimapUi.HitTest(pointer) ||
        _modalScreen.CapturesAllInput;

    private void RenderObserveUi()
    {
        _uiOpacity = _pauseMenu.IsPaused ? .28f : 1f;
        var scene = SceneClientBounds();
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        RenderVillagerOverheadSpeech(scene);
        RenderMinimap();
        RenderChatUi();
        RenderWorldClock(scene);
        _uiOpacity = 1;
    }
}
