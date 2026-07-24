using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AoeRpg.Assets;

internal sealed record GenieGraphic(
    string Name,
    string FileName,
    int SlpId,
    ushort FrameCount,
    ushort AngleCount,
    float FrameRate,
    float ReplayDelay,
    short GraphicId,
    byte MirroringMode,
    byte Layer = 0);

internal static class GenieDatReader
{
    // AoE2 HD graphic records use fixed-length name fields. Reading a named record
    // avoids having to deserialize unrelated terrain, sound, unit, and tech tables.
    private const int NameLength = 21;
    private const int FileNameLength = 13;
    private const int FixedRecordLength = 78;

    public static GenieGraphic FindGraphic(string datPath, string requestedName)
    {
        var data = Decompress(datPath);
        return FindGraphic(data, requestedName);
    }

    public static IReadOnlyList<GenieGraphic> FindTreeGraphics(string datPath)
    {
        var data = Decompress(datPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<GenieGraphic>();
        for (var offset = 0; offset <= data.Length - FixedRecordLength; offset++)
        {
            if (data[offset] is not ((byte)'T') and not ((byte)'F')) continue;
            var name = ReadFixedAscii(data, offset, NameLength);
            var isTree = name.StartsWith("TREE", StringComparison.Ordinal) ||
                         name.StartsWith("FOAK_", StringComparison.Ordinal) ||
                         name.StartsWith("FPAL_", StringComparison.Ordinal) ||
                         name.StartsWith("FPIN_", StringComparison.Ordinal) ||
                         name.StartsWith("FSNO_", StringComparison.Ordinal);
            var isLayer = name.EndsWith("_NN", StringComparison.Ordinal) ||
                          name.EndsWith("_N0", StringComparison.Ordinal);
            if (!isTree || !isLayer || !names.Add(name)) continue;
            if (TryReadGraphic(data, offset, name, out var graphic))
                results.Add(graphic);
        }
        return results;
    }

    public static IReadOnlyList<GenieGraphic> FindAllGraphics(string datPath)
    {
        var data = Decompress(datPath);
        var byId = new Dictionary<short, GenieGraphic>();
        for (var offset = 0; offset <= data.Length - FixedRecordLength; offset++)
        {
            var first = data[offset];
            if (first is < 0x20 or > 0x7e) continue;
            var nameField = data.AsSpan(offset, NameLength);
            var terminator = nameField.IndexOf((byte)0);
            if (terminator < 4) continue;
            var name = Encoding.ASCII.GetString(nameField[..terminator]);
            if (name.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))) continue;
            if (!TryReadGraphic(data, offset, name, out var graphic)) continue;
            byId.TryAdd(graphic.GraphicId, graphic);
        }
        return byId.Values.OrderBy(graphic => graphic.GraphicId).ToArray();
    }

    private static GenieGraphic FindGraphic(byte[] data, string requestedName)
    {
        var needle = Encoding.ASCII.GetBytes(requestedName);

        for (var offset = 0; offset <= data.Length - FixedRecordLength; offset++)
        {
            if (!data.AsSpan(offset, needle.Length).SequenceEqual(needle)) continue;
            if (needle.Length < NameLength && data[offset + needle.Length] != 0) continue;

            var name = ReadFixedAscii(data, offset, NameLength);
            if (!name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryReadGraphic(data, offset, name, out var graphic)) return graphic;
        }

        throw new KeyNotFoundException($"Graphic '{requestedName}' was not found in the DAT data.");
    }

    private static bool TryReadGraphic(byte[] data, int offset, string name, out GenieGraphic graphic)
    {
        graphic = null!;
        var fileName = ReadFixedAscii(data, offset + NameLength, FileNameLength);
        if (fileName.Length == 0 || fileName.Any(c => c < 32 || c > 126)) return false;

        var slpId = I32(data, offset + 34);
        var deltaCount = U16(data, offset + 52);
        var attackSoundUsed = data[offset + 56];
        var frameCount = U16(data, offset + 57);
        var angleCount = U16(data, offset + 59);
        var frameRate = F32(data, offset + 65);
        var replayDelay = F32(data, offset + 69);
        var graphicId = I16(data, offset + 74);
        var mirroringMode = data[offset + 76];

        if (slpId is < 0 or > 100000 || graphicId < 0 ||
            deltaCount > 4096 || attackSoundUsed > 1 ||
            frameCount == 0 || angleCount == 0 || angleCount > 360 ||
            !float.IsFinite(frameRate) || !float.IsFinite(replayDelay))
            return false;

        graphic = new(name, fileName, slpId, frameCount, angleCount,
            frameRate, replayDelay, graphicId, mirroringMode, data[offset + 40]);
        return true;
    }

    private static byte[] Decompress(string path)
    {
        using var input = File.OpenRead(path);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadFixedAscii(byte[] data, int offset, int length)
    {
        var field = data.AsSpan(offset, length);
        var terminator = field.IndexOf((byte)0);
        if (terminator >= 0) field = field[..terminator];
        return Encoding.ASCII.GetString(field);
    }

    private static int I32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    private static short I16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static float F32(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(I32(data, offset));
}
