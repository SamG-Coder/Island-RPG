using IslandRpg.Boats;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Gameplay;

internal static class FishingBoatTravel
{
    public static bool IsNavigable(Biome biome) =>
        biome is Biome.DeepWater or Biome.ShallowWater or Biome.RiverWater;

    public static Vector2 FindInitialPosition(long seed, Vector2 origin)
        => ToOpenTk(BoatTravelRules.FindInitialPosition(
            new ProceduralBoatNavigationQuery(seed),
            ToNumerics(origin)));

    public static IReadOnlyList<Vector2> FindPath(
        long seed,
        Vector2 start,
        Vector2 requestedTarget,
        int maximumVisited = 65536)
        => BoatRoutePlanner.Find(
                new ProceduralBoatNavigationQuery(seed),
                ToNumerics(start),
                ToNumerics(requestedTarget),
                maximumVisited)
            .Select(ToOpenTk)
            .ToArray();

    public static bool CanDisembark(
        long seed, Vector2 boatPosition, Vector2 target) =>
        BoatTravelRules.CanDisembark(
            new ProceduralBoatNavigationQuery(seed),
            ToNumerics(boatPosition),
            ToNumerics(target));

    public static Vector2? FindDisembarkLanding(
        long seed,
        Vector2 boatPosition,
        Vector2 requestedTarget)
    {
        var result = BoatTravelRules.FindDisembarkLanding(
            new ProceduralBoatNavigationQuery(seed),
            ToNumerics(boatPosition),
            ToNumerics(requestedTarget));
        return result is { } landing ? ToOpenTk(landing) : null;
    }

    private static System.Numerics.Vector2 ToNumerics(Vector2 value) =>
        new(value.X, value.Y);

    private static Vector2 ToOpenTk(System.Numerics.Vector2 value) =>
        new(value.X, value.Y);
}
