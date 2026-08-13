using System.Collections.Concurrent;

namespace IslandRpg.World;

/// <summary>
/// A deterministic, cached drainage model evaluated above the gameplay chunk scale.
/// Each region carries a halo so rivers are influenced by terrain outside the visible
/// region; neighboring solutions are blended only inside that shared halo.
/// </summary>
internal static class MacroHydrology
{
    private const int CellSize = 8;
    private const int RegionCells = 64;
    private const int RegionSpan = CellSize * RegionCells;
    private const int HaloCells = 24;
    private const int GridSize = RegionCells + HaloCells * 2;
    private const int BlendTiles = HaloCells * CellSize / 2;
    private const int MaxCachedRegions = 64;
    private const int MaxAtlasCachedRegionsPerJob = 32;
    private static readonly ConcurrentDictionary<
        (long Seed, int X, int Y), Lazy<Region>> GameplayCache = [];
    private static readonly ConcurrentDictionary<long, AtlasSamplingContext>
        ActiveAtlasContexts = [];
    private static readonly AsyncLocal<AtlasSamplingContext?>
        CurrentAtlasContext = new();
    private static long _nextAtlasContextId;

    internal readonly record struct Sample(float River, float Lake, float Flow);
    internal static int GameplayCacheCount => GameplayCache.Count;
    internal static int AtlasCacheCount =>
        ActiveAtlasContexts.Values.Sum(context => context.Cache.Count);

    public static IDisposable BeginAtlasSampling()
    {
        var previous = CurrentAtlasContext.Value;
        var context = new AtlasSamplingContext(
            Interlocked.Increment(ref _nextAtlasContextId));
        ActiveAtlasContexts[context.Id] = context;
        CurrentAtlasContext.Value = context;
        return new AtlasSamplingScope(context, previous);
    }

    public static void ClearAtlasCache()
    {
        foreach (var context in ActiveAtlasContexts.Values)
            context.Cache.Clear();
    }

    public static Sample At(long seed, float worldX, float worldY)
    {
        var regionX = FloorDiv((int)MathF.Floor(worldX), RegionSpan);
        var regionY = FloorDiv((int)MathF.Floor(worldY), RegionSpan);
        var localX = worldX - regionX * RegionSpan;
        var localY = worldY - regionY * RegionSpan;

        var xNeighbor = localX < BlendTiles ? -1 : localX > RegionSpan - BlendTiles ? 1 : 0;
        var yNeighbor = localY < BlendTiles ? -1 : localY > RegionSpan - BlendTiles ? 1 : 0;
        var xBlend = xNeighbor switch
        {
            -1 => 1f - localX / BlendTiles,
            1 => (localX - (RegionSpan - BlendTiles)) / BlendTiles,
            _ => 0
        };
        var yBlend = yNeighbor switch
        {
            -1 => 1f - localY / BlendTiles,
            1 => (localY - (RegionSpan - BlendTiles)) / BlendTiles,
            _ => 0
        };

        var center = Get(seed, regionX, regionY).At(worldX, worldY);
        if (xNeighbor == 0 && yNeighbor == 0) return center;
        var horizontal = xNeighbor == 0
            ? center
            : Lerp(center, Get(seed, regionX + xNeighbor, regionY).At(worldX, worldY), xBlend);
        if (yNeighbor == 0) return horizontal;
        var vertical = Lerp(center,
            Get(seed, regionX, regionY + yNeighbor).At(worldX, worldY), yBlend);
        if (xNeighbor == 0) return vertical;
        var diagonal = Get(seed, regionX + xNeighbor, regionY + yNeighbor).At(worldX, worldY);
        return Lerp(horizontal,
            Lerp(vertical, diagonal, xBlend), yBlend);
    }

    private static Region Get(long seed, int x, int y)
    {
        var key = (Seed: seed, X: x, Y: y);
        var atlasContext = CurrentAtlasContext.Value;
        var cache = atlasContext?.Cache ?? GameplayCache;
        var maximum = atlasContext is null
            ? MaxCachedRegions
            : MaxAtlasCachedRegionsPerJob;
        var region = cache.GetOrAdd(key, value =>
            new Lazy<Region>(() => Generate(value.Seed, value.X, value.Y),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (cache.Count > maximum)
        {
            foreach (var candidate in cache.Keys)
            {
                if (candidate == key) continue;
                cache.TryRemove(candidate, out _);
                if (cache.Count <= maximum) break;
            }
        }
        return region;
    }

    private sealed class AtlasSamplingContext(long id)
    {
        public long Id { get; } = id;
        public ConcurrentDictionary<(long Seed, int X, int Y), Lazy<Region>>
            Cache { get; } = [];
    }

    private sealed class AtlasSamplingScope(
        AtlasSamplingContext context,
        AtlasSamplingContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            CurrentAtlasContext.Value = previous;
            ActiveAtlasContexts.TryRemove(context.Id, out _);
            context.Cache.Clear();
            _disposed = true;
        }
    }

    private static Region Generate(long seed, int regionX, int regionY)
    {
        var originX = regionX * RegionSpan - HaloCells * CellSize;
        var originY = regionY * RegionSpan - HaloCells * CellSize;
        var count = GridSize * GridSize;
        var original = new float[count];
        var filled = new float[count];
        var lake = new float[count];
        var receiver = new int[count];
        var accumulation = new float[count];

        for (var y = 0; y < GridSize; y++)
        for (var x = 0; x < GridSize; x++)
        {
            var worldX = originX + x * CellSize;
            var worldY = originY + y * CellSize;
            var index = y * GridSize + x;
            original[index] = ProceduralSurfaceTerrain.BaseElevationAt(
                seed, worldX, worldY);
            filled[index] = original[index];
            accumulation[index] = ProceduralSurfaceTerrain.RainfallAt(
                seed, worldX, worldY);
            receiver[index] = -1;
        }

        // Relax enclosed pits toward their lowest spill point. This inexpensive
        // depression fill is stable because every pass can only raise a cell.
        for (var pass = 0; pass < 20; pass++)
        for (var y = 1; y < GridSize - 1; y++)
        for (var x = 1; x < GridSize - 1; x++)
        {
            var index = y * GridSize + x;
            var lowestNeighbor = float.MaxValue;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                lowestNeighbor = Math.Min(lowestNeighbor, filled[(y + oy) * GridSize + x + ox]);
            }
            if (filled[index] < lowestNeighbor)
                filled[index] = Math.Min(lowestNeighbor + .002f, original[index] + 3f);
        }

        for (var y = 1; y < GridSize - 1; y++)
        for (var x = 1; x < GridSize - 1; x++)
        {
            var index = y * GridSize + x;
            var best = filled[index];
            var bestIndex = -1;
            for (var oy = -1; oy <= 1; oy++)
            for (var ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                var candidate = (y + oy) * GridSize + x + ox;
                var diagonalPenalty = ox != 0 && oy != 0 ? .0002f : 0;
                if (filled[candidate] + diagonalPenalty >= best) continue;
                best = filled[candidate] + diagonalPenalty;
                bestIndex = candidate;
            }
            receiver[index] = bestIndex;
            lake[index] = Math.Clamp((filled[index] - original[index]) / 1.2f, 0, 1);
        }

        foreach (var index in Enumerable.Range(0, count)
                     .OrderByDescending(index => filled[index]))
        {
            var target = receiver[index];
            if (target >= 0) accumulation[target] += accumulation[index];
        }

        var river = new float[count];
        for (var index = 0; index < count; index++)
        {
            if (original[index] < .55f) continue;
            var flow = MathF.Log2(1 + accumulation[index]);
            river[index] = SmoothStep(2.7f, 6.4f, flow);
            lake[index] *= SmoothStep(.8f, 2.2f, accumulation[index]);
        }
        return new(originX, originY, river, lake, accumulation);
    }

    private sealed class Region(
        int originX, int originY, float[] river, float[] lake, float[] flow)
    {
        public Sample At(float worldX, float worldY)
        {
            var x = Math.Clamp((worldX - originX) / CellSize, 0, GridSize - 1.001f);
            var y = Math.Clamp((worldY - originY) / CellSize, 0, GridSize - 1.001f);
            var x0 = (int)x;
            var y0 = (int)y;
            var x1 = Math.Min(x0 + 1, GridSize - 1);
            var y1 = Math.Min(y0 + 1, GridSize - 1);
            var tx = x - x0;
            var ty = y - y0;
            return new(
                Bilinear(river, x0, y0, x1, y1, tx, ty),
                Bilinear(lake, x0, y0, x1, y1, tx, ty),
                Bilinear(flow, x0, y0, x1, y1, tx, ty));
        }

        private static float Bilinear(
            float[] values, int x0, int y0, int x1, int y1, float tx, float ty)
        {
            var north = Mix(values[y0 * GridSize + x0], values[y0 * GridSize + x1], tx);
            var south = Mix(values[y1 * GridSize + x0], values[y1 * GridSize + x1], tx);
            return Mix(north, south, ty);
        }
    }

    private static Sample Lerp(Sample a, Sample b, float amount) => new(
        Mix(a.River, b.River, amount),
        Mix(a.Lake, b.Lake, amount),
        Mix(a.Flow, b.Flow, amount));

    private static float Mix(float a, float b, float amount) => a + (b - a) * amount;

    private static float SmoothStep(float a, float b, float value)
    {
        var t = Math.Clamp((value - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
