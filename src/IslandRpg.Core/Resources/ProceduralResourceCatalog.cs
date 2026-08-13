using System.Collections.Immutable;
using System.Numerics;
using IslandRpg.Simulation;

namespace IslandRpg.Resources;

/// <summary>
/// Raw deterministic generator output. The authority derives Id itself, so a
/// descriptor source cannot accidentally preserve legacy/random client IDs.
/// </summary>
public sealed record ProceduralResourceSeed(
    ProceduralResourceKey Key,
    Vector2 Position,
    int InitialHealth = 0,
    int MaximumHealth = 0,
    int InitialRemaining = 0,
    double RegrowthGameSeconds = 0);

public interface IProceduralResourceDescriptorSource
{
    IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk);
}

public interface IResourceDescriptorResolver
{
    bool TryResolve(
        long worldSeed,
        ResourceNodeReference reference,
        out ResourceNodeDescriptor descriptor);

    IReadOnlyList<ResourceNodeDescriptor> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk);
}

public sealed record ProceduralResourceCatalogLimits
{
    public const float DefaultMinimumWorldCoordinate =
        ProceduralResourceIdentity.MinimumCoordinate;
    public const float DefaultMaximumWorldCoordinate =
        ProceduralResourceIdentity.MaximumCoordinate;

    public static ProceduralResourceCatalogLimits Default { get; } = new();

    public float MinimumWorldCoordinate { get; init; } =
        DefaultMinimumWorldCoordinate;

    public float MaximumWorldCoordinate { get; init; } =
        DefaultMaximumWorldCoordinate;

    public int MinimumWorldLevel { get; init; } =
        ProceduralResourceIdentity.MinimumWorldLevel;

    public int MaximumWorldLevel { get; init; } =
        ProceduralResourceIdentity.MaximumWorldLevel;

    public int MaximumNodesPerChunk { get; init; } = 4_096;

    public int MaximumCachedChunks { get; init; } = 256;

    public int MaximumHealth { get; init; } = 1_000_000;

    public int MaximumRemaining { get; init; } = 1_000_000;

    public double MaximumRegrowthGameSeconds { get; init; } =
        10 * 365 * 24 * 60 * 60;

    internal ProceduralResourceCatalogLimits ValidatedCopy()
    {
        if (!float.IsFinite(MinimumWorldCoordinate) ||
            !float.IsFinite(MaximumWorldCoordinate) ||
            MinimumWorldCoordinate >= MaximumWorldCoordinate ||
            MinimumWorldCoordinate <
                ProceduralResourceIdentity.MinimumCoordinate ||
            MaximumWorldCoordinate >
                ProceduralResourceIdentity.MaximumCoordinate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumWorldCoordinate));
        }
        if (MinimumWorldLevel < ProceduralResourceIdentity.MinimumWorldLevel ||
            MaximumWorldLevel > ProceduralResourceIdentity.MaximumWorldLevel ||
            MinimumWorldLevel > MaximumWorldLevel)
            throw new ArgumentOutOfRangeException(nameof(MinimumWorldLevel));
        if (MaximumNodesPerChunk <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumNodesPerChunk));
        if (MaximumCachedChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumCachedChunks));
        if (MaximumHealth <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumHealth));
        if (MaximumRemaining <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumRemaining));
        if (!double.IsFinite(MaximumRegrowthGameSeconds) ||
            MaximumRegrowthGameSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRegrowthGameSeconds));
        }
        return this with { };
    }
}

/// <summary>
/// Bounded, headless catalog over a deterministic resource source. Resolution
/// regenerates the claimed chunk and compares authority-derived IDs, which
/// rejects IDs forged from client-provided positions or resource metadata.
/// </summary>
public sealed class ProceduralResourceCatalog : IResourceDescriptorResolver
{
    private readonly IProceduralResourceDescriptorSource _source;
    private readonly ProceduralResourceCatalogLimits _limits;
    private readonly object _cacheSync = new();
    private readonly Dictionary<CacheKey, CachedChunk> _cache = [];
    private readonly Queue<CacheKey> _cacheOrder = [];

    public ProceduralResourceCatalog(
        IProceduralResourceDescriptorSource source,
        ProceduralResourceCatalogLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _limits = (limits ?? ProceduralResourceCatalogLimits.Default)
            .ValidatedCopy();
    }

    public bool TryResolve(
        long worldSeed,
        ResourceNodeReference reference,
        out ResourceNodeDescriptor descriptor)
    {
        descriptor = null!;
        if (!reference.IsWellFormed || !IsValidChunk(reference.Chunk))
            return false;
        return GetOrCreateChunk(worldSeed, reference.Chunk).ById.TryGetValue(
            reference.Id, out descriptor!);
    }

    public IReadOnlyList<ResourceNodeDescriptor> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (!IsValidChunk(chunk)) return [];
        return GetOrCreateChunk(worldSeed, chunk).Descriptors;
    }

    private CachedChunk GetOrCreateChunk(long worldSeed, WorldChunkKey chunk)
    {
        var key = new CacheKey(worldSeed, chunk);
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            cached = BuildChunk(worldSeed, chunk);
            while (_cache.Count >= _limits.MaximumCachedChunks)
                _cache.Remove(_cacheOrder.Dequeue());
            _cache.Add(key, cached);
            _cacheOrder.Enqueue(key);
            return cached;
        }
    }

    private CachedChunk BuildChunk(long worldSeed, WorldChunkKey chunk)
    {
        var seeds = _source.DescribeChunk(worldSeed, chunk) ??
                    throw new InvalidOperationException(
                        "The procedural resource source returned null.");
        if (seeds.Count > _limits.MaximumNodesPerChunk)
        {
            throw new InvalidOperationException(
                "The procedural resource source exceeded the per-chunk bound.");
        }

        var result = ImmutableArray.CreateBuilder<ResourceNodeDescriptor>(
            seeds.Count);
        var identities = new HashSet<ResourceNodeId>();
        for (var index = 0; index < seeds.Count; index++)
        {
            var seed = seeds[index] ?? throw new InvalidOperationException(
                "The procedural resource source returned a null seed.");
            ValidateSeed(chunk, seed);
            var id = ProceduralResourceIdentity.Derive(
                worldSeed, chunk, seed.Key);
            if (!identities.Add(id))
            {
                throw new InvalidOperationException(
                    "The procedural resource source produced a duplicate identity.");
            }
            result.Add(new ResourceNodeDescriptor(
                id,
                seed.Key.Kind,
                chunk,
                seed.Position,
                seed.Key.Variant,
                seed.InitialHealth,
                seed.MaximumHealth,
                seed.InitialRemaining,
                seed.RegrowthGameSeconds));
        }
        var descriptors = result.MoveToImmutable();
        return new CachedChunk(
            descriptors,
            descriptors.ToImmutableDictionary(static value => value.Id));
    }

    private bool IsValidChunk(WorldChunkKey chunk)
    {
        if (chunk.WorldLevel < _limits.MinimumWorldLevel ||
            chunk.WorldLevel > _limits.MaximumWorldLevel)
        {
            return false;
        }
        var chunkMinimumX = (long)chunk.X * WorldChunkKey.Size;
        var chunkMinimumY = (long)chunk.Y * WorldChunkKey.Size;
        var chunkMaximumX = chunkMinimumX + WorldChunkKey.Size;
        var chunkMaximumY = chunkMinimumY + WorldChunkKey.Size;
        return chunkMinimumX >= _limits.MinimumWorldCoordinate &&
               chunkMinimumY >= _limits.MinimumWorldCoordinate &&
               chunkMaximumX <= _limits.MaximumWorldCoordinate &&
               chunkMaximumY <= _limits.MaximumWorldCoordinate;
    }

    private void ValidateSeed(
        WorldChunkKey chunk,
        ProceduralResourceSeed seed)
    {
        if (!ProceduralResourceIdentity.IsValid(chunk, seed.Key))
            throw new InvalidOperationException(
                "The procedural resource key is invalid for its chunk.");
        if (seed.Key.SourceX < _limits.MinimumWorldCoordinate ||
            seed.Key.SourceX >= _limits.MaximumWorldCoordinate ||
            seed.Key.SourceY < _limits.MinimumWorldCoordinate ||
            seed.Key.SourceY >= _limits.MaximumWorldCoordinate)
        {
            throw new InvalidOperationException(
                "The procedural resource source coordinate is out of bounds.");
        }
        if (!float.IsFinite(seed.Position.X) ||
            !float.IsFinite(seed.Position.Y) ||
            seed.Position.X < _limits.MinimumWorldCoordinate ||
            seed.Position.X >= _limits.MaximumWorldCoordinate ||
            seed.Position.Y < _limits.MinimumWorldCoordinate ||
            seed.Position.Y >= _limits.MaximumWorldCoordinate ||
            WorldChunkKey.At(seed.Position, chunk.WorldLevel) != chunk)
        {
            throw new InvalidOperationException(
                "The procedural resource position is invalid for its chunk.");
        }
        if (seed.InitialHealth < 0 ||
            seed.InitialHealth > _limits.MaximumHealth ||
            seed.MaximumHealth < 0 ||
            seed.MaximumHealth > _limits.MaximumHealth ||
            seed.InitialHealth > seed.MaximumHealth ||
            seed.InitialRemaining < -1 ||
            seed.InitialRemaining > _limits.MaximumRemaining ||
            !double.IsFinite(seed.RegrowthGameSeconds) ||
            seed.RegrowthGameSeconds < 0 ||
            seed.RegrowthGameSeconds > _limits.MaximumRegrowthGameSeconds)
        {
            throw new InvalidOperationException(
                "The procedural resource defaults are invalid.");
        }
    }

    private readonly record struct CacheKey(
        long WorldSeed,
        WorldChunkKey Chunk);

    private sealed record CachedChunk(
        ImmutableArray<ResourceNodeDescriptor> Descriptors,
        ImmutableDictionary<ResourceNodeId, ResourceNodeDescriptor> ById);
}
