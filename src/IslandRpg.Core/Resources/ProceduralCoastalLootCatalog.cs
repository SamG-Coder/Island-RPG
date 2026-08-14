using IslandRpg.Gameplay;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Resources;

/// <summary>
/// Deterministic beach collectibles (shells, seaweed) shared by solo chunk
/// generation and the dedicated-server pickup path. IDs match
/// <c>CoastalCollectibleSpawner</c> so a click on any coastal item can be
/// resolved without publishing the whole beach set.
/// </summary>
public static class ProceduralCoastalLootCatalog
{
    public const int MaximumPerChunk = 8;

    public static IReadOnlyList<string> PortableItemIds { get; } =
    [
        ItemIds.Seaweed,
        ItemIds.ClamShell,
        ItemIds.CockleShell,
        ItemIds.SpiralShell,
        ItemIds.ScallopShell,
        ItemIds.MoonShell,
        ItemIds.ConchShell,
        ItemIds.CowrieShell,
        ItemIds.PearlOysterShell
    ];

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

    public readonly record struct Placement(
        Guid Id,
        string ItemId,
        float X,
        float Y);

    public static bool IsCoastal(string itemId) =>
        PortableItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<Placement> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != 0) return [];
        var originX = chunk.X * WorldChunkKey.Size;
        var originY = chunk.Y * WorldChunkKey.Size;
        var candidates = new List<(float Score, Placement Value)>();
        for (var localY = 0; localY < WorldChunkKey.Size; localY++)
        for (var localX = 0; localX < WorldChunkKey.Size; localX++)
        {
            var tileX = originX + localX;
            var tileY = originY + localY;
            if (!TryDescribeAt(worldSeed, tileX, tileY, out var placement))
                continue;
            candidates.Add((
                UnitHash(worldSeed, tileX, tileY, 3733),
                placement));
        }

        return candidates
            .OrderBy(static value => value.Score)
            .Take(MaximumPerChunk)
            .Select(static value => value.Value)
            .ToArray();
    }

    public static bool TryDescribeAt(
        long worldSeed,
        int tileX,
        int tileY,
        out Placement placement)
    {
        placement = default;
        var elevation =
            (ProceduralSurfaceTerrain.RawHeightAt(worldSeed, tileX, tileY) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX + 1, tileY) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX + 1, tileY + 1) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX, tileY + 1)) / 4f;
        var tile = ProceduralSurfaceTerrain.ClassifyAt(
            worldSeed, tileX, tileY, elevation);
        if (tile.Material != ProceduralSurfaceTerrain.Material.Beach)
            return false;
        var relief =
            Math.Max(
                Math.Max(
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX, tileY),
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX + 1, tileY)),
                Math.Max(
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX + 1, tileY + 1),
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX, tileY + 1))) -
            Math.Min(
                Math.Min(
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX, tileY),
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX + 1, tileY)),
                Math.Min(
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX + 1, tileY + 1),
                    ProceduralSurfaceTerrain.RawHeightAt(
                        worldSeed, tileX, tileY + 1)));
        if (relief > 2) return false;
        if (SurfaceTreeCatalog.TryDescribeAt(worldSeed, tileX, tileY, out _))
            return false;
        if (ProceduralGroundLootCatalog.TryDescribeAt(
                worldSeed, tileX, tileY, out _))
            return false;
        if (UnitHash(worldSeed, tileX, tileY, 3701) >= .075f)
            return false;

        var itemId = SelectItem(UnitHash(worldSeed, tileX, tileY, 3719));
        placement = new(
            StableId(worldSeed, tileX, tileY, itemId),
            itemId,
            tileX + .18f + UnitHash(worldSeed, tileX, tileY, 3761) * .64f,
            tileY + .18f + UnitHash(worldSeed, tileX, tileY, 3767) * .64f);
        return true;
    }

    public static bool TryResolve(
        long worldSeed,
        WorldChunkKey chunk,
        Guid objectId,
        out Placement placement)
    {
        foreach (var candidate in DescribeChunk(worldSeed, chunk))
        {
            if (candidate.Id != objectId) continue;
            placement = candidate;
            return true;
        }

        placement = default;
        return false;
    }

    public static Guid StableId(
        long worldSeed,
        int tileX,
        int tileY,
        string itemId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, worldSeed ^ 0x434f415354414cL);
        BitConverter.TryWriteBytes(bytes[8..], tileX);
        var kind = Array.FindIndex(
            Drops, drop => drop.ItemId.Equals(
                itemId, StringComparison.OrdinalIgnoreCase));
        if (kind < 0) kind = 0;
        BitConverter.TryWriteBytes(bytes[12..], tileY ^ (kind << 24));
        return new Guid(bytes);
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

    private static float UnitHash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)seed ^
                        ((ulong)(long)x * 0x9e3779b185ebca87UL) ^
                        ((ulong)(long)y * 0xc2b2ae3d27d4eb4fUL) ^
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
