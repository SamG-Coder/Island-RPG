using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

return SpriteSheetTool.Run(args);

//Guide: what we need to do now is we need to create stone hammer and stone axe.
//lookup guide on how to make sprites use reference from woodcutting-items.png and 32x32 aoe2 style.
//use tools -> islandRpg.SpriteTool

internal static class SpriteSheetTool
{
    public static int Run(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            Process(options);
            Console.WriteLine(
                $"Created {options.OutputPath} ({options.Columns * options.CellSize}x" +
                $"{options.Rows * options.CellSize}, {options.Columns * options.Rows} " +
                $"{options.CellSize}x{options.CellSize} cells).");
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
            Console.Error.WriteLine($"Sprite-sheet processing failed: {exception.Message}");
            return 1;
        }
    }

    private static void Process(Options options)
    {
        var source = LoadBgra32(options.InputPath);
        var sourcePixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(
            sourcePixels, source.PixelWidth * 4, 0);

        var crop = CalculateCenteredCrop(
            source.PixelWidth, source.PixelHeight,
            options.Columns, options.Rows);
        var outputWidth = options.Columns * options.CellSize;
        var outputHeight = options.Rows * options.CellSize;
        var outputPixels = new byte[outputWidth * outputHeight * 4];

        for (var y = 0; y < outputHeight; y++)
        {
            var sourceY = crop.Y + y * crop.Height / outputHeight;
            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = crop.X + x * crop.Width / outputWidth;
                var sourceOffset =
                    (sourceY * source.PixelWidth + sourceX) * 4;
                var outputOffset = (y * outputWidth + x) * 4;
                var blue = sourcePixels[sourceOffset];
                var green = sourcePixels[sourceOffset + 1];
                var red = sourcePixels[sourceOffset + 2];
                var alpha = sourcePixels[sourceOffset + 3];

                if (options.SoftChroma)
                {
                    var distance = Math.Sqrt(
                        Math.Pow(red - options.Chroma.Red, 2) +
                        Math.Pow(green - options.Chroma.Green, 2) +
                        Math.Pow(blue - options.Chroma.Blue, 2));
                    var transparent = options.ChromaTolerance;
                    var opaque = Math.Min(441, Math.Max(
                        transparent + 1, transparent * 3));
                    var matte = distance <= transparent
                        ? 0
                        : distance >= opaque
                            ? 255
                            : (int)Math.Round(
                                (distance - transparent) * 255 /
                                (opaque - transparent));
                    alpha = (byte)(alpha * matte / 255);
                    if (alpha == 0)
                    {
                        outputPixels[outputOffset] = 0;
                        outputPixels[outputOffset + 1] = 0;
                        outputPixels[outputOffset + 2] = 0;
                        outputPixels[outputOffset + 3] = 0;
                        continue;
                    }
                    DespillMagenta(ref red, ref green, ref blue);
                }
                else if (IsChroma(
                             red, green, blue, options.Chroma,
                             options.ChromaTolerance))
                {
                    outputPixels[outputOffset] = 0;
                    outputPixels[outputOffset + 1] = 0;
                    outputPixels[outputOffset + 2] = 0;
                    outputPixels[outputOffset + 3] = 0;
                    continue;
                }

                if (options.BottomFadeStart is { } fadeStart)
                {
                    var normalizedY = outputHeight <= 1
                        ? 1f
                        : y / (float)(outputHeight - 1);
                    var fade = Math.Clamp(
                        (normalizedY - fadeStart) /
                        Math.Max(.001f, 1f - fadeStart),
                        0f, 1f);
                    var brightness = 1f -
                        fade * options.BottomFadeStrength;
                    blue = (byte)Math.Clamp(
                        MathF.Round(blue * brightness), 0, 255);
                    green = (byte)Math.Clamp(
                        MathF.Round(green * brightness), 0, 255);
                    red = (byte)Math.Clamp(
                        MathF.Round(red * brightness), 0, 255);
                }

                outputPixels[outputOffset] = blue;
                outputPixels[outputOffset + 1] = green;
                outputPixels[outputOffset + 2] = red;
                outputPixels[outputOffset + 3] = alpha;
            }
        }

        var outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var bitmap = BitmapSource.Create(
            outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32,
            null, outputPixels, outputWidth * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(options.OutputPath);
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
        if (frame.Format == PixelFormats.Bgra32)
            return frame;
        return new FormatConvertedBitmap(
            frame, PixelFormats.Bgra32, null, 0);
    }

    private static CropRectangle CalculateCenteredCrop(
        int width, int height, int columns, int rows)
    {
        var targetAspect = (double)columns / rows;
        var sourceAspect = (double)width / height;
        if (sourceAspect > targetAspect)
        {
            var cropWidth = Math.Max(1, (int)Math.Round(height * targetAspect));
            return new((width - cropWidth) / 2, 0, cropWidth, height);
        }

        var cropHeight = Math.Max(1, (int)Math.Round(width / targetAspect));
        return new(0, (height - cropHeight) / 2, width, cropHeight);
    }

    private static bool IsChroma(
        byte red, byte green, byte blue, Rgb chroma, int tolerance) =>
        Math.Abs(red - chroma.Red) <= tolerance &&
        Math.Abs(green - chroma.Green) <= tolerance &&
        Math.Abs(blue - chroma.Blue) <= tolerance;

    private static void DespillMagenta(
        ref byte red, ref byte green, ref byte blue)
    {
        var magenta = Math.Min(red, blue);
        if (magenta <= green + 20 || Math.Abs(red - blue) > 72) return;
        var neutralLimit = (byte)Math.Min(255, green + 20);
        red = Math.Min(red, neutralLimit);
        blue = Math.Min(blue, neutralLimit);
    }

    private readonly record struct CropRectangle(
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
                    $"Invalid chroma colour '{value}'. Use six-digit RGB hex, e.g. #FF00FF.");
            return new(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
        }
    }

    private sealed record Options(
        string InputPath,
        string OutputPath,
        int Columns,
        int Rows,
        int CellSize,
        Rgb Chroma,
        int ChromaTolerance,
        bool SoftChroma,
        float? BottomFadeStart,
        float BottomFadeStrength)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length < 2 || args.Contains("--help"))
                throw new ArgumentException(
                    args.Contains("--help") ? "Sprite-sheet image processor." :
                    "Input and output paths are required.");

            var columns = 4;
            var rows = 2;
            var cellSize = 32;
            var chroma = new Rgb(255, 0, 255);
            var tolerance = 32;
            var softChroma = false;
            float? bottomFadeStart = null;
            var bottomFadeStrength = .94f;

            for (var index = 2; index < args.Length; index++)
            {
                var option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {option}.");
                var value = args[++index];
                switch (option)
                {
                    case "--columns":
                        columns = PositiveInteger(option, value);
                        break;
                    case "--rows":
                        rows = PositiveInteger(option, value);
                        break;
                    case "--cell-size":
                        cellSize = PositiveInteger(option, value);
                        break;
                    case "--chroma":
                        chroma = Rgb.Parse(value);
                        break;
                    case "--tolerance":
                        if (!int.TryParse(value, out tolerance) ||
                            tolerance is < 0 or > 255)
                            throw new ArgumentException(
                                "--tolerance must be between 0 and 255.");
                        break;
                    case "--soft-chroma":
                        softChroma = bool.Parse(value);
                        break;
                    case "--bottom-fade-start":
                        bottomFadeStart = UnitFloat(option, value);
                        break;
                    case "--bottom-fade-strength":
                        bottomFadeStrength = UnitFloat(option, value);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {option}");
                }
            }

            return new(
                Path.GetFullPath(args[0]),
                Path.GetFullPath(args[1]),
                columns, rows, cellSize, chroma, tolerance, softChroma,
                bottomFadeStart, bottomFadeStrength);
        }

        private static int PositiveInteger(string option, string value)
        {
            if (!int.TryParse(value, out var result) || result <= 0)
                throw new ArgumentException(
                    $"{option} must be a positive whole number.");
            return result;
        }

        private static float UnitFloat(string option, string value)
        {
            if (!float.TryParse(
                    value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var result) ||
                result is < 0 or > 1)
                throw new ArgumentException(
                    $"{option} must be between 0 and 1.");
            return result;
        }

        public static void PrintUsage()
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project tools/IslandRpg.SpriteTool -- " +
                "<input> <output> [options]");
            Console.Error.WriteLine("  --columns <n>      Sheet columns (default: 4)");
            Console.Error.WriteLine("  --rows <n>         Sheet rows (default: 2)");
            Console.Error.WriteLine("  --cell-size <px>   Square cell size (default: 32)");
            Console.Error.WriteLine("  --chroma <#RRGGBB> Transparent colour (default: #FF00FF)");
            Console.Error.WriteLine("  --tolerance <0-255> Chroma tolerance (default: 32)");
            Console.Error.WriteLine("  --soft-chroma <bool> Soft alpha and magenta despill (default: false)");
            Console.Error.WriteLine(
                "  --bottom-fade-start <0-1> Darken pixels below this height");
            Console.Error.WriteLine(
                "  --bottom-fade-strength <0-1> Maximum darkening (default: .94)");
        }
    }
}
