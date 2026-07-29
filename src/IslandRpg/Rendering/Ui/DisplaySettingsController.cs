using IslandRpg.Persistence;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace IslandRpg.Rendering.Ui;

internal static class DisplaySettingsController
{
    public const double SimulationUpdatesPerSecond = 60;

    public static readonly int[] FrameRateLimits =
        [0, 60, 120, 144, 165, 240];

    public static GameSettings CycleVSync(GameSettings settings)
    {
        var next = settings.VSyncMode switch
        {
            DisplayVSyncMode.On => DisplayVSyncMode.Adaptive,
            DisplayVSyncMode.Adaptive => DisplayVSyncMode.Off,
            _ => DisplayVSyncMode.On
        };
        return settings with { VSyncMode = next };
    }

    public static GameSettings CycleFrameRateLimit(GameSettings settings)
    {
        var current = Array.IndexOf(
            FrameRateLimits, settings.FrameRateLimit);
        var next = FrameRateLimits[
            (Math.Max(-1, current) + 1) %
            FrameRateLimits.Length];
        return settings with { FrameRateLimit = next };
    }

    public static string FrameRateLabel(int limit) =>
        limit <= 0 ? "Unlimited" : $"{limit} FPS";

    public static double GameLoopFrequency(GameSettings settings) =>
        FrameRateLimits.Contains(settings.FrameRateLimit)
            ? settings.FrameRateLimit
            : 0;

    public static void Apply(GameWindow window, GameSettings settings)
    {
        window.VSync = settings.VSyncMode switch
        {
            DisplayVSyncMode.On => VSyncMode.On,
            DisplayVSyncMode.Adaptive => VSyncMode.Adaptive,
            _ => VSyncMode.Off
        };
        // OpenTK 4.9 schedules UpdateFrame and RenderFrame together.
        // Gameplay itself advances through a fixed 60 Hz accumulator.
        window.UpdateFrequency = GameLoopFrequency(settings);
    }
}
