using IslandRpg.Assets;
using IslandRpg.Gameplay;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal enum SlimeAnimationState
{
    Idle,
    Move,
    Attack,
    Hurt,
    Die,
    Spawn
}

internal readonly record struct SlimeRigPose(
    SlimeAnimationState State,
    int FrameIndex,
    bool UsesBackSheet,
    bool Mirror,
    bool Completed);

internal sealed class SlimeSpriteRig
{
    public const int Columns = 8;
    public const int Rows = 6;
    public const int CellSize = 128;
    public const float WorldScale = .22f;
    public const int FrameCount = Columns * Rows;
    private const int GroundPadding = 8;

    private readonly SpriteFrame[] _front;
    private readonly SpriteFrame[] _back;
    private static readonly int[] GroundedMoveFrames =
        [0, 1, 3, 5, 6, 7, 6, 1];
    private static readonly int[] GroundedAttackFrames =
        [0, 3, 5, 6, 7, 6, 3, 0];

    private SlimeSpriteRig(SpriteFrame[] front, SpriteFrame[] back)
    {
        _front = front;
        _back = back;
    }

    public static SlimeSpriteRig Load(string frontPath, string backPath) =>
        new(LoadSheet(frontPath), LoadSheet(backPath));

    public SpriteFrame Frame(SlimeRigPose pose) =>
        (pose.UsesBackSheet ? _back : _front)[
            (int)SourceState(pose.State) * Columns +
            AuthoredFrame(pose.State, pose.FrameIndex)];

    public SpriteFrame FrameAt(
        SlimeAnimationState state, int frameIndex, bool back) =>
        (back ? _back : _front)[
            (int)SourceState(state) * Columns +
            AuthoredFrame(state, frameIndex)];

    internal static SlimeAnimationState SourceState(
        SlimeAnimationState state) =>
        state is SlimeAnimationState.Move or SlimeAnimationState.Attack
            ? SlimeAnimationState.Idle
            : state;

    internal static int AuthoredFrame(
        SlimeAnimationState state, int logicalFrame) =>
        state switch
        {
            SlimeAnimationState.Move =>
                GroundedMoveFrames[Math.Clamp(logicalFrame, 0, Columns - 1)],
            SlimeAnimationState.Attack =>
                GroundedAttackFrames[Math.Clamp(logicalFrame, 0, Columns - 1)],
            _ => Math.Clamp(logicalFrame, 0, Columns - 1)
        };

    public static SlimeRigPose Resolve(
        EntityAction action, Vector2 mapFacing, double actionSeconds)
    {
        var state = action switch
        {
            EntityAction.Move => SlimeAnimationState.Move,
            EntityAction.Attack => SlimeAnimationState.Attack,
            EntityAction.Hurt => SlimeAnimationState.Hurt,
            EntityAction.Die => SlimeAnimationState.Die,
            _ => SlimeAnimationState.Idle
        };
        return Resolve(state, mapFacing, actionSeconds);
    }

    public static SlimeRigPose Resolve(
        SlimeAnimationState state,
        Vector2 mapFacing,
        double actionSeconds)
    {
        var projected = new Vector2(
            mapFacing.X - mapFacing.Y,
            mapFacing.X + mapFacing.Y);
        var secondsPerFrame = state switch
        {
            SlimeAnimationState.Idle => .28,
            SlimeAnimationState.Move => .27,
            SlimeAnimationState.Attack => .14,
            SlimeAnimationState.Hurt => .18,
            SlimeAnimationState.Die => .24,
            SlimeAnimationState.Spawn => .20,
            _ => .20
        };
        var rawFrame = Math.Max(0, (int)(actionSeconds / secondsPerFrame));
        var loops = state is SlimeAnimationState.Idle or
            SlimeAnimationState.Move;
        var completed = !loops && rawFrame >= Columns;
        var frame = loops
            ? rawFrame % Columns
            : Math.Min(rawFrame, Columns - 1);
        return new(
            state,
            frame,
            FacesAwayFromCamera(mapFacing),
            projected.X < 0,
            completed);
    }

    internal static bool FacesAwayFromCamera(Vector2 mapFacing) =>
        mapFacing.X + mapFacing.Y < -.05f;

    private static SpriteFrame[] LoadSheet(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Slime sprite sheet not found.", path);
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        if (sheet.Width != Columns * CellSize ||
            sheet.Height != Rows * CellSize)
            throw new InvalidDataException(
                $"Slime sheet must be {Columns}x{Rows} cells of {CellSize}px.");

        var frames = new SpriteFrame[FrameCount];
        var groundedIdleFrames = ExtractGroundedIdleFrames(sheet);
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
        {
            var pixels = row == (int)SlimeAnimationState.Idle
                ? groundedIdleFrames[column]
                : SliceCell(sheet, row, column);
            // Movement deliberately reuses the grounded idle poses. Clean both
            // source rows so a detached remnant from an adjacent authored cell
            // can never become a second, apparently stale, slime frame.
            if (row is (int)SlimeAnimationState.Idle or
                (int)SlimeAnimationState.Move)
                KeepLargestOpaqueComponent(pixels, CellSize, CellSize);
            var groundY = FindGroundAnchorY(pixels, CellSize, CellSize);
            var source = new SpriteFrame(
                CellSize, CellSize, CellSize / 2, groundY, pixels);
            frames[row * Columns + column] = source with
            {
                HotspotY = FindGroundAnchorY(
                    source.Rgba, source.Width, source.Height)
            };
        }
        return frames;
    }

    private static byte[] SliceCell(
        ImageResult sheet, int row, int column)
    {
        var pixels = new byte[CellSize * CellSize * 4];
        for (var y = 0; y < CellSize; y++)
            Buffer.BlockCopy(
                sheet.Data,
                (((row * CellSize + y) * sheet.Width) +
                 column * CellSize) * 4,
                pixels,
                y * CellSize * 4,
                CellSize * 4);
        return pixels;
    }

    private static byte[][] ExtractGroundedIdleFrames(ImageResult sheet)
    {
        // Generated sheets are visually arranged as 128px cells, but the idle
        // art can straddle those boundaries. Detect complete bodies before
        // slicing so overflow never appears as part of the following frame.
        var scanHeight = Math.Min(sheet.Height, CellSize + CellSize / 3);
        var labels = new bool[sheet.Width * scanHeight];
        var queue = new Queue<int>();
        var components = new List<List<int>>();
        for (var index = 0; index < labels.Length; index++)
        {
            if (labels[index] || sheet.Data[index * 4 + 3] <= 12) continue;
            var component = new List<int>();
            labels[index] = true;
            queue.Enqueue(index);
            while (queue.TryDequeue(out var current))
            {
                component.Add(current);
                var x = current % sheet.Width;
                var y = current / sheet.Width;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);

                void Visit(int targetX, int targetY)
                {
                    if ((uint)targetX >= (uint)sheet.Width ||
                        (uint)targetY >= (uint)scanHeight) return;
                    var target = targetY * sheet.Width + targetX;
                    if (labels[target] ||
                        sheet.Data[target * 4 + 3] <= 12) return;
                    labels[target] = true;
                    queue.Enqueue(target);
                }
            }
            if (component.Count >= 100) components.Add(component);
        }

        var bodies = components
            .OrderByDescending(component => component.Count)
            .Take(Columns)
            .OrderBy(component => component.Average(
                index => index % sheet.Width))
            .ToArray();
        if (bodies.Length != Columns)
            throw new InvalidDataException(
                $"Slime idle row contains {bodies.Length} complete bodies; " +
                $"expected {Columns}.");

        var result = new byte[Columns][];
        for (var frame = 0; frame < Columns; frame++)
        {
            var component = bodies[frame];
            var minimumX = component.Min(index => index % sheet.Width);
            var maximumX = component.Max(index => index % sheet.Width);
            var minimumY = component.Min(index => index / sheet.Width);
            var maximumY = component.Max(index => index / sheet.Width);
            var bodyWidth = maximumX - minimumX + 1;
            var bodyHeight = maximumY - minimumY + 1;
            if (bodyWidth > CellSize || bodyHeight + GroundPadding > CellSize)
                throw new InvalidDataException(
                    "A slime idle body does not fit its runtime cell.");
            var left = (CellSize - bodyWidth) / 2;
            var top = CellSize - GroundPadding - bodyHeight;
            var pixels = new byte[CellSize * CellSize * 4];
            foreach (var source in component)
            {
                var sourceX = source % sheet.Width;
                var sourceY = source / sheet.Width;
                var targetX = left + sourceX - minimumX;
                var targetY = top + sourceY - minimumY;
                Buffer.BlockCopy(
                    sheet.Data, source * 4,
                    pixels, (targetY * CellSize + targetX) * 4, 4);
            }
            result[frame] = pixels;
        }
        return result;
    }

    public void ExportMovementPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        var frame = DisplayFrame(
            FrameAt(SlimeAnimationState.Move, 0, back: false));
        var exactWidth = frame.Width * Columns;
        var exactHeight = frame.Height * 2;
        var exact = new byte[exactWidth * exactHeight * 4];
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < Columns; column++)
            CopyFrame(
                DisplayFrame(FrameAt(
                    SlimeAnimationState.Move, column, row == 1)),
                exact, exactWidth, column * frame.Width,
                row * frame.Height);
        PngScreenshotWriter.Write(
            Path.Combine(directory, "slime-runtime-move-sheet.png"),
            exact, exactWidth, exactHeight, flipVertically: false);

        const int zoom = 4;
        const int padding = 5;
        var cellWidth = (frame.Width + padding * 2) * zoom;
        var cellHeight = (frame.Height + padding * 2) * zoom;
        var previewWidth = cellWidth * Columns;
        var previewHeight = cellHeight * 2;
        var preview = new byte[previewWidth * previewHeight * 4];
        FillCheckerboard(preview, previewWidth, previewHeight, 16);
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < Columns; column++)
        {
            var movementFrame = DisplayFrame(FrameAt(
                SlimeAnimationState.Move, column, row == 1));
            var left = column * cellWidth + padding * zoom;
            var top = row * cellHeight + padding * zoom;
            CopyFrameScaled(
                movementFrame, preview, previewWidth, left, top, zoom);
            DrawHorizontalLine(
                preview, previewWidth, previewHeight,
                top + movementFrame.HotspotY * zoom,
                column * cellWidth, (column + 1) * cellWidth,
                235, 82, 82);
        }
        PngScreenshotWriter.Write(
            Path.Combine(directory, "slime-runtime-move-preview.png"),
            preview, previewWidth, previewHeight, flipVertically: false);
    }

    private static SpriteFrame DisplayFrame(SpriteFrame source)
    {
        var resized = SpriteFrameTransforms.Resize(source, WorldScale);
        return resized with
        {
            HotspotY = FindGroundAnchorY(
                resized.Rgba, resized.Width, resized.Height)
        };
    }

    private static void CopyFrame(
        SpriteFrame source, byte[] target, int targetWidth, int left, int top)
    {
        for (var y = 0; y < source.Height; y++)
            Buffer.BlockCopy(
                source.Rgba, y * source.Width * 4,
                target, ((top + y) * targetWidth + left) * 4,
                source.Width * 4);
    }

    private static void CopyFrameScaled(
        SpriteFrame source, byte[] target, int targetWidth,
        int left, int top, int scale)
    {
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        for (var offsetY = 0; offsetY < scale; offsetY++)
        for (var offsetX = 0; offsetX < scale; offsetX++)
        {
            var sourceIndex = (y * source.Width + x) * 4;
            var alpha = source.Rgba[sourceIndex + 3];
            if (alpha == 0) continue;
            var targetIndex =
                (((top + y * scale + offsetY) * targetWidth) +
                 left + x * scale + offsetX) * 4;
            var amount = alpha / 255f;
            target[targetIndex] = (byte)MathF.Round(
                source.Rgba[sourceIndex] * amount +
                target[targetIndex] * (1 - amount));
            target[targetIndex + 1] = (byte)MathF.Round(
                source.Rgba[sourceIndex + 1] * amount +
                target[targetIndex + 1] * (1 - amount));
            target[targetIndex + 2] = (byte)MathF.Round(
                source.Rgba[sourceIndex + 2] * amount +
                target[targetIndex + 2] * (1 - amount));
            target[targetIndex + 3] = 255;
        }
    }

    private static void FillCheckerboard(
        byte[] pixels, int width, int height, int square)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var light = ((x / square) + (y / square)) % 2 == 0;
            var value = (byte)(light ? 190 : 145);
            var index = (y * width + x) * 4;
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
    }

    private static void DrawHorizontalLine(
        byte[] pixels, int width, int height, int y,
        int startX, int endX, byte red, byte green, byte blue)
    {
        if ((uint)y >= (uint)height) return;
        for (var x = Math.Max(0, startX); x < Math.Min(width, endX); x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = red;
            pixels[index + 1] = green;
            pixels[index + 2] = blue;
            pixels[index + 3] = 255;
        }
    }

    internal static bool HasSingleOpaqueComponent(SpriteFrame frame) =>
        CountOpaqueComponents(frame.Rgba, frame.Width, frame.Height) <= 1;

    internal static bool IsAnchoredBelowOpaquePixels(SpriteFrame frame) =>
        frame.HotspotY == FindGroundAnchorY(
            frame.Rgba, frame.Width, frame.Height);

    private static int FindGroundAnchorY(
        byte[] pixels, int width, int height)
    {
        for (var y = height - 1; y >= 0; y--)
        for (var x = 0; x < width; x++)
            if (pixels[(y * width + x) * 4 + 3] != 0)
                return Math.Min(height, y + 1);
        return height;
    }

    private static void KeepLargestOpaqueComponent(
        byte[] pixels, int width, int height)
    {
        var labels = new int[width * height];
        var sizes = new List<int> { 0 };
        var queue = new Queue<int>();
        var label = 0;
        for (var index = 0; index < labels.Length; index++)
        {
            if (labels[index] != 0 || pixels[index * 4 + 3] <= 12) continue;
            label++;
            sizes.Add(0);
            labels[index] = label;
            queue.Enqueue(index);
            while (queue.TryDequeue(out var current))
            {
                sizes[label]++;
                var x = current % width;
                var y = current / width;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);

                void Visit(int targetX, int targetY)
                {
                    if ((uint)targetX >= (uint)width ||
                        (uint)targetY >= (uint)height) return;
                    var target = targetY * width + targetX;
                    if (labels[target] != 0 ||
                        pixels[target * 4 + 3] <= 12) return;
                    labels[target] = label;
                    queue.Enqueue(target);
                }
            }
        }
        if (label <= 1) return;
        var keep = 1;
        for (var candidate = 2; candidate < sizes.Count; candidate++)
            if (sizes[candidate] > sizes[keep]) keep = candidate;
        for (var index = 0; index < labels.Length; index++)
            if (labels[index] != keep)
            {
                pixels[index * 4] = 0;
                pixels[index * 4 + 1] = 0;
                pixels[index * 4 + 2] = 0;
                pixels[index * 4 + 3] = 0;
            }
    }

    private static int CountOpaqueComponents(
        byte[] pixels, int width, int height)
    {
        var seen = new bool[width * height];
        var queue = new Queue<int>();
        var count = 0;
        for (var index = 0; index < seen.Length; index++)
        {
            if (seen[index] || pixels[index * 4 + 3] <= 12) continue;
            count++;
            seen[index] = true;
            queue.Enqueue(index);
            while (queue.TryDequeue(out var current))
            {
                var x = current % width;
                var y = current / width;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);

                void Visit(int targetX, int targetY)
                {
                    if ((uint)targetX >= (uint)width ||
                        (uint)targetY >= (uint)height) return;
                    var target = targetY * width + targetX;
                    if (seen[target] || pixels[target * 4 + 3] <= 12) return;
                    seen[target] = true;
                    queue.Enqueue(target);
                }
            }
        }
        return count;
    }
}
