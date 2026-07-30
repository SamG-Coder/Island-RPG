using FontStashSharp;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Vector4 WorldSelectionPanel() =>
        FrontendPanel(720, 600);

    private Vector4 WorldSelectionBackButtonBounds()
    {
        var panel = WorldSelectionPanel();
        return new(
            panel.X + panel.Z - 60 - 108,
            panel.Y + panel.W - 92,
            108,
            48);
    }

    private void LayoutWorldList(IReadOnlyList<WorldProfile> worlds)
    {
        var panel = WorldSelectionPanel();
        _worldList.Layout(
            new(panel.X + 44, panel.Y + 164, panel.Z - 88, 324),
            worlds.Select(world => world.Id).ToArray(),
            rowHeight: 58,
            rowGap: 8,
            deleteWidth: 88,
            actionGap: 8);
    }

    private void RenderLoadWorldMenu()
    {
        var panel = WorldSelectionPanel();
        DrawAoEPanelBorder(panel);

        RenderWorldSelectionHeader(panel);

        var worlds = _saves.ListWorlds().ToArray();
        LayoutWorldList(worlds);
        DrawWorldSelectionListHeading(panel, worlds.Length);
        if (worlds.Length == 0)
            RenderEmptyWorldSelection(panel);
        else
            RenderWorldRows(worlds);

        RenderListScrollbar(_worldList);
        var back = WorldSelectionBackButtonBounds();
        DrawUiColor(
            new(panel.X + 44, back.Y - 14,
                panel.Z - 104, 1),
            new(.25f, .20f, .11f, 1));
        DrawMainMenuButton(
            back, "Back", quiet: true);
    }

    private void RenderWorldSelectionHeader(Vector4 panel)
    {
        var header = new Vector4(
            panel.X + 18, panel.Y + 18, panel.Z - 36, 108);
        DrawUiColor(header, new(.052f, .044f, .027f, 1));
        DrawPanelOutline(header, 0, new(.34f, .27f, .13f, 1));
        DrawPanelOutline(header, 1, new(.10f, .085f, .052f, 1));
        DrawCenteredMenuTitle(
            "SELECT WORLD",
            new(header.X, header.Y + 13, header.Z, 44),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            "CONTINUE YOUR ADVENTURE",
            new(header.X + 24, header.Y + 63, header.Z - 48, 22),
            new(180, 158, 107, 255));
        DrawUiColor(
            new(header.X + 142, header.Y + 91, header.Z - 284, 1),
            new(.46f, .34f, .13f, 1));
    }

    private void DrawWorldSelectionListHeading(
        Vector4 panel,
        int worldCount)
    {
        DrawUiText(
            "SAVED WORLDS",
            new(panel.X + 44, panel.Y + 139),
            new FSColor(199, 184, 142, 255));
        var count = worldCount == 1 ? "1 WORLD" : $"{worldCount} WORLDS";
        var size = _chatFont?.MeasureString(count) ??
                   System.Numerics.Vector2.Zero;
        DrawUiText(
            count,
            new(panel.X + panel.Z - 44 - size.X, panel.Y + 139),
            new FSColor(130, 124, 106, 255));
    }

    private void RenderEmptyWorldSelection(Vector4 panel)
    {
        var empty = new Vector4(
            panel.X + 44, panel.Y + 164, panel.Z - 88, 210);
        DrawUiColor(empty, new(.032f, .030f, .025f, .82f));
        DrawPanelOutline(empty, 0, new(.22f, .19f, .12f, 1));
        DrawCenteredUiText(
            "NO SAVED WORLDS",
            new(empty.X, empty.Y + 66, empty.Z, 24),
            new(204, 190, 150, 255));
        DrawCenteredUiText(
            "Begin a new adventure from the main menu.",
            new(empty.X + 24, empty.Y + 99, empty.Z - 48, 22),
            new(145, 138, 117, 255));
    }

    private void RenderWorldRows(IReadOnlyList<WorldProfile> worlds)
    {
        foreach (var index in _worldList.VisibleIndices)
        {
            var world = worlds[index];
            var row = _worldList.RowBounds(index);
            var delete = _worldList.DeleteBounds(index);
            var hovered = row.Contains(MouseState.Position);
            var deleteHovered = delete.Contains(MouseState.Position);
            var deletePending = _worldList.IsDeletePending(world.Id);

            DrawUiColor(
                row,
                hovered
                    ? new(.105f, .088f, .050f, .98f)
                    : new(.047f, .043f, .033f, .96f));
            DrawPanelOutline(
                row, 0,
                hovered
                    ? new(.55f, .41f, .15f, 1)
                    : new(.23f, .19f, .11f, 1));
            DrawPanelOutline(row, 1, new(.045f, .040f, .029f, 1));
            if (hovered)
                DrawUiColor(
                    new(row.X + 2, row.Y + 2, 3, row.W - 4),
                    new(.68f, .49f, .16f, 1));

            DrawUiText(
                world.Name,
                new(row.X + 16, row.Y + 10),
                hovered
                    ? new FSColor(246, 226, 167, 255)
                    : new FSColor(218, 205, 166, 255));
            DrawUiText(
                $"SEED {world.Seed}   |   {WorldLastPlayed(world.UpdatedUtc)}",
                new(row.X + 16, row.Y + 34),
                new FSColor(142, 136, 116, 255));

            const string enter = "ENTER";
            var enterSize = _chatFont?.MeasureString(enter) ??
                            System.Numerics.Vector2.Zero;
            DrawUiText(
                enter,
                new(
                    row.X + row.Z - enterSize.X - 16,
                    row.Y + (row.W - enterSize.Y) * .5f),
                hovered
                    ? new FSColor(224, 194, 119, 255)
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

    private static string WorldLastPlayed(DateTime updatedUtc)
    {
        var elapsed = DateTime.UtcNow - updatedUtc;
        if (elapsed.TotalMinutes < 1)
            return "PLAYED JUST NOW";
        if (elapsed.TotalHours < 1)
            return $"PLAYED {(int)elapsed.TotalMinutes}M AGO";
        if (elapsed.TotalDays < 1)
            return $"PLAYED {(int)elapsed.TotalHours}H AGO";
        if (elapsed.TotalDays < 7)
            return $"PLAYED {(int)elapsed.TotalDays}D AGO";
        return $"PLAYED {updatedUtc.ToLocalTime():d MMM yyyy}";
    }
}
