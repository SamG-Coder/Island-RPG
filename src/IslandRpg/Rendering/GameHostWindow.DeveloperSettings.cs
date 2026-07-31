using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly ToggleControlState _navigationBlocksToggle = new(
        "Pathing blocks",
        "Show object collision cells in the world.");
    private readonly ToggleControlState _unlimitedZoomToggle = new(
        "Unlimited zoom",
        "Remove the normal gameplay camera zoom limits.");
    private readonly ToggleControlState _zoomScaledLoadingToggle = new(
        "Zoom-scaled world loading",
        "Load more surrounding chunks as the camera zooms out.");
    private readonly ToggleControlState _useTestAssetsToggle = new(
        "Use Test Assets",
        "Load Resources/Images/TestAssets after restarting.");

    private bool UpdateDeveloperSettings(
        Vector2 pointer, Vector4 panel)
    {
        if (!_settingsMenu.DeveloperModeEnabled)
            return false;
        _settingsMenu.LayoutContent(panel);
        var list = _settingsMenu.ContentList;
        _navigationBlocksToggle.Layout(
            DeveloperSettingsController.NavigationBlocksBounds(list),
            horizontalInset: 0);
        _zoomScaledLoadingToggle.Layout(
            DeveloperSettingsController.ZoomScaledLoadingBounds(list),
            horizontalInset: 0);
        var settings = _saves.LoadSettings();
        _useTestAssetsToggle.SetChecked(settings.UseTestAssets);
        _useTestAssetsToggle.Layout(
            DeveloperSettingsController.UseTestAssetsBounds(list),
            horizontalInset: 0);
        if (_activePlayer is not null &&
            list.VisibleIndices.Contains(
                DeveloperSettingsController.PrimaryToolsIndex) &&
            DeveloperSettingsController.MapToolBounds(list)
                .Contains(pointer))
        {
            OpenDeveloperMap();
            return true;
        }
        if (_activeWorld is not null &&
            list.VisibleIndices.Contains(
                DeveloperSettingsController.WorldToolsIndex) &&
            DeveloperSettingsController.AdvanceTimeBounds(list)
                .Contains(pointer))
        {
            AdvanceWorldTimeForDeveloper();
            return true;
        }
        if (_activeWorld is not null &&
            list.VisibleIndices.Contains(
                DeveloperSettingsController.WorldToolsIndex) &&
            DeveloperSettingsController.WorldLevelBounds(list)
                .Contains(pointer))
        {
            SwitchWorldLevelForDeveloper();
            return true;
        }
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.PrimaryToolsIndex) &&
            DeveloperSettingsController.ItemBankBounds(list)
                .Contains(pointer))
        {
            OpenDeveloperItemBank();
            return true;
        }
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.SoundAuditionIndex))
        {
            if (DeveloperSettingsController.SoundPreviousBounds(list)
                .Contains(pointer))
            {
                SelectPreviousDeveloperSound();
                return true;
            }
            if (DeveloperSettingsController.SoundPlayBounds(list)
                .Contains(pointer))
            {
                PlaySelectedDeveloperSound();
                return true;
            }
            if (DeveloperSettingsController.SoundNextBounds(list)
                .Contains(pointer))
            {
                SelectNextDeveloperSound();
                return true;
            }
        }
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.NavigationBlocksIndex) &&
            _navigationBlocksToggle.ToggleAt(pointer))
            return true;
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.ZoomScaledLoadingIndex) &&
            _zoomScaledLoadingToggle.ToggleAt(pointer))
            return true;
        if (list.VisibleIndices.Contains(
                DeveloperSettingsController.UseTestAssetsIndex) &&
            _useTestAssetsToggle.ToggleAt(pointer))
        {
            _saves.SaveSettings(settings with
            {
                UseTestAssets = _useTestAssetsToggle.IsChecked
            });
            _chatUi.AddMessage(
                "Asset source change will apply after restarting the game.",
                ChatMessageStyle.Action);
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
