using IslandRpg.Assets;
using IslandRpg.Gameplay;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed record PlaceableObjectSprite(
    SpriteFrame Frame,
    int Texture,
    SpriteFrame Shadow);

internal sealed class PlaceableObjectSprites
{
    private readonly Dictionary<string, PlaceableObjectSprite> _sprites =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, PlaceableObjectSprite>> All =>
        _sprites;

    public bool TryGet(
        string itemId, out PlaceableObjectSprite sprite) =>
        _sprites.TryGetValue(itemId, out sprite!);

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
            var frame = new SpriteFrame(
                image.Width,
                image.Height,
                Math.Clamp(
                    definition.HotspotX, 0, image.Width - 1),
                Math.Clamp(
                    definition.HotspotY, 0, image.Height - 1),
                image.Data);
            result._sprites[definition.ItemId] = new(
                frame,
                upload(frame),
                ItemShadowGenerator.Create(frame));
        }
        return result;
    }
}
