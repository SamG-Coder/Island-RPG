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

    public static readonly WorldFishProfile[] Profiles =
    [
        new(
            WorldFishSpecies.ShoreMinnows, "shore minnows",
            "FISHS_NN", 34, "Common",
            "Sheltered shallows and mangrove edges"),
        new(
            WorldFishSpecies.RiverPerch, "river perch",
            "FISH1_NN", 49, "Common",
            "Freshwater rivers and wetlands"),
        new(
            WorldFishSpecies.SilverHerring, "silver herring",
            "FISH2_NN", 49, "Common",
            "Coastal sea and open ocean"),
        new(
            WorldFishSpecies.BluefinTuna, "bluefin tuna",
            "FISH3_NN", 49, "Rare",
            "Deep open ocean"),
        new(
            WorldFishSpecies.RedSnapper, "red snapper",
            "FISH4_NN", 49, "Uncommon",
            "Warm coastal shallows and mangroves"),
        new(
            WorldFishSpecies.OceanMackerel, "ocean mackerel",
            "FISHX_NN", 30, "Uncommon",
            "Deep open ocean")
    ];

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
        Chance(species, tile) > 0;

    public static WorldFish[] Generate(
        long seed, IReadOnlyList<IslandTile> tiles)
    {
        var tileLookup = tiles.ToDictionary(
            tile => (tile.X, tile.Y));
        var candidates = new List<(float Priority, WorldFish Fish)>();
        foreach (var tile in tiles)
        {
            if (!IsWater(tile.Biome) ||
                HasNearbySand(
                    seed, tile.X, tile.Y, tileLookup))
                continue;
            var available = Profiles
                .Select(profile =>
                    (Profile: profile, Chance: Chance(profile.Species, tile)))
                .Where(candidate => candidate.Chance > 0)
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

            var x = tile.X + .16f +
                    Hash(seed, tile.X, tile.Y, 5113) * .68f;
            var y = tile.Y + .16f +
                    Hash(seed, tile.X, tile.Y, 5119) * .68f;
            var offset = (int)(
                Hash(seed, tile.X, tile.Y, 5147) *
                selected.FrameCount) % selected.FrameCount;
            candidates.Add((
                Hash(seed, tile.X, tile.Y, 5167),
                new(
                    x, y, selected.Species, selected.GraphicName,
                    offset,
                    $"fish:{tile.X}:{tile.Y}:{(int)selected.Species}")));
        }

        return candidates
            .OrderBy(candidate => candidate.Priority)
            .Take(MaximumPerChunk)
            .Select(candidate => candidate.Fish)
            .ToArray();
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

    private static bool HasNearbySand(
        long seed,
        int x,
        int y,
        IReadOnlyDictionary<(int X, int Y), IslandTile> tiles)
    {
        const int clearance = 2;
        for (var offsetY = -clearance; offsetY <= clearance; offsetY++)
        for (var offsetX = -clearance; offsetX <= clearance; offsetX++)
        {
            var coordinate = (x + offsetX, y + offsetY);
            var biome = tiles.TryGetValue(coordinate, out var local)
                ? local.Biome
                : InfiniteWorldGenerator.SampleTile(
                    seed, coordinate.Item1, coordinate.Item2).Biome;
            if (biome is
                Biome.Beach or Biome.DesertSand)
                return true;
        }
        return false;
    }

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
