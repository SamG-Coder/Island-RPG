using IslandRpg.Audio;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Age2MusicPlayer? _musicPlayer;

    private void InitializeMusic()
    {
        _musicPlayer = new Age2MusicPlayer(_install);
        ApplyMusicSettings();
    }

    private void ApplyMusicSettings()
    {
        var settings = _saves.LoadSettings();
        _musicPlayer?.Configure(
            settings.MusicEnabled,
            settings.MasterVolume);
    }

    internal bool UpdateSoundSettings(Vector2 pointer, Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        for (var option = 0; option < 2; option++)
        {
            if (!_settingsMenu.ContentList.VisibleIndices.Contains(option) ||
                !_settingsMenu.OptionBounds(option).Contains(pointer))
                continue;
            var settings = _saves.LoadSettings();
            settings = option == 0
                ? settings with
                {
                    MusicEnabled = !settings.MusicEnabled
                }
                : settings with
                {
                    MasterVolume =
                        MusicSettingsController.NextVolume(
                            settings.MasterVolume)
                };
            _saves.SaveSettings(settings);
            ApplyMusicSettings();
            return true;
        }
        return false;
    }
}
