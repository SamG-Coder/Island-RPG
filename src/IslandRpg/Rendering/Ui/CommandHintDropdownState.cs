using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class CommandHintDropdownState
{
    private const float RowHeight = 42;
    private IReadOnlyList<ChatCommandDefinition> _items = [];
    private bool _leftWasDown;

    public Vector4 Bounds { get; private set; }
    public IReadOnlyList<ChatCommandDefinition> Items => _items;
    public int SelectedIndex { get; private set; }
    public bool Visible => _items.Count > 0;

    public void UpdateItems(
        IReadOnlyList<ChatCommandDefinition> items,
        Vector4 inputBounds)
    {
        _items = items;
        SelectedIndex = Math.Clamp(
            SelectedIndex, 0, Math.Max(0, items.Count - 1));
        var height = items.Count * RowHeight;
        Bounds = new(
            inputBounds.X,
            inputBounds.Y - height - 4,
            inputBounds.Z,
            height);
    }

    public Vector4 RowBounds(int index) =>
        new(
            Bounds.X,
            Bounds.Y + index * RowHeight,
            Bounds.Z,
            RowHeight);

    public void MoveSelection(int direction)
    {
        if (!Visible) return;
        SelectedIndex = (SelectedIndex + direction + _items.Count) %
                        _items.Count;
    }

    public ChatCommandDefinition? Selected() =>
        Visible ? _items[SelectedIndex] : null;

    public ChatCommandDefinition? UpdatePointer(
        Vector2 pointer,
        bool leftDown)
    {
        ChatCommandDefinition? selected = null;
        if (leftDown && !_leftWasDown)
            for (var index = 0; index < _items.Count; index++)
                if (RowBounds(index).Contains(pointer))
                {
                    SelectedIndex = index;
                    selected = _items[index];
                    break;
                }
        _leftWasDown = leftDown;
        return selected;
    }

    public bool HitTest(Vector2 pointer) =>
        Visible && Bounds.Contains(pointer);
}
