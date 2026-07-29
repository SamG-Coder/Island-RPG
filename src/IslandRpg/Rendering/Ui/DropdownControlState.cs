using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal readonly record struct DropdownOption(
    string Id,
    string Label);

internal sealed class DropdownControlState
{
    private const float RowHeight = 30;
    private const int MaximumVisibleRows = 6;
    private IReadOnlyList<DropdownOption> _options = [];

    public Vector4 Bounds { get; private set; }
    public Vector4 MenuBounds { get; private set; }
    public bool IsOpen { get; private set; }
    public int FirstVisibleIndex { get; private set; }
    public int VisibleCount =>
        Math.Min(MaximumVisibleRows, _options.Count);
    public IReadOnlyList<DropdownOption> Options => _options;

    public void Layout(
        Vector4 bounds,
        IReadOnlyList<DropdownOption> options,
        Vector4 viewport)
    {
        Bounds = bounds;
        _options = options;
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex,
            0,
            MaximumFirstIndex);
        var height = VisibleCount * RowHeight;
        var below = bounds.Y + bounds.W + 3;
        var above = bounds.Y - height - 3;
        var y = below + height <= viewport.Y + viewport.W
            ? below
            : Math.Max(viewport.Y, above);
        MenuBounds = new(bounds.X, y, bounds.Z, height);
    }

    public void Toggle()
    {
        IsOpen = !IsOpen && _options.Count > 0;
    }

    public void Close() => IsOpen = false;

    public bool TrySelect(Vector2 pointer, out DropdownOption option)
    {
        option = default;
        if (!IsOpen) return false;
        for (var row = 0; row < VisibleCount; row++)
        {
            var index = FirstVisibleIndex + row;
            if (!OptionBounds(row).Contains(pointer)) continue;
            option = _options[index];
            IsOpen = false;
            return true;
        }
        return false;
    }

    public bool Scroll(Vector2 pointer, float offset)
    {
        if (!IsOpen ||
            !MenuBounds.Contains(pointer) ||
            offset == 0)
            return false;
        FirstVisibleIndex = Math.Clamp(
            FirstVisibleIndex - Math.Sign(offset),
            0,
            MaximumFirstIndex);
        return true;
    }

    public Vector4 OptionBounds(int visibleRow) =>
        new(
            MenuBounds.X,
            MenuBounds.Y + visibleRow * RowHeight,
            MenuBounds.Z,
            RowHeight);

    private int MaximumFirstIndex =>
        Math.Max(0, _options.Count - MaximumVisibleRows);
}
