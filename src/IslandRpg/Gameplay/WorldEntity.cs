using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class ActorMovementService
{
    public const float BaseMoveSpeed =
        IslandRpg.Navigation.ActorMovementService.BaseMoveSpeed;

    public static float TerrainSpeedMultiplier(
        bool wading,
        float currentHeight,
        float targetHeight)
    {
        return IslandRpg.Navigation.ActorMovementService.TerrainSpeedMultiplier(
            wading,
            currentHeight,
            targetHeight);
    }
}

internal enum EntityGender { Male, Female }

internal static class EntityActionLifecycle
{
    public const int DirectionCount = 5;

    public static int FramesPerDirection(int totalFrameCount) =>
        Math.Max(1, totalFrameCount / DirectionCount);

    public static bool CompletesAfterAnimation(EntityAction action) =>
        action is EntityAction.Attack or EntityAction.Work or
            EntityAction.Build or
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
    internal const float RemoteWalkIdleHoldSeconds = .12f;
    private readonly Queue<Vector2> _path = [];
    private float _remoteStillSeconds;
    private bool _awaitingPath;

    public Vector2 Position { get; private set; }
    public Vector2 Target { get; private set; }
    public Vector2 Facing { get; private set; } = new(1, 1);
    public EntityGender Gender { get; private set; }
    public EntityAction Action { get; private set; } = EntityAction.Idle;
    public byte VisualGeneration { get; private set; }
    public double ActionTime { get; private set; }
    public float MoveSpeed { get; set; } =
        ActorMovementService.BaseMoveSpeed;
    public float TerrainSpeedMultiplier { get; set; } = 1f;
    public float StatusSpeedMultiplier { get; set; } = 1f;

    public WorldEntity(Vector2 position, EntityGender gender = EntityGender.Male)
    {
        Position = position;
        Target = position;
        Gender = gender;
    }

    public void MoveTo(Vector2 target)
    {
        _awaitingPath = false;
        _path.Clear();
        Target = target;
        SetAction(EntityAction.Move);
    }

    public void FollowPath(IEnumerable<Vector2> path)
    {
        _awaitingPath = false;
        _path.Clear();
        foreach (var waypoint in path)
        {
            if (!float.IsFinite(waypoint.X) || !float.IsFinite(waypoint.Y))
                continue;
            _path.Enqueue(waypoint);
        }
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
        _awaitingPath = false;
        Target = Position;
        SetAction(EntityAction.Idle);
    }

    public void TeleportTo(Vector2 position)
    {
        _awaitingPath = false;
        _path.Clear();
        Position = position;
        Target = position;
        SetAction(EntityAction.Idle);
    }

    public void SyncPosition(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return;
        Position = position;
        Target = position;
    }

    public void CorrectPosition(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return;
        Position = position;
    }

    /// <summary>
    /// Applies a network-sampled pose without restarting the walk cycle.
    /// Snapshots arrive at 20 Hz; ActionTime must keep advancing at display rate.
    /// </summary>
    public void PresentNetworkLocomotion(
        Vector2 position, Vector2 velocity, bool moving)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return;
        Position = position;
        if (moving)
        {
            Face(velocity);
            if (Action != EntityAction.Move)
                SetAction(EntityAction.Move);
            Target = velocity.LengthSquared > .0001f &&
                     float.IsFinite(velocity.X) &&
                     float.IsFinite(velocity.Y)
                ? position + velocity
                : position;
            return;
        }

        Target = position;
        if (Action == EntityAction.Move)
            SetAction(EntityAction.Idle);
    }

    /// <summary>
    /// Remote locomotion. The sample is the current interpolated pose, not a
    /// walk destination. Keep the Move cycle running and advance ActionTime
    /// at display rate so observers see the same walk sheet as a local click.
    /// </summary>
    public void PresentRemoteWalk(
        Vector2 sample, Vector2 velocity, bool moving, float elapsed)
    {
        if (!float.IsFinite(sample.X) || !float.IsFinite(sample.Y))
            return;
        elapsed = Math.Max(0, elapsed);
        var displacement = sample - Position;
        var hasVelocity = velocity.LengthSquared > .0001f;
        var displaced = displacement.LengthSquared > .0004f;
        if (moving || hasVelocity || displaced)
        {
            _remoteStillSeconds = 0;
            if (Action != EntityAction.Move)
                SetAction(EntityAction.Move);
            if (hasVelocity)
                Face(velocity);
            else if (displaced)
                Face(displacement);
            CorrectPosition(sample);
            AdvanceAction(elapsed);
            return;
        }

        _remoteStillSeconds += elapsed;
        if (Action == EntityAction.Move &&
            _remoteStillSeconds < RemoteWalkIdleHoldSeconds)
        {
            CorrectPosition(sample);
            AdvanceAction(elapsed);
            return;
        }

        CorrectPosition(sample);
        if (Action == EntityAction.Move)
            Stop();
        else
            AdvanceAction(elapsed);
    }

    public void AdvanceAction(float elapsed) =>
        ActionTime += Math.Max(0, elapsed);

    public void RestartActionTime() => ActionTime = 0;

    public void PrepareForPathRequest()
    {
        // A replacement route is calculated from the current position. Do not
        // continue along the superseded route while that asynchronous request
        // is pending, or the completed path will begin behind the actor.
        // Stay in Move and keep the walk cycle running so follow/repath does
        // not drop to idle for a frame.
        _path.Clear();
        Target = Position;
        _awaitingPath = true;
        if (Action != EntityAction.Move)
            SetAction(EntityAction.Move);
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

    public void BuildAt(Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        SetAction(EntityAction.Build);
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

    public void PresentSkill(
        EntityAction action, byte generation, Vector2 target)
    {
        _path.Clear();
        Target = Position;
        var direction = target - Position;
        if (direction.LengthSquared > .0001f)
            Facing = direction.Normalized();
        if (Action == action && VisualGeneration == generation) return;
        Action = action;
        VisualGeneration = generation;
        ActionTime = 0;
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
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) ||
            !float.IsFinite(Target.X) || !float.IsFinite(Target.Y))
        {
            Stop();
            return;
        }
        var remainingMovement = MoveSpeed *
            Math.Clamp(
                float.IsFinite(TerrainSpeedMultiplier)
                    ? TerrainSpeedMultiplier : 1f,
                .35f, 1f) *
            Math.Clamp(
                float.IsFinite(StatusSpeedMultiplier)
                    ? StatusSpeedMultiplier : 1f,
                0, 1f) *
            Math.Max(0, elapsed);
        // Dense waypoint paths must not be able to hang the game thread.
        var remainingWaypoints = _path.Count + 8;
        while (Action == EntityAction.Move && remainingWaypoints-- > 0)
        {
            var displacement = Target - Position;
            var distance = displacement.Length;
            if (!float.IsFinite(distance) || !float.IsFinite(remainingMovement))
            {
                Stop();
                break;
            }
            if (distance <= ArrivalDistance)
            {
                Position = Target;
                if (_path.Count > 0)
                {
                    Target = _path.Dequeue();
                    continue;
                }
                if (_awaitingPath)
                    break;
                SetAction(EntityAction.Idle);
                break;
            }
            if (remainingMovement <= 0) break;
            Facing = displacement / distance;
            var step = Math.Min(distance, remainingMovement);
            Position += Facing * step;
            remainingMovement -= step;
        }
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
