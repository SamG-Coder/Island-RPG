using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace IslandRpg.Rendering;

internal static class PngScreenshotWriter
{
    private static readonly byte[] Signature =
        [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(
        string path,
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        bool flipVertically)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = checked(width * 4);
        if (rgba.Length != checked(stride * height))
            throw new ArgumentException(
                "RGBA data does not match the image dimensions.",
                nameof(rgba));

        using var output = File.Create(path);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var outputY = 0; outputY < height; outputY++)
            {
                zlib.WriteByte(0);
                var sourceY = flipVertically
                    ? height - 1 - outputY
                    : outputY;
                zlib.Write(rgba.Slice(sourceY * stride, stride));
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(
        Stream output,
        string type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc32(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
            crc = UpdateCrc(crc, value);
        foreach (var value in data)
            crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0
                ? 0xedb88320u ^ crc >> 1
                : crc >> 1;
        return crc;
    }
}
