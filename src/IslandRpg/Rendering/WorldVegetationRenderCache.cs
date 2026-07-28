using IslandRpg.Gameplay;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed record WorldVegetationRenderItem(
    int TileX,
    int TileY,
    Vector2 World,
    string StableKey,
    string AtlasKey,
    string? ShadowAtlasKey,
    bool CanGatherFibre);

internal static class WorldVegetationRenderCache
{
    public static WorldVegetationRenderItem[] Build(
        WorldChunk chunk,
        IReadOnlyList<float> renderedHeights)
    {
        var vegetation = chunk.Vegetation;
        var excavationTiles = new HashSet<(int X, int Y)>();
        var shafts = new List<WorldGroundObject>();
        foreach (var value in chunk.GroundObjects)
        {
            if (CaveEntranceService.IsExcavation(value))
                excavationTiles.Add((
                    (int)MathF.Floor(value.X),
                    (int)MathF.Floor(value.Y)));
            if (chunk.Coordinate.Level == (int)WorldLevel.Underground &&
                CaveEntranceService.IsCaveShaft(value))
                shafts.Add(value);
        }
        var result = new List<WorldVegetationRenderItem>(
            vegetation.Length + shafts.Count * 3);
        for (var index = 0; index < vegetation.Length; index++)
        {
            var item = vegetation[index];
            var tileX = (int)MathF.Floor(item.X);
            var tileY = (int)MathF.Floor(item.Y);
            if (excavationTiles.Contains((tileX, tileY)))
                continue;
            result.Add(Create(chunk, renderedHeights, item));
        }
        foreach (var shaft in shafts)
            {
                AddShaftGrowth(
                    shaft.Id, shaft.X - .62f, shaft.Y + .28f, 0);
                AddShaftGrowth(
                    shaft.Id, shaft.X + .64f, shaft.Y + .32f, 1);
                AddShaftGrowth(
                    shaft.Id, shaft.X + .08f, shaft.Y - .66f, 5);
            }
        return result.ToArray();

        void AddShaftGrowth(Guid shaftId, float x, float y, int frame)
        {
            var tileX = (int)MathF.Floor(x);
            var tileY = (int)MathF.Floor(y);
            var localX = PositiveMod(tileX, WorldChunk.Size);
            var localY = PositiveMod(tileY, WorldChunk.Size);
            if (!chunk.IsRenderable(localX, localY)) return;
            result.Add(Create(
                chunk,
                renderedHeights,
                new(
                    x, y,
                    UndergroundResourceGenerator.Growth,
                    frame,
                    WorldVegetationKind.Plant,
                    false),
                $"shaft-growth:{shaftId}:{frame}"));
        }
    }

    private static WorldVegetationRenderItem Create(
        WorldChunk chunk,
        IReadOnlyList<float> renderedHeights,
        WorldVegetation item,
        string? stableKey = null)
    {
            var tileX = (int)MathF.Floor(item.X);
            var tileY = (int)MathF.Floor(item.Y);
            var localX = PositiveMod(tileX, WorldChunk.Size);
            var localY = PositiveMod(tileY, WorldChunk.Size);
            var elevation = LoadedTerrainSampler.Interpolate(
                renderedHeights,
                WorldChunk.Size + 1,
                localX,
                localY,
                item.X - tileX,
                item.Y - tileY);
            var world = IsometricTerrainProjection.Project(
                item.X, item.Y, elevation);
            var resource = UndergroundResourceGenerator.IsResourceGraphic(
                item.GraphicName);
            var shadowName = resource
                ? UndergroundResourceGenerator.ShadowGraphic(item.GraphicName)
                : ShadowName(item.GraphicName);
            var biome = chunk.Tiles[
                localY * WorldChunk.Size + localX].Biome;
            return new(
                tileX,
                tileY,
                world,
                stableKey ??
                $"vegetation:{item.X:0.000}:{item.Y:0.000}",
                $"{item.GraphicName}#{item.FrameIndex}",
                shadowName is null
                    ? null
                    : $"{shadowName}#{item.FrameIndex}",
                item.CanBecomeInstance &&
                item.Kind == WorldVegetationKind.Shrub &&
                biome is not Biome.Snow and not Biome.Tundra);
    }

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static string? ShadowName(string graphicName)
    {
        if (!graphicName.StartsWith(
                "BUSH", StringComparison.OrdinalIgnoreCase))
            return null;
        return graphicName.EndsWith(
                "_NN", StringComparison.OrdinalIgnoreCase)
            ? graphicName[..^2] + "N0"
            : null;
    }
}
