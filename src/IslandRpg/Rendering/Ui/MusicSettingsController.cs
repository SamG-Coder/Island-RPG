namespace IslandRpg.Rendering.Ui;

internal static class MusicSettingsController
{
    public static float NextVolume(float current)
    {
        var normalized = Math.Clamp(current, 0, 1);
        if (normalized > .875f) return .75f;
        if (normalized > .625f) return .50f;
        if (normalized > .375f) return .25f;
        if (normalized > .125f) return 0;
        return 1;
    }
}
