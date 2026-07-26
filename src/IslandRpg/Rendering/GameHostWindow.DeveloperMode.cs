using IslandRpg.Rendering.Ui;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const string DeveloperModeCommand = "/imahacker";

    private void HandleChatSubmission(string message)
    {
        if (!string.Equals(
                message.Trim(),
                DeveloperModeCommand,
                StringComparison.OrdinalIgnoreCase))
        {
            ShowOverheadSpeech(message);
            return;
        }

        var wasEnabled = _settingsMenu.DeveloperModeEnabled;
        _settingsMenu.EnableDeveloperMode();
        _chatUi.AddMessage(
            wasEnabled
                ? "Developer mode is already enabled."
                : "Developer mode enabled. Open Pause > Settings > Dev.",
            ChatMessageStyle.Action);
    }
}
