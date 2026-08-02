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
    public const int FrameCount = Columns * Rows;

    private readonly SpriteFrame[] _front;
    private readonly SpriteFrame[] _back;

    private SlimeSpriteRig(SpriteFrame[] front, SpriteFrame[] back)
    {
        _front = front;
        _back = back;
    }

    public static SlimeSpriteRig Load(string frontPath, string backPath) =>
        new(LoadSheet(frontPath), LoadSheet(backPath));

    public SpriteFrame Frame(SlimeRigPose pose) =>
        (pose.UsesBackSheet ? _back : _front)[
            (int)pose.State * Columns + pose.FrameIndex];

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
            SlimeAnimationState.Idle => .16,
            SlimeAnimationState.Move => .10,
            SlimeAnimationState.Attack => .08,
            SlimeAnimationState.Hurt => .10,
            SlimeAnimationState.Die => .14,
            SlimeAnimationState.Spawn => .12,
            _ => .12
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
            projected.Y < -.05f,
            projected.X < 0,
            completed);
    }

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
        for (var row = 0; row < Rows; row++)
        for (var column = 0; column < Columns; column++)
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
            frames[row * Columns + column] = new(
                CellSize, CellSize, CellSize / 2, 108, pixels);
        }
        return frames;
    }
}
