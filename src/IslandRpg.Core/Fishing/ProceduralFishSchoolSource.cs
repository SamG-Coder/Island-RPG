using System.Numerics;
using IslandRpg.Resources;
using IslandRpg.Simulation;
using IslandRpg.World;

namespace IslandRpg.Fishing;

/// <summary>
/// Chunk-local deterministic fish generator shared by rendering and the
/// authoritative resource catalog. Schools never regenerate in the current
/// ruleset; only their sparse remaining count is persisted.
/// </summary>
public sealed class ProceduralFishSchoolSource :
    IProceduralResourceDescriptorSource
{
    public const int MaximumPerChunk = 12;
    public const int MinimumBeginnerShoreDistance = 1;
    public const int MaximumBeginnerShoreDistance = 3;
    public const double SchoolRegrowthGameSeconds = 0;

    public IReadOnlyList<ProceduralResourceSeed> DescribeChunk(
        long worldSeed,
        WorldChunkKey chunk) =>
        DescribeSchools(worldSeed, chunk)
            .Select(value => new ProceduralResourceSeed(
                ProceduralResourceKey.Fish(
                    (int)MathF.Floor(value.Position.X),
                    (int)MathF.Floor(value.Position.Y),
                    (int)value.Species),
                value.Position,
                InitialRemaining: value.SchoolSize,
                RegrowthGameSeconds: value.RegrowthGameSeconds))
            .ToArray();

    public static int DescribeSchoolsInvocations { get; set; }

    public IReadOnlyList<FishSchoolDescriptor> DescribeSchools(
        long worldSeed,
        WorldChunkKey chunk)
    {
        DescribeSchoolsInvocations++;
        var originLongX = (long)chunk.X * WorldChunkKey.Size;
        var originLongY = (long)chunk.Y * WorldChunkKey.Size;
        if (chunk.WorldLevel != 0 ||
            originLongX < ProceduralResourceIdentity.MinimumCoordinate ||
            originLongY < ProceduralResourceIdentity.MinimumCoordinate ||
            originLongX + WorldChunkKey.Size >
                ProceduralResourceIdentity.MaximumCoordinate ||
            originLongY + WorldChunkKey.Size >
                ProceduralResourceIdentity.MaximumCoordinate)
            return [];

        var candidates = new List<Candidate>();
        var beginnerCandidates = new List<BeginnerCandidate>();
        var classifications = new Dictionary<
            (int X, int Y), ProceduralSurfaceTerrain.Classification>();
        var originX = (int)originLongX;
        var originY = (int)originLongY;
        for (var localY = 0; localY < WorldChunkKey.Size; localY++)
        for (var localX = 0; localX < WorldChunkKey.Size; localX++)
        {
            var tileX = originX + localX;
            var tileY = originY + localY;
            var classification = ClassificationAt(tileX, tileY);
            if (!IsFishWater(classification.Material)) continue;

            var shoreDistance = LocalDistanceFromShore(tileX, tileY);
            if (shoreDistance is >= MinimumBeginnerShoreDistance and
                    <= MaximumBeginnerShoreDistance)
            {
                var beginner = Create(
                    worldSeed, chunk, tileX, tileY,
                    FishingRules.Profile(FishSpecies.ShoreMinnows));
                beginnerCandidates.Add(new(
                    shoreDistance,
                    Hash(worldSeed, tileX, tileY, 5189),
                    beginner));
            }

            var hasNearbySand = shoreDistance <= 2;
            var available = FishingRules.CatchProfiles
                .Select(profile => (
                    Profile: profile,
                    Chance: Chance(
                        profile.Species,
                        classification.Material,
                        classification.Region)))
                .Where(candidate => candidate.Chance > 0 &&
                    (candidate.Profile.Species == FishSpecies.ShoreMinnows
                        ? shoreDistance is >=
                                MinimumBeginnerShoreDistance and <=
                            MaximumBeginnerShoreDistance
                        : !hasNearbySand))
                .ToArray();
            if (available.Length == 0) continue;

            var totalChance = available.Sum(value => value.Chance);
            var roll = Hash(worldSeed, tileX, tileY, 5101);
            if (roll >= totalChance) continue;
            var selection = roll;
            var selected = available[^1].Profile;
            foreach (var candidate in available)
            {
                selection -= candidate.Chance;
                if (selection >= 0) continue;
                selected = candidate.Profile;
                break;
            }

            candidates.Add(new(
                Hash(worldSeed, tileX, tileY, 5167),
                Create(worldSeed, chunk, tileX, tileY, selected)));
        }

        var selectedSchools = candidates
            .OrderBy(value => value.Priority)
            .Take(MaximumPerChunk)
            .Select(value => value.School)
            .ToList();
        if (beginnerCandidates.Count > 0 &&
            selectedSchools.All(value =>
                value.Species != FishSpecies.ShoreMinnows))
        {
            var guaranteed = beginnerCandidates
                .OrderBy(value => value.Distance)
                .ThenBy(value => value.Priority)
                .First().School;
            if (selectedSchools.Count >= MaximumPerChunk)
                selectedSchools.RemoveAt(selectedSchools.Count - 1);
            selectedSchools.Add(guaranteed);
        }
        return selectedSchools;

        ProceduralSurfaceTerrain.Classification ClassificationAt(
            int x,
            int y)
        {
            if (classifications.TryGetValue((x, y), out var classification))
                return classification;
            classification = ProceduralSurfaceTerrain.ClassifyAt(
                worldSeed, x, y);
            classifications.Add((x, y), classification);
            return classification;
        }

        int LocalDistanceFromShore(int x, int y)
        {
            for (var distance = 1;
                 distance <= MaximumBeginnerShoreDistance;
                 distance++)
            for (var offsetY = -distance; offsetY <= distance; offsetY++)
            for (var offsetX = -distance; offsetX <= distance; offsetX++)
            {
                if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) != distance)
                    continue;
                var material = ClassificationAt(
                    x + offsetX, y + offsetY).Material;
                if (material is ProceduralSurfaceTerrain.Material.Beach or
                    ProceduralSurfaceTerrain.Material.DesertSand)
                    return distance;
            }
            return int.MaxValue;
        }
    }

    public static int DistanceFromShore(
        long worldSeed,
        int tileX,
        int tileY,
        int maximumDistance = MaximumBeginnerShoreDistance)
    {
        if (maximumDistance <= 0) return int.MaxValue;
        maximumDistance = Math.Min(maximumDistance, 64);
        for (var distance = 1; distance <= maximumDistance; distance++)
        for (var offsetY = -distance; offsetY <= distance; offsetY++)
        for (var offsetX = -distance; offsetX <= distance; offsetX++)
        {
            if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) != distance)
                continue;
            var material = ProceduralSurfaceTerrain.ClassifyAt(
                worldSeed, tileX + offsetX, tileY + offsetY).Material;
            if (material is ProceduralSurfaceTerrain.Material.Beach or
                ProceduralSurfaceTerrain.Material.DesertSand)
                return distance;
        }
        return int.MaxValue;
    }

    public static bool IsValidHabitat(
        long worldSeed,
        FishSpecies species,
        int tileX,
        int tileY)
    {
        var tile = ProceduralSurfaceTerrain.ClassifyAt(
            worldSeed, tileX, tileY);
        return Chance(species, tile.Material, tile.Region) > 0 ||
               species == FishSpecies.ShoreMinnows &&
               tile.Material == ProceduralSurfaceTerrain.Material.DeepWater;
    }

    private static FishSchoolDescriptor Create(
        long worldSeed,
        WorldChunkKey chunk,
        int tileX,
        int tileY,
        FishSpeciesProfile profile)
    {
        var position = new Vector2(
            tileX + .16f + Hash(worldSeed, tileX, tileY, 5113) * .68f,
            tileY + .16f + Hash(worldSeed, tileX, tileY, 5119) * .68f);
        var animationOffset =
            (int)(Hash(worldSeed, tileX, tileY, 5147) *
                  profile.FrameCount) % profile.FrameCount;
        return new(
            ProceduralResourceIdentity.ForFish(
                worldSeed, chunk.WorldLevel, tileX, tileY,
                (int)profile.Species),
            chunk,
            position,
            profile.Species,
            animationOffset,
            profile.ItemId,
            profile.RequiredLevel,
            profile.RequiredNetPower,
            profile.Experience,
            profile.SchoolSize,
            SchoolRegrowthGameSeconds);
    }

    private static float Chance(
        FishSpecies species,
        ProceduralSurfaceTerrain.Material material,
        ProceduralSurfaceTerrain.Region region) => species switch
        {
            FishSpecies.ShoreMinnows
                when material is ProceduralSurfaceTerrain.Material.ShallowWater
                    or ProceduralSurfaceTerrain.Material.MangroveShallows =>
                .040f,
            FishSpecies.RiverPerch
                when material == ProceduralSurfaceTerrain.Material.RiverWater ||
                     region == ProceduralSurfaceTerrain.Region.Wetland &&
                     material ==
                     ProceduralSurfaceTerrain.Material.ShallowWater => .040f,
            FishSpecies.SilverHerring
                when region is ProceduralSurfaceTerrain.Region.Ocean or
                    ProceduralSurfaceTerrain.Region.Coast &&
                     material is ProceduralSurfaceTerrain.Material.ShallowWater
                         or ProceduralSurfaceTerrain.Material.DeepWater => .025f,
            FishSpecies.BluefinTuna
                when region == ProceduralSurfaceTerrain.Region.Ocean &&
                     material == ProceduralSurfaceTerrain.Material.DeepWater =>
                .007f,
            FishSpecies.RedSnapper
                when region is ProceduralSurfaceTerrain.Region.Coast or
                    ProceduralSurfaceTerrain.Region.Wetland &&
                     material is ProceduralSurfaceTerrain.Material.ShallowWater
                         or ProceduralSurfaceTerrain.Material.MangroveShallows =>
                .014f,
            FishSpecies.OceanMackerel
                when region == ProceduralSurfaceTerrain.Region.Ocean &&
                     material == ProceduralSurfaceTerrain.Material.DeepWater =>
                .012f,
            _ => 0
        };

    private static bool IsFishWater(
        ProceduralSurfaceTerrain.Material material) =>
        material is ProceduralSurfaceTerrain.Material.DeepWater or
            ProceduralSurfaceTerrain.Material.ShallowWater or
            ProceduralSurfaceTerrain.Material.RiverWater or
            ProceduralSurfaceTerrain.Material.MangroveShallows;

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

    private sealed record Candidate(
        float Priority,
        FishSchoolDescriptor School);

    private sealed record BeginnerCandidate(
        int Distance,
        float Priority,
        FishSchoolDescriptor School);
}
