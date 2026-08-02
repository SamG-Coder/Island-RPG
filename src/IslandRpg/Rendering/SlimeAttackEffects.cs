using IslandRpg.Assets;
using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal readonly record struct SlimeAttackEffectProfile(
    Vector3 PrimaryColor,
    Vector3 SecondaryColor,
    Vector3 LightColor,
    int ParticleCount,
    float ParticleSpeed,
    float ParticleLife,
    float Gravity,
    float LightRadiusPixels,
    float LightIntensity,
    float LightLife)
{
    public static SlimeAttackEffectProfile For(EnemyKind kind) => kind switch
    {
        EnemyKind.WaterSlime => new(
            new(.22f, .72f, 1f), new(.68f, .92f, 1f),
            new(.22f, .70f, 1f), 14, 42, .68f, 34, 78, 1.05f, .62f),
        EnemyKind.GrassSlime => new(
            new(.32f, .90f, .20f), new(.82f, 1f, .34f),
            new(.48f, .95f, .22f), 12, 34, .82f, 18, 72, .92f, .76f),
        EnemyKind.SandSlime => new(
            new(1f, .66f, .18f), new(1f, .88f, .48f),
            new(1f, .54f, .14f), 13, 46, .54f, 48, 66, 1.12f, .46f),
        EnemyKind.CaveSlime => new(
            new(.66f, .24f, 1f), new(.30f, .68f, 1f),
            new(.55f, .24f, 1f), 16, 38, .92f, -4, 92, 1.25f, .88f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal readonly record struct SlimeAttackLight(
    Vector2 World,
    Vector3 Color,
    float RadiusPixels,
    float Intensity);

internal sealed class SlimeAttackEffects
{
    public const int ParticleCapacity = 160;
    public const int LightCapacity = 12;
    public const string AtlasPrefix = "SLIME_ATTACK_SPARK";

    private readonly Particle[] _particles = new Particle[ParticleCapacity];
    private readonly Light[] _lights = new Light[LightCapacity];
    private int _nextParticle;
    private int _nextLight;

    public bool Active =>
        _particles.Any(value => value.Life > 0) ||
        _lights.Any(value => value.Life > 0);
    internal int ActiveParticleCount =>
        _particles.Count(value => value.Life > 0);
    internal int ActiveLightCount =>
        _lights.Count(value => value.Life > 0);

    public void Burst(
        EnemyKind kind, Vector2 sourceWorld, Vector2 targetWorld, int seed)
    {
        var profile = SlimeAttackEffectProfile.For(kind);
        var direction = targetWorld - sourceWorld;
        if (direction.LengthSquared < .001f) direction = Vector2.UnitY;
        else direction = direction.Normalized();
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var random = new Random(seed);
        var colorBase = (int)kind * 2;
        for (var index = 0; index < profile.ParticleCount; index++)
        {
            var spread = random.NextSingle() * 1.2f - .6f;
            var speed = profile.ParticleSpeed *
                        (.68f + random.NextSingle() * .64f);
            var velocity = direction * speed +
                           perpendicular * spread * speed;
            velocity.Y -= random.NextSingle() * 12;
            var life = profile.ParticleLife *
                       (.74f + random.NextSingle() * .38f);
            _particles[_nextParticle] = new(
                sourceWorld + perpendicular *
                    (random.NextSingle() * 6 - 3),
                velocity,
                life,
                life,
                profile.Gravity,
                colorBase + index % 2);
            _nextParticle = (_nextParticle + 1) % ParticleCapacity;
        }
        _lights[_nextLight] = new(
            sourceWorld,
            targetWorld,
            profile.LightColor,
            profile.LightRadiusPixels,
            profile.LightIntensity,
            profile.LightLife,
            profile.LightLife);
        _nextLight = (_nextLight + 1) % LightCapacity;
    }

    public void Update(float elapsed)
    {
        elapsed = Math.Max(0, elapsed);
        for (var index = 0; index < _particles.Length; index++)
        {
            var particle = _particles[index];
            if (particle.Life <= 0) continue;
            particle.Life = Math.Max(0, particle.Life - elapsed);
            particle.Position += particle.Velocity * elapsed;
            particle.Velocity.Y += particle.Gravity * elapsed;
            particle.Velocity *= MathF.Pow(.78f, elapsed);
            _particles[index] = particle;
        }
        for (var index = 0; index < _lights.Length; index++)
        {
            var light = _lights[index];
            if (light.Life <= 0) continue;
            light.Life = Math.Max(0, light.Life - elapsed);
            _lights[index] = light;
        }
    }

    public void AddTo(Action<string, Vector2, float> addParticle)
    {
        foreach (var particle in _particles)
        {
            if (particle.Life <= 0) continue;
            var opacity = Math.Clamp(
                particle.Life / Math.Min(.24f, particle.MaximumLife), 0, 1);
            addParticle(
                $"{AtlasPrefix}#{particle.ColorIndex}",
                particle.Position,
                opacity);
        }
    }

    public IEnumerable<SlimeAttackLight> Lights()
    {
        foreach (var light in _lights)
        {
            if (light.Life <= 0) continue;
            var progress = 1 - light.Life / light.MaximumLife;
            var fade = Math.Clamp(light.Life / light.MaximumLife, 0, 1);
            yield return new(
                Vector2.Lerp(light.Source, light.Target, progress * .32f),
                light.Color,
                light.RadiusPixels * (.72f + fade * .28f),
                light.MaximumIntensity * fade * fade);
        }
    }

    public void Clear()
    {
        Array.Clear(_particles);
        Array.Clear(_lights);
        _nextParticle = 0;
        _nextLight = 0;
    }

    public static IEnumerable<(string Key, SpriteFrame Frame)> Frames()
    {
        foreach (var kind in Enum.GetValues<EnemyKind>())
        {
            var profile = SlimeAttackEffectProfile.For(kind);
            yield return CreateFrame((int)kind * 2, profile.PrimaryColor);
            yield return CreateFrame((int)kind * 2 + 1, profile.SecondaryColor);
        }
    }

    private static (string Key, SpriteFrame Frame) CreateFrame(
        int index, Vector3 color)
    {
        const int size = 7;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - 3;
            var dy = y - 3;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > 3) continue;
            var offset = (y * size + x) * 4;
            pixels[offset] = (byte)MathF.Round(color.X * 255);
            pixels[offset + 1] = (byte)MathF.Round(color.Y * 255);
            pixels[offset + 2] = (byte)MathF.Round(color.Z * 255);
            pixels[offset + 3] = (byte)MathF.Round((1 - distance / 3) * 255);
        }
        return (
            $"{AtlasPrefix}#{index}",
            new(size, size, 3, 3, pixels));
    }

    private struct Particle(
        Vector2 position,
        Vector2 velocity,
        float life,
        float maximumLife,
        float gravity,
        int colorIndex)
    {
        public Vector2 Position = position;
        public Vector2 Velocity = velocity;
        public float Life = life;
        public readonly float MaximumLife = maximumLife;
        public readonly float Gravity = gravity;
        public readonly int ColorIndex = colorIndex;
    }

    private struct Light(
        Vector2 source,
        Vector2 target,
        Vector3 color,
        float radiusPixels,
        float maximumIntensity,
        float life,
        float maximumLife)
    {
        public readonly Vector2 Source = source;
        public readonly Vector2 Target = target;
        public readonly Vector3 Color = color;
        public readonly float RadiusPixels = radiusPixels;
        public readonly float MaximumIntensity = maximumIntensity;
        public float Life = life;
        public readonly float MaximumLife = maximumLife;
    }
}
