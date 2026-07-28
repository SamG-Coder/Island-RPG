using IslandRpg.Assets;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed class LevelUpFireworks
{
    public const int Capacity = 48;
    public const string AtlasPrefix = "LEVEL_UP_SPARK";
    private static readonly Vector3[] Colors =
    [
        new(1f, .34f, .12f),
        new(.25f, .72f, 1f),
        new(1f, .82f, .18f),
        new(.55f, 1f, .32f)
    ];
    private readonly Particle[] _particles = new Particle[Capacity];
    private Vector2 _lightWorld;
    private float _lightLife;

    public bool Active => _lightLife > 0 ||
        _particles.Any(particle => particle.Life > 0);
    public Vector2 LightWorld => _lightWorld;
    public Vector3 LightColor => new(1f, .68f, .32f);
    public float LightIntensity =>
        Math.Clamp(_lightLife / .7f, 0, 1) * 1.2f;

    public void Burst(Vector2 playerWorld)
    {
        _lightWorld = playerWorld + new Vector2(0, -48);
        _lightLife = 1.05f;
        for (var index = 0; index < _particles.Length; index++)
        {
            var group = index % Colors.Length;
            var angle = index * MathF.Tau / _particles.Length +
                        group * .31f;
            var speed = 24f + index % 7 * 4.5f;
            _particles[index] = new(
                _lightWorld + new Vector2((group - 1.5f) * 6, group % 2 * -5),
                new(MathF.Cos(angle) * speed,
                    MathF.Sin(angle) * speed - 18f),
                1.05f + index % 5 * .07f,
                1.05f + index % 5 * .07f,
                group);
        }
    }

    public void Update(float elapsed)
    {
        _lightLife = Math.Max(0, _lightLife - elapsed);
        for (var index = 0; index < _particles.Length; index++)
        {
            var particle = _particles[index];
            if (particle.Life <= 0) continue;
            particle.Life -= elapsed;
            particle.Position += particle.Velocity * elapsed;
            particle.Velocity.Y += 42f * elapsed;
            particle.Velocity *= MathF.Pow(.82f, elapsed);
            _particles[index] = particle;
        }
    }

    public void AddTo(
        Action<string, Vector2, float> addParticle)
    {
        for (var index = 0; index < _particles.Length; index++)
        {
            var particle = _particles[index];
            if (particle.Life <= 0) continue;
            var opacity = Math.Clamp(
                particle.Life / Math.Min(.35f, particle.MaximumLife),
                0, 1);
            addParticle(
                $"{AtlasPrefix}#{particle.Color}", particle.Position,
                opacity);
        }
    }

    public void Clear()
    {
        Array.Clear(_particles);
        _lightLife = 0;
    }

    public static IEnumerable<(string Key, SpriteFrame Frame)> Frames()
    {
        for (var index = 0; index < Colors.Length; index++)
        {
            const int size = 7;
            var pixels = new byte[size * size * 4];
            var color = Colors[index];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Abs(x - 3) + Math.Abs(y - 3);
                if (distance > 3) continue;
                var offset = (y * size + x) * 4;
                pixels[offset] = (byte)(color.X * 255);
                pixels[offset + 1] = (byte)(color.Y * 255);
                pixels[offset + 2] = (byte)(color.Z * 255);
                pixels[offset + 3] = (byte)(255 - distance * 54);
            }
            yield return (
                $"{AtlasPrefix}#{index}",
                new(size, size, 3, 3, pixels));
        }
    }

    private struct Particle(
        Vector2 position,
        Vector2 velocity,
        float life,
        float maximumLife,
        int color)
    {
        public Vector2 Position = position;
        public Vector2 Velocity = velocity;
        public float Life = life;
        public readonly float MaximumLife = maximumLife;
        public readonly int Color = color;
    }
}
