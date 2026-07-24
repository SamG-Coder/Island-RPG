using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class ContextMenuControlState
{
    private const float ItemHeight = 28;
    public const float HeaderHeight = 24;
    private bool _leftWasDown;

    public bool Visible { get; private set; }
    public Vector4 Bounds { get; private set; }
    public IReadOnlyList<string> Items { get; private set; } = [];
    public int HoveredIndex { get; private set; } = -1;
    public event Action<int>? Selected;

    public void Open(
        Vector2 anchor, IReadOnlyList<string> items, Vector4 viewport,
        float width = 124)
    {
        Items = items;
        var height = HeaderHeight + items.Count * ItemHeight + 4;
        var left = anchor.X - 16;
        var top = anchor.Y - 12;
        Bounds = new(
            Math.Clamp(left, viewport.X, viewport.X + viewport.Z - width),
            Math.Clamp(top, viewport.Y, viewport.Y + viewport.W - height),
            width,
            height);
        Visible = items.Count > 0;
        HoveredIndex = -1;
        _leftWasDown = false;
    }

    public void UpdatePointer(Vector2 pointer, bool leftDown)
    {
        if (!Visible) return;
        if (!Bounds.Contains(pointer))
        {
            Close();
            return;
        }

        HoveredIndex = HitIndex(pointer);
        if (!leftDown && _leftWasDown && HoveredIndex >= 0)
        {
            var selected = HoveredIndex;
            Close();
            Selected?.Invoke(selected);
            return;
        }
        _leftWasDown = leftDown;
    }

    public Vector4 ItemBounds(int index) => new(
        Bounds.X + 2,
        Bounds.Y + HeaderHeight + 2 + index * ItemHeight,
        Bounds.Z - 4,
        ItemHeight);

    public bool HitTest(Vector2 pointer) =>
        Visible && Bounds.Contains(pointer);

    public void Close()
    {
        Visible = false;
        HoveredIndex = -1;
        _leftWasDown = false;
    }

    private int HitIndex(Vector2 pointer)
    {
        var relativeY = pointer.Y - Bounds.Y - HeaderHeight - 2;
        if (relativeY < 0) return -1;
        var index = (int)(relativeY / ItemHeight);
        return index >= 0 && index < Items.Count ? index : -1;
    }
}
