using IslandRpg.World;

namespace IslandRpg.Rendering;

internal sealed class WorldAtlasTextureCache
{
    private sealed record Entry(int Texture, long Bytes)
    {
        public long LastUse { get; set; }
    }

    private readonly Dictionary<WorldAtlasTileKey, Entry> _entries = [];
    private long _clock;

    public int Count => _entries.Count;
    public long Bytes { get; private set; }

    public bool Contains(WorldAtlasTileKey key) =>
        _entries.ContainsKey(key);

    public bool TryGet(WorldAtlasTileKey key, out int texture)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            texture = 0;
            return false;
        }
        entry.LastUse = ++_clock;
        texture = entry.Texture;
        return true;
    }

    public void Set(
        WorldAtlasTileKey key,
        int texture,
        int width,
        int height,
        Action<int> deleteTexture)
    {
        if (_entries.Remove(key, out var previous))
        {
            Bytes -= previous.Bytes;
            deleteTexture(previous.Texture);
        }
        var bytes = (long)width * height * 4;
        _entries[key] = new(texture, bytes)
        {
            LastUse = ++_clock
        };
        Bytes += bytes;
    }

    public void Trim(
        IReadOnlySet<WorldAtlasTileKey> visible,
        int maximumCount,
        Action<int> deleteTexture)
    {
        while (_entries.Count > maximumCount)
        {
            var candidates = _entries
                .Where(pair => !visible.Contains(pair.Key))
                .ToArray();
            var candidate = candidates.Length > 0
                ? candidates.MinBy(pair => pair.Value.LastUse)
                : _entries.MinBy(pair => pair.Value.LastUse);
            Remove(candidate.Key, deleteTexture);
        }
    }

    public void Clear(Action<int> deleteTexture)
    {
        foreach (var entry in _entries.Values)
            deleteTexture(entry.Texture);
        _entries.Clear();
        Bytes = 0;
    }

    private void Remove(
        WorldAtlasTileKey key, Action<int> deleteTexture)
    {
        if (!_entries.Remove(key, out var entry)) return;
        Bytes -= entry.Bytes;
        deleteTexture(entry.Texture);
    }
}
