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
    // Navigation and boundary sampling retain the full density resolution.
    // Rendering every density sub-cell produces up to sixteen times the
    // overworld triangle count, so the mesh consumes every second sample.
    private const int DensitySamplesPerTile =
        UndergroundWorldGenerator.SamplesPerTile;
    private const int MeshSamplesPerTile = 2;
    private const int DensitySampleStep =
        DensitySamplesPerTile / MeshSamplesPerTile;

    public static float[] Build(
        WorldChunk chunk,
        long seed,
        CancellationToken cancellationToken = default)
    {
        var vertices = new List<float>(
            WorldChunk.Size * WorldChunk.Size *
            MeshSamplesPerTile * MeshSamplesPerTile * 6 * 12);
        var originX = chunk.Coordinate.X * WorldChunk.Size;
        var originY = chunk.Coordinate.Y * WorldChunk.Size;
        for (var tileY = 0; tileY < WorldChunk.Size; tileY++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        for (var tileX = 0; tileX < WorldChunk.Size; tileX++)
        for (var sampleY = 0; sampleY < MeshSamplesPerTile; sampleY++)
        for (var sampleX = 0; sampleX < MeshSamplesPerTile; sampleX++)
        {
            var gridX =
                (tileX * MeshSamplesPerTile + sampleX) *
                DensitySampleStep;
            var gridY =
                (tileY * MeshSamplesPerTile + sampleY) *
                DensitySampleStep;
            if (CellCrossesBoundary(
                    chunk, gridX, gridY, DensitySampleStep))
            {
                var fineStep = DensitySampleStep / 2;
                AddCell(gridX, gridY, fineStep);
                AddCell(gridX + fineStep, gridY, fineStep);
                AddCell(gridX, gridY + fineStep, fineStep);
                AddCell(
                    gridX + fineStep,
                    gridY + fineStep,
                    fineStep);
            }
            else
            {
                AddCell(gridX, gridY, DensitySampleStep);
            }
        }
        }
        return vertices.ToArray();

        void AddCell(int gridX, int gridY, int densityStep)
        {
            var coordinateStep =
                densityStep / (float)DensitySamplesPerTile;
            var x0 = originX +
                     gridX / (float)DensitySamplesPerTile;
            var y0 = originY +
                     gridY / (float)DensitySamplesPerTile;
            var x1 = x0 + coordinateStep;
            var y1 = y0 + coordinateStep;
            var northWest = Point(chunk, x0, y0, gridX, gridY);
            var northEast = Point(
                chunk, x1, y0, gridX + densityStep, gridY);
            var southEast = Point(
                chunk, x1, y1,
                gridX + densityStep,
                gridY + densityStep);
            var southWest = Point(
                chunk, x0, y1, gridX, gridY + densityStep);
            AddClippedTriangle(
                northWest, northEast, southEast);
            AddClippedTriangle(
                northWest, southEast, southWest);
        }

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
            var projected = IsometricTerrainProjection.Project(
                point.X, point.Y, height);
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
                UndergroundWorldGenerator.EdgeVisibility(
                    seed, point.X, point.Y, point.Density) *
                WallLight(chunk, localX, localY));
        }
    }

    private static CavePoint Point(
        WorldChunk chunk, float x, float y, int gridX, int gridY) =>
        new(
            x,
            y,
            chunk.UndergroundDensity[
                gridY * UndergroundWorldGenerator.DensityStride + gridX]);

    private static bool CellCrossesBoundary(
        WorldChunk chunk,
        int gridX,
        int gridY,
        int step)
    {
        var boundary = CaveHydrologyField.Boundary;
        var first = Density(gridX, gridY) >= boundary;
        return (Density(gridX + step, gridY) >= boundary) != first ||
               (Density(gridX, gridY + step) >= boundary) != first ||
               (Density(gridX + step, gridY + step) >= boundary) !=
               first;

        float Density(int x, int y) =>
            chunk.UndergroundDensity[
                y * UndergroundWorldGenerator.DensityStride + x];
    }

    private static float WallLight(
        WorldChunk chunk,
        float localX,
        float localY)
    {
        var gridX = Math.Clamp(
            (int)MathF.Round(
                localX * DensitySamplesPerTile),
            1,
            UndergroundWorldGenerator.DensityStride - 2);
        var gridY = Math.Clamp(
            (int)MathF.Round(
                localY * DensitySamplesPerTile),
            1,
            UndergroundWorldGenerator.DensityStride - 2);
        var stride = UndergroundWorldGenerator.DensityStride;
        var density = chunk.UndergroundDensity;
        var gradient = new Vector2(
            density[gridY * stride + gridX + 1] -
            density[gridY * stride + gridX - 1],
            density[(gridY + 1) * stride + gridX] -
            density[(gridY - 1) * stride + gridX]);
        if (gradient.LengthSquared < .00001f)
            return 1f;
        gradient.Normalize();
        var light = Vector2.Normalize(new Vector2(-.55f, -.84f));
        return .82f + MathF.Max(
            0, Vector2.Dot(gradient, light)) * .18f;
    }

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
