using IslandRpg.Audio;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Age2MusicPlayer? _musicPlayer;
    private Age2SoundEffectPlayer? _soundEffects;
    private IReadOnlyList<Age2SoundEffect> _soundBrowser = [];
    private IReadOnlyDictionary<string, Age2SoundEffect[]> _soundCues =
        new Dictionary<string, Age2SoundEffect[]>();
    private readonly Dictionary<string, int> _lastSoundCueVariant =
        new(StringComparer.OrdinalIgnoreCase);
    private int _soundBrowserIndex;
    private readonly SliderControlState _musicVolumeSlider = new();
    private readonly SliderControlState _effectsVolumeSlider = new();
    private const float MusicMixScale = .55f;

    private void InitializeMusic()
    {
        _musicPlayer = new Age2MusicPlayer(_install);
        _soundBrowser = Age2SoundEffectCatalog.Find(_install);
        _soundCues = Age2SoundCueCatalog.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Resources", "Audio", "aoe-sound-cues.json"),
            _soundBrowser);
        try
        {
            _soundEffects = new Age2SoundEffectPlayer
            {
                Volume = _saves.LoadSettings().EffectsVolume
            };
            _soundEffects.Preload(
                _soundCues.Values
                    .SelectMany(variants => variants)
                    .Select(sound => sound.Path));
        }
        catch
        {
            _soundEffects = null;
        }
        _musicVolumeSlider.ValueChanged += value =>
        {
            var settings = _saves.LoadSettings();
            _musicPlayer?.Configure(
                settings.MusicEnabled, value * MusicMixScale);
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
        _effectsVolumeSlider.ValueChanged += value =>
        {
            if (_soundEffects is not null)
                _soundEffects.Volume = value;
        };
        _effectsVolumeSlider.DragCompleted += value =>
        {
            var settings = _saves.LoadSettings() with
            {
                EffectsVolume = value
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

    private void PlaySoundCue(string cue)
    {
        if (_soundEffects is null ||
            !_soundCues.TryGetValue(cue, out var variants) ||
            variants.Length == 0)
            return;
        var index = Random.Shared.Next(variants.Length);
        if (variants.Length > 1 &&
            _lastSoundCueVariant.TryGetValue(cue, out var previous) &&
            index == previous)
            index = (index + 1 + Random.Shared.Next(variants.Length - 1)) %
                    variants.Length;
        _lastSoundCueVariant[cue] = index;
        _soundEffects.Play(variants[index].Path);
    }

    private void PlayGeneratedSound(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Audio", fileName);
        if (File.Exists(path)) _soundEffects?.Play(path);
    }

    private void ApplyMusicSettings()
    {
        var settings = _saves.LoadSettings();
        _musicPlayer?.Configure(
            settings.MusicEnabled,
            settings.MasterVolume * MusicMixScale);
        if (_soundEffects is not null)
            _soundEffects.Volume = settings.EffectsVolume;
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

    internal bool UpdateSoundVolumeSliders(
        Vector2 pointer,
        bool leftDown,
        Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        var settings = _saves.LoadSettings();
        var active = false;
        if (_settingsMenu.ContentList.VisibleIndices.Contains(1))
        {
            if (!_musicVolumeSlider.Pressed)
                _musicVolumeSlider.SetValue(settings.MasterVolume);
            _musicVolumeSlider.Layout(_settingsMenu.OptionBounds(1));
            active |= _musicVolumeSlider.UpdatePointer(pointer, leftDown);
        }
        if (_settingsMenu.ContentList.VisibleIndices.Contains(2))
        {
            if (!_effectsVolumeSlider.Pressed)
                _effectsVolumeSlider.SetValue(settings.EffectsVolume);
            _effectsVolumeSlider.Layout(_settingsMenu.OptionBounds(2));
            active |= _effectsVolumeSlider.UpdatePointer(pointer, leftDown);
        }
        return active;
    }
}
