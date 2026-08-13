using System.Numerics;
using IslandRpg.Gameplay;
using IslandRpg.Simulation;

namespace IslandRpg.Resources;

/// <summary>
/// Stable numeric identity of a mineable cave visual. The value is part of
/// procedural node identity; adding a new kind must not reorder existing
/// entries.
/// </summary>
public enum UndergroundMiningVariant : byte
{
    Coal = 1,
    Tin = 2,
    Copper = 3,
    Iron = 4,
    StoneDeposit = 5,
    StoneOutcrop = 6,
    JaggedRock = 7,
    RockFormation = 8,
    LayeredRock = 9,
    StonePillar = 10,
    MassiveStoneFormation = 11
}

/// <summary>
/// Canonical gameplay and visual policy for a generated underground mining
/// node. Source coordinates and ordinal identify the placement independently
/// of rounded floats or display strings.
/// </summary>
public readonly record struct UndergroundMiningVisual(
    UndergroundMiningVariant Variant,
    string GraphicName,
    string DisplayName,
    int MaximumHealth,
    string? RewardItemId,
    int CompletionExperience);

public sealed record UndergroundFeatureDescriptor(
    int SourceTileX,
    int SourceTileY,
    int Ordinal,
    Vector2 Position,
    string GraphicName,
    int FrameIndex);

/// <summary>
/// Canonical cave scenery generation and mining definitions shared by the
/// renderer-backed solo world and the headless server catalog.
/// </summary>
public static class UndergroundMiningCatalog
{
    public const int MaximumNodesPerChunk = 18;

    public const string CoalGraphic = "CAVE_ORE_COAL";
    public const string TinGraphic = "CAVE_ORE_TIN";
    public const string CopperGraphic = "CAVE_ORE_COPPER";
    public const string IronGraphic = "CAVE_ORE_IRON";
    public const string GrowthGraphic = "CAVE_GROWTH";

    private const int FeatureCellSize = 8;

    private static readonly (float X, float Y)[] ClusterOffsets =
    [
        (0, 0), (-.72f, .34f), (.68f, .42f), (-.18f, -.72f)
    ];

    public static bool TryGetVisual(
        int variant,
        out UndergroundMiningVisual visual)
    {
        visual = (UndergroundMiningVariant)variant switch
        {
            UndergroundMiningVariant.Coal => new(
                UndergroundMiningVariant.Coal,
                CoalGraphic, "coal deposit", 75, ItemIds.Coal, 24),
            UndergroundMiningVariant.Tin => new(
                UndergroundMiningVariant.Tin,
                TinGraphic, "tin deposit", 85, ItemIds.TinOre, 28),
            UndergroundMiningVariant.Copper => new(
                UndergroundMiningVariant.Copper,
                CopperGraphic, "copper deposit", 100,
                ItemIds.CopperOre, 34),
            UndergroundMiningVariant.Iron => new(
                UndergroundMiningVariant.Iron,
                IronGraphic, "iron deposit", 125, ItemIds.IronOre, 42),
            UndergroundMiningVariant.StoneDeposit => new(
                UndergroundMiningVariant.StoneDeposit,
                "STONM_NN", "stone deposit", 95, ItemIds.LargeRock, 26),
            UndergroundMiningVariant.StoneOutcrop => new(
                UndergroundMiningVariant.StoneOutcrop,
                "OREM_NN", "stone outcrop", 80, ItemIds.LargeRock, 22),
            UndergroundMiningVariant.JaggedRock => new(
                UndergroundMiningVariant.JaggedRock,
                "ROCKX_NN", "jagged rock", 135, null, 40),
            UndergroundMiningVariant.RockFormation => new(
                UndergroundMiningVariant.RockFormation,
                "ROCK2_NN", "rock formation", 180, null, 55),
            UndergroundMiningVariant.LayeredRock => new(
                UndergroundMiningVariant.LayeredRock,
                "ROCKF1_NN", "layered rock", 150, null, 46),
            UndergroundMiningVariant.StonePillar => new(
                UndergroundMiningVariant.StonePillar,
                "ROCKF2_NN", "stone pillar", 210, null, 65),
            UndergroundMiningVariant.MassiveStoneFormation => new(
                UndergroundMiningVariant.MassiveStoneFormation,
                "ROCKF3_NN", "massive stone formation", 320, null, 95),
            _ => default
        };
        return visual.MaximumHealth > 0;
    }

    public static bool TryGetVisual(
        string graphicName,
        out UndergroundMiningVisual visual)
    {
        visual = default;
        if (string.IsNullOrWhiteSpace(graphicName)) return false;
        foreach (var variant in Enum.GetValues<UndergroundMiningVariant>())
        {
            if (!TryGetVisual((int)variant, out var candidate) ||
                !candidate.GraphicName.Equals(
                    graphicName, StringComparison.OrdinalIgnoreCase))
                continue;
            visual = candidate;
            return true;
        }
        return false;
    }

    public static int VariantCount(string graphicName) => graphicName switch
    {
        "STONM_NN" or "OREM_NN" or CoalGraphic or TinGraphic or
            CopperGraphic or IronGraphic => 7,
        "ROCKX_NN" or "ROCK2_NN" => 6,
        "ROCKF1_NN" => 4,
        "ROCKF2_NN" or "SKELA_NN" => 2,
        "ROCKF3_NN" => 1,
        "SKEL_NN" => 15,
        "RUINS_NN" => 3,
        GrowthGraphic => 10,
        _ => 0
    };

    internal static IReadOnlyList<UndergroundFeatureDescriptor> Generate(
        long seed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != -1 || !CompleteChunk(chunk)) return [];

        var originX = chunk.X * WorldChunkKey.Size;
        var originY = chunk.Y * WorldChunkKey.Size;
        var cave = new ProceduralUndergroundTerrain.SamplingContext(seed);
        var floor = new bool[WorldChunkKey.Size * WorldChunkKey.Size];
        var water = new bool[floor.Length];
        for (var localY = 0; localY < WorldChunkKey.Size; localY++)
        for (var localX = 0; localX < WorldChunkKey.Size; localX++)
        {
            var index = localY * WorldChunkKey.Size + localX;
            var worldX = originX + localX;
            var worldY = originY + localY;
            floor[index] = ProceduralUndergroundTerrain.TileIntersectsCave(
                cave, worldX, worldY);
            water[index] = ProceduralUndergroundTerrain.MaterialAt(
                seed, worldX, worldY) is
                ProceduralUndergroundTerrain.Material.ShallowWater or
                ProceduralUndergroundTerrain.Material.RiverWater;
        }

        return Generate(seed, chunk, floor, water);
    }

    internal static IReadOnlyList<UndergroundFeatureDescriptor> Generate(
        long seed,
        WorldChunkKey chunk,
        IReadOnlyList<bool> floor,
        IReadOnlyList<bool> water)
    {
        if (chunk.WorldLevel != -1 || !CompleteChunk(chunk) ||
            floor.Count != WorldChunkKey.Size * WorldChunkKey.Size ||
            water.Count != floor.Count)
            return [];

        var result = new List<UndergroundFeatureDescriptor>(
            MaximumNodesPerChunk);
        var occupied = new List<(float X, float Y)>(MaximumNodesPerChunk);
        var originX = chunk.X * WorldChunkKey.Size;
        var originY = chunk.Y * WorldChunkKey.Size;
        var anchors = new List<FeatureAnchor>(16);
        for (var cellY = 0;
             cellY < WorldChunkKey.Size / FeatureCellSize;
             cellY++)
        for (var cellX = 0;
             cellX < WorldChunkKey.Size / FeatureCellSize;
             cellX++)
        {
            var baseX = originX + cellX * FeatureCellSize;
            var baseY = originY + cellY * FeatureCellSize;
            var worldX = baseX + 1 + (int)(
                UnitHash(seed, baseX, baseY, 3011) *
                (FeatureCellSize - 2));
            var worldY = baseY + 1 + (int)(
                UnitHash(seed, baseX, baseY, 3019) *
                (FeatureCellSize - 2));
            var localX = worldX - originX;
            var localY = worldY - originY;
            if (!IsFloor(localX, localY, floor)) continue;
            var wet = IsWater(localX, localY, water) ||
                      HasAdjacentWater(localX, localY, water);
            anchors.Add(new(
                worldX,
                worldY,
                localX,
                localY,
                wet,
                WallDistance(localX, localY, floor, maximum: 3),
                UnitHash(seed, worldX, worldY, 3023)));
        }

        var ruins = anchors.Where(anchor =>
                !anchor.Wet &&
                anchor.WallDistance > 1 &&
                anchor.Roll < .003f &&
                HasOpenFloor(
                    anchor.LocalX, anchor.LocalY, floor, radius: 3))
            .ToArray();
        foreach (var ruin in ruins)
            AddRareRuin(seed, ruin.WorldX, ruin.WorldY, result, occupied);

        foreach (var anchor in anchors)
        {
            if (result.Count >= MaximumNodesPerChunk) break;
            if (ruins.Any(ruin => DistanceSquared(
                    ruin.WorldX,
                    ruin.WorldY,
                    anchor.WorldX,
                    anchor.WorldY) < 25f))
                continue;
            if (anchor.Wet)
                AddWetGrowth(
                    seed, anchor.WorldX, anchor.WorldY,
                    anchor.LocalX, anchor.LocalY,
                    water, floor, result, occupied);
            else if (anchor.WallDistance <= 1)
                AddWallFeature(
                    seed, anchor.WorldX, anchor.WorldY, result, occupied);
            else if (anchor.Roll < .58f)
                AddOreVein(
                    seed, anchor.WorldX, anchor.WorldY, result, occupied);
            else if (anchor.Roll < .91f)
                AddOrganicPocket(
                    seed, anchor.WorldX, anchor.WorldY, result, occupied);
            else if (anchor.Roll < .97f &&
                     HasOpenFloor(
                         anchor.LocalX, anchor.LocalY, floor, radius: 2))
                AddChamberFormation(
                    seed, anchor.WorldX, anchor.WorldY,
                    anchor.LocalX, anchor.LocalY,
                    floor, result, occupied);
            else if (HasOpenFloor(
                         anchor.LocalX, anchor.LocalY, floor, radius: 2))
                AddCeilingFissure(
                    seed, anchor.WorldX, anchor.WorldY, result, occupied);
        }

        for (var localY = 0;
             localY < WorldChunkKey.Size &&
             result.Count < MaximumNodesPerChunk;
             localY++)
        for (var localX = 0;
             localX < WorldChunkKey.Size &&
             result.Count < MaximumNodesPerChunk;
             localX++)
        {
            var index = localY * WorldChunkKey.Size + localX;
            if (!floor[index] || water[index] ||
                WallDistance(localX, localY, floor, maximum: 2) != 1)
                continue;
            var worldX = originX + localX;
            var worldY = originY + localY;
            if (ruins.Any(ruin => DistanceSquared(
                    ruin.WorldX,
                    ruin.WorldY,
                    worldX + .5f,
                    worldY + .5f) < 25f) ||
                UnitHash(seed, worldX, worldY, 3041) > .012f)
                continue;
            Add(
                seed,
                worldX + .5f,
                worldY + .5f,
                "ROCKX_NN",
                result,
                occupied);
        }

        result.RemoveAll(item =>
        {
            var localX = (int)MathF.Floor(item.Position.X) - originX;
            var localY = (int)MathF.Floor(item.Position.Y) - originY;
            return !IsFloor(localX, localY, floor);
        });
        // Ordinal addresses the final feature stream consumed by the solo
        // chunk. Reindex after floor filtering so both paths retain the same
        // typed identity even when a clustered decoration is discarded.
        return result
            .Select((item, ordinal) => item with { Ordinal = ordinal })
            .ToArray();
    }

    private static void AddWetGrowth(
        long seed,
        int x,
        int y,
        int localX,
        int localY,
        IReadOnlyList<bool> water,
        IReadOnlyList<bool> floor,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        var inWater = IsWater(localX, localY, water);
        AddGrowth(x + .5f, y + .5f, inWater ? 2 : 0, result, occupied);
        IReadOnlyList<int> secondaryFrames = inWater
            ? [2, 4]
            : [0, 1, 3];
        var secondary = Select(
            seed, x, y, 3053, secondaryFrames);
        AddGrowth(
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            secondary,
            result,
            occupied);
        if (!inWater && WallDistance(
                localX, localY, floor, maximum: 2) <= 1)
            AddGrowth(
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                3,
                result,
                occupied);
    }

    private static void AddWallFeature(
        long seed,
        int x,
        int y,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        var rock = Select(
            seed, x, y, 3061,
            ["ROCKF1_NN", "ROCKF1_NN", "ROCKF2_NN", "ROCKX_NN"]);
        Add(seed, x + .5f, y + .5f, rock, result, occupied);
        Add(seed, x + ClusterOffsets[1].X, y + ClusterOffsets[1].Y,
            "ROCKX_NN", result, occupied);
        if (UnitHash(seed, x, y, 3067) < .34f)
            AddGrowth(x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y, 3, result, occupied);
    }

    private static void AddOreVein(
        long seed,
        int x,
        int y,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        var ore = Select(seed, x, y, 3079,
            [CoalGraphic, TinGraphic, CopperGraphic, IronGraphic, "STONM_NN"]);
        Add(seed, x + .5f, y + .5f, ore, result, occupied);
        Add(seed, x + ClusterOffsets[1].X, y + ClusterOffsets[1].Y,
            "OREM_NN", result, occupied);
        if (UnitHash(seed, x, y, 3083) < .55f)
            Add(seed, x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y, "OREM_NN", result, occupied);
    }

    private static void AddOrganicPocket(
        long seed,
        int x,
        int y,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        var remains = UnitHash(seed, x, y, 3089) < .84f
            ? "SKEL_NN"
            : "SKELA_NN";
        Add(seed, x + .5f, y + .5f, remains, result, occupied);
        AddGrowth(x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y, 8, result, occupied);
        if (UnitHash(seed, x, y, 3109) < .46f)
            AddGrowth(x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                Select(seed, x, y, 3119, [6, 7]), result, occupied);
    }

    private static void AddCeilingFissure(
        long seed,
        int x,
        int y,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        AddGrowth(x + .5f, y + .5f, 9, result, occupied);
        AddGrowth(x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y, 0, result, occupied);
        if (UnitHash(seed, x, y, 3121) < .45f)
            AddGrowth(x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y, 5, result, occupied);
    }

    private static void AddChamberFormation(
        long seed,
        int x,
        int y,
        int localX,
        int localY,
        IReadOnlyList<bool> floor,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        var large = UnitHash(seed, x, y, 3127) < .14f &&
                    HasOpenFloor(localX, localY, floor, radius: 3);
        Add(seed, x + .5f, y + .5f,
            large ? "ROCKF3_NN" : "ROCK2_NN", result, occupied);
        if (!large)
            Add(seed, x + ClusterOffsets[1].X,
                y + ClusterOffsets[1].Y, "ROCKX_NN", result, occupied);
    }

    private static void AddRareRuin(
        long seed,
        int x,
        int y,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied)
    {
        Add(seed, x + .5f, y + .5f, "RUINS_NN", result, occupied);
        Add(seed, x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y, "SKEL_NN", result, occupied);
        AddGrowth(x + ClusterOffsets[2].X,
            y + ClusterOffsets[2].Y, 8, result, occupied);
    }

    private static void AddGrowth(
        float x,
        float y,
        int frame,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied) =>
        Add(0, x, y, GrowthGraphic, result, occupied, frame);

    private static void Add(
        long seed,
        float x,
        float y,
        string graphic,
        List<UndergroundFeatureDescriptor> result,
        List<(float X, float Y)> occupied,
        int? fixedFrame = null)
    {
        if (result.Count >= MaximumNodesPerChunk ||
            occupied.Any(value =>
                DistanceSquared(value.X, value.Y, x, y) < .18f))
            return;
        var variants = VariantCount(graphic);
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var frame = fixedFrame ?? Math.Min(
            (int)(UnitHash(seed, tileX, tileY, 3137) * variants),
            variants - 1);
        result.Add(new(
            tileX,
            tileY,
            result.Count,
            new Vector2(x, y),
            graphic,
            frame));
        occupied.Add((x, y));
    }

    private static bool IsFloor(
        int x,
        int y,
        IReadOnlyList<bool> floor) =>
        (uint)x < WorldChunkKey.Size &&
        (uint)y < WorldChunkKey.Size &&
        floor[y * WorldChunkKey.Size + x];

    private static bool IsWater(
        int x,
        int y,
        IReadOnlyList<bool> water) =>
        (uint)x < WorldChunkKey.Size &&
        (uint)y < WorldChunkKey.Size &&
        water[y * WorldChunkKey.Size + x];

    private static bool HasAdjacentWater(
        int x,
        int y,
        IReadOnlyList<bool> water)
    {
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        for (var offsetX = -1; offsetX <= 1; offsetX++)
            if ((offsetX != 0 || offsetY != 0) &&
                IsWater(x + offsetX, y + offsetY, water))
                return true;
        return false;
    }

    private static int WallDistance(
        int x,
        int y,
        IReadOnlyList<bool> floor,
        int maximum)
    {
        for (var distance = 1; distance <= maximum; distance++)
        for (var offsetY = -distance; offsetY <= distance; offsetY++)
        for (var offsetX = -distance; offsetX <= distance; offsetX++)
            if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) == distance &&
                !IsFloor(x + offsetX, y + offsetY, floor))
                return distance;
        return maximum + 1;
    }

    private static bool HasOpenFloor(
        int x,
        int y,
        IReadOnlyList<bool> floor,
        int radius)
    {
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        for (var offsetX = -radius; offsetX <= radius; offsetX++)
            if (!IsFloor(x + offsetX, y + offsetY, floor)) return false;
        return true;
    }

    private static T Select<T>(
        long seed,
        int x,
        int y,
        int salt,
        IReadOnlyList<T> values) =>
        values[Math.Min(
            (int)(UnitHash(seed, x, y, salt) * values.Count),
            values.Count - 1)];

    internal static float UnitHash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value = (ulong)(seed + salt);
            value ^= (ulong)(long)x * 0x9E3779B185EBCA87UL;
            value ^= (ulong)(long)y * 0xC2B2AE3D27D4EB4FUL;
            value ^= value >> 29;
            value *= 0x165667B19E3779F9UL;
            value ^= value >> 32;
            return (value & 0xFFFFFF) / 16777216f;
        }
    }

    private static bool CompleteChunk(WorldChunkKey chunk)
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

    private static float DistanceSquared(
        float ax,
        float ay,
        float bx,
        float by)
    {
        var x = ax - bx;
        var y = ay - by;
        return x * x + y * y;
    }

    private sealed record FeatureAnchor(
        int WorldX,
        int WorldY,
        int LocalX,
        int LocalY,
        bool Wet,
        int WallDistance,
        float Roll);
}

/// <summary>
/// Headless mining descriptor source. Non-mineable cave scenery stays in the
/// shared feature stream for exact solo parity but is intentionally omitted
/// from authoritative resource descriptors.
/// </summary>
public sealed class UndergroundMiningResourceDescriptorSource :
    IProceduralResourceDescriptorSource
{
    public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk)
    {
        if (chunk.WorldLevel != -1) return [];
        var features = UndergroundMiningCatalog.Generate(worldSeed, chunk);
        var result = new List<ProceduralResourceSeed>(features.Count);
        foreach (var feature in features)
        {
            if (!UndergroundMiningCatalog.TryGetVisual(
                    feature.GraphicName, out var visual))
                continue;
            result.Add(new ProceduralResourceSeed(
                ProceduralResourceKey.Mining(
                    feature.SourceTileX,
                    feature.SourceTileY,
                    feature.Ordinal,
                    (int)visual.Variant),
                feature.Position,
                visual.MaximumHealth,
                visual.MaximumHealth));
        }
        return result;
    }
}
