using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace IslandRpg.Audio;

internal sealed record Age2SoundEffect(int ResourceId, string Path);

internal static class Age2SoundEffectCatalog
{
    public static IReadOnlyList<Age2SoundEffect> Find(string install)
    {
        var directory = Path.Combine(
            install, "resources", "_common", "drs", "sounds");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.wav")
            .Select(path => new
            {
                Path = path,
                Parsed = int.TryParse(
                    Path.GetFileNameWithoutExtension(path),
                    out var resourceId),
                ResourceId = resourceId
            })
            .Where(value => value.Parsed)
            .OrderBy(value => value.ResourceId)
            .Select(value => new Age2SoundEffect(
                value.ResourceId, value.Path))
            .ToArray();
    }
}

internal sealed class Age2SoundEffectPlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int MaximumCachedSounds = 32;
    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _volume;
    private readonly WaveOutEvent _output;
    private readonly Dictionary<string, CachedSound> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private bool _disposed;

    public Age2SoundEffectPlayer()
    {
        _mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true
        };
        _volume = new VolumeSampleProvider(_mixer);
        _output = new WaveOutEvent();
        _output.Init(_volume);
        _output.Play();
    }

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = Math.Clamp(value, 0, 1);
    }

    public void Play(string path)
    {
        if (_disposed || !File.Exists(path)) return;
        try
        {
            if (!_cache.TryGetValue(path, out var sound))
            {
                sound = CachedSound.Load(path);
                _cache[path] = sound;
                _cacheOrder.Enqueue(path);
                TrimCache();
            }
            _mixer.AddMixerInput(new CachedSoundSampleProvider(sound));
        }
        catch
        {
            // A malformed expansion sound must not interrupt the game.
        }
    }

    public void StopAll() => _mixer.RemoveAllMixerInputs();

    private void TrimCache()
    {
        while (_cache.Count > MaximumCachedSounds &&
               _cacheOrder.TryDequeue(out var oldest))
            _cache.Remove(oldest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Stop();
        _output.Dispose();
        _cache.Clear();
        _cacheOrder.Clear();
    }

    private sealed record CachedSound(float[] Samples)
    {
        public static CachedSound Load(string path)
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider source = reader;
            if (source.WaveFormat.SampleRate != SampleRate)
                source = new WdlResamplingSampleProvider(
                    source, SampleRate);
            source = source.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(source),
                2 => source,
                _ => throw new InvalidDataException(
                    "Only mono and stereo effects are supported.")
            };
            var samples = new List<float>(
                Math.Max(
                    SampleRate,
                    (int)(reader.TotalTime.TotalSeconds *
                          SampleRate * Channels)));
            var buffer = new float[SampleRate * Channels];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                samples.AddRange(buffer.AsSpan(0, read).ToArray());
            return new CachedSound(samples.ToArray());
        }
    }

    private sealed class CachedSoundSampleProvider(CachedSound sound)
        : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = sound.Samples.Length - _position;
            var copied = Math.Min(available, count);
            Array.Copy(
                sound.Samples, _position,
                buffer, offset, copied);
            _position += copied;
            return copied;
        }
    }
}
