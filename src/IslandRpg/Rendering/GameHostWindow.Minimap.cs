using IslandRpg.Assets;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly MinimapControlState _minimapUi = new();
    private SpriteFrame? _minimapFrame;
    private int _minimapTexture;
    private Vector2i _minimapCenter =
        new(int.MinValue, int.MinValue);
    private int _minimapLevel = int.MinValue;
    private byte[]? _minimapTerrain;
    private Task<MinimapBuildResult>? _minimapBuildTask;
    private CancellationTokenSource? _minimapBuildCancellation;

    private sealed record MinimapBuildResult(
        Vector2i Center,
        int Level,
        byte[] Terrain,
        byte[] Pixels);

    private void CancelMinimapBuild(bool clearTerrain)
    {
        _minimapBuildCancellation?.Cancel();
        _minimapBuildCancellation?.Dispose();
        _minimapBuildCancellation = null;
        if (_minimapBuildTask is { } abandoned)
            _ = abandoned.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        _minimapBuildTask = null;
        if (!clearTerrain) return;
        _minimapTerrain = null;
        _minimapCenter = new(int.MinValue, int.MinValue);
        _minimapLevel = int.MinValue;
    }
}
