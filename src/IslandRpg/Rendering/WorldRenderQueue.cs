using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed class WorldRenderItem : IComparable<WorldRenderItem>
{
    public Vector2 World { get; private set; }
    public float Opacity { get; private set; }
    public string StableKey { get; private set; } = "";
    public string AtlasKey { get; private set; } = "";

    public WorldRenderItem(
        Vector2 world,
        float opacity,
        string stableKey,
        string atlasKey) =>
        Set(world, opacity, stableKey, atlasKey);

    public void Set(
        Vector2 world,
        float opacity,
        string stableKey,
        string atlasKey)
    {
        World = world;
        Opacity = opacity;
        StableKey = stableKey;
        AtlasKey = atlasKey;
    }

    public int CompareTo(WorldRenderItem? other)
    {
        if (other is null) return 1;
        var order = World.Y.CompareTo(other.World.Y);
        if (order != 0) return order;
        order = World.X.CompareTo(other.World.X);
        if (order != 0) return order;
        return string.Compare(
            StableKey,
            other.StableKey,
            StringComparison.Ordinal);
    }
}

internal sealed class AtlasDrawBatch
{
    internal sealed class Run
    {
        public int Texture { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public List<float> Vertices { get; } = [];

        public void Reset(int texture, int width, int height)
        {
            Texture = texture;
            Width = width;
            Height = height;
            Vertices.Clear();
        }
    }

    private readonly List<Run> _runs = [];
    public IReadOnlyList<Run> Runs => _runs;
    public int ActiveRunCount { get; private set; }
    public int Count { get; private set; }

    public List<float> ForPage(int texture, int width, int height)
    {
        if (ActiveRunCount > 0 &&
            _runs[ActiveRunCount - 1].Texture == texture)
            return _runs[ActiveRunCount - 1].Vertices;
        if (ActiveRunCount == _runs.Count)
            _runs.Add(new());
        var run = _runs[ActiveRunCount++];
        run.Reset(texture, width, height);
        return run.Vertices;
    }

    public void Added(int count) => Count += count;

    public void Clear()
    {
        for (var index = 0; index < ActiveRunCount; index++)
            _runs[index].Vertices.Clear();
        ActiveRunCount = 0;
        Count = 0;
    }
}

internal sealed class WorldRenderQueue
{
    public List<WorldRenderItem> Shadows { get; } = [];
    public List<WorldRenderItem> Objects { get; } = [];
    public AtlasDrawBatch ShadowVertices { get; } = new();
    public AtlasDrawBatch AtlasVertices { get; } = new();
    public AtlasDrawBatch GroundOutlineVertices { get; } = new();
    private readonly List<WorldRenderItem> _shadowPool = [];
    private readonly List<WorldRenderItem> _objectPool = [];
    private int _shadowCount;
    private int _objectCount;
    private float[] _vertexUpload = [];

    public void Reset(int estimatedItems)
    {
        Shadows.Clear();
        Objects.Clear();
        ShadowVertices.Clear();
        AtlasVertices.Clear();
        GroundOutlineVertices.Clear();
        _shadowCount = 0;
        _objectCount = 0;
        EnsureCapacity(Shadows, estimatedItems);
        EnsureCapacity(Objects, estimatedItems);
    }

    public void AddShadow(
        Vector2 world,
        float opacity,
        string stableKey,
        string atlasKey) =>
        Add(
            Shadows, _shadowPool, ref _shadowCount,
            world, opacity, stableKey, atlasKey);

    public void AddObject(
        Vector2 world,
        float opacity,
        string stableKey,
        string atlasKey) =>
        Add(
            Objects, _objectPool, ref _objectCount,
            world, opacity, stableKey, atlasKey);

    public void Sort()
    {
        Shadows.Sort();
        Objects.Sort();
    }

    public float[] CopyVertices(List<float> vertices)
    {
        if (_vertexUpload.Length < vertices.Count)
        {
            var capacity = Math.Max(256, _vertexUpload.Length);
            while (capacity < vertices.Count) capacity *= 2;
            _vertexUpload = new float[capacity];
        }
        vertices.CopyTo(_vertexUpload);
        return _vertexUpload;
    }

    internal static WorldRenderItem[] LegacyOrder(
        IEnumerable<WorldRenderItem> items) =>
        items.OrderBy(item => item.World.Y)
            .ThenBy(item => item.World.X)
            .ThenBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();

    private static void EnsureCapacity<T>(List<T> list, int capacity)
    {
        if (list.Capacity < capacity)
            list.Capacity = capacity;
    }

    private static void Add(
        List<WorldRenderItem> target,
        List<WorldRenderItem> pool,
        ref int count,
        Vector2 world,
        float opacity,
        string stableKey,
        string atlasKey)
    {
        WorldRenderItem item;
        if (count < pool.Count)
        {
            item = pool[count];
            item.Set(world, opacity, stableKey, atlasKey);
        }
        else
        {
            item = new(world, opacity, stableKey, atlasKey);
            pool.Add(item);
        }
        count++;
        target.Add(item);
    }
}
