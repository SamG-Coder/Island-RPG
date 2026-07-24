namespace IslandRpg.Assets;

using StbImageSharp;

internal static class AssetLoader
{
    public static AssetCatalog LoadAll(string install, IProgress<(int Done, int Total, string Name)> progress)
    {
        var common = Path.Combine(install, "resources", "_common");
        var dat = Path.Combine(common, "dat", "empires2_x2_p1.dat");
        var definitions = GenieDatReader.FindAllGraphics(dat);
        var loaded = new Dictionary<short, LoadedGraphic>();
        var missing = new List<MissingGraphic>();
        var paletteCache = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
        var terrainFiles = Directory.GetFiles(
            Path.Combine(common, "terrain", "textures"), "*.png", SearchOption.TopDirectoryOnly);
        var waterFiles = Directory.GetFiles(
            Path.Combine(common, "terrain", "water"), "normal*.png", SearchOption.TopDirectoryOnly);
        var total = definitions.Count + terrainFiles.Length + waterFiles.Length;

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            progress.Report((i, total, definition.Name));
            var path = ResolveSlp(common, definition.SlpId);
            if (path is null)
            {
                missing.Add(new(definition,
                    $"SLP {definition.SlpId} is not available as a loose file in this HD installation"));
                continue;
            }

            try
            {
                var resolvedPalette = Age2PaletteResolver.Resolve(install, path);
                if (!paletteCache.TryGetValue(resolvedPalette.Path, out var palette))
                    paletteCache[resolvedPalette.Path] = palette = JascPalette.Load(resolvedPalette.Path);
                var sprite = SlpDecoder.Decode(path, palette);
                loaded[definition.GraphicId] = new(definition, Classify(definition), sprite, path);
            }
            catch (Exception ex)
            {
                missing.Add(new(definition, ex.Message));
            }
        }

        var terrainTiles = new List<TerrainTile>(terrainFiles.Length);
        for (var i = 0; i < terrainFiles.Length; i++)
        {
            var path = terrainFiles[i];
            progress.Report((definitions.Count + i, total, Path.GetFileNameWithoutExtension(path)));
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            terrainTiles.Add(new(
                Path.GetFileNameWithoutExtension(path), image.Width, image.Height, image.Data, path));
        }

        var waterTextures = new List<WaterTexture>(waterFiles.Length);
        for (var i = 0; i < waterFiles.Length; i++)
        {
            var path = waterFiles[i];
            progress.Report((definitions.Count + terrainFiles.Length + i, total, Path.GetFileNameWithoutExtension(path)));
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            waterTextures.Add(new(
                Path.GetFileNameWithoutExtension(path), image.Width, image.Height, image.Data, path));
        }

        progress.Report((total, total, "Complete"));
        return new()
        {
            Graphics = loaded,
            Missing = missing,
            TerrainTiles = terrainTiles,
            WaterTextures = waterTextures
        };
    }

    private static string? ResolveSlp(string common, int id)
    {
        foreach (var folder in new[] { "graphics", "gamedata_x2", "gamedata_x1" })
        {
            var path = Path.Combine(common, "drs", folder, $"{id}.slp");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static GraphicKind Classify(GenieGraphic graphic)
    {
        if (graphic.Name.EndsWith("_N0", StringComparison.OrdinalIgnoreCase) ||
            graphic.Name.Contains("SHADOW", StringComparison.OrdinalIgnoreCase))
            return GraphicKind.ShadowLayer;
        if (graphic.Name.StartsWith("UI", StringComparison.OrdinalIgnoreCase) ||
            graphic.Name.StartsWith("ICON", StringComparison.OrdinalIgnoreCase))
            return GraphicKind.Interface;
        if (graphic.Name.Contains("FIRE", StringComparison.OrdinalIgnoreCase) ||
            graphic.Name.Contains("EXPLO", StringComparison.OrdinalIgnoreCase))
            return GraphicKind.Effect;
        if (graphic.AngleCount > 1) return GraphicKind.DirectionalObject;
        if (graphic.FrameCount > 1) return GraphicKind.AnimatedObject;
        if (graphic.FrameCount == 1) return GraphicKind.StaticObject;
        return GraphicKind.Unknown;
    }
}
