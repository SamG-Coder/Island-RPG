using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class AnimationSpriteProcessor
{
    public static int Run(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            Convert(options);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Animation-sprite conversion failed: {exception.Message}");
            return 1;
        }
    }

    private static void Convert(Options options)
    {
        var source = Load(options.Input);
        var sourcePixels = Pixels(source);
        var sourceFrameWidth = source.PixelWidth / options.Columns;
        var sourceFrameHeight = source.PixelHeight / options.Rows;
        var gridOffsetX =
            (source.PixelWidth - sourceFrameWidth * options.Columns) / 2;
        var gridOffsetY =
            (source.PixelHeight - sourceFrameHeight * options.Rows) / 2;
        var frames = new List<Frame>();
        for (var index = 0; index < options.Columns * options.Rows; index++)
        {
            var cellX =
                gridOffsetX + index % options.Columns * sourceFrameWidth;
            var cellY =
                gridOffsetY + index / options.Columns * sourceFrameHeight;
            var pixels = Crop(
                sourcePixels, source.PixelWidth, cellX, cellY,
                sourceFrameWidth, sourceFrameHeight);
            RemoveChroma(pixels, options.Chroma);
            frames.Add(new Frame(pixels, FindBounds(
                pixels, sourceFrameWidth, sourceFrameHeight)));
        }

        var widest = frames.Max(frame => frame.Bounds.Width);
        var tallest = frames.Max(frame => frame.Bounds.Height);
        var scale = Math.Min(
            (double)options.TargetWidth / widest,
            (double)options.TargetHeight / tallest);
        var runtime = new byte[
            options.CanvasWidth * frames.Count * options.CanvasHeight * 4];
        var baseFrame = Load(options.Base);
        if (baseFrame.PixelWidth != options.CanvasWidth ||
            baseFrame.PixelHeight != options.CanvasHeight)
            throw new ArgumentException(
                "The base sprite dimensions must match the output canvas.");
        var basePixels = Pixels(baseFrame);
        Frame? fuel = null;
        var fuelSourceWidth = 0;
        if (options.Fuel is not null)
        {
            var fuelSheet = Load(options.Fuel);
            fuelSourceWidth = options.FuelWidth;
            var fuelPixels = Crop(
                Pixels(fuelSheet), fuelSheet.PixelWidth,
                options.FuelX, options.FuelY,
                options.FuelWidth, options.FuelHeight);
            fuel = new Frame(
                fuelPixels,
                FindBounds(
                    fuelPixels, options.FuelWidth, options.FuelHeight));
        }

        var preview = new byte[
            options.CanvasWidth * options.Columns *
            options.CanvasHeight * options.Rows * 4];
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var width = Math.Max(
                1, (int)Math.Round(frame.Bounds.Width * scale));
            var height = Math.Max(
                1, (int)Math.Round(frame.Bounds.Height * scale));
            var composed = options.HideBase
                ? new byte[basePixels.Length]
                : (byte[])basePixels.Clone();
            if (fuel is { } fuelFrame)
                DrawNearest(
                    fuelFrame.Pixels, fuelSourceWidth, fuelFrame.Bounds,
                    composed, options.CanvasWidth, options.CanvasHeight,
                    options.FuelAnchorX - options.FuelTargetWidth / 2,
                    options.FuelAnchorY - options.FuelTargetHeight,
                    options.FuelTargetWidth, options.FuelTargetHeight);
            if (!options.HideAnimation)
                DrawNearest(
                    frame.Pixels, sourceFrameWidth, frame.Bounds,
                    composed, options.CanvasWidth, options.CanvasHeight,
                    options.AnchorX - width / 2,
                    options.AnchorY - height, width, height);
            Copy(
                composed, options.CanvasWidth, options.CanvasHeight,
                runtime, options.CanvasWidth * frames.Count,
                index * options.CanvasWidth, 0);
            Copy(
                composed, options.CanvasWidth, options.CanvasHeight,
                preview, options.CanvasWidth * options.Columns,
                index % options.Columns * options.CanvasWidth,
                index / options.Columns * options.CanvasHeight);
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(options.Output) ?? ".");
        Save(
            options.Output, runtime,
            options.CanvasWidth * frames.Count, options.CanvasHeight);
        if (options.Preview is not null)
            Save(
                options.Preview, preview,
                options.CanvasWidth * options.Columns,
                options.CanvasHeight * options.Rows);
        Console.WriteLine(
            $"Created {frames.Count}-frame animation sheet: {options.Output}");
        if (options.Preview is not null)
            Console.WriteLine($"Composite preview: {options.Preview}");
    }

    private static void RemoveChroma(byte[] pixels, Rgb key)
    {
        const double transparent = 28;
        const double opaque = 100;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var distance = Math.Sqrt(
                Math.Pow(pixels[offset + 2] - key.R, 2) +
                Math.Pow(pixels[offset + 1] - key.G, 2) +
                Math.Pow(pixels[offset] - key.B, 2));
            var alpha = distance <= transparent
                ? 0
                : distance >= opaque
                    ? 255
                    : (byte)Math.Round(
                        (distance - transparent) * 255 /
                        (opaque - transparent));
            pixels[offset + 3] =
                (byte)(pixels[offset + 3] * alpha / 255);
            if (pixels[offset + 3] == 0)
            {
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
            }
        }
    }

    private static Bounds FindBounds(byte[] pixels, int width, int height)
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
        if (maxX < minX)
            throw new ArgumentException("An animation cell is empty.");
        return new(
            minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static void DrawNearest(
        byte[] source, int sourceWidth, Bounds crop,
        byte[] destination, int destinationWidth, int destinationHeight,
        int x, int y, int width, int height)
    {
        for (var targetY = 0; targetY < height; targetY++)
        for (var targetX = 0; targetX < width; targetX++)
        {
            var destinationX = x + targetX;
            var destinationY = y + targetY;
            if (destinationX < 0 || destinationX >= destinationWidth ||
                destinationY < 0 || destinationY >= destinationHeight)
                continue;
            var sourceX = crop.X + targetX * crop.Width / width;
            var sourceY = crop.Y + targetY * crop.Height / height;
            var sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
            var destinationOffset =
                (destinationY * destinationWidth + destinationX) * 4;
            Blend(source, sourceOffset, destination, destinationOffset);
        }
    }

    private static void Blend(
        byte[] source, int sourceOffset,
        byte[] destination, int destinationOffset)
    {
        var alpha = source[sourceOffset + 3];
        if (alpha == 0) return;
        for (var channel = 0; channel < 3; channel++)
            destination[destinationOffset + channel] = (byte)(
                (source[sourceOffset + channel] * alpha +
                 destination[destinationOffset + channel] * (255 - alpha)) /
                255);
        destination[destinationOffset + 3] = (byte)Math.Min(
            255, alpha + destination[destinationOffset + 3] *
            (255 - alpha) / 255);
    }

    private static void Copy(
        byte[] source, int width, int height,
        byte[] destination, int destinationWidth, int x, int y)
    {
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(
                source, row * width * 4,
                destination, ((y + row) * destinationWidth + x) * 4,
                width * 4);
    }

    private static byte[] Crop(
        byte[] source, int sourceWidth,
        int x, int y, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(
                source, ((y + row) * sourceWidth + x) * 4,
                result, row * width * 4, width * 4);
        return result;
    }

    private static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(
            decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var result = new byte[
            bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(result, bitmap.PixelWidth * 4, 0);
        return result;
    }

    private static void Save(
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

    private readonly record struct Bounds(
        int X, int Y, int Width, int Height);
    private readonly record struct Frame(byte[] Pixels, Bounds Bounds);
    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Parse(string value)
        {
            var hex = value.TrimStart('#');
            if (hex.Length != 6 ||
                !int.TryParse(
                    hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var packed))
                throw new ArgumentException("Invalid chroma colour.");
            return new(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
        }
    }

    private sealed record Options(
        string Input,
        string Output,
        string Base,
        string? Preview,
        int Columns,
        int Rows,
        int CanvasWidth,
        int CanvasHeight,
        int TargetWidth,
        int TargetHeight,
        int AnchorX,
        int AnchorY,
        string? Fuel,
        int FuelX,
        int FuelY,
        int FuelWidth,
        int FuelHeight,
        int FuelTargetWidth,
        int FuelTargetHeight,
        int FuelAnchorX,
        int FuelAnchorY,
        bool HideAnimation,
        bool HideBase,
        Rgb Chroma)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>();
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException(
                        $"Missing value for {args[index]}.");
                values[args[index]] = args[index + 1];
            }
            string Required(string name) =>
                Path.GetFullPath(values.TryGetValue(name, out var value)
                    ? value
                    : throw new ArgumentException($"{name} is required."));
            int Number(string name, int fallback) =>
                values.TryGetValue(name, out var value)
                    ? int.Parse(value, CultureInfo.InvariantCulture)
                    : fallback;
            return new(
                Required("--input"),
                Required("--output"),
                Required("--base"),
                values.TryGetValue("--preview", out var preview)
                    ? Path.GetFullPath(preview)
                    : null,
                Number("--columns", 4),
                Number("--rows", 4),
                Number("--canvas-width", 58),
                Number("--canvas-height", 58),
                Number("--target-width", 24),
                Number("--target-height", 30),
                Number("--anchor-x", 29),
                Number("--anchor-y", 38),
                values.TryGetValue("--fuel", out var fuel)
                    ? Path.GetFullPath(fuel)
                    : null,
                Number("--fuel-x", 0),
                Number("--fuel-y", 0),
                Number("--fuel-width", 32),
                Number("--fuel-height", 32),
                Number("--fuel-target-width", 22),
                Number("--fuel-target-height", 12),
                Number("--fuel-anchor-x", 29),
                Number("--fuel-anchor-y", 37),
                values.TryGetValue("--hide-animation", out var hidden) &&
                bool.Parse(hidden),
                values.TryGetValue("--hide-base", out var hideBase) &&
                bool.Parse(hideBase),
                Rgb.Parse(values.GetValueOrDefault(
                    "--chroma", "#FF00FF")));
        }
    }
}
