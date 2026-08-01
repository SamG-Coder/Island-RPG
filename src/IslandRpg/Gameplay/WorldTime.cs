namespace IslandRpg.Gameplay;

internal readonly record struct WorldTimeSnapshot(
    long Day, int Hour, int Minute, float Daylight);

internal static class WorldTime
{
    public const double NewGameStartGameSeconds = 3 * 60 * 60;
    public const double RealSecondsPerGameDay = 24 * 60;
    public const double GameSecondsPerDay = 24 * 60 * 60;
    public const double GameMinutesPerRealSecond = 1;

    public static WorldTimeSnapshot At(double gameSeconds)
    {
        gameSeconds = Math.Max(0, gameSeconds);
        var totalMinutes = gameSeconds / 60;
        var day = (long)Math.Floor(totalMinutes / (24 * 60)) + 1;
        var minuteOfDay = totalMinutes % (24 * 60);
        var hour = (int)(minuteOfDay / 60);
        var minute = (int)minuteOfDay % 60;
        var phase = minuteOfDay / (24 * 60);
        var daylight = Math.Clamp(
            .5f + .5f * MathF.Sin((float)((phase - .25) * Math.Tau)),
            0, 1);
        return new(day, hour, minute, daylight);
    }

    public static double Advance(double gameSeconds, double realSeconds) =>
        Math.Max(0, gameSeconds) +
        Math.Max(0, realSeconds) * GameMinutesPerRealSecond * 60;
}
