namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void CancelWorldLevelWork(bool clearMinimap)
    {
        if (_atlasOpen)
            CloseWorldAtlasSession();
        _worldActions.CancelPath();
        CancelPendingChunkLoad();
        CancelMinimapBuild(clearMinimap);
        ClearFallbackCaveSampling();
        _queuedAction = null;
        _moveMarker = null;
        _activeMiningKey = null;
        _miningContext.Close();
    }
}
