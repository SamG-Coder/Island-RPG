namespace IslandRpg.World;

/// <summary>
/// Builds sparse, deterministic cave features from terrain context. Features
/// are generated once with their chunk; rendering only consumes cached items.
/// </summary>
internal static class CaveFeaturePlacement
{
    public const int MaximumNodes = 18;
    private const int FeatureCellSize = 8;

    private static readonly (float X, float Y)[] ClusterOffsets =
    [
        (0, 0), (-.72f, .34f), (.68f, .42f), (-.18f, -.72f)
    ];

    public static WorldVegetation[] Generate(
        long seed,
        ChunkCoordinate coordinate,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable)
    {
        var result = new List<WorldVegetation>(MaximumNodes);
        var occupied = new List<(float X, float Y)>(MaximumNodes);
        var originX = coordinate.X * WorldChunk.Size;
        var originY = coordinate.Y * WorldChunk.Size;
        var anchors = new List<FeatureAnchor>(16);

        // Coarse feature cells create recognisable groups rather than an even
        // per-tile scatter. Four-by-four anchors cover a standard chunk.
        for (var cellY = 0; cellY < WorldChunk.Size / FeatureCellSize; cellY++)
        for (var cellX = 0; cellX < WorldChunk.Size / FeatureCellSize; cellX++)
        {
            var baseX = originX + cellX * FeatureCellSize;
            var baseY = originY + cellY * FeatureCellSize;
            var worldX = baseX + 1 + (int)(
                UndergroundResourceGenerator.Hash(
                    seed, baseX, baseY, 3011) * (FeatureCellSize - 2));
            var worldY = baseY + 1 + (int)(
                UndergroundResourceGenerator.Hash(
                    seed, baseX, baseY, 3019) * (FeatureCellSize - 2));
            var localX = worldX - originX;
            var localY = worldY - originY;
            if (!IsFloor(localX, localY, renderable)) continue;

            var wet = IsWater(localX, localY, tiles) ||
                      HasAdjacentWater(localX, localY, tiles);
            var wallDistance = WallDistance(
                localX, localY, renderable, maximum: 3);
            var roll = UndergroundResourceGenerator.Hash(
                seed, worldX, worldY, 3023);
            anchors.Add(new(
                worldX, worldY, localX, localY,
                wet, wallDistance, roll));
        }

        var ruins = anchors.Where(anchor =>
                !anchor.Wet &&
                anchor.WallDistance > 1 &&
                anchor.Roll < .003f &&
                HasOpenFloor(
                    anchor.LocalX, anchor.LocalY,
                    renderable, radius: 3))
            .ToArray();
        foreach (var ruin in ruins)
            AddRareRuin(
                seed, ruin.WorldX, ruin.WorldY, result, occupied);

        foreach (var anchor in anchors)
        {
            if (result.Count >= MaximumNodes) break;
            if (ruins.Any(ruin =>
                    DistanceSquared(
                        ruin.WorldX, ruin.WorldY,
                        anchor.WorldX, anchor.WorldY) < 25f))
                continue;
            if (anchor.Wet)
                AddWetGrowth(
                    seed, anchor.WorldX, anchor.WorldY,
                    anchor.LocalX, anchor.LocalY,
                    tiles, renderable, result, occupied);
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
                         anchor.LocalX, anchor.LocalY,
                         renderable, radius: 2))
                AddChamberFormation(
                    seed, anchor.WorldX, anchor.WorldY,
                    anchor.LocalX, anchor.LocalY,
                    renderable, result, occupied);
            else if (HasOpenFloor(
                         anchor.LocalX, anchor.LocalY,
                         renderable, radius: 2))
                AddCeilingFissure(
                    seed, anchor.WorldX, anchor.WorldY, result, occupied);
        }

        // A few tiny edge-biased rocks break up otherwise empty corridors
        // without becoming another expensive feature pass.
        for (var index = 0;
             index < tiles.Count && result.Count < MaximumNodes;
             index++)
        {
            if (!renderable[index] ||
                tiles[index].Biome is Biome.ShallowWater or Biome.RiverWater)
                continue;
            var localX = index % WorldChunk.Size;
            var localY = index / WorldChunk.Size;
            if (WallDistance(localX, localY, renderable, maximum: 2) != 1)
                continue;
            var tile = tiles[index];
            if (ruins.Any(ruin =>
                    DistanceSquared(
                        ruin.WorldX, ruin.WorldY,
                        tile.X + .5f, tile.Y + .5f) < 25f))
                continue;
            if (UndergroundResourceGenerator.Hash(
                    seed, tile.X, tile.Y, 3041) > .012f)
                continue;
            Add(
                seed, tile.X + .5f, tile.Y + .5f,
                "ROCKX_NN", result, occupied);
        }
        result.RemoveAll(item =>
        {
            var localX = (int)MathF.Floor(item.X) - originX;
            var localY = (int)MathF.Floor(item.Y) - originY;
            return !IsFloor(localX, localY, renderable);
        });
        return result.ToArray();
    }

    private static void AddWetGrowth(
        long seed,
        int x,
        int y,
        int localX,
        int localY,
        IReadOnlyList<IslandTile> tiles,
        IReadOnlyList<bool> renderable,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        var water = IsWater(localX, localY, tiles);
        var first = water ? 2 : 0; // algae in water, moss on its bank
        AddGrowth(x + .5f, y + .5f, first, result, occupied);
        var secondary = Select<int>(
            seed, x, y, 3053,
            water ? new[] { 2, 4 } : [0, 1, 3]);
        AddGrowth(
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            secondary, result, occupied);
        if (!water && WallDistance(
                localX, localY, renderable, maximum: 2) <= 1)
            AddGrowth(
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                3, result, occupied);
    }

    private static void AddWallFeature(
        long seed,
        int x,
        int y,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        var rock = Select(
            seed, x, y, 3061,
            ["ROCKF1_NN", "ROCKF1_NN", "ROCKF2_NN", "ROCKX_NN"]);
        Add(seed, x + .5f, y + .5f, rock, result, occupied);
        Add(
            seed,
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            "ROCKX_NN", result, occupied);
        if (UndergroundResourceGenerator.Hash(seed, x, y, 3067) < .34f)
            AddGrowth(
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                3, result, occupied);
    }

    private static void AddOreVein(
        long seed,
        int x,
        int y,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        var ore = Select(
            seed, x, y, 3079,
            [
                UndergroundResourceGenerator.Coal,
                UndergroundResourceGenerator.Tin,
                UndergroundResourceGenerator.Copper,
                UndergroundResourceGenerator.Iron,
                "STONM_NN"
            ]);
        Add(seed, x + .5f, y + .5f, ore, result, occupied);
        Add(
            seed,
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            "OREM_NN", result, occupied);
        if (UndergroundResourceGenerator.Hash(seed, x, y, 3083) < .55f)
            Add(
                seed,
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                "OREM_NN", result, occupied);
    }

    private static void AddOrganicPocket(
        long seed,
        int x,
        int y,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        var remains = UndergroundResourceGenerator.Hash(
            seed, x, y, 3089) < .84f ? "SKEL_NN" : "SKELA_NN";
        Add(seed, x + .5f, y + .5f, remains, result, occupied);
        AddGrowth(
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            8, result, occupied);
        if (UndergroundResourceGenerator.Hash(seed, x, y, 3109) < .46f)
            AddGrowth(
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                Select(seed, x, y, 3119, [6, 7]),
                result, occupied);
    }

    private static void AddCeilingFissure(
        long seed,
        int x,
        int y,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        AddGrowth(x + .5f, y + .5f, 9, result, occupied);
        AddGrowth(
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            0, result, occupied);
        if (UndergroundResourceGenerator.Hash(seed, x, y, 3121) < .45f)
            AddGrowth(
                x + ClusterOffsets[2].X,
                y + ClusterOffsets[2].Y,
                5, result, occupied);
    }

    private static void AddChamberFormation(
        long seed,
        int x,
        int y,
        int localX,
        int localY,
        IReadOnlyList<bool> renderable,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        var large = UndergroundResourceGenerator.Hash(
            seed, x, y, 3127) < .14f &&
            HasOpenFloor(localX, localY, renderable, radius: 3);
        Add(
            seed, x + .5f, y + .5f,
            large ? "ROCKF3_NN" : "ROCK2_NN",
            result, occupied);
        if (!large)
            Add(
                seed,
                x + ClusterOffsets[1].X,
                y + ClusterOffsets[1].Y,
                "ROCKX_NN", result, occupied);
    }

    private static void AddRareRuin(
        long seed,
        int x,
        int y,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied)
    {
        Add(seed, x + .5f, y + .5f, "RUINS_NN", result, occupied);
        Add(
            seed,
            x + ClusterOffsets[1].X,
            y + ClusterOffsets[1].Y,
            "SKEL_NN", result, occupied);
        AddGrowth(
            x + ClusterOffsets[2].X,
            y + ClusterOffsets[2].Y,
            8, result, occupied);
    }

    private static void AddGrowth(
        float x,
        float y,
        int cell,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied) =>
        Add(
            0, x, y, UndergroundResourceGenerator.Growth,
            result, occupied, cell);

    private static void Add(
        long seed,
        float x,
        float y,
        string graphic,
        List<WorldVegetation> result,
        List<(float X, float Y)> occupied,
        int? fixedFrame = null)
    {
        if (result.Count >= MaximumNodes ||
            occupied.Any(value =>
                DistanceSquared(value.X, value.Y, x, y) < .18f))
            return;
        var variants = UndergroundResourceGenerator.VariantCount(graphic);
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var frame = fixedFrame ?? Math.Min(
            (int)(UndergroundResourceGenerator.Hash(
                seed, tileX, tileY, 3137) * variants),
            variants - 1);
        result.Add(new(
            x, y, graphic, frame,
            WorldVegetationKind.Shrub, false));
        occupied.Add((x, y));
    }

    private static bool IsFloor(
        int x, int y, IReadOnlyList<bool> renderable) =>
        (uint)x < WorldChunk.Size &&
        (uint)y < WorldChunk.Size &&
        renderable[y * WorldChunk.Size + x];

    private static bool IsWater(
        int x, int y, IReadOnlyList<IslandTile> tiles) =>
        (uint)x < WorldChunk.Size &&
        (uint)y < WorldChunk.Size &&
        tiles[y * WorldChunk.Size + x].Biome is
            Biome.ShallowWater or Biome.RiverWater;

    private static bool HasAdjacentWater(
        int x, int y, IReadOnlyList<IslandTile> tiles)
    {
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        for (var offsetX = -1; offsetX <= 1; offsetX++)
            if ((offsetX != 0 || offsetY != 0) &&
                IsWater(x + offsetX, y + offsetY, tiles))
                return true;
        return false;
    }

    private static int WallDistance(
        int x,
        int y,
        IReadOnlyList<bool> renderable,
        int maximum)
    {
        for (var distance = 1; distance <= maximum; distance++)
        for (var offsetY = -distance; offsetY <= distance; offsetY++)
        for (var offsetX = -distance; offsetX <= distance; offsetX++)
            if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) == distance &&
                !IsFloor(x + offsetX, y + offsetY, renderable))
                return distance;
        return maximum + 1;
    }

    private static bool HasOpenFloor(
        int x,
        int y,
        IReadOnlyList<bool> renderable,
        int radius)
    {
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        for (var offsetX = -radius; offsetX <= radius; offsetX++)
            if (!IsFloor(x + offsetX, y + offsetY, renderable))
                return false;
        return true;
    }

    private static T Select<T>(
        long seed, int x, int y, int salt, IReadOnlyList<T> values) =>
        values[Math.Min(
            (int)(UndergroundResourceGenerator.Hash(
                seed, x, y, salt) * values.Count),
            values.Count - 1)];

    private static float DistanceSquared(
        float ax, float ay, float bx, float by)
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
