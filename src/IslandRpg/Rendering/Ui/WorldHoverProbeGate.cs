using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class WorldHoverProbeGate
{
    private const double MaximumStaleSeconds = .10;
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
        var changed = !_initialized ||
                      mouse != _mouse ||
                      camera != _camera ||
                      zoom != _zoom ||
                      blocked != _blocked ||
                      nowSeconds - _lastProbeSeconds >=
                      MaximumStaleSeconds;
        if (!changed) return false;
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
