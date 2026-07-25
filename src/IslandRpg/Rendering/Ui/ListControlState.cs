using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class ListControlState
{
    private IReadOnlyList<string> _itemIds = [];
    private bool _leftWasDown;
    private bool _draggingThumb;
    private float _thumbGrabOffset;

    public Vector4 Bounds { get; private set; }
    public PanelControlState ScrollTrack { get; } = new();
    public PanelControlState ScrollThumb { get; } = new();
    public float RowHeight { get; private set; } = 54;
    public float RowGap { get; private set; } = 12;
    public float DeleteWidth { get; private set; } = 124;
    public float ActionGap { get; private set; } = 10;
    public string? SelectedId { get; set; }
    public string? PendingDeleteId { get; private set; }
    public int FirstVisibleIndex { get; private set; }
    public int Count => _itemIds.Count;
    public int VisibleRows => Math.Max(
        1, (int)MathF.Floor(
            (Bounds.W + RowGap) / (RowHeight + RowGap)));
    public int MaximumFirstIndex =>
        Math.Max(0, Count - VisibleRows);
    public IEnumerable<int> VisibleIndices =>
        Enumerable.Range(
            FirstVisibleIndex,
            Math.Min(VisibleRows, Count - FirstVisibleIndex));

    public void Layout(
        Vector4 bounds,
        IReadOnlyList<string> itemIds,
        float rowHeight = 54,
        float rowGap = 12,
        float deleteWidth = 124,
        float actionGap = 10)
    {
        Bounds = bounds;
        _itemIds = itemIds;
        RowHeight = rowHeight;
        RowGap = rowGap;
        DeleteWidth = deleteWidth;
        ActionGap = actionGap;
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex, 0, MaximumFirstIndex);
        ScrollTrack.Bounds = new(
            Bounds.X + Bounds.Z - 10,
            Bounds.Y,
            8,
            Bounds.W);
        UpdateThumbBounds();
        if (SelectedId is not null && !_itemIds.Contains(SelectedId))
            SelectedId = null;
        if (PendingDeleteId is not null &&
            !_itemIds.Contains(PendingDeleteId))
            PendingDeleteId = null;
    }

    public Vector4 RowBounds(int index) => new(
        Bounds.X,
        Bounds.Y + (index - FirstVisibleIndex) *
        (RowHeight + RowGap),
        Math.Max(
            1,
            Bounds.Z - DeleteWidth - ActionGap -
            (MaximumFirstIndex > 0 ? 16 : 0)),
        RowHeight);

    public Vector4 DeleteBounds(int index)
    {
        var row = RowBounds(index);
        return new(
            row.X + row.Z + ActionGap,
            row.Y,
            DeleteWidth,
            row.W);
    }

    public bool TryHit(
        Vector2 pointer, out int index, out bool delete)
    {
        foreach (var candidate in VisibleIndices)
        {
            if (DeleteBounds(candidate).Contains(pointer))
            {
                index = candidate;
                delete = true;
                return true;
            }
            if (RowBounds(candidate).Contains(pointer))
            {
                index = candidate;
                delete = false;
                return true;
            }
        }

        index = -1;
        delete = false;
        return false;
    }

    public bool ApproveDelete(string itemId)
    {
        if (PendingDeleteId == itemId)
        {
            PendingDeleteId = null;
            return true;
        }
        PendingDeleteId = itemId;
        return false;
    }

    public bool IsDeletePending(string itemId) =>
        PendingDeleteId == itemId;

    public void ClearDeleteApproval() => PendingDeleteId = null;

    public bool Scroll(Vector2 pointer, float wheelDelta)
    {
        if (!Bounds.Contains(pointer) || wheelDelta == 0) return false;
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex - Math.Sign(wheelDelta) * 3,
            0,
            MaximumFirstIndex);
        UpdateThumbBounds();
        return true;
    }

    public void UpdatePointer(Vector2 pointer, bool leftDown)
    {
        ScrollTrack.Hovered = ScrollTrack.HitTest(pointer);
        ScrollThumb.Hovered = ScrollThumb.HitTest(pointer);
        if (leftDown && !_leftWasDown)
        {
            if (MaximumFirstIndex > 0 &&
                ScrollThumb.HitTest(pointer))
            {
                _draggingThumb = true;
                ScrollThumb.Pressed = true;
                _thumbGrabOffset =
                    pointer.Y - ScrollThumb.Bounds.Y;
            }
            else if (MaximumFirstIndex > 0 &&
                     ScrollTrack.HitTest(pointer))
                PageToward(pointer.Y);
        }
        if (_draggingThumb && leftDown)
            DragThumb(pointer.Y - _thumbGrabOffset);
        if (!leftDown && _leftWasDown)
        {
            _draggingThumb = false;
            ScrollThumb.Pressed = false;
        }
        _leftWasDown = leftDown;
    }

    private void PageToward(float pointerY)
    {
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex +
            (pointerY < ScrollThumb.Bounds.Y
                ? -VisibleRows
                : VisibleRows),
            0,
            MaximumFirstIndex);
        UpdateThumbBounds();
    }

    private void DragThumb(float thumbTop)
    {
        var track = ScrollTrack.Bounds;
        var travel = Math.Max(
            0, track.W - ScrollThumb.Bounds.W);
        if (travel <= 0 || MaximumFirstIndex == 0)
        {
            FirstVisibleIndex = 0;
            return;
        }
        var ratio = Math.Clamp(
            (thumbTop - track.Y) / travel, 0, 1);
        FirstVisibleIndex = (int)MathF.Round(
            ratio * MaximumFirstIndex);
        UpdateThumbBounds();
    }

    private void UpdateThumbBounds()
    {
        var track = ScrollTrack.Bounds;
        var ratio = Count == 0
            ? 1
            : Math.Min(1, VisibleRows / (float)Count);
        var height = Math.Max(18, track.W * ratio);
        var travel = Math.Max(0, track.W - height);
        var position = MaximumFirstIndex == 0
            ? 0
            : FirstVisibleIndex / (float)MaximumFirstIndex;
        ScrollTrack.Visible = MaximumFirstIndex > 0;
        ScrollThumb.Visible = MaximumFirstIndex > 0;
        ScrollThumb.Bounds = new(
            track.X,
            track.Y + travel * position,
            track.Z,
            height);
    }
}
