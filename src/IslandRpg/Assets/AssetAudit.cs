using System.Text.Json;

namespace IslandRpg.Assets;

internal static class AssetAudit
{
    public static string Save(AssetCatalog catalog)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "AssetCache");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "asset-report.json");
        var report = new
        {
            generatedUtc = DateTime.UtcNow,
            loadedCount = catalog.Graphics.Count,
            unavailableReferenceCount = catalog.Missing.Count,
            terrainTileCount = catalog.TerrainTiles.Count,
            classifications = catalog.Graphics.Values
                .GroupBy(value => value.Kind)
                .ToDictionary(group => group.Key.ToString(), group => group.Count()),
            loaded = catalog.Graphics.Values.Select(value => new
            {
                value.Definition.GraphicId,
                value.Definition.Name,
                value.Definition.SlpId,
                kind = value.Kind.ToString(),
                frames = value.Sprite.Frames.Count,
                value.SourcePath
            }),
            unavailableReferences = catalog.Missing.Select(value => new
            {
                value.Definition.GraphicId,
                value.Definition.Name,
                value.Definition.SlpId,
                value.Reason
            }),
            terrainTiles = catalog.TerrainTiles.Select(value => new
            {
                value.Name,
                value.Width,
                value.Height,
                value.SourcePath
            })
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
