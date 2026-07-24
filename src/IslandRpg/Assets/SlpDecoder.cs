using System.Buffers.Binary;

namespace IslandRpg.Assets;

internal static class SlpDecoder
{
    public static Sprite Decode(string path, uint[] palette)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 32 || System.Text.Encoding.ASCII.GetString(data, 0, 4) is not ("2.0N" or "3.0N"))
            throw new InvalidDataException("Only classic 2.0N/3.0N SLP sprites are supported.");
        var frameCount = I32(data, 4);
        if (frameCount is < 1 or > 10000 || 32 + frameCount * 32 > data.Length)
            throw new InvalidDataException("Invalid SLP frame table.");

        var frames = new List<SpriteFrame>(frameCount);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var h = 32 + frameIndex * 32;
            var commandTable = I32(data, h);
            var outlineTable = I32(data, h + 4);
            var width = I32(data, h + 16);
            var height = I32(data, h + 20);
            var hotspotX = I32(data, h + 24);
            var hotspotY = I32(data, h + 28);
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidDataException($"Invalid dimensions in frame {frameIndex}.");

            var rgba = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
            {
                var outline = outlineTable + y * 4;
                var left = U16(data, outline);
                var right = U16(data, outline + 2);
                if (left == 0x8000 || right == 0x8000) continue;
                var x = (int)left;
                var pos = I32(data, commandTable + y * 4);
                while (pos < data.Length)
                {
                    var cmd = data[pos++];
                    if ((cmd & 0x0f) == 0x0f) break;
                    switch (cmd & 0x0f)
                    {
                        case 0x03:
                            x += ((cmd & 0xf0) << 4) | data[pos++];
                            break;
                        case 0x02:
                        {
                            var count = ((cmd & 0xf0) << 4) | data[pos++];
                            for (var n = 0; n < count; n++)
                                Put(rgba, width, x++, y, data[pos++], palette);
                            break;
                        }
                        case 0x06:
                        {
                            var count = cmd >> 4;
                            if (count == 0) count = data[pos++];
                            for (var n = 0; n < count; n++)
                                Put(rgba, width, x++, y, PlayerColor(data[pos++]), palette);
                            break;
                        }
                        case 0x07:
                        {
                            var count = cmd >> 4;
                            if (count == 0) count = data[pos++];
                            var color = data[pos++];
                            for (var n = 0; n < count; n++) Put(rgba, width, x++, y, color, palette);
                            break;
                        }
                        case 0x0a:
                        {
                            var count = cmd >> 4;
                            if (count == 0) count = data[pos++];
                            pos++; // Transform-table index; approximate the transformed run as a translucent shadow.
                            for (var n = 0; n < count; n++) PutShadow(rgba, width, x++, y);
                            break;
                        }
                        case 0x0b:
                        {
                            var count = cmd >> 4;
                            if (count == 0) count = data[pos++];
                            for (var n = 0; n < count; n++) PutShadow(rgba, width, x++, y);
                            break;
                        }
                        case 0x0e:
                            // Outline/extended pixels. A visible dark pixel is a useful approximation here.
                            if (cmd is 0x4e or 0x6e) PutShadow(rgba, width, x++, y);
                            else if (cmd is 0x5e or 0x7e)
                            {
                                var count = data[pos++];
                                for (var n = 0; n < count; n++) PutShadow(rgba, width, x++, y);
                            }
                            break;
                        default:
                        {
                            if ((cmd & 3) == 0)
                            {
                                var count = cmd >> 2;
                                for (var n = 0; n < count; n++) Put(rgba, width, x++, y, data[pos++], palette);
                            }
                            else if ((cmd & 3) == 1)
                            {
                                var count = cmd >> 2;
                                x += count;
                            }
                            else throw new InvalidDataException($"Unsupported SLP command 0x{cmd:x2}.");
                            break;
                        }
                    }
                }
                var expectedEnd = width - right;
                if (x != expectedEnd)
                    throw new InvalidDataException(
                        $"Frame {frameIndex}, row {y} decoded to x={x}; expected {expectedEnd}.");
            }
            frames.Add(new(width, height, hotspotX, hotspotY, rgba));
        }
        return new(frames);
    }

    private static byte PlayerColor(byte shade) => (byte)(16 | (shade & 0x0f)); // blue player
    private static void Put(byte[] pixels, int width, int x, int y, byte index, uint[] palette)
    {
        if ((uint)x >= (uint)width || index >= palette.Length) return;
        var c = palette[index];
        BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan((y * width + x) * 4, 4), c);
    }
    private static void PutShadow(byte[] pixels, int width, int x, int y)
    {
        if ((uint)x >= (uint)width) return;
        var i = (y * width + x) * 4;
        pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        pixels[i + 3] = 100;
    }
    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
}
