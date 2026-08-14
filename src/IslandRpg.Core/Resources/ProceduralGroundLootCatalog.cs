using IslandRpg.Gameplay;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Resources;

/// <summary>
/// Deterministic overworld ground loot (sticks, rocks, crop seeds) shared by
/// solo chunk generation and the dedicated-server pickup path.
/// </summary>
public static class ProceduralGroundLootCatalog
{
    public const int MaximumPerChunk = 8;

    public readonly record struct Placement(
        Guid Id,
        string ItemId,
        float X,
        float Y);

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
                UnitHash(worldSeed, tileX, tileY, 829),
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
        if (tile.Material is
                ProceduralSurfaceTerrain.Material.DeepWater or
                ProceduralSurfaceTerrain.Material.ShallowWater or
                ProceduralSurfaceTerrain.Material.RiverWater or
                ProceduralSurfaceTerrain.Material.MangroveShallows)
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

        var stickChance = tile.Region switch
        {
            ProceduralSurfaceTerrain.Region.TemperateForest or
                ProceduralSurfaceTerrain.Region.Rainforest => .035f,
            ProceduralSurfaceTerrain.Region.Taiga or
                ProceduralSurfaceTerrain.Region.Wetland => .025f,
            ProceduralSurfaceTerrain.Region.Savanna => .012f,
            _ => 0
        };
        var rockChance = tile.Region switch
        {
            ProceduralSurfaceTerrain.Region.Alpine => .055f,
            ProceduralSurfaceTerrain.Region.Tundra or
                ProceduralSurfaceTerrain.Region.Coast => .024f,
            ProceduralSurfaceTerrain.Region.Desert => .018f,
            ProceduralSurfaceTerrain.Region.TemperateGrassland => .008f,
            _ => 0
        };
        var cropSeedChance = tile.Region switch
        {
            ProceduralSurfaceTerrain.Region.TemperateGrassland => .018f,
            ProceduralSurfaceTerrain.Region.Savanna => .012f,
            ProceduralSurfaceTerrain.Region.Wetland => .009f,
            _ => 0
        };
        var roll = UnitHash(worldSeed, tileX, tileY, 811);
        string? itemId = roll < stickChance
            ? ItemIds.Sticks
            : roll < stickChance + rockChance
                ? ItemIds.LargeRock
                : roll < stickChance + rockChance + cropSeedChance
                    ? SelectCropSeed(UnitHash(worldSeed, tileX, tileY, 817))
                    : null;
        if (itemId is null) return false;
        placement = new(
            StableId(worldSeed, tileX, tileY, itemId),
            itemId,
            tileX + .18f + UnitHash(worldSeed, tileX, tileY, 823) * .64f,
            tileY + .18f + UnitHash(worldSeed, tileX, tileY, 827) * .64f);
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
        BitConverter.TryWriteBytes(bytes, worldSeed);
        BitConverter.TryWriteBytes(bytes[8..], tileX);
        var discriminator = itemId.Equals(
            ItemIds.Sticks, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        BitConverter.TryWriteBytes(bytes[12..], tileY ^ (discriminator << 28));
        return new Guid(bytes);
    }

    private static string SelectCropSeed(float roll) => roll switch
    {
        < 1f / 3f => ItemIds.WildGrainSeeds,
        < 2f / 3f => ItemIds.BeanSeeds,
        _ => ItemIds.RootSeeds
    };

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
