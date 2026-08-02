using OpenTK.Mathematics;
using IslandRpg.Persistence;

namespace IslandRpg.Rendering.Ui;

internal enum ChatChannel
{
    All,
    Combat,
    Story,
    Debug
}

internal enum ChatMessageStyle
{
    Normal,
    Player,
    Npc,
    Action,
    Damage,
    Miss,
    Experience,
    LevelUp,
    Reward,
    Monologue,
    Warning,
    Debug
}

internal sealed record ChatMessage(string Text, ChatMessageStyle Style);

internal sealed class ChatChannelControlState : ControlState
{
}

internal sealed class ChatInputControlState : ControlState
{
    public bool Focused { get; internal set; }
}

internal sealed class ChatUiControlState
{
    private const int MaximumMessages = 200;
    private const float ChannelButtonWidth = 63;
    private const float ControlGap = 4;
    private readonly List<ChatMessage> _messages = [];
    private readonly List<ChatMessage> _displayLines = [];
    private bool _leftWasDown;
    private bool _draggingThumb;
    private float _thumbGrabOffset;
    private ChatDisplaySize _displaySize;
    private bool _wrapText = true;
    private float _lineHeight = 16;
    private float _lastWrapWidth = -1;
    private Func<string, float> _measureText = text => text.Length * 7;

    public PanelControlState LogPanel { get; } = new();
    public ChatChannelControlState ChannelButton { get; } = new();
    public ChatInputControlState Input { get; } = new();
    public PanelControlState ScrollTrack { get; } = new();
    public PanelControlState ScrollThumb { get; } = new();
    public ChatChannel Channel { get; private set; }
    public string InputText { get; private set; } = "";
    public int FirstVisibleLine { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public IReadOnlyList<ChatMessage> DisplayLines => _displayLines;
    public int VisibleRows => _displaySize switch
    {
        ChatDisplaySize.Medium => 12,
        ChatDisplaySize.Large => 16,
        _ => 8
    };
    public bool IsAtBottom => FirstVisibleLine >= MaximumFirstLine;
    public event Action<string>? Submitted;
    public event Action<ChatMessage>? MessageAdded;

    private int MaximumFirstLine =>
        Math.Max(0, _displayLines.Count - VisibleRows);

    public void Configure(
        ChatDisplaySize displaySize,
        bool wrapText,
        float lineHeight,
        Func<string, float> measureText)
    {
        var changed = _displaySize != displaySize ||
                      _wrapText != wrapText ||
                      Math.Abs(_lineHeight - lineHeight) > .01f;
        _displaySize = displaySize;
        _wrapText = wrapText;
        _lineHeight = Math.Max(1, lineHeight);
        _measureText = measureText;
        if (changed) RebuildDisplayLines();
    }

    public void Layout(Vector4 viewport)
    {
        var desiredWidth = _displaySize switch
        {
            ChatDisplaySize.Medium => 650,
            ChatDisplaySize.Large => 800,
            _ => 500
        };
        var width = Math.Min(desiredWidth, Math.Max(240, viewport.Z - 24));
        var left = viewport.X + 12;
        var bottom = viewport.Y + viewport.W - 12;
        var logHeight = VisibleRows * _lineHeight + 12;
        LogPanel.Bounds = new(
            left, Math.Max(viewport.Y, bottom - 40 - logHeight),
            width, logHeight);
        ChannelButton.Bounds = new(
            left, bottom - 38, ChannelButtonWidth, 38);
        Input.Bounds = new(
            left + ChannelButtonWidth + ControlGap,
            bottom - 38,
            width - ChannelButtonWidth - ControlGap,
            38);
        ScrollTrack.Bounds = new(
            LogPanel.Bounds.X + LogPanel.Bounds.Z - 14,
            LogPanel.Bounds.Y + 5,
            9,
            LogPanel.Bounds.W - 10);
        var wrapWidth = Math.Max(20, LogPanel.Bounds.Z - 32);
        if (Math.Abs(wrapWidth - _lastWrapWidth) > .5f)
        {
            _lastWrapWidth = wrapWidth;
            RebuildDisplayLines();
        }
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

    public void SetInputText(string text)
    {
        InputText = text[..Math.Min(text.Length, 256)];
        Input.Focused = true;
    }

    public void ClearMessages()
    {
        _messages.Clear();
        _displayLines.Clear();
        FirstVisibleLine = 0;
        UpdateThumbBounds();
    }

    public void Submit()
    {
        if (!Input.Focused || string.IsNullOrWhiteSpace(InputText)) return;
        var message = InputText.Trim();
        AddMessage(message, ChatMessageStyle.Player);
        InputText = "";
        Submitted?.Invoke(message);
    }

    public void BlurInput() => Input.Focused = false;

    public void FocusInput() => Input.Focused = true;

    public void AddMessage(
        string message, ChatMessageStyle style = ChatMessageStyle.Normal)
    {
        var keepAtBottom = IsAtBottom;
        var chatMessage = new ChatMessage(message, style);
        _messages.Add(chatMessage);
        var removed = Math.Max(0, _messages.Count - MaximumMessages);
        if (removed > 0)
        {
            _messages.RemoveRange(0, removed);
        }
        RebuildDisplayLines();
        if (keepAtBottom) FirstVisibleLine = MaximumFirstLine;
        UpdateThumbBounds();
        MessageAdded?.Invoke(chatMessage);
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
        var ratio = _displayLines.Count == 0
            ? 1
            : Math.Min(1, VisibleRows / (float)_displayLines.Count);
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

    private void RebuildDisplayLines()
    {
        var keepAtBottom = IsAtBottom;
        _displayLines.Clear();
        var maximumWidth = Math.Max(20, _lastWrapWidth);
        foreach (var message in _messages)
        {
            var lines = _wrapText
                ? ChatTextLayout.Wrap(
                    message.Text, maximumWidth, _measureText)
                : [message.Text];
            foreach (var line in lines)
                _displayLines.Add(message with { Text = line });
        }
        FirstVisibleLine = keepAtBottom
            ? MaximumFirstLine
            : Math.Clamp(FirstVisibleLine, 0, MaximumFirstLine);
    }
}

internal static class ChatTextLayout
{
    public static IReadOnlyList<string> Wrap(
        string text,
        float maximumWidth,
        Func<string, float> measureText)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        maximumWidth = Math.Max(1, maximumWidth);
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var remaining = paragraph.TrimEnd();
            if (remaining.Length == 0)
            {
                result.Add("");
                continue;
            }
            while (remaining.Length > 0)
            {
                var count = FittingLength(
                    remaining, maximumWidth, measureText);
                if (count >= remaining.Length)
                {
                    result.Add(remaining);
                    break;
                }
                var breakAt = remaining.LastIndexOf(' ', count - 1, count);
                if (breakAt <= 0) breakAt = count;
                result.Add(remaining[..breakAt].TrimEnd());
                remaining = remaining[breakAt..].TrimStart();
            }
        }
        return result;
    }

    private static int FittingLength(
        string text,
        float maximumWidth,
        Func<string, float> measureText)
    {
        var low = 1;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (measureText(text[..middle]) <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }
}
