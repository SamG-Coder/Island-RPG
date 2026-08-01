using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private string? _observedVillagerId;
    private bool _observeRosterLeftWasDown;

    private void UpdateObserveUi()
    {
        var scene = SceneClientBounds();
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        UpdateCommandHints();
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        var clicked = leftDown && !_observeRosterLeftWasDown;
        _observeRosterLeftWasDown = leftDown;
        if (clicked)
        {
            var pointer = MouseState.Position;
            for (var index = 0; index < _villagers.Count; index++)
            {
                if (!ObserveVillagerRowBounds(index).Contains(pointer))
                    continue;
                _observedVillagerId = _villagers[index].Id;
                SnapCameraToObservedVillager();
                break;
            }
        }
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
        ObserveVillagerPanelBounds().Contains(pointer) ||
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
        RenderObserveVillagerRoster();
        RenderMinimap();
        RenderChatUi();
        RenderWorldClock(scene);
        _uiOpacity = 1;
    }

    private Vector4 ObserveVillagerPanelBounds()
    {
        const float width = 292;
        var height = 42 + Math.Min(8, _villagers.Count) * 70;
        return new(12, 12, width, height);
    }

    private Vector4 ObserveVillagerRowBounds(int index)
    {
        var panel = ObserveVillagerPanelBounds();
        return new(panel.X + 8, panel.Y + 34 + index * 70,
            panel.Z - 16, 62);
    }

    private void RenderObserveVillagerRoster()
    {
        var panel = ObserveVillagerPanelBounds();
        DrawRoundedUiColor(panel, 7, new(.025f, .024f, .019f, .94f));
        DrawUiText("SURVIVORS", new(panel.X + 12, panel.Y + 8),
            new FSColor(224, 207, 150, 255));
        for (var index = 0;
             index < _villagers.Count && index < 8;
             index++)
        {
            var villager = _villagers[index];
            var row = ObserveVillagerRowBounds(index);
            var selected = villager.Id == _observedVillagerId;
            var hovered = row.Contains(MouseState.Position);
            DrawRoundedUiColor(
                row, 5,
                selected
                    ? new(.17f, .19f, .11f, .98f)
                    : hovered
                        ? new(.09f, .085f, .06f, .98f)
                        : new(.052f, .048f, .036f, .96f));
            var phase = _npcController.Phase(villager.Id);
            var status = phase is null
                ? $"{villager.Activity} · {villager.Action}"
                : $"{phase} · {villager.Action}";
            var thought = villager.LastDeliberation?.PrivateThought;
            if (string.IsNullOrWhiteSpace(thought))
                thought = $"Needs {villager.Need.ToString().ToLowerInvariant()}";
            DrawUiText(villager.Name,
                new(row.X + 9, row.Y + 6),
                villager.Health > 0
                    ? new FSColor(239, 229, 194, 255)
                    : new FSColor(170, 110, 100, 255));
            DrawUiText(TrimObserveText(status, 38),
                new(row.X + 9, row.Y + 25),
                new FSColor(174, 190, 145, 255));
            DrawUiText(TrimObserveText(thought!, 43),
                new(row.X + 9, row.Y + 43),
                new FSColor(176, 170, 151, 255));
        }
    }

    private static string TrimObserveText(string value, int maximum) =>
        value.Length <= maximum
            ? value
            : value[..Math.Max(1, maximum - 1)] + "…";

    private void SnapCameraToObservedVillager()
    {
        var villager = _villagers.FirstOrDefault(value =>
            value.Id == _observedVillagerId && value.Health > 0);
        if (villager is null)
        {
            _observedVillagerId = null;
            return;
        }
        var terrain = SamplePlayerTerrain(
            villager.PositionX, villager.PositionY);
        var projected = IsometricTerrainProjection.Project(
            villager.PositionX, villager.PositionY, terrain.Height);
        _camera = -projected * _zoom;
    }
}
