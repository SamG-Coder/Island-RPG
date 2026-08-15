using IslandRpg.Gameplay;

namespace IslandRpg.Simulation;

/// <summary>
/// Published remote skill clip: one-shot Gather expires, looping skills
/// stay until the actor walks, and a generation nibble restarts a clip
/// that is already playing.
/// </summary>
public static class ActorSkillStance
{
    public const double OneShotSeconds = .75;

    public static int OneShotTicks { get; } =
        (int)Math.Ceiling(OneShotSeconds * SimulationTiming.TicksPerSecond);

    public readonly record struct Snapshot(
        EntityAction Action,
        byte Generation,
        long? ExpiresAtTick);

    public static Snapshot Idle { get; } = new(EntityAction.Idle, 0, null);

    public static bool IsLooping(EntityAction action) =>
        action is EntityAction.Work or EntityAction.Build or
            EntityAction.Mine or EntityAction.Dig or EntityAction.Fish;

    public static bool IsPublished(EntityAction action) =>
        IsLooping(action) ||
        action is EntityAction.Gather or EntityAction.Attack;

    /// <summary>
    /// Idle is the explicit cancel of a published clip. Boat occupants cannot
    /// send Stop, so remotes only leave Fish when the fisher presents Idle.
    /// </summary>
    public static bool CanPresent(EntityAction action) =>
        IsPublished(action) || action == EntityAction.Idle;

    public static int TicksForSeconds(double seconds) =>
        (int)Math.Ceiling(
            Math.Max(0, seconds) * SimulationTiming.TicksPerSecond);

    public static Snapshot Begin(
        EntityAction action,
        Snapshot previous,
        long tick,
        double durationSeconds = OneShotSeconds)
    {
        if (!IsPublished(action))
            return new(EntityAction.Idle, previous.Generation, null);
        var generation = previous.Action == action && IsLooping(action)
            ? previous.Generation
            : (byte)((previous.Generation + 1) & 0x0F);
        return new(
            action,
            generation,
            IsLooping(action)
                ? null
                : tick + TicksForSeconds(durationSeconds));
    }

    public static Snapshot FromAcceptedIntent(
        SessionIntent intent, Snapshot previous, long tick)
    {
        if (intent is PresentSkillIntent present)
            return Begin(
                present.Action, previous, tick, present.DurationSeconds);
        var action = ActionFor(intent);
        if (action is not { } next) return previous;
        if (next == EntityAction.Idle)
            return new(EntityAction.Idle, previous.Generation, null);
        if (IsOneShotCommit(intent))
            return previous;
        return Begin(next, previous, tick);
    }

    public static bool IsOneShotCommit(SessionIntent intent) =>
        intent is PickUpWorldObjectIntent or HarvestCropIntent or
            CookOnCampfireIntent or CookStewIntent or
            GatherTreeStickIntent or GatherFibreIntent or
            GatherBerriesIntent or CatchFishIntent or
            FillBucketIntent;

    public static EntityAction? ActionFor(SessionIntent intent) =>
        intent switch
        {
            PresentSkillIntent present => present.Action,
            PickUpWorldObjectIntent or HarvestCropIntent or
                CookOnCampfireIntent or CookStewIntent or
                GatherTreeStickIntent or GatherFibreIntent or
                GatherBerriesIntent or FillBucketIntent =>
                EntityAction.Gather,
            StrikeTreeIntent or StrikePlantedTreeIntent =>
                EntityAction.Work,
            BuildConstructionIntent => EntityAction.Build,
            MineResourceIntent => EntityAction.Mine,
            CatchFishIntent => EntityAction.Fish,
            WorkExcavationIntent or StartExcavationIntent =>
                EntityAction.Dig,
            WalkIntent => EntityAction.Idle,
            _ => null
        };

    public static Snapshot Advance(Snapshot state, long tick)
    {
        if (state.ExpiresAtTick is { } expires && tick >= expires)
            return new(EntityAction.Idle, state.Generation, null);
        return state;
    }

    public static byte Pack(Snapshot state) =>
        (byte)(((state.Generation & 0x0F) << 4) |
               ((int)state.Action & 0x0F));

    public static EntityAction UnpackAction(byte packed) =>
        (EntityAction)(packed & 0x0F);

    public static byte UnpackGeneration(byte packed) =>
        (byte)(packed >> 4);
}