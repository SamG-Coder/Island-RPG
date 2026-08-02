using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

return FaceRemovalTool.Run(args);

internal static class FaceRemovalTool
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Contains("--self-test"))
            {
                RunSelfTest();
                Console.WriteLine("Face-removal checks passed.");
                return 0;
            }
            var options = Options.Parse(args);
            var result = Process(options);
            Console.WriteLine(
                $"Created {options.OutputPath} ({result.Width}x" +
                $"{result.Height}); replaced {result.ReplacedPixels:N0} " +
                $"pixels, protected {result.ProtectedMatches:N0} edge matches.");
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
                $"Face removal failed: {exception.Message}");
            return 1;
        }
    }

    private static ProcessingResult Process(Options options)
    {
        var source = LoadBgra32(options.InputPath);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        var original = options.PreserveColors.Count > 0
            ? (byte[])pixels.Clone()
            : null;
        var result = RemoveColors(
            pixels,
            source.PixelWidth,
            source.PixelHeight,
            options.Colors,
            options.ColorThreshold,
            options.IgnoreTransparentEdge,
            options.TransparentAlpha,
            options.Columns,
            options.Rows,
            options.BlurRadius,
            options.RegionExpand,
            options.FillDirection,
            options.EdgeProtection);
        if (original is not null)
            RestoreColors(
                pixels, original, options.PreserveColors,
                options.PreserveThreshold);

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var bitmap = BitmapSource.Create(
            source.PixelWidth, source.PixelHeight,
            source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null,
            pixels, source.PixelWidth * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(options.OutputPath);
        encoder.Save(output);
        return result;
    }

    private static void RestoreColors(
        byte[] pixels, byte[] original, IReadOnlyList<Rgb> colors,
        int threshold)
    {
        var thresholdSquared = threshold * threshold;
        for (var offset = 0; offset < original.Length; offset += 4)
        {
            if (original[offset + 3] == 0 || !colors.Any(color =>
                    color.DistanceSquared(
                        original[offset + 2], original[offset + 1],
                        original[offset]) <= thresholdSquared))
                continue;
            pixels[offset] = original[offset];
            pixels[offset + 1] = original[offset + 1];
            pixels[offset + 2] = original[offset + 2];
            pixels[offset + 3] = original[offset + 3];
        }
    }

    internal static ProcessingResult RemoveColors(
        byte[] pixels,
        int width,
        int height,
        IReadOnlyList<Rgb> colors,
        int threshold,
        int ignoreTransparentEdge,
        byte transparentAlpha,
        int columns,
        int rows,
        int blurRadius = 2,
        int regionExpand = 0,
        FillDirection fillDirection = FillDirection.LeftToRight,
        EdgeProtection edgeProtection = EdgeProtection.Distance)
    {
        if (width <= 0 || height <= 0 ||
            pixels.Length != width * height * 4)
            throw new ArgumentException("Invalid BGRA image buffer.");
        if (colors.Count == 0)
            throw new ArgumentException("At least one removal color is required.");
        if (columns <= 0 || rows <= 0 || columns > width || rows > height)
            throw new ArgumentException(
                "Sheet columns and rows must fit within the image dimensions.");

        var edgeDistance = TransparentEdgeDistances(
            pixels, width, height, transparentAlpha,
            ignoreTransparentEdge);
        var replaced = 0;
        var protectedMatches = 0;
        var replacedMask = new bool[width * height];
        var removalMask = BuildRemovalMask(
            pixels, width, height, colors, threshold, transparentAlpha,
            edgeDistance, ignoreTransparentEdge, columns, rows,
            regionExpand, edgeProtection, out protectedMatches,
            out var protectedMask);
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var left = column * cellWidth;
            var right = column == columns - 1
                ? width
                : left + cellWidth;
            var top = row * cellHeight;
            var bottom = row == rows - 1
                ? height
                : top + cellHeight;
            if (fillDirection == FillDirection.BottomToTop)
            {
                for (var x = left; x < right; x++)
                {
                    var previousSafeOffset = -1;
                    for (var y = bottom - 1; y >= top; y--)
                    {
                        ReplaceOrRemember(x, y, ref previousSafeOffset);
                    }
                }
            }
            else
            {
                for (var y = top; y < bottom; y++)
                {
                    var previousSafeOffset = -1;
                    for (var x = left; x < right; x++)
                    {
                        ReplaceOrRemember(x, y, ref previousSafeOffset);
                    }
                }
            }

            void ReplaceOrRemember(int x, int y, ref int previousSafeOffset)
            {
                var pixelIndex = y * width + x;
                var offset = pixelIndex * 4;
                if (pixels[offset + 3] <= transparentAlpha)
                {
                    previousSafeOffset = -1;
                    return;
                }
                if (!removalMask[pixelIndex])
                {
                    if (protectedMask[pixelIndex]) return;
                    previousSafeOffset = offset;
                    return;
                }
                if (previousSafeOffset < 0) return;
                pixels[offset] = pixels[previousSafeOffset];
                pixels[offset + 1] = pixels[previousSafeOffset + 1];
                pixels[offset + 2] = pixels[previousSafeOffset + 2];
                pixels[offset + 3] = pixels[previousSafeOffset + 3];
                replacedMask[pixelIndex] = true;
                replaced++;
            }
        }
        if (blurRadius > 0 && replaced > 0)
            BlurReconstructedRegions(
                pixels, replacedMask, width, height, transparentAlpha,
                columns, rows, blurRadius);
        return new(width, height, replaced, protectedMatches);
    }

    private static bool[] BuildRemovalMask(
        byte[] pixels, int width, int height, IReadOnlyList<Rgb> colors,
        int threshold, byte transparentAlpha, ushort[] edgeDistance,
        int ignoredEdge, int columns, int rows, int expansion,
        EdgeProtection edgeProtection, out int protectedMatches,
        out bool[] protectedMask)
    {
        var mask = new bool[width * height];
        var colorMatches = new bool[width * height];
        protectedMask = new bool[width * height];
        protectedMatches = 0;
        for (var index = 0; index < colorMatches.Length; index++)
        {
            var offset = index * 4;
            if (pixels[offset + 3] <= transparentAlpha) continue;
            colorMatches[index] = colors.Any(color => color.DistanceSquared(
                pixels[offset + 2], pixels[offset + 1], pixels[offset]) <=
                threshold * threshold);
        }
        var connectedEdge = edgeProtection == EdgeProtection.ColorConnected
            ? ColorConnectedEdgePixels(
                pixels, colorMatches, edgeDistance, width, height,
                transparentAlpha, ignoredEdge, threshold)
            : null;
        for (var index = 0; index < mask.Length; index++)
        {
            if (!colorMatches[index]) continue;
            var isProtected = connectedEdge is null
                ? edgeDistance[index] <= ignoredEdge
                : connectedEdge[index];
            if (isProtected)
            {
                protectedMatches++;
                protectedMask[index] = true;
            }
            else
                mask[index] = true;
        }
        if (expansion <= 0) return mask;
        var original = (bool[])mask.Clone();
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (!original[index]) continue;
            var left = x / cellWidth * cellWidth;
            var top = y / cellHeight * cellHeight;
            var right = x / cellWidth == columns - 1 ? width : left + cellWidth;
            var bottom = y / cellHeight == rows - 1 ? height : top + cellHeight;
            for (var targetY = Math.Max(top, y - expansion);
                 targetY <= Math.Min(bottom - 1, y + expansion); targetY++)
            for (var targetX = Math.Max(left, x - expansion);
                 targetX <= Math.Min(right - 1, x + expansion); targetX++)
            {
                if (Math.Abs(targetX - x) + Math.Abs(targetY - y) > expansion)
                    continue;
                var target = targetY * width + targetX;
                if (pixels[target * 4 + 3] > transparentAlpha &&
                    edgeDistance[target] > ignoredEdge)
                    mask[target] = true;
            }
        }
        return mask;
    }

    private static bool[] ColorConnectedEdgePixels(
        byte[] pixels, bool[] colorMatches, ushort[] edgeDistance,
        int width, int height, byte transparentAlpha, int ignoredEdge,
        int colorThreshold)
    {
        var connected = new bool[colorMatches.Length];
        var allowedBrightness = Enumerable.Repeat(
            -1, colorMatches.Length).ToArray();
        var queue = new Queue<int>();
        for (var index = 0; index < colorMatches.Length; index++)
        {
            if (!colorMatches[index] || edgeDistance[index] != 1) continue;
            connected[index] = true;
            allowedBrightness[index] = Math.Min(
                255, Brightness(pixels, index) + colorThreshold);
            queue.Enqueue(index);
        }
        while (queue.TryDequeue(out var index))
        {
            var x = index % width;
            var y = index / width;
            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);

            void Visit(int targetX, int targetY)
            {
                if ((uint)targetX >= (uint)width ||
                    (uint)targetY >= (uint)height) return;
                var target = targetY * width + targetX;
                var permitted = allowedBrightness[index];
                if (!colorMatches[target] ||
                    edgeDistance[target] > ignoredEdge ||
                    pixels[target * 4 + 3] <= transparentAlpha ||
                    Brightness(pixels, target) > permitted ||
                    allowedBrightness[target] >= permitted) return;
                connected[target] = true;
                allowedBrightness[target] = permitted;
                queue.Enqueue(target);
            }
        }
        return connected;

        static int Brightness(byte[] buffer, int pixelIndex)
        {
            var offset = pixelIndex * 4;
            return (buffer[offset + 2] * 54 +
                    buffer[offset + 1] * 183 +
                    buffer[offset] * 19) >> 8;
        }
    }

    private static void BlurReconstructedRegions(
        byte[] pixels,
        bool[] reconstructed,
        int width,
        int height,
        byte transparentAlpha,
        int columns,
        int rows,
        int radius)
    {
        var source = (byte[])pixels.Clone();
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var pixelIndex = y * width + x;
            if (!reconstructed[pixelIndex]) continue;

            var cellLeft = x / cellWidth * cellWidth;
            var cellTop = y / cellHeight * cellHeight;
            var cellRight = x / cellWidth == columns - 1
                ? width
                : cellLeft + cellWidth;
            var cellBottom = y / cellHeight == rows - 1
                ? height
                : cellTop + cellHeight;
            var red = 0;
            var green = 0;
            var blue = 0;
            var alpha = 0;
            var samples = 0;
            for (var sampleY = Math.Max(cellTop, y - radius);
                 sampleY <= Math.Min(cellBottom - 1, y + radius);
                 sampleY++)
            for (var sampleX = Math.Max(cellLeft, x - radius);
                 sampleX <= Math.Min(cellRight - 1, x + radius);
                 sampleX++)
            {
                var sampleOffset = (sampleY * width + sampleX) * 4;
                if (source[sampleOffset + 3] <= transparentAlpha) continue;
                blue += source[sampleOffset];
                green += source[sampleOffset + 1];
                red += source[sampleOffset + 2];
                alpha += source[sampleOffset + 3];
                samples++;
            }
            if (samples == 0) continue;
            var offset = pixelIndex * 4;
            pixels[offset] = (byte)(blue / samples);
            pixels[offset + 1] = (byte)(green / samples);
            pixels[offset + 2] = (byte)(red / samples);
            pixels[offset + 3] = (byte)(alpha / samples);
        }
    }

    private static ushort[] TransparentEdgeDistances(
        byte[] pixels,
        int width,
        int height,
        byte transparentAlpha,
        int maximumDistance)
    {
        var cap = Math.Max(0, maximumDistance) + 1;
        var result = Enumerable.Repeat(
            (ushort)cap, width * height).ToArray();
        var queue = new Queue<int>();
        for (var index = 0; index < result.Length; index++)
            if (pixels[index * 4 + 3] <= transparentAlpha)
            {
                result[index] = 0;
                queue.Enqueue(index);
            }
        while (queue.TryDequeue(out var index))
        {
            var distance = result[index];
            if (distance >= cap) continue;
            var x = index % width;
            var y = index / width;
            Visit(x - 1, y, distance);
            Visit(x + 1, y, distance);
            Visit(x, y - 1, distance);
            Visit(x, y + 1, distance);
        }
        return result;

        void Visit(int x, int y, ushort previous)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            var candidate = (ushort)(previous + 1);
            var target = y * width + x;
            if (candidate >= result[target] || candidate > cap) return;
            result[target] = candidate;
            queue.Enqueue(target);
        }
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

    private static void RunSelfTest()
    {
        const int width = 7;
        const int height = 5;
        var pixels = new byte[width * height * 4];
        SetOpaqueRectangle(pixels, width, 1, 1, 5, 3, 180);
        SetPixel(pixels, width, 3, 2, 8, 8, 8, 255);
        SetPixel(pixels, width, 1, 2, 8, 8, 8, 255);
        var result = RemoveColors(
            pixels, width, height, [new(0, 0, 0)],
            20, 1, 0, 1, 1, 0);
        var center = (2 * width + 3) * 4;
        var edge = (2 * width + 1) * 4;
        if (result.ReplacedPixels != 1 ||
            result.ProtectedMatches != 1 ||
            pixels[center] != 180 || pixels[center + 1] != 180 ||
            pixels[center + 2] != 180 ||
            pixels[edge] != 8)
            throw new InvalidOperationException(
                "Color replacement or transparent-edge protection failed.");

        var blurred = new byte[width * height * 4];
        SetOpaqueRectangle(blurred, width, 1, 1, 5, 3, 180);
        SetPixel(blurred, width, 2, 2, 120, 120, 120, 255);
        SetPixel(blurred, width, 3, 2, 0, 0, 0, 255);
        RemoveColors(
            blurred, width, height, [new(0, 0, 0)],
            0, 1, 0, 1, 1, 1);
        var blurredCenter = (2 * width + 3) * 4;
        var untouchedNeighbor = (2 * width + 2) * 4;
        if (blurred[blurredCenter] <= 120 ||
            blurred[blurredCenter] >= 180 ||
            blurred[untouchedNeighbor] != 120)
            throw new InvalidOperationException(
                "Reconstructed-region blur changed the wrong pixels.");

        var vertical = new byte[width * height * 4];
        SetOpaqueRectangle(vertical, width, 1, 1, 5, 3, 180);
        SetPixel(vertical, width, 3, 3, 120, 120, 120, 255);
        SetPixel(vertical, width, 3, 2, 0, 0, 0, 255);
        RemoveColors(
            vertical, width, height, [new(0, 0, 0)],
            0, 1, 0, 1, 1, 0, 0, FillDirection.BottomToTop);
        if (vertical[blurredCenter] != 120)
            throw new InvalidOperationException(
                "Bottom-to-top replacement did not use the lower neighbor.");

        var connectedEdge = new byte[width * height * 4];
        SetOpaqueRectangle(connectedEdge, width, 1, 1, 5, 3, 180);
        SetPixel(connectedEdge, width, 1, 2, 0, 0, 0, 255);
        SetPixel(connectedEdge, width, 3, 2, 0, 0, 0, 255);
        RemoveColors(
            connectedEdge, width, height, [new(0, 0, 0)],
            0, 4, 0, 1, 1, 0, 0, FillDirection.LeftToRight,
            EdgeProtection.ColorConnected);
        if (connectedEdge[edge] != 0 || connectedEdge[center] != 180)
            throw new InvalidOperationException(
                "Color-connected edge protection confused an interior dark feature with the border.");
    }

    private static void SetOpaqueRectangle(
        byte[] pixels, int width,
        int x, int y, int rectangleWidth, int rectangleHeight, byte value)
    {
        for (var row = y; row < y + rectangleHeight; row++)
        for (var column = x; column < x + rectangleWidth; column++)
            SetPixel(pixels, width, column, row, value, value, value, 255);
    }

    private static void SetPixel(
        byte[] pixels, int width, int x, int y,
        byte red, byte green, byte blue, byte alpha)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = alpha;
    }

    internal readonly record struct ProcessingResult(
        int Width, int Height, int ReplacedPixels, int ProtectedMatches);

    internal enum FillDirection
    {
        LeftToRight,
        BottomToTop
    }

    internal enum EdgeProtection
    {
        Distance,
        ColorConnected
    }

    internal readonly record struct Rgb(byte Red, byte Green, byte Blue)
    {
        public int DistanceSquared(byte red, byte green, byte blue)
        {
            var redDifference = red - Red;
            var greenDifference = green - Green;
            var blueDifference = blue - Blue;
            return redDifference * redDifference +
                   greenDifference * greenDifference +
                   blueDifference * blueDifference;
        }

        public static Rgb Parse(string value)
        {
            var hex = value.Trim().TrimStart('#');
            if (hex.Length != 6 ||
                !int.TryParse(
                    hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var packed))
                throw new ArgumentException(
                    $"Invalid color '{value}'. Use #RRGGBB.");
            return new(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
        }
    }

    private sealed record Options(
        string InputPath,
        string OutputPath,
        IReadOnlyList<Rgb> Colors,
        int ColorThreshold,
        int IgnoreTransparentEdge,
        byte TransparentAlpha,
        int Columns,
        int Rows,
        int BlurRadius,
        int RegionExpand,
        FillDirection FillDirection,
        EdgeProtection EdgeProtection,
        IReadOnlyList<Rgb> PreserveColors,
        int PreserveThreshold)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length < 2 || args.Contains("--help"))
                throw new ArgumentException(
                    args.Contains("--help")
                        ? "Face-removal sprite processor."
                        : "Input and output paths are required.");
            var colors = new List<Rgb>();
            var threshold = 24;
            var edge = 2;
            byte alpha = 8;
            var columns = 1;
            var rows = 1;
            var blurRadius = 2;
            var regionExpand = 0;
            var fillDirection = FillDirection.LeftToRight;
            var edgeProtection = EdgeProtection.Distance;
            var preserveColors = new List<Rgb>();
            var preserveThreshold = 20;
            for (var index = 2; index < args.Length; index++)
            {
                var option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {option}.");
                var value = args[++index];
                switch (option)
                {
                    case "--color":
                        colors.AddRange(value.Split(',',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries).Select(Rgb.Parse));
                        break;
                    case "--threshold":
                        threshold = Integer(option, value, 0, 441);
                        break;
                    case "--ignore-transparent-edge":
                        edge = Integer(option, value, 0, 64);
                        break;
                    case "--transparent-alpha":
                        alpha = (byte)Integer(option, value, 0, 255);
                        break;
                    case "--columns":
                        columns = Integer(option, value, 1, 256);
                        break;
                    case "--rows":
                        rows = Integer(option, value, 1, 256);
                        break;
                    case "--blur-radius":
                        blurRadius = Integer(option, value, 0, 32);
                        break;
                    case "--region-expand":
                        regionExpand = Integer(option, value, 0, 16);
                        break;
                    case "--fill-direction":
                        fillDirection = value.ToLowerInvariant() switch
                        {
                            "left-to-right" => FillDirection.LeftToRight,
                            "bottom-to-top" => FillDirection.BottomToTop,
                            _ => throw new ArgumentException(
                                "--fill-direction must be left-to-right or bottom-to-top.")
                        };
                        break;
                    case "--edge-protection":
                        edgeProtection = value.ToLowerInvariant() switch
                        {
                            "distance" => EdgeProtection.Distance,
                            "color-connected" => EdgeProtection.ColorConnected,
                            _ => throw new ArgumentException(
                                "--edge-protection must be distance or color-connected.")
                        };
                        break;
                    case "--preserve-color":
                        preserveColors.AddRange(value.Split(',',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries).Select(Rgb.Parse));
                        break;
                    case "--preserve-threshold":
                        preserveThreshold = Integer(option, value, 0, 441);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {option}");
                }
            }
            if (colors.Count == 0) colors.Add(new(0, 0, 0));
            return new(
                Path.GetFullPath(args[0]),
                Path.GetFullPath(args[1]),
                colors, threshold, edge, alpha, columns, rows, blurRadius,
                regionExpand, fillDirection, edgeProtection,
                preserveColors, preserveThreshold);
        }

        private static int Integer(
            string option, string value, int minimum, int maximum)
        {
            if (!int.TryParse(value, out var result) ||
                result < minimum || result > maximum)
                throw new ArgumentException(
                    $"{option} must be between {minimum} and {maximum}.");
            return result;
        }

        public static void PrintUsage()
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project tools/IslandRpg.FaceRemovalTool -- " +
                "<input.png> <output.png> [options]");
            Console.Error.WriteLine(
                "  --color <#RRGGBB,...>          Colors to remove; repeatable (default #000000)");
            Console.Error.WriteLine(
                "  --threshold <0-441>            Euclidean RGB distance (default 24)");
            Console.Error.WriteLine(
                "  --ignore-transparent-edge <px> Protect pixels near transparency (default 2)");
            Console.Error.WriteLine(
                "  --transparent-alpha <0-255>    Alpha treated as transparent (default 8)");
            Console.Error.WriteLine(
                "  --columns <n> --rows <n>       Process sheet cells independently");
            Console.Error.WriteLine(
                "  --blur-radius <0-32>            Blur reconstructed regions (default 2; 0 disables)");
            Console.Error.WriteLine(
                "  --region-expand <0-16>          Grow removal mask around matched colors");
            Console.Error.WriteLine(
                "  --fill-direction <direction>    left-to-right or bottom-to-top");
            Console.Error.WriteLine(
                "  --edge-protection <mode>        distance or color-connected");
            Console.Error.WriteLine(
                "  --preserve-color <#RRGGBB,...> Restore matching source details after processing");
            Console.Error.WriteLine(
                "  --preserve-threshold <0-441>    Preserve-color RGB distance (default 20)");
            Console.Error.WriteLine(
                "  --self-test                    Run built-in regression checks");
        }
    }
}
