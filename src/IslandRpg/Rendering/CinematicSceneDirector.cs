using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal enum SceneCameraTarget : byte
{
    Fixed,
    Player,
    Actor
}

internal readonly record struct SceneCameraShot(
    double StartsAt,
    double EndsAt,
    SceneCameraTarget Target,
    Vector2 Position,
    float StartZoom,
    float EndZoom,
    string? ActorId = null);

internal readonly record struct SceneTimedCue(
    double At,
    string Name);

internal sealed class CinematicSceneDirector
{
    private readonly SceneCameraShot[] _shots;
    private readonly SceneTimedCue[] _cues;
    private int _nextCue;

    public CinematicSceneDirector(
        double duration,
        IReadOnlyList<SceneCameraShot> shots,
        IReadOnlyList<SceneTimedCue> cues)
    {
        Duration = Math.Max(0, duration);
        _shots = shots.OrderBy(value => value.StartsAt).ToArray();
        _cues = cues.OrderBy(value => value.At).ToArray();
    }

    public double Duration { get; }
    public double Time { get; private set; }
    public bool Active { get; private set; }
    public bool Complete => !Active && Time >= Duration;

    public void Start()
    {
        Time = 0;
        _nextCue = 0;
        Active = Duration > 0;
    }

    public void Advance(double elapsed)
    {
        if (!Active || elapsed <= 0) return;
        Time = Math.Min(Duration, Time + elapsed);
        if (Time >= Duration) Active = false;
    }

    public bool TryDequeueCue(out string cue)
    {
        if (_nextCue < _cues.Length &&
            _cues[_nextCue].At <= Time)
        {
            cue = _cues[_nextCue++].Name;
            return true;
        }
        cue = string.Empty;
        return false;
    }

    public SceneCameraShot? CurrentShot()
    {
        for (var index = _shots.Length - 1; index >= 0; index--)
            if (Time >= _shots[index].StartsAt &&
                Time <= _shots[index].EndsAt)
                return _shots[index];
        return null;
    }

    public float ShotProgress(SceneCameraShot shot) =>
        shot.EndsAt <= shot.StartsAt
            ? 1
            : Math.Clamp((float)((Time - shot.StartsAt) /
                (shot.EndsAt - shot.StartsAt)), 0, 1);

    public float CurrentZoom(SceneCameraShot shot)
    {
        var t = SmoothStep(ShotProgress(shot));
        return shot.StartZoom + (shot.EndZoom - shot.StartZoom) * t;
    }

    public static float SmoothStep(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }
}
