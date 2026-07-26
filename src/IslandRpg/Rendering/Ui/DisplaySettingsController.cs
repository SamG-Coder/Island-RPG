using IslandRpg.Persistence;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace IslandRpg.Rendering.Ui;

internal static class DisplaySettingsController
{
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

    public static void Apply(GameWindow window, GameSettings settings)
    {
        window.VSync = settings.VSyncMode switch
        {
            DisplayVSyncMode.On => VSyncMode.On,
            DisplayVSyncMode.Adaptive => VSyncMode.Adaptive,
            _ => VSyncMode.Off
        };
        var frequency = FrameRateLimits.Contains(settings.FrameRateLimit)
            ? settings.FrameRateLimit
            : 0;
        window.UpdateFrequency = frequency;
    }
}
