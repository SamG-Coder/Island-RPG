using IslandRpg.Assets;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed class CaveGrowthSprites
{
    public const int Count = 10;
    public const int CellSize = 96;

    public SpriteFrame?[] Frames { get; } = new SpriteFrame?[Count];

    public static CaveGrowthSprites Load(string path)
    {
        var result = new CaveGrowthSprites();
        if (!File.Exists(path)) return result;

        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        if (sheet.Width != CellSize * Count ||
            sheet.Height != CellSize)
            throw new InvalidDataException(
                "The cave-growth sheet must contain ten horizontal 96px cells.");

        for (var cell = 0; cell < Count; cell++)
        {
            var pixels = new byte[CellSize * CellSize * 4];
            for (var row = 0; row < CellSize; row++)
                Buffer.BlockCopy(
                    sheet.Data,
                    (row * sheet.Width + cell * CellSize) * 4,
                    pixels,
                    row * CellSize * 4,
                    CellSize * 4);
            result.Frames[cell] = new(
                CellSize, CellSize, CellSize / 2, CellSize - 4, pixels);
        }
        return result;
    }

    public static string AtlasKey(int cell) => $"CAVE_GROWTH#{cell}";
}
