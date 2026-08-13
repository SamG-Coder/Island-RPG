using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Resources;

public enum SurfaceVegetationKind : byte
{
    Plant,
    Shrub,
    FloweringShrub,
    BerryBush
}

/// <summary>
/// Renderer-independent visual and gathering policy for one authored
/// vegetation variant. A null ResourceKind marks decorative vegetation.
/// </summary>
public readonly record struct SurfaceVegetationVisual(
    int Variant,
    string GraphicName,
    int FrameIndex,
    SurfaceVegetationKind Kind,
    bool CanBecomeInstance,
    ResourceNodeKind? ResourceKind,
    string? GatherItemId,
    int InitialRemaining,
    double RegrowthGameSeconds);

/// <summary>
/// Canonical generated vegetation placement. SourceTile and Ordinal form the
/// stable procedural address; Position remains the exact solo interaction and
/// render position inside that source tile.
/// </summary>
public readonly record struct SurfaceVegetationPlacement(
    int SourceTileX,
    int SourceTileY,
    int Ordinal,
    Vector2 Position,
    SurfaceVegetationVisual Visual);

internal readonly record struct SurfaceVegetationTile(
    int X,
    int Y,
    ProceduralSurfaceTerrain.Material Material,
    ProceduralSurfaceTerrain.Region Region,
    byte North,
    byte East,
    byte South,
    byte West);

/// <summary>
/// Canonical surface-vegetation generator shared by solo presentation and the
/// headless authority. Generation is chunk-local by design, matching the
/// established solo tree-edge influence and coastal-fibre guarantee exactly.
/// </summary>
public static class SurfaceVegetationCatalog
{
    public const int MinimumCoastalFibreSourcesPerChunk = 2;
    public const int MaximumPlacementsPerChunk = 98;
    public const double FibreRegrowthGameSeconds = 5 * 60;
    public const double BerryRegrowthGameSeconds = 12 * 60;

    private const int CandidateLimit = 96;
    private const int VariantFrameBits = 5;
    private const int VariantFrameMask = (1 << VariantFrameBits) - 1;

    public static IReadOnlyList<string> RequiredGraphicNames { get; } =
    [
        "PLANTS",
        "BUSH_NN", "BUSH_N0",
        "BUSH2_NN", "BUSH2_N0",
        "BUSH3_NN", "BUSH3_N0",
        "FORAG_NN", "FORAGM_NN"
    ];

    public static bool IsVegetationGraphic(string name) =>
        name.Equals("PLANTS", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("BUSH", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FORAG", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetVisual(
        int variant,
        out SurfaceVegetationVisual visual)
    {
        if (variant < 0)
        {
            visual = default;
            return false;
        }

        var profileIndex = variant >> VariantFrameBits;
        var frameIndex = variant & VariantFrameMask;
        if ((uint)profileIndex >= ProfileCount ||
            frameIndex >= FrameCount(profileIndex))
        {
            visual = default;
            return false;
        }

        visual = CreateVisual(profileIndex, frameIndex, gatherable: true);
        return true;
    }

    internal static IReadOnlyList<SurfaceVegetationPlacement> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != 0) return [];

        var originX = checked(chunk.X * WorldChunkKey.Size);
        var originY = checked(chunk.Y * WorldChunkKey.Size);
        var tiles = new SurfaceVegetationTile[
            WorldChunkKey.Size * WorldChunkKey.Size];
        var treeTiles = new HashSet<(int X, int Y)>();
        for (var localY = 0; localY < WorldChunkKey.Size; localY++)
        for (var localX = 0; localX < WorldChunkKey.Size; localX++)
        {
            var worldX = originX + localX;
            var worldY = originY + localY;
            var north = ProceduralSurfaceTerrain.RawHeightAt(
                worldSeed, worldX, worldY);
            var east = ProceduralSurfaceTerrain.RawHeightAt(
                worldSeed, worldX + 1, worldY);
            var south = ProceduralSurfaceTerrain.RawHeightAt(
                worldSeed, worldX + 1, worldY + 1);
            var west = ProceduralSurfaceTerrain.RawHeightAt(
                worldSeed, worldX, worldY + 1);
            var average = (north + east + south + west) / 4f;
            var classification = ProceduralSurfaceTerrain.ClassifyAt(
                worldSeed, worldX, worldY, average);
            tiles[localY * WorldChunkKey.Size + localX] = new(
                worldX,
                worldY,
                classification.Material,
                classification.Region,
                Surface(north),
                Surface(east),
                Surface(south),
                Surface(west));
            if (SurfaceTreeCatalog.TryDescribeAt(
                    worldSeed,
                    worldX,
                    worldY,
                    classification.Region,
                    classification.Material,
                    average,
                    out _))
            {
                treeTiles.Add((worldX, worldY));
            }
        }

        return Generate(worldSeed, tiles, treeTiles);
    }

    internal static IReadOnlyList<SurfaceVegetationPlacement> Generate(
        long worldSeed,
        IReadOnlyList<SurfaceVegetationTile> tiles,
        IReadOnlyCollection<(int X, int Y)> trees)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(trees);

        var treeTiles = trees.ToHashSet();
        var candidates = new List<Candidate>();
        foreach (var tile in tiles)
        {
            if (treeTiles.Contains((tile.X, tile.Y)) ||
                IsWater(tile.Material) ||
                IsSand(tile.Material) ||
                Relief(tile) > 2)
            {
                continue;
            }

            var treeInfluence = NearbyTreeInfluence(
                tile.X, tile.Y, treeTiles);
            for (var profileIndex = 0;
                 profileIndex < ProfileCount;
                 profileIndex++)
            {
                if (profileIndex == 3 &&
                    tile.Material != ProceduralSurfaceTerrain.Material.Snow)
                {
                    continue;
                }

                var chance = HabitatChance(profileIndex, tile.Region);
                if (chance <= 0) continue;
                var patch = PatchValue(
                    worldSeed,
                    tile.X,
                    tile.Y,
                    PatchScale(profileIndex),
                    1709 + profileIndex * 97);
                var colony = MathF.Pow(patch, 2.2f) * 2.8f + .12f;
                var edgeFactor = Kind(profileIndex) switch
                {
                    SurfaceVegetationKind.BerryBush =>
                        .72f + treeInfluence * .75f,
                    SurfaceVegetationKind.Shrub or
                        SurfaceVegetationKind.FloweringShrub =>
                        .82f + treeInfluence * .55f,
                    _ => 1f
                };
                var roll = Hash(
                    worldSeed,
                    tile.X,
                    tile.Y,
                    2003 + profileIndex * 101);
                if (roll >= chance * colony * edgeFactor) continue;

                var frameRoll = Hash(
                    worldSeed,
                    tile.X,
                    tile.Y,
                    2309 + profileIndex * 103);
                var frame = SelectFrame(profileIndex, tile, frameRoll);
                var x = tile.X + .12f +
                        Hash(worldSeed, tile.X, tile.Y, 2551) * .76f;
                var y = tile.Y + .12f +
                        Hash(worldSeed, tile.X, tile.Y, 2557) * .76f;
                candidates.Add(new(
                    Hash(
                        worldSeed,
                        tile.X,
                        tile.Y,
                        2801 + profileIndex * 107),
                    new(
                        tile.X,
                        tile.Y,
                        0,
                        new Vector2(x, y),
                        CreateVisual(
                            profileIndex,
                            frame,
                            IsGatherable(profileIndex, tile.Material)))));
                break;
            }
        }

        var result = candidates
            .OrderBy(static candidate => candidate.Priority)
            .Take(CandidateLimit)
            .Select(static candidate => candidate.Placement)
            .ToList();
        EnsureCoastalFibreSources(
            worldSeed, tiles, treeTiles, result);
        return result;
    }

    private static void EnsureCoastalFibreSources(
        long worldSeed,
        IReadOnlyList<SurfaceVegetationTile> tiles,
        HashSet<(int X, int Y)> treeTiles,
        List<SurfaceVegetationPlacement> vegetation)
    {
        var existingTiles = vegetation
            .Select(static value =>
                (value.SourceTileX, value.SourceTileY))
            .ToHashSet();
        var existingFibre = vegetation.Count(value =>
            value.Visual.ResourceKind == ResourceNodeKind.FibreShrub &&
            CoastalTileAt(tiles, value.Position));
        if (existingFibre >= MinimumCoastalFibreSourcesPerChunk) return;

        foreach (var tile in tiles
                     .Where(tile =>
                         tile.Material ==
                             ProceduralSurfaceTerrain.Material.Beach &&
                         Relief(tile) <= 2 &&
                         !treeTiles.Contains((tile.X, tile.Y)) &&
                         !existingTiles.Contains((tile.X, tile.Y)))
                     .OrderBy(tile => Hash(
                         worldSeed, tile.X, tile.Y, 3191)))
        {
            const int bush2Profile = 2;
            var frame = FrameIndex(
                Hash(worldSeed, tile.X, tile.Y, 3203), 12);
            vegetation.Add(new(
                tile.X,
                tile.Y,
                0,
                new Vector2(
                    tile.X + .2f +
                    Hash(worldSeed, tile.X, tile.Y, 3217) * .6f,
                    tile.Y + .2f +
                    Hash(worldSeed, tile.X, tile.Y, 3221) * .6f),
                CreateVisual(bush2Profile, frame, gatherable: true)));
            existingFibre++;
            if (existingFibre >= MinimumCoastalFibreSourcesPerChunk) return;
        }
    }

    private static bool CoastalTileAt(
        IReadOnlyList<SurfaceVegetationTile> tiles,
        Vector2 position)
    {
        var tileX = (int)MathF.Floor(position.X);
        var tileY = (int)MathF.Floor(position.Y);
        return tiles.Any(tile =>
            tile.X == tileX &&
            tile.Y == tileY &&
            tile.Material == ProceduralSurfaceTerrain.Material.Beach);
    }

    private static SurfaceVegetationVisual CreateVisual(
        int profileIndex,
        int frameIndex,
        bool gatherable)
    {
        var variant = (profileIndex << VariantFrameBits) | frameIndex;
        if (gatherable && Kind(profileIndex) == SurfaceVegetationKind.Shrub)
        {
            return new(
                variant,
                GraphicName(profileIndex),
                frameIndex,
                Kind(profileIndex),
                CanBecomeInstance(profileIndex),
                ResourceNodeKind.FibreShrub,
                ItemIds.PlantFibres,
                1,
                FibreRegrowthGameSeconds);
        }
        if (gatherable &&
            Kind(profileIndex) == SurfaceVegetationKind.BerryBush)
        {
            return new(
                variant,
                GraphicName(profileIndex),
                frameIndex,
                Kind(profileIndex),
                CanBecomeInstance(profileIndex),
                ResourceNodeKind.BerryBush,
                profileIndex == 5
                    ? ItemIds.TropicalBerries
                    : ItemIds.WildBerries,
                1,
                BerryRegrowthGameSeconds);
        }
        return new(
            variant,
            GraphicName(profileIndex),
            frameIndex,
            Kind(profileIndex),
            CanBecomeInstance(profileIndex),
            null,
            null,
            0,
            0);
    }

    private static bool IsGatherable(
        int profileIndex,
        ProceduralSurfaceTerrain.Material material)
    {
        if (!CanBecomeInstance(profileIndex)) return false;
        return Kind(profileIndex) switch
        {
            SurfaceVegetationKind.BerryBush => true,
            SurfaceVegetationKind.Shrub =>
                material is not ProceduralSurfaceTerrain.Material.Snow and
                    not ProceduralSurfaceTerrain.Material.Tundra,
            _ => false
        };
    }

    private static float NearbyTreeInfluence(
        int x,
        int y,
        HashSet<(int X, int Y)> trees)
    {
        var nearby = 0;
        for (var offsetY = -2; offsetY <= 2; offsetY++)
        for (var offsetX = -2; offsetX <= 2; offsetX++)
            if (trees.Contains((x + offsetX, y + offsetY))) nearby++;
        return nearby switch
        {
            0 => .25f,
            <= 3 => 1f,
            <= 7 => .65f,
            _ => .30f
        };
    }

    private static int SelectFrame(
        int profileIndex,
        SurfaceVegetationTile tile,
        float roll)
    {
        if (profileIndex == 2)
        {
            const int snowFrameStart = 12;
            const int snowFrameCount = 6;
            return tile.Material == ProceduralSurfaceTerrain.Material.Snow
                ? snowFrameStart + FrameIndex(roll, snowFrameCount)
                : FrameIndex(roll, snowFrameStart);
        }
        return FrameIndex(roll, FrameCount(profileIndex));
    }

    private static int FrameIndex(float roll, int count) =>
        Math.Min((int)(roll * count), count - 1);

    private static float HabitatChance(
        int profileIndex,
        ProceduralSurfaceTerrain.Region region) =>
        profileIndex switch
        {
            0 => region switch
            {
                ProceduralSurfaceTerrain.Region.TemperateGrassland => .105f,
                ProceduralSurfaceTerrain.Region.TemperateForest => .075f,
                ProceduralSurfaceTerrain.Region.Rainforest => .090f,
                ProceduralSurfaceTerrain.Region.Wetland => .085f,
                ProceduralSurfaceTerrain.Region.Savanna => .055f,
                ProceduralSurfaceTerrain.Region.Taiga => .030f,
                _ => 0
            },
            1 => region switch
            {
                ProceduralSurfaceTerrain.Region.TemperateForest => .018f,
                ProceduralSurfaceTerrain.Region.Rainforest => .024f,
                ProceduralSurfaceTerrain.Region.Wetland => .016f,
                _ => 0
            },
            2 => region switch
            {
                ProceduralSurfaceTerrain.Region.TemperateForest => .038f,
                ProceduralSurfaceTerrain.Region.Rainforest => .042f,
                ProceduralSurfaceTerrain.Region.TemperateGrassland => .020f,
                ProceduralSurfaceTerrain.Region.Savanna => .018f,
                ProceduralSurfaceTerrain.Region.Taiga => .022f,
                ProceduralSurfaceTerrain.Region.Tundra => .008f,
                _ => 0
            },
            3 => region switch
            {
                ProceduralSurfaceTerrain.Region.Tundra => .030f,
                ProceduralSurfaceTerrain.Region.Alpine => .022f,
                ProceduralSurfaceTerrain.Region.Taiga => .012f,
                _ => 0
            },
            4 => region switch
            {
                ProceduralSurfaceTerrain.Region.TemperateForest => .018f,
                ProceduralSurfaceTerrain.Region.Wetland => .015f,
                ProceduralSurfaceTerrain.Region.Taiga => .011f,
                ProceduralSurfaceTerrain.Region.TemperateGrassland => .007f,
                _ => 0
            },
            5 => region switch
            {
                ProceduralSurfaceTerrain.Region.Rainforest => .015f,
                ProceduralSurfaceTerrain.Region.Savanna => .010f,
                ProceduralSurfaceTerrain.Region.Coast => .006f,
                _ => 0
            },
            _ => 0
        };

    private static string GraphicName(int profileIndex) =>
        profileIndex switch
        {
            0 => "PLANTS",
            1 => "BUSH_NN",
            2 => "BUSH2_NN",
            3 => "BUSH3_NN",
            4 => "FORAG_NN",
            5 => "FORAGM_NN",
            _ => throw new ArgumentOutOfRangeException(nameof(profileIndex))
        };

    private static int FrameCount(int profileIndex) =>
        profileIndex switch
        {
            0 => 5,
            1 => 2,
            2 => 18,
            3 => 9,
            4 or 5 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(profileIndex))
        };

    private static SurfaceVegetationKind Kind(int profileIndex) =>
        profileIndex switch
        {
            0 => SurfaceVegetationKind.Plant,
            1 or 2 => SurfaceVegetationKind.Shrub,
            3 => SurfaceVegetationKind.FloweringShrub,
            4 or 5 => SurfaceVegetationKind.BerryBush,
            _ => throw new ArgumentOutOfRangeException(nameof(profileIndex))
        };

    private static bool CanBecomeInstance(int profileIndex) =>
        profileIndex is 1 or 2 or 4 or 5;

    private static float PatchScale(int profileIndex) =>
        profileIndex switch
        {
            0 => 4.5f,
            1 => 8f,
            2 => 7f,
            3 => 6f,
            4 or 5 => 9f,
            _ => throw new ArgumentOutOfRangeException(nameof(profileIndex))
        };

    private static float PatchValue(
        long seed,
        int x,
        int y,
        float scale,
        int salt)
    {
        var scaledX = x / scale;
        var scaledY = y / scale;
        var x0 = (int)MathF.Floor(scaledX);
        var y0 = (int)MathF.Floor(scaledY);
        var fx = Smooth(scaledX - x0);
        var fy = Smooth(scaledY - y0);
        var north = Lerp(
            Hash(seed, x0, y0, salt),
            Hash(seed, x0 + 1, y0, salt), fx);
        var south = Lerp(
            Hash(seed, x0, y0 + 1, salt),
            Hash(seed, x0 + 1, y0 + 1, salt), fx);
        return Lerp(north, south, fy);
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

    private static bool IsWater(ProceduralSurfaceTerrain.Material material) =>
        material is ProceduralSurfaceTerrain.Material.DeepWater or
            ProceduralSurfaceTerrain.Material.ShallowWater or
            ProceduralSurfaceTerrain.Material.RiverWater or
            ProceduralSurfaceTerrain.Material.MangroveShallows;

    private static bool IsSand(ProceduralSurfaceTerrain.Material material) =>
        material is ProceduralSurfaceTerrain.Material.Beach or
            ProceduralSurfaceTerrain.Material.DesertSand;

    private static int Relief(SurfaceVegetationTile tile) =>
        Math.Max(Math.Max(tile.North, tile.East),
            Math.Max(tile.South, tile.West)) -
        Math.Min(Math.Min(tile.North, tile.East),
            Math.Min(tile.South, tile.West));

    private static byte Surface(byte height) => height <= 2 ? (byte)0 : height;

    private static float Smooth(float value) =>
        value * value * (3 - 2 * value);

    private static float Lerp(float left, float right, float amount) =>
        left + (right - left) * amount;

    private const uint ProfileCount = 6;

    private readonly record struct Candidate(
        float Priority,
        SurfaceVegetationPlacement Placement);
}

/// <summary>
/// Headless descriptor source for interactive overworld shrubs and berry
/// bushes. One ready harvest is the procedural default; an accepted harvest
/// consumes it until the solo-equivalent regrowth time has elapsed.
/// </summary>
public sealed class SurfaceVegetationResourceDescriptorSource :
    IProceduralResourceDescriptorSource
{
    public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != 0 || !IsCompleteWorldChunk(chunk)) return [];

        return SurfaceVegetationCatalog.DescribeChunk(worldSeed, chunk)
            .Where(static value => value.Visual.ResourceKind.HasValue)
            .Select(static value => new ProceduralResourceSeed(
                ProceduralResourceKey.Vegetation(
                    value.Visual.ResourceKind!.Value,
                    value.SourceTileX,
                    value.SourceTileY,
                    value.Ordinal,
                    value.Visual.Variant),
                value.Position,
                InitialRemaining: value.Visual.InitialRemaining,
                RegrowthGameSeconds: value.Visual.RegrowthGameSeconds))
            .ToArray();
    }

    private static bool IsCompleteWorldChunk(WorldChunkKey chunk)
    {
        var minimumX = (long)chunk.X * WorldChunkKey.Size;
        var minimumY = (long)chunk.Y * WorldChunkKey.Size;
        var maximumX = minimumX + WorldChunkKey.Size;
        var maximumY = minimumY + WorldChunkKey.Size;
        return minimumX >= ProceduralResourceIdentity.MinimumCoordinate &&
               minimumY >= ProceduralResourceIdentity.MinimumCoordinate &&
               maximumX <= ProceduralResourceIdentity.MaximumCoordinate &&
               maximumY <= ProceduralResourceIdentity.MaximumCoordinate;
    }
}
