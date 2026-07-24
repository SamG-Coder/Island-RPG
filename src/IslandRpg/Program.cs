using IslandRpg.Assets;
using IslandRpg.Rendering;

try
{
    var options = AppOptions.Parse(args);
    var install = Age2InstallLocator.Find(options.Age2Path);
    Console.WriteLine($"Age2HD: {install}");

    var datPath = Path.Combine(install, "resources", "_common", "dat", "empires2_x2_p1.dat");
    if (options.Catalog)
    {
        AssetCatalog assetCatalog;
        if (options.ValidateOnly)
        {
            assetCatalog = AssetLoader.LoadAll(
                install,
                new Progress<(int Done, int Total, string Name)>());
        }
        else
        {
            using var host = new GameHostWindow(
                install,
                options.Island
                    ? GameHostWindow.PreviewMode.Island
                    : GameHostWindow.PreviewMode.Assets);
            host.Run();
            assetCatalog = host.Catalog ??
                           throw new InvalidOperationException("The asset catalogue did not finish loading.");
        }
        Console.WriteLine(
            $"Asset inventory: {assetCatalog.Graphics.Count} loose graphics loaded into memory");
        Console.WriteLine($"Terrain textures loaded: {assetCatalog.TerrainTiles.Count}");
        Console.WriteLine($"Water normal maps loaded: {assetCatalog.WaterTextures.Count}");
        Console.WriteLine(
            $"DAT references without a loose SLP: {assetCatalog.Missing.Count} " +
            "(legacy, superseded, unused, or unavailable; not an installation error)");
        Console.WriteLine($"Full asset report: {AssetAudit.Save(assetCatalog)}");
        return;
    }

    var graphicId = options.GraphicId;
    GenieGraphic? graphic = null;
    if (graphicId is null)
    {
        graphic = GenieDatReader.FindGraphic(datPath, options.GraphicName);
        graphicId = graphic.SlpId;
        Console.WriteLine(
            $"DAT graphic {graphic.Name}: SLP {graphic.SlpId}, " +
            $"{graphic.FrameCount} frame(s)/angle, {graphic.AngleCount} angle(s), " +
            $"{graphic.FrameRate:0.###} sec/frame");
    }

    var spritePath = Path.Combine(install, "resources", "_common", "drs", "graphics", $"{graphicId}.slp");
    if (!File.Exists(spritePath))
        throw new FileNotFoundException($"Graphic {graphicId} was not found.", spritePath);

    var resolvedPalette = Age2PaletteResolver.Resolve(install, spritePath);
    Console.WriteLine($"Palette: {resolvedPalette.Description} ({Path.GetFileName(resolvedPalette.Path)})");
    var palette = JascPalette.Load(resolvedPalette.Path);
    var sprite = SlpDecoder.Decode(spritePath, palette);
    Console.WriteLine($"Decoded SLP {graphicId}: {sprite.Frames.Count} frame(s)");

    if (graphic?.Name.EndsWith("_NN", StringComparison.OrdinalIgnoreCase) == true)
    {
        var shadowName = graphic.Name[..^2] + "N0";
        try
        {
            var shadowGraphic = GenieDatReader.FindGraphic(datPath, shadowName);
            var shadowPath = Path.Combine(install, "resources", "_common", "drs", "graphics", $"{shadowGraphic.SlpId}.slp");
            var shadow = SlpDecoder.Decode(shadowPath, palette);
            sprite = SpriteCompositor.Layer(shadow, sprite);
            Console.WriteLine(
                $"Composited shadow {shadowName} (SLP {shadowGraphic.SlpId}) using shared SLP hotspots");
        }
        catch (KeyNotFoundException)
        {
            Console.WriteLine($"No companion shadow record found for {graphic.Name}");
        }
    }

    if (options.ValidateOnly) return;
    using var game = new DemoWindow(sprite, graphicId.Value, graphic?.FrameRate, graphic?.FrameCount);
    game.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(
        "Usage: IslandRpg [--age2-path <folder>] [--graphic <SLP id> | --graphic-name <DAT name>]");
    Environment.ExitCode = 1;
}

internal sealed record AppOptions(
    string? Age2Path,
    int? GraphicId,
    string GraphicName,
    bool ValidateOnly,
    bool Catalog,
    bool Island)
{
    public static AppOptions Parse(string[] args)
    {
        string? path = null;
        int? graphic = null;
        // TREEA_N0 is the ground-shadow layer. TREEA_NN is the visible,
        // full-colour tree sprite.
        var graphicName = "TREEA_NN";
        var validateOnly = false;
        var catalog = true;
        var island = true;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--age2-path" && i + 1 < args.Length) path = args[++i];
            else if (args[i] == "--graphic" && i + 1 < args.Length && int.TryParse(args[++i], out var id))
            {
                graphic = id;
                catalog = false;
            }
            else if (args[i] == "--graphic-name" && i + 1 < args.Length)
            {
                graphicName = args[++i];
                catalog = false;
            }
            else if (args[i] == "--catalog")
            {
                catalog = true;
                island = false;
            }
            else if (args[i] == "--island")
            {
                catalog = true;
                island = true;
            }
            else if (args[i] == "--validate") validateOnly = true;
            else throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
        }
        return new(path, graphic, graphicName, validateOnly, catalog, island);
    }
}
