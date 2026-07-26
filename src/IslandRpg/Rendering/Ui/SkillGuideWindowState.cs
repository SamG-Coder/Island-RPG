using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class SkillGuideWindowState
{
    private bool _leftWasDown;
    private bool _positionAtCurrentLevel;

    public bool Visible { get; private set; }
    public SkillGuideDefinition? Guide { get; private set; }
    public int CurrentLevel { get; private set; }
    public ListControlState List { get; } = new();

    public void Open(SkillGuideDefinition guide, int currentLevel)
    {
        Guide = guide;
        CurrentLevel = Math.Clamp(currentLevel, 1, SkillService.MaximumLevel);
        _positionAtCurrentLevel = true;
        Visible = true;
    }

    public void Close() => Visible = false;

    public void UpdatePointer(
        Vector4 viewport, Vector2 pointer, bool leftDown)
    {
        if (!Visible)
        {
            _leftWasDown = leftDown;
            return;
        }
        Layout(viewport);
        List.UpdatePointer(pointer, leftDown);
        if (leftDown && !_leftWasDown)
        {
            var window = WindowBounds(viewport);
            if (CloseBounds(window).Contains(pointer) ||
                BackBounds(window).Contains(pointer))
                Close();
        }
        _leftWasDown = leftDown;
    }

    public bool Scroll(Vector2 pointer, float wheelOffset)
    {
        if (!Visible) return false;
        return List.Scroll(pointer, wheelOffset);
    }

    public void Layout(Vector4 viewport)
    {
        if (Guide is null) return;
        var list = ListBounds(WindowBounds(viewport));
        const float gap = 3;
        const int visibleRows = 12;
        var rowHeight =
            (list.W - gap * (visibleRows - 1)) / visibleRows;
        List.Layout(
            list,
            Guide.Entries.Select(entry => entry.Level.ToString()).ToArray(),
            rowHeight,
            gap,
            deleteWidth: 0,
            actionGap: 0);
        if (!_positionAtCurrentLevel) return;
        var currentEntry = Guide.Entries
            .Select((entry, index) => (entry, index))
            .Where(value => value.entry.Level <= CurrentLevel)
            .Select(value => value.index)
            .DefaultIfEmpty(0)
            .Last();
        List.ScrollToIndex(currentEntry, leadingRows: 2);
        _positionAtCurrentLevel = false;
    }

    public static Vector4 WindowBounds(Vector4 viewport)
    {
        var width = Math.Min(760, Math.Max(620, viewport.Z - 48));
        var height = Math.Min(650, Math.Max(540, viewport.W - 48));
        return new(
            viewport.X + (viewport.Z - width) * .5f,
            viewport.Y + (viewport.W - height) * .5f,
            width,
            height);
    }

    public static Vector4 CloseBounds(Vector4 window) =>
        new(window.X + window.Z - 40, window.Y + 12, 28, 28);

    public static Vector4 ListBounds(Vector4 window) =>
        new(window.X + 24, window.Y + 96, window.Z - 48, window.W - 166);

    public static Vector4 BackBounds(Vector4 window) =>
        new(
            window.X + window.Z - 132,
            window.Y + window.W - 56,
            108,
            36);
}
