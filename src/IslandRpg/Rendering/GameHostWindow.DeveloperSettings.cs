using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool UpdateDeveloperSettings(
        Vector2 pointer, Vector4 panel)
    {
        if (!_settingsMenu.DeveloperModeEnabled)
            return false;
        if (_activePlayer is not null &&
            DeveloperSettingsController.MapToolBounds(panel)
                .Contains(pointer))
        {
            OpenDeveloperMap();
            return true;
        }
        if (_activeWorld is not null &&
            DeveloperSettingsController.AdvanceTimeBounds(panel)
                .Contains(pointer))
        {
            AdvanceWorldTimeForDeveloper();
            return true;
        }
        var changed = _developerSettings.TryUpdate(
            pointer, panel, _activePlayer, out var updated);
        if (!changed || updated is null) return false;
        _activePlayer = updated;
        _saves.SavePlayer(_activePlayer);
        return true;
    }

    private void AdvanceWorldTimeForDeveloper()
    {
        const double twelveHours = 12 * 60 * 60;
        _worldGameSeconds += twelveHours;
        _activeWorld = _activeWorld! with
        {
            ElapsedGameSeconds = _worldGameSeconds,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SaveWorld(_activeWorld);
        var time = Gameplay.WorldTime.At(_worldGameSeconds);
        _chatUi.AddMessage(
            $"Developer time advance: Day {time.Day}, " +
            $"{time.Hour:00}:{time.Minute:00}.",
            ChatMessageStyle.Action);
    }
}
