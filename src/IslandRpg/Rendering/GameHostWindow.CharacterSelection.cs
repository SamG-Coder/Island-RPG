using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Vector4 CharacterSelectionPanel() =>
        FrontendPanel(720, 600);

    private Vector4 NewCharacterButtonBounds()
    {
        var panel = CharacterSelectionPanel();
        return new(
            panel.X + 44,
            panel.Y + panel.W - 92,
            176,
            48);
    }

    private Vector4 CharacterSelectionBackButtonBounds()
    {
        var panel = CharacterSelectionPanel();
        return new(
            panel.X + 232,
            panel.Y + panel.W - 92,
            108,
            48);
    }

    private Vector4 ContinueCharacterButtonBounds()
    {
        var panel = CharacterSelectionPanel();
        return new(
            panel.X + panel.Z - 60 - 190,
            panel.Y + panel.W - 92,
            190,
            48);
    }

    private Vector4 CharacterListBounds()
    {
        var panel = IsMultiplayerCharacterStep
            ? MultiplayerPanel()
            : CharacterSelectionPanel();
        var top = IsMultiplayerCharacterStep ? 200f : 164f;
        var height = IsMultiplayerCharacterStep ? 248f : 324f;
        return new(panel.X + 44, panel.Y + top, panel.Z - 88, height);
    }

    private void LayoutCharacterList(
        IReadOnlyList<PlayerProfile> players)
    {
        _characterList.SelectedId = _selectedPlayer?.Id;
        _characterList.Layout(
            CharacterListBounds(),
            players.Select(player => player.Id).ToArray(),
            rowHeight: 58,
            rowGap: 8,
            deleteWidth: 88,
            actionGap: 8);
    }

    private void RenderCharacterSelectMenu()
    {
        var panel = CharacterSelectionPanel();
        DrawAoEPanelBorder(panel);
        RenderCharacterSelectionHeader(panel);

        var players = _saves.ListPlayers().ToArray();
        LayoutCharacterList(players);
        DrawCharacterSelectionListHeading(panel, players.Length);
        if (players.Length == 0)
            RenderEmptyCharacterSelection(panel);
        else
            RenderCharacterRows(players);

        RenderListScrollbar(_characterList);
        var back = CharacterSelectionBackButtonBounds();
        DrawUiColor(
            new(panel.X + 44, back.Y - 14, panel.Z - 104, 1),
            new(.25f, .20f, .11f, 1));
        DrawMainMenuButton(
            NewCharacterButtonBounds(), "NEW CHARACTER");
        DrawMainMenuButton(back, "Back", quiet: true);
        if (_selectedPlayer is not null)
            DrawMainMenuButton(
                ContinueCharacterButtonBounds(),
                "USE CHARACTER",
                primary: true);
    }

    private void RenderCharacterSelectionHeader(Vector4 panel)
    {
        var header = new Vector4(
            panel.X + 18, panel.Y + 18, panel.Z - 36, 108);
        DrawUiColor(header, new(.052f, .044f, .027f, 1));
        DrawPanelOutline(header, 0, new(.34f, .27f, .13f, 1));
        DrawPanelOutline(header, 1, new(.10f, .085f, .052f, 1));
        DrawCenteredMenuTitle(
            "CHOOSE CHARACTER",
            new(header.X, header.Y + 13, header.Z, 44),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            "SELECT YOUR ADVENTURER",
            new(header.X + 24, header.Y + 63, header.Z - 48, 22),
            new(180, 158, 107, 255));
        DrawUiColor(
            new(header.X + 142, header.Y + 91, header.Z - 284, 1),
            new(.46f, .34f, .13f, 1));
    }

    private void DrawCharacterSelectionListHeading(
        Vector4 panel,
        int characterCount)
    {
        DrawUiText(
            "ADVENTURERS",
            new(panel.X + 44, panel.Y + 139),
            new FSColor(199, 184, 142, 255));
        var count = characterCount == 1
            ? "1 CHARACTER"
            : $"{characterCount} CHARACTERS";
        var size = _chatFont?.MeasureString(count) ??
                   System.Numerics.Vector2.Zero;
        DrawUiText(
            count,
            new(panel.X + panel.Z - 44 - size.X, panel.Y + 139),
            new FSColor(130, 124, 106, 255));
    }

    private void RenderEmptyCharacterSelection(Vector4 panel)
    {
        _ = panel;
        var list = CharacterListBounds();
        var empty = new Vector4(
            list.X, list.Y, list.Z, MathF.Min(210, list.W));
        DrawUiColor(empty, new(.032f, .030f, .025f, .82f));
        DrawPanelOutline(empty, 0, new(.22f, .19f, .12f, 1));
        DrawCenteredUiText(
            "NO CHARACTERS",
            new(empty.X, empty.Y + 66, empty.Z, 24),
            new(204, 190, 150, 255));
        DrawCenteredUiText(
            "Create an adventurer to begin.",
            new(empty.X + 24, empty.Y + 99, empty.Z - 48, 22),
            new(145, 138, 117, 255));
    }

    private void RenderCharacterRows(
        IReadOnlyList<PlayerProfile> players)
    {
        foreach (var index in _characterList.VisibleIndices)
        {
            var player = players[index];
            var row = _characterList.RowBounds(index);
            var delete = _characterList.DeleteBounds(index);
            var selected = _selectedPlayer?.Id == player.Id;
            var hovered = row.Contains(MouseState.Position);
            var deleteHovered = delete.Contains(MouseState.Position);
            var deletePending =
                _characterList.IsDeletePending(player.Id);

            DrawUiColor(
                row,
                selected
                    ? new(.13f, .105f, .054f, .98f)
                    : hovered
                        ? new(.095f, .080f, .047f, .98f)
                        : new(.047f, .043f, .033f, .96f));
            DrawPanelOutline(
                row, 0,
                selected
                    ? new(.65f, .48f, .17f, 1)
                    : hovered
                        ? new(.48f, .37f, .15f, 1)
                        : new(.23f, .19f, .11f, 1));
            DrawPanelOutline(row, 1, new(.045f, .040f, .029f, 1));

            var teamColor = TeamColor(player.TeamColor);
            DrawUiColor(
                new(row.X + 2, row.Y + 2, 4, row.W - 4),
                new(teamColor.X, teamColor.Y, teamColor.Z, 1));
            DrawUiText(
                player.Name,
                new(row.X + 17, row.Y + 10),
                selected || hovered
                    ? new FSColor(246, 226, 167, 255)
                    : new FSColor(218, 205, 166, 255));

            var level = AdventureService.LevelForExperience(
                player.AdventureExperience);
            DrawUiText(
                $"{player.Gender.ToString().ToUpperInvariant()}   |   ADVENTURE LEVEL {level}",
                new(row.X + 17, row.Y + 34),
                new FSColor(142, 136, 116, 255));

            var state = selected ? "SELECTED" : "SELECT";
            var stateSize = _chatFont?.MeasureString(state) ??
                            System.Numerics.Vector2.Zero;
            DrawUiText(
                state,
                new(
                    row.X + row.Z - stateSize.X - 16,
                    row.Y + (row.W - stateSize.Y) * .5f),
                selected
                    ? new FSColor(228, 196, 113, 255)
                    : hovered
                        ? new FSColor(206, 182, 122, 255)
                        : new FSColor(133, 125, 103, 255));

            DrawUiColor(
                delete,
                deletePending
                    ? new(.25f, .075f, .055f, 1)
                    : deleteHovered
                        ? new(.13f, .060f, .045f, 1)
                        : new(.035f, .032f, .027f, 1));
            DrawPanelOutline(
                delete, 0,
                deletePending
                    ? new(.65f, .22f, .15f, 1)
                    : deleteHovered
                        ? new(.42f, .18f, .12f, 1)
                        : new(.19f, .16f, .11f, 1));
            DrawCenteredUiText(
                deletePending ? "CONFIRM" : "DELETE",
                delete,
                deletePending || deleteHovered
                    ? new FSColor(239, 174, 145, 255)
                    : new FSColor(139, 130, 110, 255));
        }
    }

}
