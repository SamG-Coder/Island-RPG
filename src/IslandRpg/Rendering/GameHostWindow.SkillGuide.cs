using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void OpenSkillGuideWindow(SkillType skill)
    {
        if (!SkillGuideService.IsSupported(skill)) return;
        var experience = skill switch
        {
            SkillType.Fishing =>
                _activePlayer?.FishingExperience ?? 0,
            _ => _activePlayer?.WoodcuttingExperience ?? 0
        };
        _skillGuideWindow.Open(
            SkillGuideService.Definition(skill),
            SkillService.LevelForExperience(experience));
        _modalScreen.Open(ModalScreenKind.SkillGuide);
        _chatUi.BlurInput();
        _inventoryContext.Close();
        _vegetationContext.Close();
        UseDefaultGameCursor();
    }

    private void CloseSkillGuideWindow()
    {
        _skillGuideWindow.Close();
        _modalScreen.Close(ModalScreenKind.SkillGuide);
        if (_defaultNativeCursor is not null)
            Cursor = _defaultNativeCursor;
    }

    private void UpdateSkillGuideWindowInput(
        Vector2 pointer, bool leftDown)
    {
        _skillGuideWindow.UpdatePointer(
            SceneClientBounds(), pointer, leftDown);
        if (!_skillGuideWindow.Visible)
            CloseSkillGuideWindow();
    }

    private void RenderSkillGuideWindow()
    {
        var guide = _skillGuideWindow.Guide;
        if (guide is null) return;
        _skillGuideWindow.Layout(SceneClientBounds());
        var window = SkillGuideWindowState.WindowBounds(
            SceneClientBounds());
        DrawAoEPanelBorder(window);
        DrawCenteredUiText(
            $"{guide.Name.ToUpperInvariant()} LEVEL GUIDE",
            new(window.X + 52, window.Y + 20, window.Z - 104, 32),
            new(232, 217, 166, 255));
        DrawCenteredUiText(
            $"Current level: {_skillGuideWindow.CurrentLevel}",
            new(window.X + 24, window.Y + 57, window.Z - 48, 24),
            new(183, 173, 143, 255));
        DrawMenuButton(
            SkillGuideWindowState.CloseBounds(window), "X");

        var list = _skillGuideWindow.List.Bounds;
        DrawUiColor(list, new(.035f, .032f, .025f, .96f));
        DrawPanelOutline(list, 1, new(.29f, .235f, .13f, 1));
        foreach (var index in _skillGuideWindow.List.VisibleIndices)
        {
            var entry = guide.Entries[index];
            var row = _skillGuideWindow.List.RowBounds(index);
            var unlocked =
                entry.Level <= _skillGuideWindow.CurrentLevel;
            var current =
                entry.Level == _skillGuideWindow.CurrentLevel;
            DrawUiColor(
                row,
                current
                    ? new(.20f, .16f, .065f, .98f)
                    : unlocked
                        ? new(.075f, .095f, .048f, .96f)
                        : new(.075f, .045f, .040f, .94f));
            DrawPanelOutline(
                row, current ? 2 : 1,
                current
                    ? new(.62f, .46f, .17f, 1)
                    : new(.22f, .18f, .11f, 1));
            var levelBounds = new Vector4(
                row.X + 8, row.Y, 74, row.W);
            DrawCenteredUiText(
                $"Level {entry.Level}",
                levelBounds,
                unlocked
                    ? new(226, 215, 174, 255)
                    : new(145, 115, 108, 255));
            DrawUiText(
                entry.Description,
                VerticallyCenteredTextPosition(
                    entry.Description,
                    new(
                        row.X + 92, row.Y,
                        row.Z - 104, row.W),
                    14),
                unlocked
                    ? new(201, 192, 158, 255)
                    : new(137, 112, 105, 255));
        }
        RenderListScrollbar(_skillGuideWindow.List);

        DrawCenteredUiText(
            "Mouse wheel to browse levels",
            new(window.X + 24, window.Y + window.W - 57,
                window.Z - 180, 36),
            new(168, 159, 132, 255));
        DrawMenuButton(
            SkillGuideWindowState.BackBounds(window), "Back");
    }
}
