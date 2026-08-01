using StbImageSharp;

namespace IslandRpg.Assets;

internal static class TestAssetLoader
{
    private static readonly string[] TerrainNames =
    [
        "g_wt4_00_COLOR", "g_wt3_00_color", "g_sha_00_color",
        "g_sh3_00_color", "g_bch_00_color", "g_for_00_color",
        "g_fo2_00_color", "g_gr5_00_color", "g_gr4_00_color",
        "g_gr3_00_color", "g_rck_00_COLOR", "g_sng_00_color",
        "g_sno_00_color", "g_pal_00_color", "g_pal1_00_COLOR",
        "g_grs_00_color"
    ];

    public static AssetCatalog LoadAll(
        string root,
        IProgress<(int Done, int Total, string Name)> progress,
        IReadOnlySet<string> requiredGraphics)
    {
        Directory.CreateDirectory(root);
        var graphicNames = requiredGraphics
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = graphicNames.Length + TerrainNames.Length + 4;
        var done = 0;
        short graphicId = 1;
        var graphics = new Dictionary<short, LoadedGraphic>();
        foreach (var name in graphicNames)
        {
            progress.Report((done++, total, name));
            var frames = LoadGraphicFrames(root, name);
            if (frames.Count == 0)
                frames = [DebugGraphic(name)];
            var directional = IsDirectional(name) && frames.Count >= 5;
            var angles = directional ? 5 : 1;
            var definition = new GenieGraphic(
                name,
                name,
                0,
                (ushort)Math.Max(1, frames.Count / angles),
                (ushort)angles,
                .1f,
                0,
                graphicId,
                0);
            graphics[graphicId++] = new(
                definition,
                directional
                    ? GraphicKind.DirectionalObject
                    : frames.Count > 1
                        ? GraphicKind.AnimatedObject
                        : GraphicKind.StaticObject,
                new Sprite(frames),
                Path.Combine(root, "Graphics", name));
        }

        var terrainImages = TerrainNames
            .Select(name =>
            {
                var path = FindPng(Path.Combine(root, "Terrain"), name);
                return (Name: name, Path: path,
                    Image: path is null ? null : LoadImage(path));
            })
            .ToArray();
        var terrainWidth = terrainImages
            .FirstOrDefault(item => item.Image is not null).Image?.Width ?? 64;
        var terrainHeight = terrainImages
            .FirstOrDefault(item => item.Image is not null).Image?.Height ?? 64;
        var terrain = new List<TerrainTile>(TerrainNames.Length);
        foreach (var item in terrainImages)
        {
            progress.Report((done++, total, item.Name));
            terrain.Add(new(
                item.Name,
                item.Image?.Width ?? terrainWidth,
                item.Image?.Height ?? terrainHeight,
                item.Image?.Data ??
                DebugTerrain(item.Name, terrainWidth, terrainHeight),
                item.Path ?? "generated:test-terrain"));
        }

        var waterImages = Enumerable.Range(0, 4)
            .Select(index =>
            {
                var name = $"normal{index}";
                var path = FindPng(Path.Combine(root, "Water"), name);
                return (Name: name, Path: path,
                    Image: path is null ? null : LoadImage(path));
            })
            .ToArray();
        var waterWidth = waterImages
            .FirstOrDefault(item => item.Image is not null).Image?.Width ?? 32;
        var waterHeight = waterImages
            .FirstOrDefault(item => item.Image is not null).Image?.Height ?? 32;
        var water = new List<WaterTexture>(4);
        foreach (var item in waterImages)
        {
            progress.Report((done++, total, item.Name));
            water.Add(new(
                item.Name,
                item.Image?.Width ?? waterWidth,
                item.Image?.Height ?? waterHeight,
                item.Image?.Data ??
                NeutralWaterNormal(waterWidth, waterHeight),
                item.Path ?? "generated:test-water"));
        }
        progress.Report((total, total, "Complete"));
        return new()
        {
            Graphics = graphics,
            Missing = [],
            TerrainTiles = terrain,
            WaterTextures = water
        };
    }

    private static IReadOnlyList<SpriteFrame> LoadGraphicFrames(
        string root,
        string name)
    {
        var graphics = Path.Combine(root, "Graphics");
        var single = FindPng(graphics, name);
        var paths = single is null
            ? Directory.Exists(Path.Combine(graphics, name))
                ? Directory.GetFiles(
                        Path.Combine(graphics, name), "*.png")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : []
            : [single];
        return paths.Select(path =>
            {
                var image = LoadImage(path);
                return new SpriteFrame(
                    image.Width,
                    image.Height,
                    image.Width / 2,
                    image.Height - 1,
                    image.Data);
            })
            .ToArray();
    }

    private static ImageResult LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
    }

    private static string? FindPng(string directory, string name)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.png")
            .FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(
                    name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDirectional(string name) =>
        name.EndsWith("_WN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_FN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_AN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_TN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_DN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_SN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SHIPF5SF", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("COGX_1H", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SHIP_3BF", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("COGXX_DN", StringComparison.OrdinalIgnoreCase);

    private static SpriteFrame DebugGraphic(string name)
    {
        const int size = 32;
        var rgba = new byte[size * size * 4];
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
        var red = (byte)(96 + Math.Abs(hash % 128));
        var blue = (byte)(96 + Math.Abs(hash / 128 % 128));
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var visible = x is >= 5 and <= 26 && y is >= 5 and <= 26 &&
                          (x is 5 or 26 || y is 5 or 26 ||
                           x == y || x + y == size - 1);
            if (!visible) continue;
            var offset = (y * size + x) * 4;
            rgba[offset] = red;
            rgba[offset + 1] = 32;
            rgba[offset + 2] = blue;
            rgba[offset + 3] = 255;
        }
        return new(size, size, size / 2, size - 1, rgba);
    }

    private static byte[] DebugTerrain(string name, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
        var baseRed = (byte)(48 + Math.Abs(hash % 96));
        var baseGreen = (byte)(48 + Math.Abs(hash / 97 % 96));
        var baseBlue = (byte)(48 + Math.Abs(hash / 193 % 96));
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var checker = ((x / 8 + y / 8) & 1) == 0 ? 18 : 0;
            var offset = (y * width + x) * 4;
            rgba[offset] = (byte)Math.Min(255, baseRed + checker);
            rgba[offset + 1] = (byte)Math.Min(255, baseGreen + checker);
            rgba[offset + 2] = (byte)Math.Min(255, baseBlue + checker);
            rgba[offset + 3] = 255;
        }
        return rgba;
    }

    private static byte[] NeutralWaterNormal(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            rgba[offset] = 128;
            rgba[offset + 1] = 128;
            rgba[offset + 2] = 255;
            rgba[offset + 3] = 255;
        }
        return rgba;
    }
}
