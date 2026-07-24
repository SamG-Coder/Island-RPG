using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering.Ui;

internal sealed class TextBoxControlState(string text = "") : ControlState
{
    private int _selectionAnchor;
    private bool _selecting;

    public string Text { get; private set; } = text;
    public int MaximumLength { get; init; } = 32;
    public int Caret { get; private set; } = text.Length;
    public bool Focused { get; private set; }
    public bool HasSelection => Caret != _selectionAnchor;
    public int SelectionStart => Math.Min(Caret, _selectionAnchor);
    public int SelectionEnd => Math.Max(Caret, _selectionAnchor);

    public void Focus(
        Vector2 pointer,
        Func<string, float> measureText,
        float horizontalPadding)
    {
        Focused = true;
        Caret = CaretFromPointer(pointer, measureText, horizontalPadding);
        _selectionAnchor = Caret;
        _selecting = true;
    }

    public void FocusAtEnd()
    {
        Focused = true;
        Caret = Text.Length;
        _selectionAnchor = Caret;
    }

    public void Blur()
    {
        Focused = false;
        _selecting = false;
    }

    public void UpdatePointer(
        Vector2 pointer,
        bool leftDown,
        Func<string, float> measureText,
        float horizontalPadding)
    {
        if (!_selecting) return;
        if (!leftDown)
        {
            _selecting = false;
            return;
        }
        Caret = CaretFromPointer(pointer, measureText, horizontalPadding);
    }

    public void UpdateKeyboard(
        KeyboardState keyboard,
        Func<string?> readClipboard,
        Action<string> writeClipboard)
    {
        if (!Focused) return;
        ClampSelection();
        var control = keyboard.IsKeyDown(Keys.LeftControl) ||
                      keyboard.IsKeyDown(Keys.RightControl);
        var shift = keyboard.IsKeyDown(Keys.LeftShift) ||
                    keyboard.IsKeyDown(Keys.RightShift);

        if (control && keyboard.IsKeyPressed(Keys.A))
        {
            _selectionAnchor = 0;
            Caret = Text.Length;
        }
        if (control && keyboard.IsKeyPressed(Keys.C))
            Copy(writeClipboard);
        if (control && keyboard.IsKeyPressed(Keys.X) && HasSelection)
        {
            Copy(writeClipboard);
            DeleteSelection();
        }
        if (control && keyboard.IsKeyPressed(Keys.V))
            Paste(readClipboard());

        if (keyboard.IsKeyPressed(Keys.Backspace))
        {
            if (HasSelection)
                DeleteSelection();
            else if (Caret > 0)
            {
                Text = Text.Remove(Caret - 1, 1);
                Caret--;
                _selectionAnchor = Caret;
            }
        }
        if (keyboard.IsKeyPressed(Keys.Delete))
        {
            if (HasSelection)
                DeleteSelection();
            else if (Caret < Text.Length)
                Text = Text.Remove(Caret, 1);
        }
        if (keyboard.IsKeyPressed(Keys.Left))
            MoveCaret(
                HasSelection && !shift ? SelectionStart : Math.Max(0, Caret - 1),
                shift);
        if (keyboard.IsKeyPressed(Keys.Right))
            MoveCaret(
                HasSelection && !shift
                    ? SelectionEnd
                    : Math.Min(Text.Length, Caret + 1),
                shift);
        if (keyboard.IsKeyPressed(Keys.Home))
            MoveCaret(0, shift);
        if (keyboard.IsKeyPressed(Keys.End))
            MoveCaret(Text.Length, shift);
    }

    public void Insert(string input)
    {
        if (!Focused) return;
        var clean = new string(input
            .Where(character => !char.IsControl(character))
            .ToArray());
        if (clean.Length == 0) return;
        DeleteSelection();
        clean = clean[..Math.Min(clean.Length, MaximumLength - Text.Length)];
        Text = Text.Insert(Caret, clean);
        Caret += clean.Length;
        _selectionAnchor = Caret;
    }

    public void SetText(string value)
    {
        Text = value[..Math.Min(value.Length, MaximumLength)];
        Caret = Text.Length;
        _selectionAnchor = Caret;
    }

    private int CaretFromPointer(
        Vector2 pointer,
        Func<string, float> measureText,
        float horizontalPadding)
    {
        var localX = Math.Max(
            0, pointer.X - Bounds.X - horizontalPadding);
        var previousWidth = 0f;
        for (var index = 1; index <= Text.Length; index++)
        {
            var width = measureText(Text[..index]);
            if (localX < (previousWidth + width) * .5f)
                return index - 1;
            previousWidth = width;
        }
        return Text.Length;
    }

    private void MoveCaret(int position, bool extendSelection)
    {
        if (!extendSelection) _selectionAnchor = position;
        Caret = position;
    }

    private void Copy(Action<string> writeClipboard)
    {
        if (HasSelection)
            writeClipboard(Text[SelectionStart..SelectionEnd]);
    }

    private void Paste(string? clipboard)
    {
        if (string.IsNullOrEmpty(clipboard)) return;
        Insert(clipboard);
    }

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        var start = SelectionStart;
        Text = Text.Remove(start, SelectionEnd - start);
        Caret = start;
        _selectionAnchor = start;
    }

    private void ClampSelection()
    {
        Caret = Math.Clamp(Caret, 0, Text.Length);
        _selectionAnchor = Math.Clamp(
            _selectionAnchor, 0, Text.Length);
    }
}
