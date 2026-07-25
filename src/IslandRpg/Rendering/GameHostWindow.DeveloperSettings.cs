using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool UpdateDeveloperSettings(
        Vector2 pointer, Vector4 panel)
    {
        if (!System.Diagnostics.Debugger.IsAttached)
            return false;
        var changed = _developerSettings.TryUpdate(
            pointer, panel, _activePlayer, out var updated);
        if (!changed || updated is null) return false;
        _activePlayer = updated;
        _saves.SavePlayer(_activePlayer);
        return true;
    }
}
