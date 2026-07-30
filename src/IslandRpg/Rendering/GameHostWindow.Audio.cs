using IslandRpg.Audio;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Age2MusicPlayer? _musicPlayer;
    private Age2SoundEffectPlayer? _soundEffects;
    private IReadOnlyList<Age2SoundEffect> _soundBrowser = [];
    private int _soundBrowserIndex;
    private readonly SliderControlState _musicVolumeSlider = new();

    private void InitializeMusic()
    {
        _musicPlayer = new Age2MusicPlayer(_install);
        _soundBrowser = Age2SoundEffectCatalog.Find(_install);
        try
        {
            _soundEffects = new Age2SoundEffectPlayer
            {
                Volume = _saves.LoadSettings().MasterVolume
            };
        }
        catch
        {
            _soundEffects = null;
        }
        _musicVolumeSlider.ValueChanged += value =>
        {
            var settings = _saves.LoadSettings();
            _musicPlayer?.Configure(settings.MusicEnabled, value);
            if (_soundEffects is not null)
                _soundEffects.Volume = value;
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

    private Age2SoundEffect? SelectedDeveloperSound() =>
        _soundBrowser.Count == 0
            ? null
            : _soundBrowser[Math.Clamp(
                _soundBrowserIndex, 0, _soundBrowser.Count - 1)];

    private void SelectPreviousDeveloperSound()
    {
        if (_soundBrowser.Count == 0) return;
        _soundBrowserIndex =
            (_soundBrowserIndex - 1 + _soundBrowser.Count) %
            _soundBrowser.Count;
    }

    private void SelectNextDeveloperSound()
    {
        if (_soundBrowser.Count == 0) return;
        _soundBrowserIndex =
            (_soundBrowserIndex + 1) % _soundBrowser.Count;
    }

    private void PlaySelectedDeveloperSound()
    {
        if (SelectedDeveloperSound() is { } sound)
        {
            _soundEffects?.StopAll();
            _soundEffects?.Play(sound.Path);
        }
    }

    private void ApplyMusicSettings()
    {
        var settings = _saves.LoadSettings();
        _musicPlayer?.Configure(
            settings.MusicEnabled,
            settings.MasterVolume);
        if (_soundEffects is not null)
            _soundEffects.Volume = settings.MasterVolume;
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
