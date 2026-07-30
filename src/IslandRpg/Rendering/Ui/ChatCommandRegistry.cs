namespace IslandRpg.Rendering.Ui;

internal sealed record ChatCommandDefinition(
    string Name,
    string Usage,
    string Description,
    bool RequiresDeveloperMode = false);

internal readonly record struct ParsedChatCommand(
    ChatCommandDefinition Definition,
    string[] Arguments);

internal static class ChatCommandRegistry
{
    public static readonly IReadOnlyList<ChatCommandDefinition> All =
    [
        new("/help", "/help", "List available commands."),
        new("/die", "/die", "Enter the normal death flow."),
        new("/stuck", "/stuck", "Move to the nearest safe spawn."),
        new("/where", "/where", "Show position and world layer."),
        new("/clear", "/clear", "Clear the local chat log."),
        new("/seed", "/seed", "Show the current world seed."),
        new("/imahacker", "/imahacker", "Enable developer mode."),
        new("/respawn", "/respawn", "Restore and return to spawn.", true),
        new("/heal", "/heal", "Restore maximum health.", true),
        new("/feed", "/feed", "Restore hunger.", true),
        new("/god", "/god", "Toggle damage and starvation immunity.", true),
        new("/noclip", "/noclip", "Toggle pathing restrictions.", true),
        new("/teleport", "/teleport <x> <y>", "Teleport to coordinates.", true),
        new("/surface", "/surface", "Move to the overworld.", true),
        new("/underground", "/underground", "Move underground.", true),
        new("/time", "/time <hour>", "Set the time of day.", true),
        new("/give", "/give <item-id> [amount]", "Grant inventory items.", true),
        new("/xp", "/xp <skill> <amount>", "Grant skill experience.", true),
        new("/level", "/level <skill> <level>", "Set a skill level.", true),
        new("/damage", "/damage <amount>", "Apply player damage.", true),
        new("/spawn", "/spawn <creature-id>", "Spawn a hostile creature.", true),
        new("/killall", "/killall", "Remove nearby spawned creatures.", true),
        new("/debug", "/debug", "Show active developer states.", true)
    ];

    public static IReadOnlyList<ChatCommandDefinition> Visible(
        bool developerMode) =>
        All.Where(command =>
                developerMode || !command.RequiresDeveloperMode)
            .ToArray();

    public static IReadOnlyList<ChatCommandDefinition> Filter(
        string input,
        bool developerMode)
    {
        if (!input.StartsWith('/')) return [];
        if (input.Contains(' ')) return [];
        var token = input.Split(' ', 2)[0];
        return Visible(developerMode)
            .Where(command => command.Name.StartsWith(
                token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static bool TryParse(
        string text,
        out ParsedChatCommand parsed)
    {
        parsed = default;
        var parts = text.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].StartsWith('/')) return false;
        var definition = All.FirstOrDefault(command =>
            command.Name.Equals(
                parts[0], StringComparison.OrdinalIgnoreCase));
        if (definition is null) return false;
        parsed = new(definition, parts.Skip(1).ToArray());
        return true;
    }
}
