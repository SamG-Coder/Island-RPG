using NAudio.Wave;

namespace IslandRpg.Audio;

internal sealed class Age2MusicPlayer : IDisposable
{
    private readonly string[] _tracks;
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private int _trackIndex = -1;
    private bool _enabled;
    private float _volume = 1;

    public Age2MusicPlayer(string install)
    {
        _tracks = Age2MusicCatalog.FindTracks(install).ToArray();
    }

    public bool Available => _tracks.Length > 0;

    public void Configure(bool enabled, float volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _enabled = enabled && _volume > 0 && Available;
        if (!_enabled)
        {
            CloseCurrent();
            _trackIndex = -1;
            return;
        }

        if (_reader is not null)
            _reader.Volume = _volume;
        else
            PlayNext();
    }

    public void Update()
    {
        if (!_enabled || !Available) return;
        if (_output?.PlaybackState == PlaybackState.Stopped)
            PlayNext();
    }

    private void PlayNext()
    {
        CloseCurrent();
        _trackIndex = (_trackIndex + 1) % _tracks.Length;
        try
        {
            _reader = new AudioFileReader(_tracks[_trackIndex])
            {
                Volume = _volume
            };
            _output = new WaveOutEvent();
            _output.Init(_reader);
            _output.Play();
        }
        catch
        {
            CloseCurrent();
        }
    }

    private void CloseCurrent()
    {
        _output?.Stop();
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
    }

    public void Dispose()
    {
        _enabled = false;
        CloseCurrent();
        _trackIndex = -1;
    }
}

internal static class Age2MusicCatalog
{
    public static IReadOnlyList<string> FindTracks(string install)
    {
        var directory = Path.Combine(
            install, "resources", "_common", "sound", "music");
        if (!Directory.Exists(directory)) return [];

        var tracks = new List<(int Order, string Path)>();
        Add("music1.mp3", 0);
        for (var index = 1; index <= 99; index++)
            Add($"xmusic{index}.mp3", index);
        return tracks
            .OrderBy(track => track.Order)
            .Select(track => track.Path)
            .ToArray();

        void Add(string name, int order)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                tracks.Add((order, path));
        }
    }
}
