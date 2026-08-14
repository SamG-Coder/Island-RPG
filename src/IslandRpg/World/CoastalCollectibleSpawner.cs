using IslandRpg.Gameplay;
using IslandRpg.Resources;
using OpenTK.Mathematics;

namespace IslandRpg.World;

internal static class CoastalCollectibleSpawner
{
    public const int MaximumPerChunk = 8;

    private static readonly (string ItemId, int Weight)[] Drops =
    [
        (ItemIds.Seaweed, 38),
        (ItemIds.ClamShell, 28),
        (ItemIds.CockleShell, 22),
        (ItemIds.SpiralShell, 13),
        (ItemIds.ScallopShell, 11),
        (ItemIds.MoonShell, 7),
        (ItemIds.ConchShell, 4),
        (ItemIds.CowrieShell, 3),
        (ItemIds.PearlOysterShell, 1)
    ];

    public static bool IsCoastal(string itemId) =>
        ProceduralCoastalLootCatalog.IsCoastal(itemId);

    public static List<WorldGroundObject> GenerateInitial(
        long seed,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyCollection<IslandTree> trees,
        IReadOnlyCollection<WorldGroundObject> existing)
    {
        var occupied = trees.Select(tree => (tree.X, tree.Y))
            .Concat(existing.Select(item =>
                ((int)MathF.Floor(item.X), (int)MathF.Floor(item.Y))))
            .ToHashSet();
        var candidates = new List<(float Score, WorldGroundObject Item)>();
        foreach (var tile in tiles)
        {
            if (tile.Biome != Biome.Beach ||
                occupied.Contains((tile.X, tile.Y)) ||
                Relief(tile) > 2 ||
                Hash(seed, tile.X, tile.Y, 3701) >= .075f)
                continue;

            var itemId = SelectItem(
                Hash(seed, tile.X, tile.Y, 3719));
            candidates.Add((
                Hash(seed, tile.X, tile.Y, 3733),
                new(
                    StableId(seed, tile.X, tile.Y, itemId),
                    itemId,
                    tile.X + .18f + Hash(seed, tile.X, tile.Y, 3761) * .64f,
                    tile.Y + .18f + Hash(seed, tile.X, tile.Y, 3767) * .64f)));
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .Take(MaximumPerChunk)
            .Select(candidate => candidate.Item)
            .ToList();
    }

    public static bool TryRespawn(
        WorldChunk chunk,
        Vector2 playerPosition,
        out WorldGroundObject spawned)
    {
        spawned = null!;
        if (chunk.GroundObjects.Count >=
                WorldChunk.MaximumStoredGroundObjects ||
            chunk.GroundObjects.Count(item => IsCoastal(item.ItemId)) >=
            MaximumPerChunk)
            return false;

        var eligible = chunk.Tiles
            .Where(tile => tile.Biome == Biome.Beach && Relief(tile) <= 2)
            .OrderBy(_ => Random.Shared.Next())
            .Take(24);
        foreach (var tile in eligible)
        {
            var position = new Vector2(
                tile.X + .18f + Random.Shared.NextSingle() * .64f,
                tile.Y + .18f + Random.Shared.NextSingle() * .64f);
            if ((position - playerPosition).LengthSquared < 64 ||
                chunk.Trees.Any(tree =>
                    tree.X == tile.X && tree.Y == tile.Y) ||
                chunk.GroundObjects.Any(item =>
                    Vector2.DistanceSquared(
                        position, new(item.X, item.Y)) < .36f))
                continue;

            var itemId = SelectItem(Random.Shared.NextSingle());
            spawned = new(Guid.NewGuid(), itemId, position.X, position.Y);
            chunk.GroundObjects.Add(spawned);
            return true;
        }

        return false;
    }

    private static string SelectItem(float roll)
    {
        var total = Drops.Sum(drop => drop.Weight);
        var selected = roll * total;
        foreach (var drop in Drops)
        {
            selected -= drop.Weight;
            if (selected < 0) return drop.ItemId;
        }
        return Drops[^1].ItemId;
    }

    private static int Relief(IslandTile tile) =>
        Math.Max(
            Math.Max(tile.North, tile.East),
            Math.Max(tile.South, tile.West)) -
        Math.Min(
            Math.Min(tile.North, tile.East),
            Math.Min(tile.South, tile.West));

    private static Guid StableId(
        long seed, int x, int y, string itemId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, seed ^ 0x434f415354414cL);
        BitConverter.TryWriteBytes(bytes[8..], x);
        var kind = Array.FindIndex(
            Drops, drop => drop.ItemId == itemId);
        BitConverter.TryWriteBytes(bytes[12..], y ^ (kind << 24));
        return new(bytes);
    }

    private static float Hash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)seed ^
                        (ulong)(long)x * 0x9e3779b185ebca87UL ^
                        (ulong)(long)y * 0xc2b2ae3d27d4eb4fUL ^
                        (uint)salt;
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (value >> 40) / 16777216f;
        }
    }
}

internal sealed class CoastalCollectibleRespawnController
{
    private float _elapsed;
    private float _nextAttempt = 45;

    public void Update(
        float elapsed,
        IEnumerable<WorldChunk> chunks,
        Vector2 playerPosition,
        Action<WorldChunk> changed)
    {
        _elapsed += elapsed;
        if (_elapsed < _nextAttempt) return;
        _elapsed = 0;
        _nextAttempt = 35 + Random.Shared.NextSingle() * 55;

        foreach (var chunk in chunks.OrderBy(_ => Random.Shared.Next()))
        {
            if (!CoastalCollectibleSpawner.TryRespawn(
                    chunk, playerPosition, out _))
                continue;
            changed(chunk);
            return;
        }
    }
}
