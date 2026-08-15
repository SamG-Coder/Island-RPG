using IslandRpg.Client;
using IslandRpg.Fishing;
using IslandRpg.Rendering;
using IslandRpg.Resources;
using IslandRpg.Simulation;

internal static class NetworkResourceHotPathChecks
{
    public static void Run()
    {
        UnknownMeansPresent();
        StickGatherDoesNotHideTree();
        ApproachRangesMatchSinglePlayer();
        SecondPassDoesNotDescribeFishOrTrees();
        RememberFishFromWorldDoesNotDescribe();
    }

    private static void UnknownMeansPresent()
    {
        var hotPath = new NetworkResourceHotPath();
        Assert(
            !hotPath.IsFishDepleted("fish:0:0:0", chunks: null) &&
            !hotPath.IsTreeDepleted(0, chunks: null) &&
            hotPath.TreeBlocks(0, chunks: null),
            "unknown network resources must stay live and blocking");
    }

    private static void StickGatherDoesNotHideTree()
    {
        const long seed = 67;
        var hotPath = new NetworkResourceHotPath();
        var chunk = new WorldChunkKey(3, -4, 0);
        var tree = new SurfaceTreeResourceDescriptorSource()
            .DescribeChunk(seed, chunk)
            .First();
        var id = ProceduralResourceIdentity.ForTree(
            seed, chunk.WorldLevel,
            tree.Key.SourceX, tree.Key.SourceY, tree.Key.Variant);
        var tileKey = WorldHoverSelection.TileKey(
            tree.Key.SourceX, tree.Key.SourceY);
        hotPath.RememberTree(tileKey, id, chunk);
        var chunks = new Dictionary<WorldChunkKey, NetworkResourceChunkState>
        {
            [chunk] = new(
                chunk,
                1,
                new Dictionary<ResourceNodeId, ResourceNodeSparseState>
                {
                    [id] = new(
                        id,
                        ResourceNodeKind.Tree,
                        chunk,
                        NodeRevision: 1,
                        Health: tree.InitialHealth,
                        Remaining: 0,
                        ReadyAtGameSeconds: 0,
                        Depleted: false)
                },
                new Dictionary<ResourceNodeId, uint> { [id] = 1 })
        };
        Assert(
            !hotPath.IsTreeDepleted(tileKey, chunks) &&
            hotPath.TreeBlocks(tileKey, chunks),
            "gathering the last stick must leave the tree standing");
    }

    private static void ApproachRangesMatchSinglePlayer()
    {
        var from = new OpenTK.Mathematics.Vector2(4, 1);
        var to = new OpenTK.Mathematics.Vector2(1, 1);
        var stand = WorldActionReach.StandOff(
            from, to, WorldActionReach.GroundPickup);
        Assert(
            WorldActionReach.GroundPickup == .46f &&
            WorldActionReach.Vegetation == .72f &&
            WorldActionReach.Mining == .82f &&
            WorldActionReach.CaveDig == .82f &&
            MathF.Abs((stand - to).Length - WorldActionReach.GroundPickup) <
            .0001f &&
            WorldActionReach.InRange(stand, to, WorldActionReach.GroundPickup) &&
            !WorldActionReach.InRange(from, to, WorldActionReach.GroundPickup),
            "multiplayer approach must walk to the single-player stand-off");
    }

    private static void SecondPassDoesNotDescribeFishOrTrees()
    {
        const long seed = 67;
        var hotPath = new NetworkResourceHotPath();
        var chunks =
            new Dictionary<WorldChunkKey, NetworkResourceChunkState>();
        var fishKeys = new List<string>();
        var treeKeys = new List<long>();

        var source = new ProceduralFishSchoolSource();
        for (var chunkY = -2; chunkY <= 2 && fishKeys.Count < 4; chunkY++)
        for (var chunkX = -2; chunkX <= 2 && fishKeys.Count < 4; chunkX++)
        {
            var chunk = new WorldChunkKey(chunkX, chunkY, 0);
            foreach (var school in source.DescribeSchools(seed, chunk))
            {
                hotPath.RememberFish(school.StableKey, school.Id, chunk);
                fishKeys.Add(school.StableKey);
                if (fishKeys.Count != 1) continue;
                var depleted = new ResourceNodeSparseState(
                    school.Id,
                    ResourceNodeKind.FishSchool,
                    chunk,
                    NodeRevision: 1,
                    Health: 0,
                    Remaining: 0,
                    ReadyAtGameSeconds: 0,
                    Depleted: true);
                chunks[chunk] = new NetworkResourceChunkState(
                    chunk,
                    1,
                    new Dictionary<ResourceNodeId, ResourceNodeSparseState>
                    {
                        [school.Id] = depleted
                    },
                    new Dictionary<ResourceNodeId, uint>
                    {
                        [school.Id] = 1
                    });
            }
        }

        var treeChunk = new WorldChunkKey(3, -4, 0);
        var trees = new SurfaceTreeResourceDescriptorSource()
            .DescribeChunk(seed, treeChunk);
        Assert(fishKeys.Count >= 3 && trees.Count >= 3,
            "prepared chunk data must include several fish and trees");
        foreach (var tree in trees.Take(4))
        {
            var tileKey = WorldHoverSelection.TileKey(
                tree.Key.SourceX, tree.Key.SourceY);
            var id = ProceduralResourceIdentity.ForTree(
                seed,
                treeChunk.WorldLevel,
                tree.Key.SourceX,
                tree.Key.SourceY,
                tree.Key.Variant);
            hotPath.RememberTree(tileKey, id, treeChunk);
            treeKeys.Add(tileKey);
        }

        var firstDepleted = hotPath.IsFishDepleted(fishKeys[0], chunks);
        var firstLive = hotPath.IsFishDepleted(fishKeys[1], chunks);
        var firstTreeBlocks = hotPath.TreeBlocks(treeKeys[0], chunks);
        Assert(
            firstDepleted && !firstLive && firstTreeBlocks,
            "the first helper pass must use remembered identity and sparse state");

        var fishDescribes =
            ProceduralFishSchoolSource.DescribeSchoolsInvocations;
        var treeDescribes = SurfaceTreeCatalog.TryDescribeAtInvocations;
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var key in fishKeys)
                _ = hotPath.IsFishDepleted(key, chunks);
            foreach (var key in treeKeys)
            {
                _ = hotPath.TreeBlocks(key, chunks);
                _ = hotPath.IsTreeDepleted(key, chunks);
            }
        }

        Assert(
            ProceduralFishSchoolSource.DescribeSchoolsInvocations ==
            fishDescribes &&
            SurfaceTreeCatalog.TryDescribeAtInvocations == treeDescribes,
            "a second pass of the shipped helpers must not describe fish or trees");
    }

    private static void RememberFishFromWorldDoesNotDescribe()
    {
        const long seed = 67;
        var source = new ProceduralFishSchoolSource();
        FishSchoolDescriptor? sample = null;
        for (var chunkY = -2; chunkY <= 2 && sample is null; chunkY++)
        for (var chunkX = -2; chunkX <= 2 && sample is null; chunkX++)
        {
            sample = source.DescribeSchools(
                    seed, new WorldChunkKey(chunkX, chunkY, 0))
                .FirstOrDefault();
        }

        Assert(sample is not null,
            "the remember-from-world fixture must find a school");
        var school = sample!;
        var describes = ProceduralFishSchoolSource.DescribeSchoolsInvocations;
        var hotPath = new NetworkResourceHotPath();
        hotPath.RememberFishFromWorld(
            seed,
            school.Chunk.WorldLevel,
            school.Position.X,
            school.Position.Y,
            (int)school.Species,
            school.StableKey);
        Assert(
            ProceduralFishSchoolSource.DescribeSchoolsInvocations ==
            describes &&
            hotPath.Fish.ContainsKey(school.StableKey) &&
            hotPath.Fish[school.StableKey].Id == school.Id,
            "remembering a loaded fish must hash identity without describing schools");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
