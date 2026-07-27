using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

/// <summary>
/// Triangulates the cave density zero-contour at sub-tile resolution. Each
/// source triangle is clipped against the continuous field, avoiding the
/// ambiguous diagonal cases and diamond stepping of tile visibility masks.
/// </summary>
internal static class UndergroundTerrainMeshBuilder
{
    private const int SamplesPerTile =
        UndergroundWorldGenerator.SamplesPerTile;

    public static float[] Build(WorldChunk chunk, long seed)
    {
        var vertices = new List<float>(
            WorldChunk.Size * WorldChunk.Size *
            SamplesPerTile * SamplesPerTile * 6 * 12);
        var originX = chunk.Coordinate.X * WorldChunk.Size;
        var originY = chunk.Coordinate.Y * WorldChunk.Size;
        var step = 1f / SamplesPerTile;
        for (var tileY = 0; tileY < WorldChunk.Size; tileY++)
        for (var tileX = 0; tileX < WorldChunk.Size; tileX++)
        for (var sampleY = 0; sampleY < SamplesPerTile; sampleY++)
        for (var sampleX = 0; sampleX < SamplesPerTile; sampleX++)
        {
            var x0 = originX + tileX + sampleX * step;
            var y0 = originY + tileY + sampleY * step;
            var x1 = x0 + step;
            var y1 = y0 + step;
            var gridX = tileX * SamplesPerTile + sampleX;
            var gridY = tileY * SamplesPerTile + sampleY;
            var northWest = Point(chunk, x0, y0, gridX, gridY);
            var northEast = Point(chunk, x1, y0, gridX + 1, gridY);
            var southEast = Point(chunk, x1, y1, gridX + 1, gridY + 1);
            var southWest = Point(chunk, x0, y1, gridX, gridY + 1);
            AddClippedTriangle(
                northWest, northEast, southEast);
            AddClippedTriangle(
                northWest, southEast, southWest);
        }
        return vertices.ToArray();

        void AddClippedTriangle(
            CavePoint first,
            CavePoint second,
            CavePoint third)
        {
            Span<CavePoint> polygon = stackalloc CavePoint[5];
            var count = Clip(first, second, third, polygon);
            if (count < 3) return;
            for (var index = 1; index < count - 1; index++)
            {
                AddVertex(polygon[0]);
                AddVertex(polygon[index]);
                AddVertex(polygon[index + 1]);
            }
        }

        void AddVertex(CavePoint point)
        {
            var height = UndergroundWorldGenerator.Height(point.Density);
            var projected = new Vector2(
                (point.X - point.Y) * 48,
                (point.X + point.Y) * 24 - height * 20);
            var localX = point.X - originX;
            var localY = point.Y - originY;
            var haloSamples =
                WorldChunk.WeightHaloTiles *
                WorldChunk.WeightSamplesPerTile;
            var weightX =
                (haloSamples +
                 localX * WorldChunk.WeightSamplesPerTile) /
                (WorldChunk.WeightTextureSize - 1f);
            var weightY =
                (haloSamples +
                 localY * WorldChunk.WeightSamplesPerTile) /
                (WorldChunk.WeightTextureSize - 1f);
            var material = UndergroundWorldGenerator.MaterialAt(
                seed,
                (int)MathF.Floor(point.X),
                (int)MathF.Floor(point.Y));
            var layer = (float)(int)material;
            vertices.Add(projected.X);
            vertices.Add(projected.Y);
            vertices.Add(point.X / 8f);
            vertices.Add(point.Y / 8f);
            vertices.Add(weightX);
            vertices.Add(weightY);
            vertices.Add(layer);
            vertices.Add(layer);
            vertices.Add(layer);
            vertices.Add(layer);
            vertices.Add(layer);
            vertices.Add(
                .72f +
                CaveHydrologyField.Strength(point.Density) * .22f);
        }
    }

    private static CavePoint Point(
        WorldChunk chunk, float x, float y, int gridX, int gridY) =>
        new(
            x,
            y,
            chunk.UndergroundDensity[
                gridY * UndergroundWorldGenerator.DensityStride + gridX]);

    private static int Clip(
        CavePoint first,
        CavePoint second,
        CavePoint third,
        Span<CavePoint> output)
    {
        Span<CavePoint> input = stackalloc CavePoint[3]
            { first, second, third };
        var count = 0;
        var previous = input[2];
        var previousInside =
            previous.Density >= CaveHydrologyField.Boundary;
        foreach (var current in input)
        {
            var currentInside =
                current.Density >= CaveHydrologyField.Boundary;
            if (currentInside != previousInside)
                output[count++] = Intersect(previous, current);
            if (currentInside) output[count++] = current;
            previous = current;
            previousInside = currentInside;
        }
        return count;
    }

    private static CavePoint Intersect(
        CavePoint outside,
        CavePoint inside)
    {
        var denominator = inside.Density - outside.Density;
        var amount = MathF.Abs(denominator) < .00001f
            ? .5f
            : Math.Clamp(
                (CaveHydrologyField.Boundary - outside.Density) /
                denominator,
                0f, 1f);
        return new(
            Lerp(outside.X, inside.X, amount),
            Lerp(outside.Y, inside.Y, amount),
            CaveHydrologyField.Boundary);
    }

    private static float Lerp(float a, float b, float amount) =>
        a + (b - a) * amount;

    private readonly record struct CavePoint(
        float X,
        float Y,
        float Density);
}
