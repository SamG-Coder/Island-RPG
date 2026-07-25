using IslandRpg.Assets;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void PrepareGameCursors()
    {
        var path = Path.Combine(
            _install, "resources", "_common", "drs", "interface", "51000.slp");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The installed interface assets do not contain the AoE cursor sheet.", path);

        var palette = JascPalette.Load(Age2PaletteResolver.Resolve(_install, path).Path);
        var cursorSheet = SlpDecoder.Decode(path, palette);
        if (cursorSheet.Frames.Count <= 8)
            throw new InvalidDataException(
                "The installed AoE cursor sheet does not contain the tree-cut cursor.");

        _defaultNativeCursor = CreateNativeCursor(cursorSheet.Frames[0]);
        _pickupNativeCursor = CreateNativeCursor(cursorSheet.Frames[3]);
        _dropNativeCursor = CreateNativeCursor(cursorSheet.Frames[7]);
        _cutNativeCursor = CreateNativeCursor(cursorSheet.Frames[8]);
        Cursor = _defaultNativeCursor;
        CursorState = CursorState.Normal;
    }

    private void UseDefaultGameCursor()
    {
        _gameCursorKind = GameCursorKind.Default;
        Cursor = _defaultNativeCursor ?? MouseCursor.Default;
    }

    private static MouseCursor CreateNativeCursor(SpriteFrame frame)
    {
        var pixels = (byte[])frame.Rgba.Clone();
        // GLFW's Windows backend expects premultiplied RGB for translucent
        // custom-cursor pixels.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }

        return new MouseCursor(
            Math.Clamp(frame.HotspotX, 0, frame.Width - 1),
            Math.Clamp(frame.HotspotY, 0, frame.Height - 1),
            frame.Width,
            frame.Height,
            pixels);
    }

    private void UpdateNativeCursor()
    {
        if (_defaultNativeCursor is null || _cutNativeCursor is null) return;

        var next = GameCursorKind.Default;
        MouseCursor cursor = _defaultNativeCursor;
        if (IsWorldDropDragOutsideInventory() &&
            _dropNativeCursor is not null)
        {
            next = GameCursorKind.DropItem;
            cursor = _dropNativeCursor;
        }
        else if (!IsPointerOverGameUi(MouseState.Position))
        {
            if (TryGetGroundObjectUnderMouse(
                    SceneMousePosition(), out _, out _) &&
                _pickupNativeCursor is not null)
            {
                next = GameCursorKind.PickUpItem;
                cursor = _pickupNativeCursor;
            }
            else if (TryGetFishUnderMouse(
                         SceneMousePosition(), out _) &&
                     _pickupNativeCursor is not null)
            {
                next = GameCursorKind.PickUpItem;
                cursor = _pickupNativeCursor;
            }
            else if (TryGetTreeUnderMouse(SceneMousePosition(), out _))
            {
                next = GameCursorKind.CutTree;
                cursor = _cutNativeCursor;
            }
        }

        if (next == _gameCursorKind) return;
        _gameCursorKind = next;
        Cursor = cursor;
    }
}
