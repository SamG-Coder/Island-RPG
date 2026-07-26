using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

return ObjectSpriteTool.Run(args);

internal static class ObjectSpriteTool
{
    public static int Run(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var definitions = LoadDefinitions(options.DefinitionsPath);
            if (!definitions.Presets.TryGetValue(
                    options.Preset, out var preset))
                throw new ArgumentException(
                    $"Unknown preset '{options.Preset}'. Available: " +
                    string.Join(", ", definitions.Presets.Keys));

            Convert(options, definitions.Projection, preset);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Options.PrintUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Object-sprite conversion failed: {exception.Message}");
            return 1;
        }
    }

    private static void Convert(
        Options options, Projection projection, ObjectPreset preset)
    {
        var source = LoadBgra32(options.InputPath);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        RemoveChroma(
            pixels, options.Chroma, options.TransparentTolerance,
            options.OpaqueTolerance);
        var subject = FindOpaqueBounds(
            pixels, source.PixelWidth, source.PixelHeight);

        var footprintWidth = (preset.FootprintWidth +
                              preset.FootprintDepth) *
                             projection.TileWidthPixels * .5;
        var footprintHeight = (preset.FootprintWidth +
                               preset.FootprintDepth) *
                              projection.TileHeightPixels * .5;
        var visibleHeight = footprintHeight +
                            preset.Height *
                            projection.VerticalPixelsPerUnit;
        var targetWidth = Math.Max(
            1, (int)Math.Round(
                footprintWidth * preset.VisualScale));
        var targetHeight = Math.Max(
            1, (int)Math.Round(
                visibleHeight * preset.VisualScale));
        var scale = Math.Min(
            (double)targetWidth / subject.Width,
            (double)targetHeight / subject.Height);
        var spriteWidth = Math.Max(
            1, (int)Math.Round(subject.Width * scale));
        var spriteHeight = Math.Max(
            1, (int)Math.Round(subject.Height * scale));
        var canvasWidth = Even(spriteWidth + preset.Padding * 2);
        var canvasHeight = Even(spriteHeight + preset.Padding * 2);
        var offsetX = (canvasWidth - spriteWidth) / 2;
        var offsetY = canvasHeight - preset.Padding - spriteHeight;
        var output = ResizeNearest(
            pixels, source.PixelWidth, subject,
            canvasWidth, canvasHeight,
            offsetX, offsetY, spriteWidth, spriteHeight);
        ApplySpriteFinish(output);

        Directory.CreateDirectory(
            Path.GetDirectoryName(options.OutputPath) ?? ".");
        SavePng(
            options.OutputPath, output, canvasWidth, canvasHeight);
        var hotspotX = canvasWidth / 2;
        var hotspotY = canvasHeight - preset.Padding;
        var metadata = new ObjectSpriteMetadata(
            options.Preset,
            preset.FootprintWidth,
            preset.FootprintDepth,
            preset.Height,
            canvasWidth,
            canvasHeight,
            hotspotX,
            hotspotY,
            projection.TileWidthPixels,
            projection.TileHeightPixels);
        var metadataPath = Path.ChangeExtension(
            options.OutputPath, ".object.json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions { WriteIndented = true }));

        if (options.PreviewPath is not null)
            SavePreview(
                options.PreviewPath, output, canvasWidth, canvasHeight,
                hotspotX, hotspotY);

        Console.WriteLine(
            $"Created {options.OutputPath} ({canvasWidth}x{canvasHeight}), " +
            $"footprint {preset.FootprintWidth:0.##}x" +
            $"{preset.FootprintDepth:0.##}, hotspot {hotspotX},{hotspotY}.");
        Console.WriteLine($"Metadata: {metadataPath}");
        if (options.PreviewPath is not null)
            Console.WriteLine($"Preview: {options.PreviewPath}");
    }

    private static void RemoveChroma(
        byte[] pixels, Rgb key, int transparentTolerance,
        int opaqueTolerance)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var distance = Math.Sqrt(
                Math.Pow(pixels[index + 2] - key.Red, 2) +
                Math.Pow(pixels[index + 1] - key.Green, 2) +
                Math.Pow(pixels[index] - key.Blue, 2));
            var alpha = distance <= transparentTolerance
                ? 0
                : distance >= opaqueTolerance
                    ? 255
                    : (byte)Math.Round(
                        (distance - transparentTolerance) * 255 /
                        (opaqueTolerance - transparentTolerance));
            pixels[index + 3] = (byte)(
                pixels[index + 3] * alpha / 255);
            if (pixels[index + 3] == 0)
            {
                pixels[index] = pixels[index + 1] =
                    pixels[index + 2] = 0;
                continue;
            }
            DespillChroma(pixels, index, key);
        }
    }

    private static void DespillChroma(
        byte[] pixels, int index, Rgb key)
    {
        // Generated chroma art often contains opaque antialias pixels blended
        // with the key. Suppress only the key's characteristic excess, leaving
        // ordinary warm wood and metal colours intact.
        if (key.Red < 200 || key.Blue < 200 || key.Green > 80)
            return;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var magenta = Math.Min(red, blue);
        if (magenta <= green + 20 ||
            Math.Abs(red - blue) > 72)
            return;
        var neutralLimit = (byte)Math.Min(255, green + 20);
        pixels[index] = Math.Min(blue, neutralLimit);
        pixels[index + 2] = Math.Min(red, neutralLimit);
    }

    private static PixelBounds FindOpaqueBounds(
        byte[] pixels, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 16) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < minX || maxY < minY)
            throw new ArgumentException(
                "No object remained after chroma-key removal.");
        return new(
            minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static byte[] ResizeNearest(
        byte[] source, int sourceWidth, PixelBounds crop,
        int canvasWidth, int canvasHeight,
        int offsetX, int offsetY, int width, int height)
    {
        var output = new byte[canvasWidth * canvasHeight * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = crop.Y +
                          Math.Min(
                              crop.Height - 1,
                              y * crop.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = crop.X +
                              Math.Min(
                                  crop.Width - 1,
                                  x * crop.Width / width);
                var sourceOffset =
                    (sourceY * sourceWidth + sourceX) * 4;
                var outputOffset =
                    ((offsetY + y) * canvasWidth + offsetX + x) * 4;
                Buffer.BlockCopy(
                    source, sourceOffset, output, outputOffset, 4);
            }
        }
        return output;
    }

    private static void ApplySpriteFinish(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 0) continue;
            pixels[index] = Quantize(pixels[index]);
            pixels[index + 1] = Quantize(pixels[index + 1]);
            pixels[index + 2] = Quantize(pixels[index + 2]);
            pixels[index + 3] = pixels[index + 3] < 96
                ? (byte)0
                : pixels[index + 3] < 208 ? (byte)192 : (byte)255;
        }

        static byte Quantize(byte value) =>
            (byte)Math.Clamp(
                (int)Math.Round(value / 17d) * 17, 0, 255);
    }

    private static void SavePreview(
        string path, byte[] sprite, int width, int height,
        int hotspotX, int hotspotY)
    {
        const int scale = 4;
        var previewWidth = width * scale;
        var previewHeight = height * scale;
        var preview = new byte[previewWidth * previewHeight * 4];
        for (var y = 0; y < previewHeight; y++)
        for (var x = 0; x < previewWidth; x++)
        {
            var offset = (y * previewWidth + x) * 4;
            var light = ((x / 16) + (y / 16)) % 2 == 0;
            var shade = light ? (byte)72 : (byte)48;
            preview[offset] = preview[offset + 1] =
                preview[offset + 2] = shade;
            preview[offset + 3] = 255;
            var sourceOffset =
                ((y / scale) * width + x / scale) * 4;
            var alpha = sprite[sourceOffset + 3];
            if (alpha == 0) continue;
            for (var channel = 0; channel < 3; channel++)
                preview[offset + channel] = (byte)(
                    (sprite[sourceOffset + channel] * alpha +
                     preview[offset + channel] * (255 - alpha)) / 255);
        }
        var anchorY = Math.Min(previewHeight - 1, hotspotY * scale);
        var anchorX = Math.Min(previewWidth - 1, hotspotX * scale);
        for (var x = Math.Max(0, anchorX - 12);
             x <= Math.Min(previewWidth - 1, anchorX + 12); x++)
            SetPreviewPixel(preview, previewWidth, x, anchorY);
        for (var y = Math.Max(0, anchorY - 12);
             y <= Math.Min(previewHeight - 1, anchorY + 12); y++)
            SetPreviewPixel(preview, previewWidth, anchorX, y);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        SavePng(path, preview, previewWidth, previewHeight);
    }

    private static void SetPreviewPixel(
        byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = 32;
        pixels[offset + 1] = 32;
        pixels[offset + 2] = 255;
        pixels[offset + 3] = 255;
    }

    private static void SavePng(
        string path, byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static BitmapSource LoadBgra32(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"Input image does not exist: {path}");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(
                frame, PixelFormats.Bgra32, null, 0);
    }

    private static Definitions LoadDefinitions(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException(
                $"Definitions file does not exist: {path}");
        return JsonSerializer.Deserialize<Definitions>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   }) ??
               throw new ArgumentException(
                   $"Definitions file is invalid: {path}");
    }

    private static int Even(int value) => value % 2 == 0
        ? value
        : value + 1;

    private readonly record struct PixelBounds(
        int X, int Y, int Width, int Height);
    private readonly record struct Rgb(byte Red, byte Green, byte Blue)
    {
        public static Rgb Parse(string value)
        {
            var hex = value.Trim().TrimStart('#');
            if (hex.Length != 6 ||
                !int.TryParse(
                    hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var packed))
                throw new ArgumentException(
                    $"Invalid chroma colour '{value}'. Use #RRGGBB.");
            return new(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
        }
    }

    private sealed record Definitions(
        Projection Projection,
        Dictionary<string, ObjectPreset> Presets);
    private sealed record Projection(
        int TileWidthPixels,
        int TileHeightPixels,
        int VerticalPixelsPerUnit);
    private sealed record ObjectPreset(
        double FootprintWidth,
        double FootprintDepth,
        double Height,
        double VisualScale,
        int Padding,
        string Description);
    private sealed record ObjectSpriteMetadata(
        string Preset,
        double FootprintWidth,
        double FootprintDepth,
        double Height,
        int PixelWidth,
        int PixelHeight,
        int HotspotX,
        int HotspotY,
        int TileWidthPixels,
        int TileHeightPixels);

    private sealed record Options(
        string InputPath,
        string OutputPath,
        string Preset,
        string DefinitionsPath,
        string? PreviewPath,
        Rgb Chroma,
        int TransparentTolerance,
        int OpaqueTolerance)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help"))
                throw new ArgumentException(
                    args.Contains("--help")
                        ? "Isometric object sprite converter."
                        : "Arguments are required.");
            string? input = null;
            string? output = null;
            string? preview = null;
            var preset = "workbench";
            var definitions = Path.Combine(
                AppContext.BaseDirectory, "object-definitions.json");
            var chroma = new Rgb(255, 0, 255);
            var transparentTolerance = 28;
            var opaqueTolerance = 100;
            for (var index = 0; index < args.Length; index++)
            {
                var option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException(
                        $"Missing value for {option}.");
                var value = args[++index];
                switch (option)
                {
                    case "--input":
                        input = Path.GetFullPath(value);
                        break;
                    case "--output":
                        output = Path.GetFullPath(value);
                        break;
                    case "--preset":
                        preset = value;
                        break;
                    case "--definitions":
                        definitions = Path.GetFullPath(value);
                        break;
                    case "--preview":
                        preview = Path.GetFullPath(value);
                        break;
                    case "--chroma":
                        chroma = Rgb.Parse(value);
                        break;
                    case "--transparent-tolerance":
                        transparentTolerance =
                            Tolerance(option, value);
                        break;
                    case "--opaque-tolerance":
                        opaqueTolerance = Tolerance(option, value);
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown option: {option}");
                }
            }
            if (string.IsNullOrWhiteSpace(input) ||
                string.IsNullOrWhiteSpace(output))
                throw new ArgumentException(
                    "--input and --output are required.");
            if (opaqueTolerance <= transparentTolerance)
                throw new ArgumentException(
                    "--opaque-tolerance must exceed " +
                    "--transparent-tolerance.");
            return new(
                input, output, preset, definitions, preview, chroma,
                transparentTolerance, opaqueTolerance);
        }

        private static int Tolerance(string option, string value)
        {
            if (!int.TryParse(value, out var result) ||
                result is < 0 or > 441)
                throw new ArgumentException(
                    $"{option} must be between 0 and 441.");
            return result;
        }

        public static void PrintUsage()
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project " +
                "tools/IslandRpg.ObjectSpriteTool -- " +
                "--input <png> --output <png> --preset <name> [options]");
            Console.Error.WriteLine(
                "  --definitions <json>  Presets and projection constants");
            Console.Error.WriteLine(
                "  --preview <png>       Write a 4x checkerboard preview");
            Console.Error.WriteLine(
                "  --chroma <#RRGGBB>    Background key (default #FF00FF)");
        }
    }
}
