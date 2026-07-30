using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Usage: IslandRpg.SoundMap <Age2HD install> [output.html]");
    return 1;
}

var install = Path.GetFullPath(args[0]);
var soundsDirectory = Path.Combine(
    install, "resources", "_common", "drs", "sounds");
if (!Directory.Exists(soundsDirectory))
{
    Console.Error.WriteLine($"Sound directory not found: {soundsDirectory}");
    return 2;
}

var output = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(Environment.CurrentDirectory, "aoe-sound-map.html");
var sounds = Directory.EnumerateFiles(soundsDirectory, "*.wav")
    .Select(path => new
    {
        Path = path,
        Parsed = int.TryParse(
            Path.GetFileNameWithoutExtension(path), out var resourceId),
        ResourceId = resourceId
    })
    .Where(value => value.Parsed)
    .OrderBy(value => value.ResourceId)
    .Select(value => Analyse(value.ResourceId, value.Path))
    .Where(value => value is not null)
    .Cast<SoundFeatures>()
    .ToArray();

var categories = new[]
{
    new SoundCategory(
        "Combat impact",
        "Short, immediate transient suitable for an unarmed hit.",
        sound => Impact(sound, preferredDuration: .32) +
                 Prefer(sound.HighFrequency, .42, .34)),
    new SoundCategory(
        "Woodcutting",
        "Firm tool impact with a moderately woody, low-frequency body.",
        sound => Impact(sound, preferredDuration: .48) +
                 Prefer(sound.HighFrequency, .30, .28) +
                 Prefer(sound.DecayRatio, .24, .28)),
    new SoundCategory(
        "Mining",
        "Sharp pick/stone or metallic impact.",
        sound => Impact(sound, preferredDuration: .42) +
                 Prefer(sound.HighFrequency, .68, .30)),
    new SoundCategory(
        "Digging",
        "Lower, softer shovel/soil impact.",
        sound => Impact(sound, preferredDuration: .58) +
                 Prefer(sound.HighFrequency, .22, .24) +
                 Prefer(sound.CrestFactor, 3.2, 2.2)),
    new SoundCategory(
        "Gathering",
        "Soft sustained rustle or pull rather than a hard impact.",
        sound => Prefer(sound.DurationSeconds, 1.0, .85) +
                 Prefer(sound.CrestFactor, 2.5, 1.8) +
                 Prefer(sound.AttackRatio, .22, .28) +
                 Prefer(sound.HighFrequency, .50, .38)),
    new SoundCategory(
        "Fishing / water",
        "Sustained noisy splash, cast, or catch sound.",
        sound => Prefer(sound.DurationSeconds, 1.35, 1.05) +
                 Prefer(sound.CrestFactor, 2.8, 2.0) +
                 Prefer(sound.HighFrequency, .57, .38) +
                 Prefer(sound.DecayRatio, .48, .40))
};

var groups = GroupVariants(sounds);
var verified = LoadVerifiedMappings();
var html = BuildReport(
    sounds, groups, categories, soundsDirectory, verified);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, html, Encoding.UTF8);
Console.WriteLine(
    $"Analysed {sounds.Length:N0} WAV files. Report: {output}");
return 0;

static SoundFeatures? Analyse(int resourceId, string path)
{
    try
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        var interleaved = new List<float>(
            (int)Math.Min(
                int.MaxValue,
                reader.TotalTime.TotalSeconds * sampleRate * channels));
        var buffer = new float[sampleRate * channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var index = 0; index < read; index++)
                interleaved.Add(buffer[index]);
        if (interleaved.Count == 0) return null;

        var frameCount = interleaved.Count / channels;
        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
                sum += interleaved[frame * channels + channel];
            mono[frame] = sum / channels;
        }

        double energy = 0;
        double differenceEnergy = 0;
        var peak = 0f;
        var peakIndex = 0;
        var crossings = 0;
        for (var index = 0; index < mono.Length; index++)
        {
            var value = mono[index];
            energy += value * value;
            if (Math.Abs(value) > peak)
            {
                peak = Math.Abs(value);
                peakIndex = index;
            }
            if (index == 0) continue;
            var difference = value - mono[index - 1];
            differenceEnergy += difference * difference;
            if ((value >= 0) != (mono[index - 1] >= 0))
                crossings++;
        }

        var rms = Math.Sqrt(energy / mono.Length);
        var differenceRms = Math.Sqrt(
            differenceEnergy / Math.Max(1, mono.Length - 1));
        var tailStart = mono.Length * 3 / 4;
        double tailEnergy = 0;
        for (var index = tailStart; index < mono.Length; index++)
            tailEnergy += mono[index] * mono[index];
        var tailRms = Math.Sqrt(
            tailEnergy / Math.Max(1, mono.Length - tailStart));
        var duration = mono.Length / (double)sampleRate;
        return new(
            resourceId,
            path,
            duration,
            rms,
            peak,
            peak / Math.Max(rms, 0.000001),
            crossings / (double)Math.Max(1, mono.Length - 1),
            Math.Clamp(
                differenceRms / Math.Max(rms * 2, .000001), 0, 1),
            peakIndex / (double)Math.Max(1, mono.Length - 1),
            Math.Clamp(tailRms / Math.Max(rms, .000001), 0, 2));
    }
    catch
    {
        return null;
    }
}

static double Impact(SoundFeatures sound, double preferredDuration) =>
    Prefer(sound.DurationSeconds, preferredDuration, .55) +
    Prefer(sound.AttackRatio, .06, .15) +
    Prefer(sound.CrestFactor, 5.0, 3.8) +
    Prefer(sound.DecayRatio, .18, .30);

static double Prefer(double value, double target, double tolerance) =>
    Math.Exp(-Math.Pow((value - target) / Math.Max(.0001, tolerance), 2));

static IReadOnlyList<SoundGroup> GroupVariants(
    IReadOnlyList<SoundFeatures> sounds)
{
    var groups = new List<SoundGroup>();
    var current = new List<SoundFeatures>();
    foreach (var sound in sounds)
    {
        if (current.Count > 0)
        {
            var previous = current[^1];
            var closeId = sound.ResourceId - previous.ResourceId <= 3;
            var similar = FeatureDistance(sound, previous) <= 1.65;
            if (!closeId || !similar || current.Count >= 6)
            {
                groups.Add(new(current.ToArray()));
                current.Clear();
            }
        }
        current.Add(sound);
    }
    if (current.Count > 0)
        groups.Add(new(current.ToArray()));
    return groups;
}

static double FeatureDistance(SoundFeatures left, SoundFeatures right)
{
    static double Delta(double a, double b, double scale) =>
        Math.Pow((a - b) / scale, 2);
    return Math.Sqrt(
        Delta(left.DurationSeconds, right.DurationSeconds, .55) +
        Delta(left.CrestFactor, right.CrestFactor, 2.5) +
        Delta(left.HighFrequency, right.HighFrequency, .24) +
        Delta(left.AttackRatio, right.AttackRatio, .22) +
        Delta(left.DecayRatio, right.DecayRatio, .35));
}

static string BuildReport(
    IReadOnlyList<SoundFeatures> sounds,
    IReadOnlyList<SoundGroup> groups,
    IReadOnlyList<SoundCategory> categories,
    string soundsDirectory,
    IReadOnlyDictionary<string, int[]> verified)
{
    var builder = new StringBuilder();
    builder.Append("""
        <!doctype html><html><head><meta charset="utf-8">
        <title>Island RPG — AoE Sound Map</title>
        <style>
        body{background:#17150f;color:#e5d8ad;font:14px system-ui;margin:28px}
        h1,h2{font-weight:500;color:#f0df9f} p{color:#bdb28f}
        section{margin:28px 0;padding:18px;background:#242016;border:1px solid #66552c}
        table{width:100%;border-collapse:collapse}th,td{padding:7px;border-bottom:1px solid #39321f;text-align:left}
        th{color:#c8a950}code{color:#f1ce69}audio{height:28px;width:260px}
        .score{color:#8fd36b}.features{color:#aaa184;font-size:12px}
        </style></head><body>
        <h1>AoE2 HD action-sound candidates</h1>
        """);
    builder.Append($"<p>Analysed {sounds.Count:N0} WAV files from " +
                   $"<code>{WebUtility.HtmlEncode(soundsDirectory)}</code>. " +
                   $"Grouped into {groups.Count:N0} likely variation sets. " +
                   "Scores are heuristic; audition the top candidates before mapping.</p>");
    if (verified.Count > 0)
    {
        builder.Append("<section><h2>Verified by audition</h2>");
        builder.Append("<p>Human-identified families recorded in the project cue catalog.</p><table>");
        builder.Append("<tr><th>Label</th><th>Resource IDs</th></tr>");
        foreach (var pair in verified.OrderBy(pair => pair.Key))
        {
            builder.Append("<tr><td>");
            builder.Append(WebUtility.HtmlEncode(pair.Key));
            builder.Append("</td><td><code>");
            builder.Append(string.Join(", ", pair.Value));
            builder.Append("</code></td></tr>");
        }
        builder.Append("</table></section>");
    }
    foreach (var category in categories)
    {
        builder.Append($"<section><h2>{category.Name}</h2>");
        builder.Append($"<p>{category.Description}</p>");
        builder.Append("<table><tr><th>Rank</th><th>Likely variant IDs</th><th>Preview</th><th>Score</th><th>Features</th></tr>");
        var ranked = groups
            .Select(group =>
            {
                var representative = group.Sounds
                    .MaxBy(category.Score)!;
                return (
                    Group: group,
                    Sound: representative,
                    Score: category.Score(representative));
            })
            .OrderByDescending(value => value.Score)
            .Take(30)
            .ToArray();
        for (var index = 0; index < ranked.Length; index++)
        {
            var item = ranked[index];
            var uri = new Uri(item.Sound.Path).AbsoluteUri;
            builder.Append("<tr>");
            builder.Append($"<td>{index + 1}</td>");
            builder.Append("<td><code>");
            builder.Append(string.Join(
                ", ", item.Group.Sounds.Select(sound => sound.ResourceId)));
            builder.Append("</code></td>");
            builder.Append($"<td><audio controls preload=\"none\" src=\"{WebUtility.HtmlEncode(uri)}\"></audio></td>");
            builder.Append($"<td class=\"score\">{item.Score:0.000}</td>");
            builder.Append("<td class=\"features\">");
            builder.Append(
                $"{item.Sound.DurationSeconds:0.00}s · " +
                $"crest {item.Sound.CrestFactor:0.0} · " +
                $"HF {item.Sound.HighFrequency:0.00} · " +
                $"attack {item.Sound.AttackRatio:0.00} · " +
                $"tail {item.Sound.DecayRatio:0.00}");
            builder.Append("</td></tr>");
        }
        builder.Append("</table></section>");
    }
    builder.Append("</body></html>");
    return builder.ToString();
}

static IReadOnlyDictionary<string, int[]> LoadVerifiedMappings()
{
    var path = Path.Combine(
        Environment.CurrentDirectory,
        "src", "IslandRpg", "Resources", "Audio",
        "aoe-sound-cues.json");
    if (!File.Exists(path)) return new Dictionary<string, int[]>();
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, int[]>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var sectionName in new[] { "cues", "reference" })
        {
            if (!document.RootElement.TryGetProperty(
                    sectionName, out var section))
                continue;
            foreach (var property in section.EnumerateObject())
                result[property.Name] = property.Value
                    .EnumerateArray()
                    .Select(value => value.GetInt32())
                    .ToArray();
        }
        return result;
    }
    catch
    {
        return new Dictionary<string, int[]>();
    }
}

internal sealed record SoundFeatures(
    int ResourceId,
    string Path,
    double DurationSeconds,
    double Rms,
    double Peak,
    double CrestFactor,
    double ZeroCrossingRate,
    double HighFrequency,
    double AttackRatio,
    double DecayRatio);

internal sealed record SoundCategory(
    string Name,
    string Description,
    Func<SoundFeatures, double> Score);

internal sealed record SoundGroup(
    IReadOnlyList<SoundFeatures> Sounds);
