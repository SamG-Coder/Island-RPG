using IslandRpg.Assets;
using IslandRpg.Gameplay;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed record PlaceableObjectSprite(
    SpriteFrame Frame,
    int Texture,
    SpriteFrame? Shadow);

internal sealed class PlaceableObjectSprites
{
    private readonly Dictionary<string, PlaceableObjectSprite> _sprites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaceableObjectSprite>
        _campfireFueled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string FuelItemId, int FlameTier, int Frame),
        PlaceableObjectSprite> _campfireLit = [];

    public IEnumerable<KeyValuePair<string, PlaceableObjectSprite>> All =>
        _sprites;

    public IEnumerable<(string Key, SpriteFrame Frame)> CampfireAtlasFrames
    {
        get
        {
            foreach (var fueled in _campfireFueled)
                yield return (
                    CampfirePresentation.FueledAtlasKey(fueled.Key),
                    fueled.Value.Frame);
            foreach (var lit in _campfireLit)
                yield return (
                    CampfirePresentation.LitAtlasKey(
                        lit.Key.FuelItemId,
                        lit.Key.Frame,
                        lit.Key.FlameTier),
                    lit.Value.Frame);
        }
    }

    public bool TryGet(
        string itemId, out PlaceableObjectSprite sprite) =>
        _sprites.TryGetValue(itemId, out sprite!);

    public bool TryGetCampfireFueled(
        string fuelItemId, out PlaceableObjectSprite sprite) =>
        _campfireFueled.TryGetValue(fuelItemId, out sprite!);

    public static PlaceableObjectSprites Load(
        string imageDirectory,
        Func<SpriteFrame, int> upload)
    {
        var result = new PlaceableObjectSprites();
        foreach (var definition in PlaceableObjectCatalog.All)
        {
            var path = Path.Combine(
                imageDirectory, definition.SpriteFile);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(
                stream, ColorComponents.RedGreenBlueAlpha);
            var width = image.Width;
            var height = image.Height;
            var pixels = image.Data;
            if (definition.RenderWidth > 0 &&
                definition.RenderHeight > 0)
            {
                pixels = ResizeObject(
                    image,
                    definition.RenderWidth,
                    definition.RenderHeight,
                    definition.ChromaKeyMagenta);
                width = definition.RenderWidth;
                height = definition.RenderHeight;
            }
            var frame = new SpriteFrame(
                width,
                height,
                Math.Clamp(
                    definition.HotspotX, 0, width - 1),
                Math.Clamp(
                    definition.HotspotY, 0, height - 1),
                pixels);
            result._sprites[definition.ItemId] = new(
                frame,
                upload(frame),
                null);
        }
        result.LoadCampfireStates(imageDirectory, upload);
        return result;
    }

    private static byte[] ResizeObject(
        ImageResult source,
        int targetWidth,
        int targetHeight,
        bool chromaKeyMagenta)
    {
        var left = source.Width;
        var right = 0;
        var top = source.Height;
        var bottom = 0;
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var offset = (y * source.Width + x) * 4;
            if (source.Data[offset + 3] <= 8 ||
                chromaKeyMagenta &&
                IsMagenta(source.Data, offset))
                continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        if (right < left || bottom < top)
            return new byte[targetWidth * targetHeight * 4];
        var contentWidth = right - left + 1;
        var contentHeight = bottom - top + 1;
        var scale = Math.Min(
            (targetWidth - 4) / (float)contentWidth,
            (targetHeight - 4) / (float)contentHeight);
        var drawnWidth = Math.Max(1, (int)MathF.Round(contentWidth * scale));
        var drawnHeight = Math.Max(1, (int)MathF.Round(contentHeight * scale));
        var startX = (targetWidth - drawnWidth) / 2;
        var startY = targetHeight - drawnHeight - 2;
        var result = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < drawnHeight; y++)
        for (var x = 0; x < drawnWidth; x++)
        {
            var sourceX = left + Math.Min(
                contentWidth - 1, x * contentWidth / drawnWidth);
            var sourceY = top + Math.Min(
                contentHeight - 1, y * contentHeight / drawnHeight);
            var sourceOffset =
                (sourceY * source.Width + sourceX) * 4;
            if (source.Data[sourceOffset + 3] <= 8 ||
                chromaKeyMagenta &&
                IsMagenta(source.Data, sourceOffset))
                continue;
            var targetOffset =
                ((startY + y) * targetWidth + startX + x) * 4;
            result[targetOffset] = source.Data[sourceOffset];
            result[targetOffset + 1] = source.Data[sourceOffset + 1];
            result[targetOffset + 2] = source.Data[sourceOffset + 2];
            result[targetOffset + 3] =
                source.Data[sourceOffset + 3];
        }
        return result;
    }

    private static bool IsMagenta(byte[] pixels, int offset)
    {
        var red = pixels[offset];
        var green = pixels[offset + 1];
        var blue = pixels[offset + 2];
        return red > 115 && blue > 110 &&
               red - green > 50 && blue - green > 50;
    }

    private void LoadCampfireStates(
        string imageDirectory, Func<SpriteFrame, int> upload)
    {
        if (!_sprites.TryGetValue(
                ItemIds.Campfire, out var campfireBase))
            return;
        var itemSheetPath = Path.Combine(
            imageDirectory, "woodcutting-items.png");
        var firePath = Path.Combine(
            imageDirectory, "campfire-fire-16.png");
        if (!File.Exists(itemSheetPath) ||
            !File.Exists(firePath))
            return;
        using var itemStream = File.OpenRead(itemSheetPath);
        var itemSheet = ImageResult.FromStream(
            itemStream, ColorComponents.RedGreenBlueAlpha);
        using var fireStream = File.OpenRead(firePath);
        var fireSheet = ImageResult.FromStream(
            fireStream, ColorComponents.RedGreenBlueAlpha);
        const int frameCount = CampfireService.AnimationFrameCount;
        var frameWidth = fireSheet.Width / frameCount;
        if (frameWidth <= 0 ||
            frameWidth * frameCount != fireSheet.Width ||
            frameWidth != campfireBase.Frame.Width ||
            fireSheet.Height != campfireBase.Frame.Height)
            throw new InvalidDataException(
                "The campfire animation must be a 16-frame horizontal strip.");

        foreach (var fuel in ItemCatalog.All.Where(item =>
                     item.HasTag(ItemTag.Log) &&
                     item.SpriteCell is not null))
        {
            var fuelCell = fuel.SpriteCell.GetValueOrDefault();
            var fueledPixels = ComposeFuel(
                campfireBase.Frame.Rgba,
                campfireBase.Frame.Width,
                campfireBase.Frame.Height,
                itemSheet,
                fuelCell);
            _campfireFueled[fuel.Id] =
                CreateSprite(
                    fueledPixels, frameWidth, fireSheet.Height,
                    campfireBase.Shadow, upload);
            for (var flameTier = 0;
                 flameTier < FiremakingSkill.FlameTierCount;
                 flameTier++)
            for (var frameIndex = 0;
                 frameIndex < frameCount;
                 frameIndex++)
            {
                var litPixels =
                    (byte[])campfireBase.Frame.Rgba.Clone();
                BlendFire(
                    fireSheet,
                    frameIndex * frameWidth,
                    frameWidth,
                    fireSheet.Height,
                    litPixels,
                    flameTier);
                _campfireLit[(fuel.Id, flameTier, frameIndex)] =
                    CreateSprite(
                        litPixels, frameWidth, fireSheet.Height,
                        campfireBase.Shadow, upload);
            }
        }
    }

    private static void BlendFire(
        ImageResult source,
        int sourceX,
        int width,
        int height,
        byte[] destination,
        int flameTier)
    {
        if (flameTier == 0)
        {
            BlendCell(
                source, sourceX, 0, width, height,
                destination, width);
            return;
        }

        var framePixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(
                source.Data,
                (row * source.Width + sourceX) * 4,
                framePixels,
                row * width * 4,
                width * 4);
        var bounds = OpaqueBounds(framePixels, width, height);
        var scale = FiremakingSkill.FlameScaleForTier(flameTier);
        var targetWidth = Math.Max(
            1, (int)MathF.Round(bounds.Width * scale));
        var targetHeight = Math.Max(
            1, (int)MathF.Round(bounds.Height * scale));
        DrawNearest(
            framePixels,
            width,
            bounds,
            destination,
            width,
            height,
            CampfirePresentation.FireAnchorX - targetWidth / 2,
            CampfirePresentation.FireAnchorY - targetHeight,
            targetWidth,
            targetHeight);
    }

    private static byte[] ComposeFuel(
        byte[] campfire,
        int width,
        int height,
        ImageResult itemSheet,
        int cell)
    {
        const int cellSize = 32;
        const int columns = 4;
        var cellPixels = new byte[cellSize * cellSize * 4];
        var sourceX = cell % columns * cellSize;
        var sourceY = cell / columns * cellSize;
        for (var row = 0; row < cellSize; row++)
            Buffer.BlockCopy(
                itemSheet.Data,
                ((sourceY + row) * itemSheet.Width + sourceX) * 4,
                cellPixels,
                row * cellSize * 4,
                cellSize * 4);
        var bounds = OpaqueBounds(cellPixels, cellSize, cellSize);
        var result = (byte[])campfire.Clone();
        DrawNearest(
            cellPixels, cellSize, bounds,
            result, width, height,
            x: CampfirePresentation.FuelAnchorX -
               CampfirePresentation.FuelWidth / 2,
            y: CampfirePresentation.FuelAnchorY -
               CampfirePresentation.FuelHeight,
            targetWidth: CampfirePresentation.FuelWidth,
            targetHeight: CampfirePresentation.FuelHeight);
        return result;
    }

    private static PlaceableObjectSprite CreateSprite(
        byte[] pixels, int width, int height,
        SpriteFrame? shadow,
        Func<SpriteFrame, int> upload)
    {
        var frame = new SpriteFrame(width, height, 29, 54, pixels);
        return new(
            frame, upload(frame), shadow);
    }

    private static void BlendCell(
        ImageResult source, int sourceX, int sourceY,
        int width, int height,
        byte[] destination, int destinationWidth)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceOffset =
                ((sourceY + y) * source.Width + sourceX + x) * 4;
            var destinationOffset =
                (y * destinationWidth + x) * 4;
            Blend(
                source.Data, sourceOffset,
                destination, destinationOffset);
        }
    }

    private static void DrawNearest(
        byte[] source, int sourceWidth,
        (int X, int Y, int Width, int Height) crop,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        for (var targetY = 0; targetY < targetHeight; targetY++)
        for (var targetX = 0; targetX < targetWidth; targetX++)
        {
            var destinationX = x + targetX;
            var destinationY = y + targetY;
            if ((uint)destinationX >= (uint)destinationWidth ||
                (uint)destinationY >= (uint)destinationHeight)
                continue;
            var sampledX =
                crop.X + targetX * crop.Width / targetWidth;
            var sampledY =
                crop.Y + targetY * crop.Height / targetHeight;
            Blend(
                source, (sampledY * sourceWidth + sampledX) * 4,
                destination,
                (destinationY * destinationWidth + destinationX) * 4);
        }
    }

    private static void Blend(
        byte[] source, int sourceOffset,
        byte[] destination, int destinationOffset)
    {
        var alpha = source[sourceOffset + 3];
        if (alpha == 0) return;
        for (var channel = 0; channel < 3; channel++)
            destination[destinationOffset + channel] = (byte)(
                (source[sourceOffset + channel] * alpha +
                 destination[destinationOffset + channel] * (255 - alpha)) /
                255);
        destination[destinationOffset + 3] = (byte)Math.Min(
            255,
            alpha + destination[destinationOffset + 3] *
            (255 - alpha) / 255);
    }

    private static (int X, int Y, int Width, int Height) OpaqueBounds(
        byte[] pixels, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 16) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < minX)
            throw new InvalidDataException(
                "A campfire fuel sprite is empty.");
        return (
            minX, minY,
            maxX - minX + 1,
            maxY - minY + 1);
    }
}
