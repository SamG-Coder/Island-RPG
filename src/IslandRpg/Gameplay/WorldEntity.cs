using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class ActorMovementService
{
    public const float BaseMoveSpeed = 2.8f;

    public static float TerrainSpeedMultiplier(
        bool wading,
        float currentHeight,
        float targetHeight)
    {
        var uphill = Math.Max(0, targetHeight - currentHeight);
        return (wading ? .62f : 1f) /
               (1f + uphill * .18f);
    }
}

internal enum EntityGender { Male, Female }

internal enum EntityAction
{
    Idle,
    Move,
    Attack,
    Work,
    Gather,
    Dig,
    Mine,
    Fish,
    Die
}

internal static class EntityActionLifecycle
{
    public const int DirectionCount = 5;

    public static int FramesPerDirection(int totalFrameCount) =>
        Math.Max(1, totalFrameCount / DirectionCount);

    public static bool CompletesAfterAnimation(EntityAction action) =>
        action is EntityAction.Attack or EntityAction.Work or
            EntityAction.Gather or EntityAction.Dig or
            EntityAction.Mine or EntityAction.Fish;

    public static bool HasCompletedAnimation(
        EntityAction action,
        double actionTime,
        int frameCount,
        float secondsPerFrame) =>
        CompletesAfterAnimation(action) &&
        frameCount > 0 && secondsPerFrame > 0 &&
        actionTime >= frameCount * secondsPerFrame;
}

internal readonly record struct DirectionalFrame(int Index, bool Mirror);

internal sealed class WorldEntity
{
    private const float ArrivalDistance = .06f;
    private readonly Queue<Vector2> _path = [];

    public Vector2 Position { get; private set; }
    public Vector2 Target { get; private set; }
    public Vector2 Facing { get; private set; } = new(1, 1);
    public EntityGender Gender { get; private set; }
    public EntityAction Action { get; private set; } = EntityAction.Idle;
    public double ActionTime { get; private set; }
    public float MoveSpeed { get; set; } =
        ActorMovementService.BaseMoveSpeed;
    public float TerrainSpeedMultiplier { get; set; } = 1f;

    public WorldEntity(Vector2 position, EntityGender gender = EntityGender.Male)
    {
        Position = position;
        Target = position;
        Gender = gender;
    }

    public void MoveTo(Vector2 target)
    {
        _path.Clear();
        Target = target;
        SetAction(EntityAction.Move);
    }

    public void FollowPath(IEnumerable<Vector2> path)
    {
        _path.Clear();
        foreach (var waypoint in path) _path.Enqueue(waypoint);
        if (_path.Count == 0)
        {
            Stop();
            return;
        }
        Target = _path.Dequeue();
        SetAction(EntityAction.Move);
    }

    public void Stop()
    {
        Target = Position;
        SetAction(EntityAction.Idle);
    }

    public void TeleportTo(Vector2 position)
    {
        _path.Clear();
        Position = position;
        Target = position;
        SetAction(EntityAction.Idle);
    }

    public void SyncPosition(Vector2 position)
    {
        Position = position;
        Target = position;
    }

    public void AdvanceAction(float elapsed) =>
        ActionTime += Math.Max(0, elapsed);

    public void PrepareForPathRequest()
    {
        // Repathing while already moving must not bounce through Idle. Keeping
        // Move active preserves the walk cycle until the replacement arrives.
        if (Action != EntityAction.Move)
            Stop();
    }

    public void Attack() => SetAction(EntityAction.Attack);
    public void AttackAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Attack);
    }
    public void RestartAttackAt(Vector2 target)
    {
        AttackAt(target);
        ActionTime = 0;
    }
    public void Work() => SetAction(EntityAction.Work);
    public void Gather() => SetAction(EntityAction.Gather);
    public void Die() => SetAction(EntityAction.Die);

    public void Face(Vector2 direction)
    {
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
    }

    public void WorkAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Work);
    }

    public void GatherAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Gather);
    }

    public void DigAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Dig);
    }

    public void MineAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Mine);
    }

    public void FishAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Fish);
    }

    public void SetGender(EntityGender gender)
    {
        if (Gender == gender) return;
        Gender = gender;
        ActionTime = 0;
    }

    public void Update(float elapsed)
    {
        ActionTime += elapsed;
        if (Action != EntityAction.Move) return;
        var displacement = Target - Position;
        var distance = displacement.Length;
        if (distance <= ArrivalDistance)
        {
            Position = Target;
            if (_path.Count > 0)
                Target = _path.Dequeue();
            else
                SetAction(EntityAction.Idle);
            return;
        }
        Facing = displacement / distance;
        Position += Facing * Math.Min(
            distance,
            MoveSpeed * Math.Clamp(TerrainSpeedMultiplier, .35f, 1f) * elapsed);
    }

    private void SetAction(EntityAction action)
    {
        if (Action == action) return;
        Action = action;
        ActionTime = 0;
    }
}

internal static class VillagerDirectionRig
{
    // AoE2 villager sheets author five directions and mirror three of them.
    // Gameplay uses eight directions, clockwise in projected screen space.
    private static readonly (int Authored, bool Mirror)[] Directions =
    [
        // E, SE, S, SW, W, NW, N, NE. The source sheet stores
        // S, SW, W, NW, N; eastern views mirror their western partner.
        (2, true), (1, true), (0, false), (1, false),
        (2, false), (3, false), (4, false), (3, true)
    ];

    public static DirectionalFrame Resolve(
        Vector2 mapDirection,
        int totalFrames,
        int authoredAngles,
        int animationFrame)
    {
        var projected = new Vector2(
            mapDirection.X - mapDirection.Y,
            mapDirection.X + mapDirection.Y);
        var direction = ScreenDirection(projected);
        var mapping = Directions[direction];
        var angleCount = Math.Max(1, authoredAngles);
        var framesPerAngle = Math.Max(1, totalFrames / angleCount);
        var authored = Math.Min(mapping.Authored, angleCount - 1);
        var frame = authored * framesPerAngle + PositiveMod(animationFrame, framesPerAngle);
        return new(Math.Min(frame, totalFrames - 1), mapping.Mirror);
    }

    private static int ScreenDirection(Vector2 direction)
    {
        var x = direction.X;
        var y = direction.Y;
        var absoluteX = MathF.Abs(x);
        var absoluteY = MathF.Abs(y);
        if (absoluteX + absoluteY < .0001f) return 2;

        // Cardinal wedges are deliberately broad. A route must have substantial
        // movement on both screen axes before changing to a diagonal animation.
        const float cardinalRatio = .62f;
        if (absoluteY <= absoluteX * cardinalRatio) return x >= 0 ? 0 : 4;
        if (absoluteX <= absoluteY * cardinalRatio) return y >= 0 ? 2 : 6;
        if (x >= 0) return y >= 0 ? 1 : 7;
        return y >= 0 ? 3 : 5;
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
