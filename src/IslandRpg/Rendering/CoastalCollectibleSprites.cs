using IslandRpg.Assets;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed class CoastalCollectibleSprites
{
    public const int Count = 9;

    public int[] Textures { get; } = new int[Count];
    public SpriteFrame?[] Frames { get; } = new SpriteFrame?[Count];
    public int[] GroundTextures { get; } = new int[Count];
    public SpriteFrame?[] GroundFrames { get; } = new SpriteFrame?[Count];
    public SpriteFrame?[] GroundShadows { get; } = new SpriteFrame?[Count];

    public static CoastalCollectibleSprites Load(
        string path, Func<SpriteFrame, int> upload)
    {
        var result = new CoastalCollectibleSprites();
        if (!File.Exists(path)) return result;

        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        const int cellSize = 32;
        if (sheet.Width != cellSize * 3 ||
            sheet.Height != cellSize * 3)
            throw new InvalidDataException(
                "The coastal collectible sheet must be a 3x3 grid of 32px cells.");

        for (var cell = 0; cell < Count; cell++)
        {
            var pixels = new byte[cellSize * cellSize * 4];
            var cellX = cell % 3 * cellSize;
            var cellY = cell / 3 * cellSize;
            for (var row = 0; row < cellSize; row++)
                Buffer.BlockCopy(
                    sheet.Data,
                    ((cellY + row) * sheet.Width + cellX) * 4,
                    pixels, row * cellSize * 4, cellSize * 4);
            var frame = new SpriteFrame(
                cellSize, cellSize, cellSize / 2, 28, pixels);
            result.Frames[cell] = frame;
            result.Textures[cell] = upload(frame);
            var groundFrame = SpriteFrameTransforms.Resize(
                frame, cell == Count - 1 ? .75f : .50f);
            result.GroundFrames[cell] = groundFrame;
            result.GroundShadows[cell] =
                ItemShadowGenerator.Create(groundFrame);
            result.GroundTextures[cell] = upload(groundFrame);
        }
        return result;
    }
}
