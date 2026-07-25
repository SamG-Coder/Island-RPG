using IslandRpg.Assets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine(
        "Usage: AoeCursorSheet <Age2HD install> [output.png]");
    return 2;
}

var install = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(
    args.Length == 2 ? args[1] : "aoe-cursor-sheet.png");
var slpPath = Path.Combine(
    install, "resources", "_common", "drs", "interface", "51000.slp");
var palette = JascPalette.Load(
    Age2PaletteResolver.Resolve(install, slpPath).Path);
var sprite = SlpDecoder.Decode(slpPath, palette);

const int columns = 4;
const int cell = 96;
const int labelHeight = 20;
var rows = (sprite.Frames.Count + columns - 1) / columns;
var visual = new DrawingVisual();
using (var drawing = visual.RenderOpen())
{
    drawing.DrawRectangle(
        new SolidColorBrush(Color.FromRgb(28, 31, 34)), null,
        new Rect(0, 0, columns * cell, rows * cell));
    for (var index = 0; index < sprite.Frames.Count; index++)
    {
        var frame = sprite.Frames[index];
        var x = index % columns * cell;
        var y = index / columns * cell;
        drawing.DrawRectangle(
            new SolidColorBrush(
                index % 2 == 0
                    ? Color.FromRgb(54, 59, 63)
                    : Color.FromRgb(45, 50, 54)),
            new Pen(new SolidColorBrush(Color.FromRgb(92, 98, 102)), 1),
            new Rect(x + 2, y + 2, cell - 4, cell - 4));
        var bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96,
            PixelFormats.Rgba64, null,
            ExpandToRgba64(frame.Rgba), frame.Width * 8);
        var scale = Math.Min(
            2.0,
            Math.Min(
                (cell - 12.0) / frame.Width,
                (cell - labelHeight - 8.0) / frame.Height));
        var width = frame.Width * scale;
        var height = frame.Height * scale;
        drawing.DrawImage(
            bitmap,
            new Rect(
                x + (cell - width) / 2,
                y + labelHeight + (cell - labelHeight - height) / 2,
                width, height));
        var label = new FormattedText(
            $"Frame {index}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"), 12,
            Brushes.White, 1);
        drawing.DrawText(label, new Point(x + 6, y + 4));
    }
}

var target = new RenderTargetBitmap(
    columns * cell, rows * cell, 96, 96, PixelFormats.Pbgra32);
target.Render(visual);
var encoder = new PngBitmapEncoder();
encoder.Frames.Add(BitmapFrame.Create(target));
var outputDirectory = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrWhiteSpace(outputDirectory))
    Directory.CreateDirectory(outputDirectory);
using var output = File.Create(outputPath);
encoder.Save(output);
Console.WriteLine(
    $"Decoded {sprite.Frames.Count} cursor frames to {outputPath}");
return 0;

static ushort[] ExpandToRgba64(byte[] rgba)
{
    var result = new ushort[rgba.Length];
    for (var i = 0; i < rgba.Length; i++)
        result[i] = (ushort)(rgba[i] * 257);
    return result;
}
