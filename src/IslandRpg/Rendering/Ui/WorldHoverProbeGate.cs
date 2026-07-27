using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class WorldHoverProbeGate
{
    private const double MaximumStaleSeconds = .10;
    private const float ImmediateCameraProbeDistance = 48;
    private Vector2 _mouse;
    private Vector2 _camera;
    private float _zoom;
    private bool _blocked;
    private bool _initialized;
    private double _lastProbeSeconds;

    public bool ShouldProbe(
        Vector2 mouse,
        Vector2 camera,
        float zoom,
        bool blocked,
        double nowSeconds)
    {
        var inputChanged = !_initialized ||
                           mouse != _mouse ||
                           zoom != _zoom ||
                           blocked != _blocked;
        var cameraJumped = _initialized &&
                           (camera - _camera).LengthSquared >=
                           ImmediateCameraProbeDistance *
                           ImmediateCameraProbeDistance;
        var refreshDue =
            nowSeconds - _lastProbeSeconds >= MaximumStaleSeconds;
        // Following a moving player changes the camera every frame. The world
        // beneath a stationary cursor still needs refreshing, but probing the
        // complete interaction set at render frequency is unnecessary.
        if (!inputChanged && !cameraJumped && !refreshDue)
            return false;
        _initialized = true;
        _mouse = mouse;
        _camera = camera;
        _zoom = zoom;
        _blocked = blocked;
        _lastProbeSeconds = nowSeconds;
        return true;
    }

    public void Invalidate() => _initialized = false;
}
