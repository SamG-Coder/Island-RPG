using IslandRpg.Assets;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed class FibreNetItemSprites
{
    public const int CellCount = 2;

    public SpriteFrame?[] Frames { get; } =
        new SpriteFrame?[CellCount];
    public SpriteFrame?[] Shadows { get; } =
        new SpriteFrame?[CellCount];
    public int[] Textures { get; } = new int[CellCount];

    public static FibreNetItemSprites Load(
        string path, Func<SpriteFrame, int> upload)
    {
        var result = new FibreNetItemSprites();
        if (!File.Exists(path)) return result;
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        const int cellSize = 32;
        for (var cell = 0; cell < CellCount; cell++)
        {
            var pixels = new byte[cellSize * cellSize * 4];
            for (var row = 0; row < cellSize; row++)
                Buffer.BlockCopy(
                    sheet.Data,
                    (row * sheet.Width + cell * cellSize) * 4,
                    pixels,
                    row * cellSize * 4,
                    cellSize * 4);
            var frame = new SpriteFrame(
                cellSize, cellSize, cellSize / 2, 28, pixels);
            result.Frames[cell] = frame;
            result.Shadows[cell] = ItemShadowGenerator.Create(frame);
            result.Textures[cell] = upload(frame);
        }
        return result;
    }
}
