using IslandRpg.World;
using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void CloseWorldAtlasSession()
    {
        _atlasOpen = false;
        _atlasDragging = false;
        _atlasLeftWasDown = false;
        _atlasGeneration.CancelAll(() =>
        {
            if (!_atlasOpen)
                MacroHydrology.ClearAtlasCache();
        });
        _atlasTextures.Clear(GL.DeleteTexture);
        MacroHydrology.ClearAtlasCache();
        _visibleAtlasTiles.Clear();
        _visibleAtlasTileOrder = [];
        _visibleAtlasRenderOrder = [];
        Interlocked.Exchange(ref _atlasDone, 0);
        Volatile.Write(ref _atlasTotal, 1);
    }
}
