using IslandRpg.Client;
using IslandRpg.Resources;
using IslandRpg.Simulation;

namespace IslandRpg.Rendering;

/// <summary>
/// Cache-only multiplayer resource lookups for render, hover, walk, and zoom.
/// Unknown identity means the resource is still present. This type cannot
/// call procedural describe or catalog generate.
/// </summary>
internal sealed class NetworkResourceHotPath
{
    public readonly record struct KnownNode(
        ResourceNodeId Id,
        WorldChunkKey Chunk,
        ResourceNodeKind Kind,
        double RegrowthGameSeconds);

    private readonly Dictionary<string, KnownNode> _fish =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, KnownNode> _trees = [];

    public IReadOnlyDictionary<string, KnownNode> Fish => _fish;
    public IReadOnlyDictionary<long, KnownNode> Trees => _trees;

    public void RememberFish(
        string stableKey,
        ResourceNodeId id,
        WorldChunkKey chunk) =>
        _fish[stableKey] = new(id, chunk, ResourceNodeKind.FishSchool, 0);

    public void RememberFishFromWorld(
        long worldSeed,
        int worldLevel,
        float x,
        float y,
        int species,
        string stableKey)
    {
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(x, y), worldLevel);
        RememberFish(
            stableKey,
            ProceduralResourceIdentity.ForFish(
                worldSeed, worldLevel, tileX, tileY, species),
            chunk);
    }

    public void RememberTree(
        long tileKey,
        ResourceNodeId id,
        WorldChunkKey chunk) =>
        _trees[tileKey] = new(id, chunk, ResourceNodeKind.Tree, 0);

    public void RememberTreeFromWorld(
        long worldSeed,
        int worldLevel,
        int tileX,
        int tileY,
        int variant)
    {
        var chunk = WorldChunkKey.At(
            new System.Numerics.Vector2(tileX + .5f, tileY + .5f),
            worldLevel);
        RememberTree(
            WorldHoverSelection.TileKey(tileX, tileY),
            ProceduralResourceIdentity.ForTree(
                worldSeed, worldLevel, tileX, tileY, variant),
            chunk);
    }

    public void Clear()
    {
        _fish.Clear();
        _trees.Clear();
    }

    public void ForgetChunk(WorldChunkKey chunk)
    {
        foreach (var key in _fish
                     .Where(pair => pair.Value.Chunk == chunk)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _fish.Remove(key);
        foreach (var key in _trees
                     .Where(pair => pair.Value.Chunk == chunk)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _trees.Remove(key);
    }

    /// <summary>
    /// Missing cache or missing sparse state means the school is still live.
    /// </summary>
    public bool IsFishDepleted(
        string stableKey,
        IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>? chunks)
    {
        if (!_fish.TryGetValue(stableKey, out var known))
            return false;
        return IsDepleted(known, chunks);
    }

    /// <summary>
    /// Missing cache means a standing tree (blocks). Depleted sparse state
    /// releases the cell.
    /// </summary>
    public bool TreeBlocks(
        long tileKey,
        IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>? chunks)
    {
        if (!_trees.TryGetValue(tileKey, out var known))
            return true;
        return NetworkResourceObstacleRules.BlocksWorld(
            known.Kind,
            known.RegrowthGameSeconds,
            Lookup(known, chunks));
    }

    public bool IsTreeDepleted(
        long tileKey,
        IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>? chunks)
    {
        if (!_trees.TryGetValue(tileKey, out var known))
            return false;
        return IsDepleted(known, chunks);
    }

    private static bool IsDepleted(
        KnownNode known,
        IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>? chunks)
    {
        var state = Lookup(known, chunks);
        if (state is null) return false;
        // Tree sticks are secondary stock. Remaining == 0 must not hide or
        // fell the tree — only health/depleted does that.
        if (known.Kind == ResourceNodeKind.Tree)
            return state.Depleted || state.Health <= 0;
        return state.Depleted || state.Remaining <= 0;
    }

    private static ResourceNodeSparseState? Lookup(
        KnownNode known,
        IReadOnlyDictionary<WorldChunkKey, NetworkResourceChunkState>? chunks)
    {
        if (chunks is null ||
            !chunks.TryGetValue(known.Chunk, out var chunk) ||
            !chunk.Nodes.TryGetValue(known.Id, out var state))
            return null;
        return state;
    }
}