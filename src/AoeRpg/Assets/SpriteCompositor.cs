namespace AoeRpg.Assets;

internal static class SpriteCompositor
{
    public static Sprite Layer(Sprite back, Sprite front)
    {
        var frames = new List<SpriteFrame>(front.Frames.Count);
        for (var i = 0; i < front.Frames.Count; i++)
            frames.Add(LayerFrame(back.Frames[Math.Min(i, back.Frames.Count - 1)], front.Frames[i]));
        return new(frames);
    }

    private static SpriteFrame LayerFrame(SpriteFrame back, SpriteFrame front)
    {
        // Frame coordinates are relative to the common ground hotspot.
        var left = Math.Min(-back.HotspotX, -front.HotspotX);
        var top = Math.Min(-back.HotspotY, -front.HotspotY);
        var right = Math.Max(back.Width - back.HotspotX, front.Width - front.HotspotX);
        var bottom = Math.Max(back.Height - back.HotspotY, front.Height - front.HotspotY);
        var width = right - left;
        var height = bottom - top;
        var rgba = new byte[checked(width * height * 4)];

        Blit(back, rgba, width, -back.HotspotX - left, -back.HotspotY - top);
        Blit(front, rgba, width, -front.HotspotX - left, -front.HotspotY - top);
        return new(width, height, -left, -top, rgba);
    }

    private static void Blit(SpriteFrame source, byte[] destination, int destinationWidth, int offsetX, int offsetY)
    {
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var sourceOffset = (y * source.Width + x) * 4;
            var sourceAlpha = source.Rgba[sourceOffset + 3];
            if (sourceAlpha == 0) continue;

            var destinationOffset = ((y + offsetY) * destinationWidth + x + offsetX) * 4;
            var destinationAlpha = destination[destinationOffset + 3];
            var sa = sourceAlpha / 255f;
            var da = destinationAlpha / 255f;
            var outAlpha = sa + da * (1 - sa);
            if (outAlpha <= 0) continue;

            for (var channel = 0; channel < 3; channel++)
            {
                var value = (source.Rgba[sourceOffset + channel] * sa +
                             destination[destinationOffset + channel] * da * (1 - sa)) / outAlpha;
                destination[destinationOffset + channel] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
            }
            destination[destinationOffset + 3] = (byte)Math.Clamp((int)MathF.Round(outAlpha * 255), 0, 255);
        }
    }
}
