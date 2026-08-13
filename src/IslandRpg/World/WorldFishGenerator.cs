using IslandRpg.Fishing;
using IslandRpg.Simulation;

namespace IslandRpg.World;

internal enum WorldFishSpecies : byte
{
    ShoreMinnows,
    RiverPerch,
    SilverHerring,
    BluefinTuna,
    RedSnapper,
    OceanMackerel
}

internal sealed record WorldFish(
    float X,
    float Y,
    WorldFishSpecies Species,
    string GraphicName,
    int AnimationOffset,
    string StableKey);

internal sealed record WorldFishProfile(
    WorldFishSpecies Species,
    string DisplayName,
    string GraphicName,
    int FrameCount,
    string Rarity,
    string Habitat);

internal static class WorldFishGenerator
{
    public const int MaximumPerChunk = 12;
    public const int MinimumBeginnerShoreDistance = 1;
    public const int MaximumBeginnerShoreDistance = 3;

    public static readonly WorldFishProfile[] Profiles =
        FishingRules.CatchProfiles
            .Select(profile => new WorldFishProfile(
                (WorldFishSpecies)profile.Species,
                profile.DisplayName,
                profile.GraphicName,
                profile.FrameCount,
                profile.Rarity,
                profile.Habitat))
            .ToArray();

    public static readonly string[] RequiredGraphicNames =
        Profiles.Select(profile => profile.GraphicName).ToArray();

    public static bool IsFishGraphic(string graphicName) =>
        Profiles.Any(profile =>
            profile.GraphicName.Equals(
                graphicName, StringComparison.OrdinalIgnoreCase));

    public static int FrameCount(string graphicName) =>
        Profiles.FirstOrDefault(profile =>
            profile.GraphicName.Equals(
                graphicName, StringComparison.OrdinalIgnoreCase))
            ?.FrameCount ?? 1;

    public static WorldFishProfile Profile(WorldFishSpecies species) =>
        Profiles.First(profile => profile.Species == species);

    public static bool IsValidHabitat(
        WorldFishSpecies species, IslandTile tile) =>
        Chance(species, tile) > 0 ||
        species == WorldFishSpecies.ShoreMinnows &&
        tile.Biome == Biome.DeepWater;

    public static WorldFish[] Generate(
        long seed, IReadOnlyList<IslandTile> tiles)
    {
        if (tiles.Count > 0 && TryChunk(tiles, out var chunk))
        {
            return new ProceduralFishSchoolSource()
                .DescribeSchools(seed, chunk)
                .Select(ToWorldFish)
                .ToArray();
        }

        // Kept for callers providing a partial/non-chunk tile fixture. World
        // chunks delegate to the canonical procedural source above.
        var tileLookup = tiles.ToDictionary(
            tile => (tile.X, tile.Y));
        var candidates = new List<(float Priority, WorldFish Fish)>();
        var beginnerCandidates = new List<(int Distance, float Priority, WorldFish Fish)>();
        foreach (var tile in tiles)
        {
            if (!IsWater(tile.Biome))
                continue;
            var shoreDistance = DistanceFromShore(
                seed, tile.X, tile.Y, tileLookup,
                MaximumBeginnerShoreDistance);
            if (shoreDistance is >= MinimumBeginnerShoreDistance and
                    <= MaximumBeginnerShoreDistance)
            {
                var beginner = CreateFish(
                    seed, tile, Profile(WorldFishSpecies.ShoreMinnows));
                beginnerCandidates.Add((
                    shoreDistance,
                    Hash(seed, tile.X, tile.Y, 5189),
                    beginner));
            }
            var hasNearbySand = shoreDistance <= 2;
            var available = Profiles
                .Select(profile =>
                    (Profile: profile, Chance: Chance(profile.Species, tile)))
                .Where(candidate => candidate.Chance > 0 &&
                    (candidate.Profile.Species ==
                        WorldFishSpecies.ShoreMinnows
                        ? shoreDistance is >= MinimumBeginnerShoreDistance and
                            <= MaximumBeginnerShoreDistance
                        : !hasNearbySand))
                .ToArray();
            if (available.Length == 0) continue;

            var totalChance = available.Sum(candidate => candidate.Chance);
            var roll = Hash(seed, tile.X, tile.Y, 5101);
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

            candidates.Add((
                Hash(seed, tile.X, tile.Y, 5167),
                CreateFish(seed, tile, selected)));
        }

        var selectedFish = candidates
            .OrderBy(candidate => candidate.Priority)
            .Take(MaximumPerChunk)
            .Select(candidate => candidate.Fish)
            .ToList();
        if (beginnerCandidates.Count > 0 &&
            selectedFish.All(fish =>
                fish.Species != WorldFishSpecies.ShoreMinnows))
        {
            var guaranteed = beginnerCandidates
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Priority)
                .First().Fish;
            if (selectedFish.Count >= MaximumPerChunk)
                selectedFish.RemoveAt(selectedFish.Count - 1);
            selectedFish.Add(guaranteed);
        }
        return selectedFish.ToArray();
    }

    private static bool TryChunk(
        IReadOnlyList<IslandTile> tiles,
        out WorldChunkKey chunk)
    {
        chunk = default;
        if (tiles.Count != WorldChunk.Size * WorldChunk.Size)
            return false;
        var candidate = WorldChunkKey.At(
            new System.Numerics.Vector2(tiles[0].X, tiles[0].Y), 0);
        var originX = candidate.X * WorldChunk.Size;
        var originY = candidate.Y * WorldChunk.Size;
        for (var index = 0; index < tiles.Count; index++)
        {
            var expectedX = originX + index % WorldChunk.Size;
            var expectedY = originY + index / WorldChunk.Size;
            if (tiles[index].X != expectedX || tiles[index].Y != expectedY)
                return false;
        }
        chunk = candidate;
        return true;
    }

    private static WorldFish ToWorldFish(FishSchoolDescriptor value)
    {
        var profile = FishingRules.Profile(value.Species);
        return new(
            value.Position.X,
            value.Position.Y,
            (WorldFishSpecies)value.Species,
            profile.GraphicName,
            value.AnimationOffset,
            value.StableKey);
    }

    public static int DistanceFromShore(
        long seed,
        int x,
        int y,
        IReadOnlyDictionary<(int X, int Y), IslandTile>? tiles = null,
        int maximumDistance = MaximumBeginnerShoreDistance)
    {
        for (var distance = 1; distance <= maximumDistance; distance++)
        for (var offsetY = -distance; offsetY <= distance; offsetY++)
        for (var offsetX = -distance; offsetX <= distance; offsetX++)
        {
            if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) != distance)
                continue;
            var coordinate = (x + offsetX, y + offsetY);
            var biome = tiles is not null &&
                        tiles.TryGetValue(coordinate, out var local)
                ? local.Biome
                : InfiniteWorldGenerator.SampleTile(
                    seed, coordinate.Item1, coordinate.Item2).Biome;
            if (biome is Biome.Beach or Biome.DesertSand)
                return distance;
        }
        return int.MaxValue;
    }

    private static WorldFish CreateFish(
        long seed, IslandTile tile, WorldFishProfile profile)
    {
        var x = tile.X + .16f +
                Hash(seed, tile.X, tile.Y, 5113) * .68f;
        var y = tile.Y + .16f +
                Hash(seed, tile.X, tile.Y, 5119) * .68f;
        var offset = (int)(Hash(seed, tile.X, tile.Y, 5147) *
            profile.FrameCount) % profile.FrameCount;
        return new(
            x, y, profile.Species, profile.GraphicName, offset,
            $"fish:{tile.X}:{tile.Y}:{(int)profile.Species}");
    }

    private static float Chance(
        WorldFishSpecies species, IslandTile tile) =>
        species switch
        {
            WorldFishSpecies.ShoreMinnows
                when tile.Biome is Biome.ShallowWater or
                    Biome.MangroveShallows => .040f,
            WorldFishSpecies.RiverPerch
                when tile.Biome == Biome.RiverWater ||
                     tile.Region == WorldBiome.Wetland &&
                     tile.Biome == Biome.ShallowWater => .040f,
            WorldFishSpecies.SilverHerring
                when tile.Region is WorldBiome.Ocean or WorldBiome.Coast &&
                     tile.Biome is Biome.ShallowWater or Biome.DeepWater =>
                .025f,
            WorldFishSpecies.BluefinTuna
                when tile.Region == WorldBiome.Ocean &&
                     tile.Biome == Biome.DeepWater => .007f,
            WorldFishSpecies.RedSnapper
                when tile.Region is WorldBiome.Coast or WorldBiome.Wetland &&
                     tile.Biome is Biome.ShallowWater or
                         Biome.MangroveShallows => .014f,
            WorldFishSpecies.OceanMackerel
                when tile.Region == WorldBiome.Ocean &&
                     tile.Biome == Biome.DeepWater => .012f,
            _ => 0
        };

    private static bool IsWater(Biome biome) =>
        biome is Biome.DeepWater or Biome.ShallowWater or
            Biome.RiverWater or Biome.MangroveShallows;

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
