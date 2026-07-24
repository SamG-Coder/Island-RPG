using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal enum ChatChannel
{
    All,
    Combat,
    Story,
    Debug
}

internal sealed class ChatChannelControlState : ControlState
{
}

internal sealed class ChatInputControlState : ControlState
{
    public bool Focused { get; internal set; }
}

internal sealed class ChatUiControlState
{
    private const int VisibleRows = 8;
    private readonly List<string> _messages = [];
    private bool _leftWasDown;
    private bool _draggingThumb;
    private float _thumbGrabOffset;

    public PanelControlState LogPanel { get; } = new();
    public ChatChannelControlState ChannelButton { get; } = new();
    public ChatInputControlState Input { get; } = new();
    public PanelControlState ScrollTrack { get; } = new();
    public PanelControlState ScrollThumb { get; } = new();
    public ChatChannel Channel { get; private set; }
    public string InputText { get; private set; } = "";
    public int FirstVisibleLine { get; private set; }
    public IReadOnlyList<string> Messages => _messages;
    public bool IsAtBottom => FirstVisibleLine >= MaximumFirstLine;

    private int MaximumFirstLine => Math.Max(0, _messages.Count - VisibleRows);

    public void Layout(Vector4 viewport)
    {
        var width = Math.Min(500, Math.Max(240, viewport.Z - 24));
        var left = viewport.X + 12;
        var bottom = viewport.Y + viewport.W - 12;
        LogPanel.Bounds = new(left, Math.Max(viewport.Y, bottom - 180), width, 140);
        ChannelButton.Bounds = new(left, bottom - 38, 42, 38);
        Input.Bounds = new(left + 42, bottom - 38, width - 42, 38);
        ScrollTrack.Bounds = new(
            LogPanel.Bounds.X + LogPanel.Bounds.Z - 14,
            LogPanel.Bounds.Y + 5,
            9,
            LogPanel.Bounds.W - 10);
        UpdateThumbBounds();
    }

    public void UpdatePointer(Vector2 pointer, bool leftDown)
    {
        ChannelButton.Hovered = ChannelButton.HitTest(pointer);
        Input.Hovered = Input.HitTest(pointer);
        ScrollTrack.Hovered = ScrollTrack.HitTest(pointer);
        ScrollThumb.Hovered = ScrollThumb.HitTest(pointer);

        if (leftDown && !_leftWasDown)
        {
            if (ScrollThumb.HitTest(pointer))
            {
                _draggingThumb = true;
                ScrollThumb.Pressed = true;
                _thumbGrabOffset = pointer.Y - ScrollThumb.Bounds.Y;
            }
            else if (ScrollTrack.HitTest(pointer))
            {
                PageToward(pointer.Y);
            }
            else if (ChannelButton.HitTest(pointer))
            {
                ChannelButton.Pressed = true;
            }

            Input.Focused = Input.HitTest(pointer);
        }

        if (_draggingThumb && leftDown)
            DragThumb(pointer.Y - _thumbGrabOffset);

        if (!leftDown && _leftWasDown)
        {
            if (ChannelButton.Pressed && ChannelButton.HitTest(pointer))
                Channel = (ChatChannel)(((int)Channel + 1) % 4);
            ChannelButton.Pressed = false;
            ScrollThumb.Pressed = false;
            _draggingThumb = false;
        }
        _leftWasDown = leftDown;
    }

    public bool Scroll(Vector2 pointer, float wheelDelta)
    {
        if (!LogPanel.HitTest(pointer) || wheelDelta == 0) return false;
        FirstVisibleLine = Math.Clamp(
            FirstVisibleLine - Math.Sign(wheelDelta) * 3,
            0,
            MaximumFirstLine);
        UpdateThumbBounds();
        return true;
    }

    public bool BlocksWorldInput(Vector2 pointer) =>
        LogPanel.HitTest(pointer) ||
        ChannelButton.HitTest(pointer) ||
        Input.HitTest(pointer);

    public void AppendText(string text)
    {
        if (!Input.Focused || string.IsNullOrEmpty(text)) return;
        foreach (var character in text)
            if (!char.IsControl(character) && InputText.Length < 256)
                InputText += character;
    }

    public void Backspace()
    {
        if (Input.Focused && InputText.Length > 0)
            InputText = InputText[..^1];
    }

    public void Submit()
    {
        if (!Input.Focused || string.IsNullOrWhiteSpace(InputText)) return;
        AddMessage(InputText);
        InputText = "";
    }

    public void BlurInput() => Input.Focused = false;

    public void AddMessage(string message)
    {
        var keepAtBottom = IsAtBottom;
        _messages.Add(message);
        if (keepAtBottom) FirstVisibleLine = MaximumFirstLine;
        UpdateThumbBounds();
    }

    private void PageToward(float pointerY)
    {
        FirstVisibleLine = Math.Clamp(
            FirstVisibleLine + (pointerY < ScrollThumb.Bounds.Y
                ? -VisibleRows
                : VisibleRows),
            0,
            MaximumFirstLine);
        UpdateThumbBounds();
    }

    private void DragThumb(float thumbTop)
    {
        var track = ScrollTrack.Bounds;
        var travel = Math.Max(0, track.W - ScrollThumb.Bounds.W);
        if (travel <= 0 || MaximumFirstLine == 0)
        {
            FirstVisibleLine = 0;
            return;
        }
        var ratio = Math.Clamp((thumbTop - track.Y) / travel, 0, 1);
        FirstVisibleLine = (int)MathF.Round(ratio * MaximumFirstLine);
        UpdateThumbBounds();
    }

    private void UpdateThumbBounds()
    {
        var track = ScrollTrack.Bounds;
        var ratio = _messages.Count == 0
            ? 1
            : Math.Min(1, VisibleRows / (float)_messages.Count);
        var height = Math.Max(18, track.W * ratio);
        var travel = Math.Max(0, track.W - height);
        var position = MaximumFirstLine == 0
            ? 0
            : FirstVisibleLine / (float)MaximumFirstLine;
        ScrollThumb.Bounds = new(
            track.X,
            track.Y + travel * position,
            track.Z,
            height);
    }
}
