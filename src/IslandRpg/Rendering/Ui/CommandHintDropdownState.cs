using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class CommandHintDropdownState
{
    private const float RowHeight = 42;
    private const int MaximumVisibleRows = 6;
    private const float ScrollbarWidth = 10;
    private IReadOnlyList<ChatCommandDefinition> _items = [];
    private bool _leftWasDown;

    public Vector4 Bounds { get; private set; }
    public Vector4 ScrollTrackBounds { get; private set; }
    public Vector4 ScrollThumbBounds { get; private set; }
    public IReadOnlyList<ChatCommandDefinition> Items => _items;
    public int SelectedIndex { get; private set; }
    public int FirstVisibleIndex { get; private set; }
    public int VisibleCount =>
        Math.Min(MaximumVisibleRows, _items.Count);
    public bool Visible => _items.Count > 0;
    public bool CanScroll => _items.Count > MaximumVisibleRows;

    public void UpdateItems(
        IReadOnlyList<ChatCommandDefinition> items,
        Vector4 inputBounds)
    {
        var changed = !_items.Select(item => item.Name)
            .SequenceEqual(items.Select(item => item.Name));
        _items = items;
        if (changed)
        {
            SelectedIndex = 0;
            FirstVisibleIndex = 0;
        }
        SelectedIndex = Math.Clamp(
            SelectedIndex, 0, Math.Max(0, items.Count - 1));
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex, 0, MaximumFirstVisible);
        var height = VisibleCount * RowHeight;
        Bounds = new(
            inputBounds.X,
            inputBounds.Y - height - 4,
            inputBounds.Z,
            height);
        LayoutScrollbar();
    }

    public Vector4 RowBounds(int visibleRow) =>
        new(
            Bounds.X,
            Bounds.Y + visibleRow * RowHeight,
            Bounds.Z - (CanScroll ? ScrollbarWidth + 4 : 0),
            RowHeight);

    public ChatCommandDefinition ItemAtVisibleRow(int visibleRow) =>
        _items[FirstVisibleIndex + visibleRow];

    public bool IsSelectedVisibleRow(int visibleRow) =>
        FirstVisibleIndex + visibleRow == SelectedIndex;

    public void MoveSelection(int direction)
    {
        if (!Visible) return;
        SelectedIndex = (SelectedIndex + direction + _items.Count) %
                        _items.Count;
        EnsureSelectionVisible();
        LayoutScrollbar();
    }

    public bool Scroll(Vector2 pointer, float wheelDelta)
    {
        if (!CanScroll || wheelDelta == 0 || !Bounds.Contains(pointer))
            return false;
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex - Math.Sign(wheelDelta),
            0,
            MaximumFirstVisible);
        SelectedIndex = Math.Clamp(
            SelectedIndex,
            FirstVisibleIndex,
            FirstVisibleIndex + VisibleCount - 1);
        LayoutScrollbar();
        return true;
    }

    public ChatCommandDefinition? Selected() =>
        Visible ? _items[SelectedIndex] : null;

    public ChatCommandDefinition? UpdatePointer(
        Vector2 pointer,
        bool leftDown)
    {
        ChatCommandDefinition? selected = null;
        if (leftDown && !_leftWasDown)
            for (var row = 0; row < VisibleCount; row++)
                if (RowBounds(row).Contains(pointer))
                {
                    SelectedIndex = FirstVisibleIndex + row;
                    selected = _items[SelectedIndex];
                    break;
                }
        _leftWasDown = leftDown;
        return selected;
    }

    public bool HitTest(Vector2 pointer) =>
        Visible && Bounds.Contains(pointer);

    private int MaximumFirstVisible =>
        Math.Max(0, _items.Count - MaximumVisibleRows);

    private void EnsureSelectionVisible()
    {
        if (SelectedIndex < FirstVisibleIndex)
            FirstVisibleIndex = SelectedIndex;
        else if (SelectedIndex >= FirstVisibleIndex + VisibleCount)
            FirstVisibleIndex = SelectedIndex - VisibleCount + 1;
    }

    private void LayoutScrollbar()
    {
        ScrollTrackBounds = new(
            Bounds.X + Bounds.Z - ScrollbarWidth - 2,
            Bounds.Y + 3,
            ScrollbarWidth,
            Math.Max(0, Bounds.W - 6));
        if (!CanScroll)
        {
            ScrollThumbBounds = default;
            return;
        }
        var ratio = VisibleCount / (float)_items.Count;
        var height = Math.Max(18, ScrollTrackBounds.W * ratio);
        var travel = ScrollTrackBounds.W - height;
        var position = MaximumFirstVisible == 0
            ? 0
            : FirstVisibleIndex / (float)MaximumFirstVisible;
        ScrollThumbBounds = new(
            ScrollTrackBounds.X,
            ScrollTrackBounds.Y + travel * position,
            ScrollTrackBounds.Z,
            height);
    }
}
