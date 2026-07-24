namespace AoeRpg.Assets;

internal static class JascPalette
{
    public static uint[] Load(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 3 || lines[0].Trim() != "JASC-PAL")
            throw new InvalidDataException($"Unsupported palette: {path}");
        var count = int.Parse(lines[2]);
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var rgb = lines[i + 3].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(byte.Parse).ToArray();
            result[i] = (uint)(rgb[0] | rgb[1] << 8 | rgb[2] << 16 | 0xff << 24);
        }
        return result;
    }
}
