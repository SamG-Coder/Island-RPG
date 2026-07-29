using IslandRpg.Audio;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Age2MusicPlayer? _musicPlayer;
    private readonly SliderControlState _musicVolumeSlider = new();

    private void InitializeMusic()
    {
        _musicPlayer = new Age2MusicPlayer(_install);
        _musicVolumeSlider.ValueChanged += value =>
        {
            var settings = _saves.LoadSettings();
            _musicPlayer?.Configure(settings.MusicEnabled, value);
        };
        _musicVolumeSlider.DragCompleted += value =>
        {
            var settings = _saves.LoadSettings() with
            {
                MasterVolume = value
            };
            _saves.SaveSettings(settings);
            ApplyMusicSettings();
        };
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
        if (!_settingsMenu.ContentList.VisibleIndices.Contains(0) ||
            !_settingsMenu.OptionBounds(0).Contains(pointer))
            return false;
        var current = _saves.LoadSettings();
        var settings = current with
        {
            MusicEnabled = !current.MusicEnabled
        };
        _saves.SaveSettings(settings);
        ApplyMusicSettings();
        return true;
    }

    internal bool UpdateMusicVolumeSlider(
        Vector2 pointer,
        bool leftDown,
        Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        if (!_settingsMenu.ContentList.VisibleIndices.Contains(1))
            return false;
        var settings = _saves.LoadSettings();
        if (!_musicVolumeSlider.Pressed)
            _musicVolumeSlider.SetValue(settings.MasterVolume);
        _musicVolumeSlider.Layout(_settingsMenu.OptionBounds(1));
        return _musicVolumeSlider.UpdatePointer(pointer, leftDown);
    }
}
