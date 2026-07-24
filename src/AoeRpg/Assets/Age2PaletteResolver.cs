using System.Buffers.Binary;

namespace AoeRpg.Assets;

internal sealed record ResolvedPalette(string Path, string Description);

internal static class Age2PaletteResolver
{
    public static ResolvedPalette Resolve(string install, string slpPath)
    {
        using var stream = File.OpenRead(slpPath);
        Span<byte> headerAndFirstFrame = stackalloc byte[48];
        stream.ReadExactly(headerAndFirstFrame);
        var properties = BinaryPrimitives.ReadUInt32LittleEndian(headerAndFirstFrame[44..48]);

        // SLP frame property 0x10 explicitly selects the default game palette.
        // Property 0x00 selects the global palette, which is the same palette
        // for the Age2HD graphics used here. palette_offset is normally unused.
        // HD-produced SLPs also commonly use 0x18 for the same standard
        // indexed-palette path.
        if (properties is not (0x00 or 0x10 or 0x18))
            throw new InvalidDataException(
                $"Unsupported SLP palette property 0x{properties:x8} in {Path.GetFileName(slpPath)}.");

        var path = Path.Combine(
            install, "resources", "_common", "drs", "interface", "50500.bina");
        return new(path, properties == 0x10
            ? "SLP default game palette"
            : "SLP global palette");
    }
}
