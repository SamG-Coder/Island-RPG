using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private void AddBluntToolMonologue(string toolId)
    {
        var toolName = ItemCatalog.Get(toolId).Name;
        var thought =
            $"My {toolName} has gone blunt. Maybe I should try using some small rocks to sharpen it.";
        _chatUi.AddMessage(thought, ChatMessageStyle.Monologue);
        ShowOverheadSpeech(thought);
    }
}
