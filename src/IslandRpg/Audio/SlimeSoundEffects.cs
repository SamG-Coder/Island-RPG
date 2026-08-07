using IslandRpg.Gameplay;

namespace IslandRpg.Audio;

/// <summary>
/// Deterministically synthesizes layered slime impacts once, then reuses the
/// cached PCM files. This avoids shipping generic effects or synthesizing on
/// the render thread.
/// </summary>
internal static class SlimeSoundEffects
{
    private const int SampleRate = 44100;

    public static string Attack(EnemyKind kind) => Ensure(kind, split: false);
    public static string Split(EnemyKind kind) => Ensure(kind, split: true);

    public static IReadOnlyList<string> Prepare()
    {
        var paths = new List<string>(8);
        foreach (var kind in Enum.GetValues<EnemyKind>())
        {
            var attack = Attack(kind);
            var split = Split(kind);
            if (attack.Length > 0) paths.Add(attack);
            if (split.Length > 0) paths.Add(split);
        }
        return paths;
    }

    private static string Ensure(EnemyKind kind, bool split)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "IslandRpg", "GeneratedAudio");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"slime-{kind.ToString().ToLowerInvariant()}-" +
                $"{(split ? "split" : "attack")}.wav");
            if (!File.Exists(path)) Write(path, kind, split);
            return path;
        }
        catch
        {
            // Audio cache permissions must never prevent combat or other
            // mapped effects from loading.
            return string.Empty;
        }
    }

    private static void Write(string path, EnemyKind kind, bool split)
    {
        var duration = split ? .62f : .42f;
        var count = (int)(SampleRate * duration);
        var samples = new short[count];
        var random = new Random(HashCode.Combine(kind, split, 0x51_1A_1E));
        var baseFrequency = kind switch
        {
            EnemyKind.WaterSlime => 230f,
            EnemyKind.GrassSlime => 175f,
            EnemyKind.SandSlime => 115f,
            EnemyKind.CaveSlime => 82f,
            _ => 150f
        };
        var filteredNoise = 0f;
        for (var index = 0; index < count; index++)
        {
            var t = index / (float)SampleRate;
            var progress = t / duration;
            var attack = Math.Clamp(t / .018f, 0, 1);
            var release = MathF.Pow(Math.Max(0, 1 - progress), 2.2f);
            var envelope = attack * release;
            var pitchDrop = baseFrequency * (1.7f - progress * 1.05f);
            var body = MathF.Sin(MathF.Tau * pitchDrop * t +
                                 MathF.Sin(t * 31) * .8f);
            var rawNoise = random.NextSingle() * 2 - 1;
            filteredNoise += (rawNoise - filteredNoise) *
                (kind == EnemyKind.SandSlime ? .42f : .16f);
            var texture = kind switch
            {
                EnemyKind.WaterSlime =>
                    MathF.Sin(MathF.Tau * (520 - progress * 180) * t) * .24f +
                    filteredNoise * .24f,
                EnemyKind.GrassSlime =>
                    MathF.Sin(MathF.Tau * 43 * t) *
                    MathF.Sin(MathF.Tau * 310 * t) * .30f,
                EnemyKind.SandSlime => filteredNoise * .62f,
                EnemyKind.CaveSlime =>
                    MathF.Sin(MathF.Tau * pitchDrop * .5f * t) * .46f +
                    filteredNoise * .18f,
                _ => 0
            };
            var splitPulse = split
                ? MathF.Sin(MathF.Tau * (64 + progress * 190) * t) * .35f
                : 0;
            var value = Math.Clamp(
                (body * .48f + texture + splitPulse) * envelope,
                -1, 1);
            samples[index] = (short)(value * short.MaxValue * .78f);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataLength = samples.Length * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        foreach (var sample in samples) writer.Write(sample);
    }
}
