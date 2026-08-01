if (args.Length != 1)
    throw new ArgumentException("Usage: IslandRpg.SoundGenerator <output.wav>");

const int sampleRate = 44_100;
const double duration = 5.2;
var samples = new short[(int)(sampleRate * duration)];
var random = new Random(1200);
double low = 0;
double slower = 0;
for (var index = 0; index < samples.Length; index++)
{
    var time = index / (double)sampleRate;
    var noise = random.NextDouble() * 2 - 1;
    low += (noise - low) * .018;
    slower += (low - slower) * .0035;
    var firstCrack = Math.Exp(-time * 12) *
                     Math.Sin(time * 2 * Math.PI * 47);
    var secondCrackTime = Math.Max(0, time - .23);
    var secondCrack = time >= .23
        ? Math.Exp(-secondCrackTime * 18) *
          Math.Sin(secondCrackTime * 2 * Math.PI * 71)
        : 0;
    var rumbleEnvelope = Math.Exp(-time * .72) *
                         Math.Min(1, time * 8);
    var distantRoll = Math.Sin(time * 2 * Math.PI * 22 +
                               Math.Sin(time * 5.7) * 2.4);
    var value = (firstCrack * .34 + secondCrack * .22 +
                 slower * 8.5 + distantRoll * .12) * rumbleEnvelope;
    value = Math.Tanh(value * 1.35) * .88;
    samples[index] = (short)Math.Clamp(
        (int)Math.Round(value * short.MaxValue),
        short.MinValue, short.MaxValue);
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var stream = File.Create(output);
using var writer = new BinaryWriter(stream);
writer.Write("RIFF"u8);
writer.Write(36 + samples.Length * sizeof(short));
writer.Write("WAVE"u8);
writer.Write("fmt "u8);
writer.Write(16);
writer.Write((short)1);
writer.Write((short)1);
writer.Write(sampleRate);
writer.Write(sampleRate * sizeof(short));
writer.Write((short)sizeof(short));
writer.Write((short)16);
writer.Write("data"u8);
writer.Write(samples.Length * sizeof(short));
foreach (var sample in samples) writer.Write(sample);
Console.WriteLine($"Generated thunder: {output} ({duration:0.0}s)");
