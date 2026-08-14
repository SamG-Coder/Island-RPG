using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Resources;

/// <summary>
/// Stable, renderer-independent description of one generated surface tree.
/// Variant encodes both the tree family and its visual frame so the client
/// and authority derive the same resource identity without asset lookups.
/// </summary>
public readonly record struct SurfaceTreeVisual(
    int Variant,
    string GraphicName,
    int FrameIndex,
    int MaximumHealth,
    string LogItemId,
    string SeedItemId,
    int FellingLogCount);

/// <summary>
/// Canonical tree placement and variant policy shared by solo chunk
/// generation and the headless authoritative resource catalog.
/// </summary>
public static class SurfaceTreeCatalog
{
    private const int VariantFrameBits = 5;
    private const int VariantFrameMask = (1 << VariantFrameBits) - 1;
    private const int GenericFamilyCount = 12;
    private const int PalmFamily = 12;
    private const int PineFamily = 13;
    private const int OakFamily = 14;
    private const int JungleFamily = 15;
    private const int SnowFamily = 16;
    private const int BambooFamily = 17;
    private const int CactusFamily = 18;
    private const int MaximumFamily = CactusFamily;

    /// <summary>
    /// Recreates the exact surface-tree decision for one world tile. It is
    /// intentionally stateless, making chunk edges and negative coordinates
    /// independent of generation order.
    /// </summary>
    public static int TryDescribeAtInvocations { get; set; }

    public static bool TryDescribeAt(
        long worldSeed,
        int tileX,
        int tileY,
        out SurfaceTreeVisual visual)
    {
        TryDescribeAtInvocations++;
        var elevation =
            (ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX, tileY) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX + 1, tileY) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX + 1, tileY + 1) +
             ProceduralSurfaceTerrain.RawHeightAt(
                 worldSeed, tileX, tileY + 1)) / 4f;
        var classification = ProceduralSurfaceTerrain.ClassifyAt(
            worldSeed, tileX, tileY, elevation);
        return TryDescribeAt(
            worldSeed,
            tileX,
            tileY,
            classification.Region,
            classification.Material,
            elevation,
            out visual);
    }

    internal static bool TryDescribeAt(
        long worldSeed,
        int tileX,
        int tileY,
        ProceduralSurfaceTerrain.Region region,
        ProceduralSurfaceTerrain.Material material,
        float elevation,
        out SurfaceTreeVisual visual)
    {
        if (UnitHash(worldSeed, tileX, tileY, 91) >=
            SpawnChance(region, elevation))
        {
            visual = default;
            return false;
        }

        var graphicName = SelectGraphic(
            worldSeed,
            tileX,
            tileY,
            region,
            material);
        var frameIndex = SelectFrame(
            worldSeed, tileX, tileY, graphicName);
        visual = CreateVisual(graphicName, frameIndex);
        return true;
    }

    public static bool TryGetVisual(
        int variant,
        out SurfaceTreeVisual visual)
    {
        if (variant < 0)
        {
            visual = default;
            return false;
        }

        var family = variant >> VariantFrameBits;
        var frame = variant & VariantFrameMask;
        if (family > MaximumFamily)
        {
            visual = default;
            return false;
        }

        var graphicName = GraphicName(family);
        if (frame >= FrameCount(graphicName))
        {
            visual = default;
            return false;
        }

        visual = new SurfaceTreeVisual(
            variant,
            graphicName,
            frame,
            MaximumHealth(graphicName),
            LogItemId(graphicName),
            SeedItemId(graphicName),
            FellingLogCount(MaximumHealth(graphicName)));
        return true;
    }

    internal static bool HasVariants(string graphicName) =>
        FrameCount(graphicName) > 1;

    internal static float SpawnChance(
        ProceduralSurfaceTerrain.Region region,
        float elevation)
    {
        var chance = region switch
        {
            ProceduralSurfaceTerrain.Region.Rainforest => .31f,
            ProceduralSurfaceTerrain.Region.TemperateForest => .23f,
            ProceduralSurfaceTerrain.Region.Taiga => .19f,
            ProceduralSurfaceTerrain.Region.Wetland => .13f,
            ProceduralSurfaceTerrain.Region.Savanna => .065f,
            ProceduralSurfaceTerrain.Region.Alpine => .045f,
            ProceduralSurfaceTerrain.Region.Coast => .012f,
            ProceduralSurfaceTerrain.Region.Tundra => .025f,
            ProceduralSurfaceTerrain.Region.Desert => .009f,
            _ => 0
        };
        return region == ProceduralSurfaceTerrain.Region.Alpine
            ? chance * Math.Clamp((12f - elevation) / 4f, 0, 1)
            : chance;
    }

    internal static int FrameCount(string graphicName) =>
        VisibleName(graphicName).ToUpperInvariant() switch
        {
            "FPAL_NN" => 13,
            "FPIN_NN" => 9,
            "FOAK_NN" => 14,
            "FJUN_NN" => 13,
            "FSNO_NN" => 9,
            "FBAM_NN" => 4,
            "FCAC_NN" => 6,
            _ => 1
        };

    internal static int SelectFrame(
        long seed,
        int x,
        int y,
        string graphicName)
    {
        var count = FrameCount(graphicName);
        return count == 1
            ? 0
            : (int)(UnitHash(seed, x, y, 3137) * count) % count;
    }

    internal static string SelectGraphic(
        long seed,
        int x,
        int y,
        ProceduralSurfaceTerrain.Region region,
        ProceduralSurfaceTerrain.Material material)
    {
        var roll = UnitHash(seed, x, y, 137);
        var generic = GenericTree(UnitHash(seed, x, y, 149));
        return region switch
        {
            ProceduralSurfaceTerrain.Region.Coast => "FPAL_NN",
            ProceduralSurfaceTerrain.Region.Savanna => roll < .62f
                ? "FPAL_NN"
                : generic,
            ProceduralSurfaceTerrain.Region.Rainforest => roll switch
            {
                < .72f => "FJUN_NN",
                < .88f => "FBAM_NN",
                _ => generic
            },
            ProceduralSurfaceTerrain.Region.TemperateForest => roll < .72f
                ? "FOAK_NN"
                : generic,
            ProceduralSurfaceTerrain.Region.Wetland => roll switch
            {
                < .55f => "FBAM_NN",
                < .82f => "FJUN_NN",
                _ => generic
            },
            ProceduralSurfaceTerrain.Region.Taiga => "FPIN_NN",
            ProceduralSurfaceTerrain.Region.Tundra => "FSNO_NN",
            ProceduralSurfaceTerrain.Region.Alpine =>
                material == ProceduralSurfaceTerrain.Material.Snow
                    ? "FSNO_NN"
                    : "FPIN_NN",
            ProceduralSurfaceTerrain.Region.Desert => "FCAC_NN",
            _ => generic
        };
    }

    internal static int MaximumHealth(string graphicName)
    {
        if (graphicName.StartsWith(
                "FPAL", StringComparison.OrdinalIgnoreCase))
            return 75;
        if (graphicName.StartsWith(
                "FPIN", StringComparison.OrdinalIgnoreCase))
            return 125;
        if (graphicName.StartsWith(
                "FOAK", StringComparison.OrdinalIgnoreCase))
            return 150;
        if (graphicName.StartsWith(
                "FJUN", StringComparison.OrdinalIgnoreCase))
            return 175;
        if (graphicName.StartsWith(
                "FSNO", StringComparison.OrdinalIgnoreCase))
            return 135;
        if (graphicName.StartsWith(
                "FBAM", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (graphicName.StartsWith(
                "FCAC", StringComparison.OrdinalIgnoreCase))
            return 65;
        if (graphicName.StartsWith(
                "TREE", StringComparison.OrdinalIgnoreCase) &&
            graphicName.Length > 4)
        {
            ReadOnlySpan<int> healthByVariant =
                [100, 125, 90, 150, 110, 175, 95, 135, 105, 160, 120, 145];
            var variant = char.ToUpperInvariant(graphicName[4]) - 'A';
            if ((uint)variant < (uint)healthByVariant.Length)
                return healthByVariant[variant];
        }
        return 100;
    }

    public static string LogItemId(string graphicName)
    {
        if (graphicName.StartsWith(
                "FOAK", StringComparison.OrdinalIgnoreCase))
            return ItemIds.OakLogs;
        if (graphicName.StartsWith(
                "FPIN", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PineLogs;
        if (graphicName.StartsWith(
                "FPAL", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PalmLogs;
        if (graphicName.StartsWith(
                "FBAM", StringComparison.OrdinalIgnoreCase))
            return ItemIds.Bamboo;
        return ItemIds.Logs;
    }

    public static string SeedItemId(string graphicName)
    {
        if (graphicName.StartsWith(
                "FPAL", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PalmSeeds;
        if (graphicName.StartsWith(
                "FPIN", StringComparison.OrdinalIgnoreCase))
            return ItemIds.PineSeeds;
        if (graphicName.StartsWith(
                "FOAK", StringComparison.OrdinalIgnoreCase))
            return ItemIds.OakSeeds;
        if (graphicName.StartsWith(
                "FJUN", StringComparison.OrdinalIgnoreCase))
            return ItemIds.JungleTreeSeeds;
        if (graphicName.StartsWith(
                "FSNO", StringComparison.OrdinalIgnoreCase))
            return ItemIds.SnowTreeSeeds;
        if (graphicName.StartsWith(
                "FBAM", StringComparison.OrdinalIgnoreCase))
            return ItemIds.BambooSeeds;
        if (graphicName.StartsWith(
                "FCAC", StringComparison.OrdinalIgnoreCase))
            return ItemIds.CactusSeeds;
        return ItemIds.TreeSeeds;
    }

    public static int FellingLogCount(int maximumHealth) =>
        WoodcuttingSkill.FellingLogCount(maximumHealth);

    internal static int InitialStickCount(
        long seed,
        int x,
        int y,
        int maximumHealth)
    {
        var rolls = maximumHealth >= 90 ? 3 :
            maximumHealth >= 55 ? 2 : 1;
        var sticks = 0;
        for (var roll = 0; roll < rolls; roll++)
        {
            if (UnitHash(seed, x, y, 4201 + roll) >= .5f)
                sticks++;
        }
        return Math.Min(sticks, 3);
    }

    private static SurfaceTreeVisual CreateVisual(
        string graphicName,
        int frameIndex)
    {
        var family = Family(graphicName);
        var variant = (family << VariantFrameBits) | frameIndex;
        return new SurfaceTreeVisual(
            variant,
            graphicName,
            frameIndex,
            MaximumHealth(graphicName),
            LogItemId(graphicName),
            SeedItemId(graphicName),
            FellingLogCount(MaximumHealth(graphicName)));
    }

    private static int Family(string graphicName)
    {
        var visible = VisibleName(graphicName);
        if (visible.StartsWith("TREE", StringComparison.OrdinalIgnoreCase) &&
            visible.Length > 4)
        {
            var generic = char.ToUpperInvariant(visible[4]) - 'A';
            if ((uint)generic < GenericFamilyCount) return generic;
        }
        return visible.ToUpperInvariant() switch
        {
            "FPAL_NN" => PalmFamily,
            "FPIN_NN" => PineFamily,
            "FOAK_NN" => OakFamily,
            "FJUN_NN" => JungleFamily,
            "FSNO_NN" => SnowFamily,
            "FBAM_NN" => BambooFamily,
            "FCAC_NN" => CactusFamily,
            _ => throw new ArgumentOutOfRangeException(nameof(graphicName))
        };
    }

    private static string GraphicName(int family) => family switch
    {
        >= 0 and < GenericFamilyCount =>
            $"TREE{(char)('A' + family)}_NN",
        PalmFamily => "FPAL_NN",
        PineFamily => "FPIN_NN",
        OakFamily => "FOAK_NN",
        JungleFamily => "FJUN_NN",
        SnowFamily => "FSNO_NN",
        BambooFamily => "FBAM_NN",
        CactusFamily => "FCAC_NN",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    private static string VisibleName(string graphicName) =>
        graphicName.EndsWith("_N0", StringComparison.OrdinalIgnoreCase)
            ? graphicName[..^2] + "NN"
            : graphicName;

    private static string GenericTree(float roll)
    {
        var variant = Math.Min((int)(roll * GenericFamilyCount),
            GenericFamilyCount - 1);
        return $"TREE{(char)('A' + variant)}_NN";
    }

    private static float UnitHash(long seed, int x, int y, int salt)
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

/// <summary>
/// Headless procedural descriptor source for overworld trees. Unsupported
/// levels are deliberately empty; underground resources have their own
/// generator and identity domain.
/// </summary>
public sealed class SurfaceTreeResourceDescriptorSource :
    IProceduralResourceDescriptorSource
{
    public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != 0 || !IsCompleteWorldChunk(chunk)) return [];

        var originX = chunk.X * WorldChunkKey.Size;
        var originY = chunk.Y * WorldChunkKey.Size;
        var result = new List<ProceduralResourceSeed>();
        for (var localY = 0; localY < WorldChunkKey.Size; localY++)
        for (var localX = 0; localX < WorldChunkKey.Size; localX++)
        {
            var worldX = originX + localX;
            var worldY = originY + localY;
            if (!SurfaceTreeCatalog.TryDescribeAt(
                    worldSeed, worldX, worldY, out var visual))
                continue;
            result.Add(new ProceduralResourceSeed(
                ProceduralResourceKey.Tree(
                    worldX, worldY, visual.Variant),
                new Vector2(worldX + .5f, worldY + .5f),
                visual.MaximumHealth,
                visual.MaximumHealth,
                SurfaceTreeCatalog.InitialStickCount(
                    worldSeed,
                    worldX,
                    worldY,
                    visual.MaximumHealth)));
        }
        return result;
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
