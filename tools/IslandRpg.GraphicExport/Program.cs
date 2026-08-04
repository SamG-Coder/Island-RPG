using IslandRpg.Assets;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var options = Options.Parse(args);
var install = options.Install;
var outputRoot = options.Output;
var common = Path.Combine(install, "resources", "_common");
var datPath = Path.Combine(common, "dat", "empires2_x2_p1.dat");
if (!File.Exists(datPath))
    throw new FileNotFoundException("The Age2HD DAT file was not found.", datPath);

var allGraphics = GenieDatReader.FindAllGraphics(datPath);
var graphicsById = allGraphics.ToDictionary(graphic => graphic.GraphicId);
var constructionStageNames = new HashSet<string>(
    [
        "CNST1_NN", "CNST2_NN", "CNST3_NN", "CNST4_NN",
        "CNST8_NN", "CNST12_NN", "CNSTD_NN"
    ],
    StringComparer.OrdinalIgnoreCase);
var matches = (options.ConstructionStages
    ? allGraphics.Where(graphic => constructionStageNames.Contains(graphic.Name))
    : options.AllConstructionCandidates
    ? allGraphics.Where(graphic =>
        graphic.AngleCount == 1 && graphic.FrameCount <= 12)
    : allGraphics
    .Where(graphic =>
        options.ExactNames.Contains(graphic.Name) ||
        options.Queries.Any(query =>
            graphic.Name.Contains(
                query, StringComparison.OrdinalIgnoreCase) ||
            graphic.FileName.Contains(
                query, StringComparison.OrdinalIgnoreCase))))
    .OrderBy(graphic => graphic.Name, StringComparer.OrdinalIgnoreCase)
    .ThenBy(graphic => graphic.GraphicId)
    .ToArray();
Directory.CreateDirectory(outputRoot);
var manifest = new StringBuilder(
    "name,graphic_id,slp_id,declared_frames,angles,decoded_frames,source,status\r\n");
var exportedGraphics = 0;
var exportedFrames = 0;
foreach (var graphic in matches)
{
    var deltas = GenieDatReader.FindGraphicDeltas(datPath, graphic.Name);
    if (deltas.Count > 0)
        Console.WriteLine($"{graphic.Name,-21} deltas: " + string.Join(", ",
            deltas.Select(delta =>
            {
                var name = graphicsById.TryGetValue(delta.GraphicId, out var referenced)
                    ? referenced.Name
                    : "missing";
                return $"{delta.GraphicId}:{name}@{delta.OffsetX},{delta.OffsetY}/{delta.DisplayAngle}";
            })));
    var slpPath = ResolveSlp(common, graphic.SlpId);
    if (slpPath is null)
    {
        AddManifest(graphic, 0, "", "missing loose SLP");
        Console.WriteLine(
            $"{graphic.Name,-21} SLP {graphic.SlpId,-6} unavailable");
        continue;
    }

    try
    {
        var palettePath = Age2PaletteResolver.Resolve(install, slpPath).Path;
        var sprite = SlpDecoder.Decode(
            slpPath, JascPalette.Load(palettePath));
        var frames = options.AllConstructionCandidates
            ? sprite.Frames.Where(IsConstructionCandidate).ToArray()
            : sprite.Frames.ToArray();
        if (frames.Length == 0) continue;
        var duplicateName = matches.Count(value =>
            value.Name.Equals(graphic.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        var folderName = duplicateName
            ? $"{SafeName(graphic.Name)}_{graphic.GraphicId}"
            : SafeName(graphic.Name);
        var graphicFolder = Path.Combine(outputRoot, folderName);
        Directory.CreateDirectory(graphicFolder);
        for (var index = 0; index < sprite.Frames.Count; index++)
        {
            var frame = sprite.Frames[index];
            if (options.AllConstructionCandidates &&
                !IsConstructionCandidate(frame))
                continue;
            SavePng(
                frame,
                Path.Combine(
                    graphicFolder,
                    $"{SafeName(graphic.Name)}_{index:000}.png"));
        }
        AddManifest(
            graphic, sprite.Frames.Count,
            Path.GetRelativePath(outputRoot, slpPath), "exported");
        exportedGraphics++;
        exportedFrames += sprite.Frames.Count;
        Console.WriteLine(
            $"{graphic.Name,-21} SLP {graphic.SlpId,-6} " +
            $"{sprite.Frames.Count,3} frame(s): " +
            string.Join(", ", sprite.Frames.Select((frame, index) =>
                $"#{index} {frame.Width}x{frame.Height} hotspot " +
                $"{frame.HotspotX},{frame.HotspotY}")));
    }
    catch (Exception exception)
    {
        AddManifest(
            graphic, 0, Path.GetRelativePath(outputRoot, slpPath),
            $"error: {exception.Message}");
        Console.WriteLine(
            $"{graphic.Name,-21} SLP {graphic.SlpId,-6} " +
            $"error: {exception.Message}");
    }
}

File.WriteAllText(
    Path.Combine(outputRoot, "manifest.csv"),
    manifest.ToString(),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine(
    $"Matched {matches.Length} DAT graphic(s); exported " +
    $"{exportedFrames} frame(s) from {exportedGraphics} graphic(s) to {outputRoot}");
return matches.Length == 0 ? 1 : 0;

static bool IsConstructionCandidate(SpriteFrame frame)
{
    if (frame.Width is < 70 or > 260 || frame.Height is < 40 or > 190)
        return false;
    var opaque = 0;
    var authoredBlue = 0;
    var timberOrStone = 0;
    for (var index = 0; index < frame.Rgba.Length; index += 4)
    {
        var alpha = frame.Rgba[index + 3];
        if (alpha < 48) continue;
        opaque++;
        var red = frame.Rgba[index];
        var green = frame.Rgba[index + 1];
        var blue = frame.Rgba[index + 2];
        if (blue > 70 && blue > red * 1.25f && blue > green * 1.18f)
            authoredBlue++;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        if (red is > 45 and < 190 && green is > 30 and < 155 &&
            blue < 125 || maximum - minimum < 28 && maximum is > 45 and < 190)
            timberOrStone++;
    }
    return opaque >= 180 && authoredBlue >= 4 &&
           timberOrStone >= opaque * .12f;
}

void AddManifest(
    GenieGraphic graphic, int decodedFrames, string source, string status)
{
    manifest.AppendLine(string.Join(",",
        Csv(graphic.Name),
        graphic.GraphicId.ToString(CultureInfo.InvariantCulture),
        graphic.SlpId.ToString(CultureInfo.InvariantCulture),
        graphic.FrameCount.ToString(CultureInfo.InvariantCulture),
        graphic.AngleCount.ToString(CultureInfo.InvariantCulture),
        decodedFrames.ToString(CultureInfo.InvariantCulture),
        Csv(source),
        Csv(status)));
}

static string? ResolveSlp(string common, int id)
{
    foreach (var folder in new[]
             {
                 "graphics", "gamedata_x2", "gamedata_x1",
                 "interface", "terrain"
             })
    {
        var path = Path.Combine(common, "drs", folder, $"{id}.slp");
        if (File.Exists(path)) return path;
    }
    return null;
}

static void SavePng(SpriteFrame frame, string path)
{
    var bitmap = BitmapSource.Create(
        frame.Width, frame.Height, 96, 96,
        PixelFormats.Rgba64, null,
        ExpandToRgba64(frame.Rgba), frame.Width * 8);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var output = File.Create(path);
    encoder.Save(output);
}

static ushort[] ExpandToRgba64(byte[] rgba)
{
    var result = new ushort[rgba.Length];
    for (var index = 0; index < rgba.Length; index++)
        result[index] = (ushort)(rgba[index] * 257);
    return result;
}

static string SafeName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new(value.Select(character =>
        invalid.Contains(character) ? '_' : character).ToArray());
}

static string Csv(string value) =>
    $"\"{value.Replace("\"", "\"\"")}\"";

internal sealed record Options(
    string Install,
    string Output,
    string[] Queries,
    HashSet<string> ExactNames,
    bool AllConstructionCandidates,
    bool ConstructionStages)
{
    public static Options Parse(string[] args)
    {
        string? install = null;
        string? output = null;
        var queries = new List<string>();
        var exactNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var allConstructionCandidates = false;
        var constructionStages = false;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--help" or "-h")
            {
                PrintUsage();
                Environment.Exit(0);
            }
            if (option == "--all-construction-candidates")
            {
                allConstructionCandidates = true;
                continue;
            }
            if (option == "--construction-stages")
            {
                constructionStages = true;
                continue;
            }
            if (index + 1 >= args.Length)
                throw new ArgumentException(
                    $"Missing value for {option}.");
            var value = args[++index].Trim();
            switch (option)
            {
                case "--install":
                    install = Path.GetFullPath(value);
                    break;
                case "--output":
                    output = Path.GetFullPath(value);
                    break;
                case "--query":
                    if (value.Length > 0) queries.Add(value);
                    break;
                case "--exact":
                    if (value.Length > 0) exactNames.Add(value);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown option: {option}");
            }
        }

        if (install is null || output is null ||
            queries.Count == 0 && exactNames.Count == 0 &&
            !allConstructionCandidates && !constructionStages)
        {
            PrintUsage();
            throw new ArgumentException(
                "--install, --output, and at least one --query or --exact are required.");
        }
        return new(
            install, output, queries.ToArray(), exactNames,
            allConstructionCandidates, constructionStages);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: IslandRpg.GraphicExport --install <Age2HD folder> " +
            "--output <folder> [--query <text> ...] [--exact <DAT name> ...]");
        Console.Error.WriteLine(
            "       IslandRpg.GraphicExport --install <folder> --output <folder> " +
            "--all-construction-candidates");
        Console.Error.WriteLine(
            "       IslandRpg.GraphicExport --install <folder> --output <folder> " +
            "--construction-stages");
        Console.Error.WriteLine(
            "Search terms match DAT graphic names and filenames; multiple " +
            "queries/exact names are combined.");
    }
}
