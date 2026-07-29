using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class ToggleControlState(
    string label,
    string description = "") : ControlState
{
    public string Label { get; set; } = label;
    public string Description { get; set; } = description;
    public bool IsChecked { get; private set; }
    public event Action<bool>? Changed;

    public void Layout(Vector4 row, float horizontalInset = 8)
    {
        Bounds = new(
            row.X + horizontalInset,
            row.Y + 4,
            row.Z - horizontalInset * 2,
            row.W - 8);
    }

    public bool ToggleAt(Vector2 pointer)
    {
        Hovered = HitTest(pointer);
        if (!Hovered) return false;
        SetChecked(!IsChecked);
        return true;
    }

    public void SetChecked(bool value)
    {
        if (IsChecked == value) return;
        IsChecked = value;
        Changed?.Invoke(value);
    }
}
