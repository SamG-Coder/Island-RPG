namespace AoeRpg.Assets;

internal sealed record Sprite(IReadOnlyList<SpriteFrame> Frames);
internal sealed record SpriteFrame(int Width, int Height, int HotspotX, int HotspotY, byte[] Rgba);
