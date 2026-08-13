using IslandRpg.Resources;
using OpenTK.Mathematics;

namespace IslandRpg.World;

/// <summary>
/// Legacy world-facing adapter over the headless cave sampler. Keeping this
/// facade lets navigation and rendering retain OpenTK vectors while mining
/// authority and solo generation consume one canonical terrain field.
/// </summary>
internal static class CaveHydrologyField
{
    internal const float Boundary = ProceduralUndergroundTerrain.Boundary;

    public static float Density(long seed, float worldX, float worldY) =>
        ProceduralUndergroundTerrain.Density(seed, worldX, worldY);

    internal static float Density(
        long seed,
        float worldX,
        float worldY,
        SamplingContext? context) =>
        context is null
            ? ProceduralUndergroundTerrain.Density(seed, worldX, worldY)
            : context.Density(worldX, worldY);

    public static float Strength(long seed, float worldX, float worldY) =>
        ProceduralUndergroundTerrain.Strength(seed, worldX, worldY);

    internal static float Strength(float density) =>
        ProceduralUndergroundTerrain.Strength(density);

    internal static float EdgeVariation(
        long seed,
        float worldX,
        float worldY) =>
        ProceduralUndergroundTerrain.EdgeVariation(seed, worldX, worldY);

    internal sealed class SamplingContext
    {
        private readonly ProceduralUndergroundTerrain.SamplingContext _value;

        internal SamplingContext(long seed) =>
            _value = new ProceduralUndergroundTerrain.SamplingContext(seed);

        internal float Density(float worldX, float worldY) =>
            _value.Density(worldX, worldY);

        internal CellTopology Topology(int x, int y)
        {
            var value = _value.Topology(x, y);
            return new(
                ToOpenTk(value.Start),
                ToOpenTk(value.End),
                value.Incoming,
                value.HasCrossLink,
                ToOpenTk(value.CrossEnd));
        }

        private static Vector2 ToOpenTk(System.Numerics.Vector2 value) =>
            new(value.X, value.Y);
    }

    internal readonly record struct CellTopology(
        Vector2 Start,
        Vector2 End,
        int Incoming,
        bool HasCrossLink,
        Vector2 CrossEnd);
}
