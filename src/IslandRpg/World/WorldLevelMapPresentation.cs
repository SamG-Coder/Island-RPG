namespace IslandRpg.World;

/// <summary>
/// Shared level-aware map sampling and colors used by compact and regional
/// maps. Keeping this independent of either UI prevents the two maps from
/// drifting into different representations of the same world level.
/// </summary>
internal static class WorldLevelMapPresentation
{
    public static (byte Red, byte Green, byte Blue) UndergroundColor(
        long seed,
        CaveHydrologyField.SamplingContext context,
        float worldX,
        float worldY)
    {
        var density = context.Density(worldX, worldY);
        return UndergroundColor(seed, density, worldX, worldY);
    }

    public static (byte Red, byte Green, byte Blue) UndergroundColor(
        long seed,
        float density,
        float worldX,
        float worldY)
    {
        if (density < CaveHydrologyField.Boundary)
            return (2, 2, 2);

        var material = UndergroundWorldGenerator.MaterialAt(
            seed,
            (int)MathF.Floor(worldX),
            (int)MathF.Floor(worldY));
        var baseColor = material switch
        {
            Biome.Mud => (Red: 91, Green: 70, Blue: 48),
            Biome.CrackedEarth => (Red: 118, Green: 101, Blue: 76),
            _ => (Red: 91, Green: 94, Blue: 96)
        };
        var shade = .58f + CaveHydrologyField.Strength(density) * .42f;
        return (
            (byte)(baseColor.Red * shade),
            (byte)(baseColor.Green * shade),
            (byte)(baseColor.Blue * shade));
    }

    public static string LevelName(int level) =>
        level == (int)WorldLevel.Underground
            ? "UNDERGROUND (-1)"
            : "OVERWORLD (0)";
}
