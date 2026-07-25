using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed record WorldVegetationRenderItem(
    Vector2 World,
    string StableKey,
    string AtlasKey,
    string? ShadowAtlasKey);

internal static class WorldVegetationRenderCache
{
    public static WorldVegetationRenderItem[] Build(
        long seed, IReadOnlyList<WorldVegetation> vegetation)
    {
        var result = new WorldVegetationRenderItem[vegetation.Count];
        for (var index = 0; index < vegetation.Count; index++)
        {
            var item = vegetation[index];
            var elevation = InfiniteWorldGenerator.SampleRenderedHeight(
                seed, item.X, item.Y);
            var world = new Vector2(
                (item.X - item.Y) * 48,
                (item.X + item.Y) * 24 - elevation * 20);
            var shadowName = ShadowName(item.GraphicName);
            result[index] = new(
                world,
                $"vegetation:{item.X:0.000}:{item.Y:0.000}",
                $"{item.GraphicName}#{item.FrameIndex}",
                shadowName is null
                    ? null
                    : $"{shadowName}#{item.FrameIndex}");
        }
        return result;
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
