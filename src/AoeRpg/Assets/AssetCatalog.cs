namespace AoeRpg.Assets;

internal enum GraphicKind
{
    StaticObject,
    AnimatedObject,
    DirectionalObject,
    ShadowLayer,
    Effect,
    Interface,
    Unknown
}

internal sealed record LoadedGraphic(
    GenieGraphic Definition,
    GraphicKind Kind,
    Sprite Sprite,
    string SourcePath);

internal sealed record MissingGraphic(GenieGraphic Definition, string Reason);
internal sealed record TerrainTile(string Name, int Width, int Height, byte[] Rgba, string SourcePath);
internal sealed record WaterTexture(string Name, int Width, int Height, byte[] Rgba, string SourcePath);

internal sealed class AssetCatalog
{
    public required IReadOnlyDictionary<short, LoadedGraphic> Graphics { get; init; }
    public required IReadOnlyList<MissingGraphic> Missing { get; init; }
    public required IReadOnlyList<TerrainTile> TerrainTiles { get; init; }
    public required IReadOnlyList<WaterTexture> WaterTextures { get; init; }
}
