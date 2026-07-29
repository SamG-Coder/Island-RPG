using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class SliderControlState : ControlState
{
    private bool _leftWasDown;

    public float Value { get; private set; }
    public Vector4 TrackBounds { get; private set; }
    public Vector4 FillBounds { get; private set; }
    public Vector4 ThumbBounds { get; private set; }

    public event Action<float>? ValueChanged;
    public event Action<float>? DragCompleted;

    public void Layout(Vector4 row, float leftInset = 154, float rightInset = 18)
    {
        Bounds = row;
        TrackBounds = new(
            row.X + leftInset,
            MathF.Round(row.Y + (row.W - 8) * .5f),
            Math.Max(40, row.Z - leftInset - rightInset),
            8);
        UpdateGeometry();
    }

    public void SetValue(float value)
    {
        Value = Math.Clamp(value, 0, 1);
        UpdateGeometry();
    }

    public bool UpdatePointer(Vector2 pointer, bool leftDown)
    {
        Hovered = HitTest(pointer);
        var pressedNow = leftDown && !_leftWasDown && Hovered;
        var consumed = Pressed || pressedNow;
        if (pressedNow)
            Pressed = true;

        if (Pressed && leftDown)
            SetFromPointer(pointer.X);
        else if (Pressed)
        {
            Pressed = false;
            DragCompleted?.Invoke(Value);
        }

        _leftWasDown = leftDown;
        return consumed;
    }

    public override bool HitTest(Vector2 point) =>
        Visible && Enabled &&
        point.X >= TrackBounds.X - 10 &&
        point.X < TrackBounds.X + TrackBounds.Z + 10 &&
        point.Y >= Bounds.Y &&
        point.Y < Bounds.Y + Bounds.W;

    private void SetFromPointer(float pointerX)
    {
        var next = Math.Clamp(
            (pointerX - TrackBounds.X) /
            Math.Max(1, TrackBounds.Z),
            0,
            1);
        if (MathF.Abs(next - Value) < .0001f) return;
        Value = next;
        UpdateGeometry();
        ValueChanged?.Invoke(Value);
    }

    private void UpdateGeometry()
    {
        FillBounds = new(
            TrackBounds.X,
            TrackBounds.Y,
            TrackBounds.Z * Value,
            TrackBounds.W);
        const float thumbWidth = 14;
        const float thumbHeight = 22;
        ThumbBounds = new(
            MathF.Round(
                TrackBounds.X + TrackBounds.Z * Value -
                thumbWidth * .5f),
            MathF.Round(
                TrackBounds.Y + TrackBounds.W * .5f -
                thumbHeight * .5f),
            thumbWidth,
            thumbHeight);
    }
}
