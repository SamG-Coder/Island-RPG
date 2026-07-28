using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool UpdateDeveloperSettings(
        Vector2 pointer, Vector4 panel)
    {
        if (!_settingsMenu.DeveloperModeEnabled)
            return false;
        _settingsMenu.LayoutContent(panel);
        var list = _settingsMenu.ContentList;
        if (_activePlayer is not null &&
            list.VisibleIndices.Contains(0) &&
            DeveloperSettingsController.MapToolBounds(list)
                .Contains(pointer))
        {
            OpenDeveloperMap();
            return true;
        }
        if (_activeWorld is not null &&
            list.VisibleIndices.Contains(1) &&
            DeveloperSettingsController.AdvanceTimeBounds(list)
                .Contains(pointer))
        {
            AdvanceWorldTimeForDeveloper();
            return true;
        }
        if (_activeWorld is not null &&
            list.VisibleIndices.Contains(1) &&
            DeveloperSettingsController.WorldLevelBounds(list)
                .Contains(pointer))
        {
            SwitchWorldLevelForDeveloper();
            return true;
        }
        if (list.VisibleIndices.Contains(2) &&
            DeveloperSettingsController.ItemBankBounds(list)
                .Contains(pointer))
        {
            OpenDeveloperItemBank();
            return true;
        }
        var changed = _developerSettings.TryUpdate(
            pointer, list, _activePlayer, out var updated);
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

    private void SwitchWorldLevelForDeveloper()
    {
        CancelWorldLevelWork(clearMinimap: true);
        _activeWorldLevel =
            _activeWorldLevel == (int)WorldLevel.Overworld
                ? (int)WorldLevel.Underground
                : (int)WorldLevel.Overworld;
        _caveEntranceLightWorld = null;
        StreamWorld();
    }
}
