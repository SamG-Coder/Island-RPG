using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private enum QuestFilter { All, Active, Complete }

    private readonly ListControlState _questList = new();
    private string? _selectedQuestId;
    private QuestDefinition? _completedQuest;
    private bool _questLeftWasDown;
    private QuestFilter _questFilter;
    private ModalScreenKind _questReturnModal;
    private string?[]? _lastQuestInventory;
    private IReadOnlyList<QuestProgress>? _lastQuestProgress;
    private string? _lastInventoryQuestId;

    private void OpenQuestWindow()
    {
        if (_activePlayer is null || _modalScreen.IsOpen) return;
        var progress = QuestService.Normalize(_activePlayer.Quests);
        _selectedQuestId = progress.FirstOrDefault(value =>
                value.Status == QuestStatus.InProgress)?.QuestId ??
            progress.FirstOrDefault()?.QuestId;
        _modalScreen.Open(ModalScreenKind.QuestJournal);
        _chatUi.BlurInput();
        UseDefaultGameCursor();
    }

    private void CloseQuestWindow()
    {
        ConsumeWorldPointerInput();
        if (_modalScreen.Active == ModalScreenKind.QuestComplete)
        {
            _modalScreen.Close(ModalScreenKind.QuestComplete);
            _completedQuest = null;
            if (_questReturnModal != ModalScreenKind.None)
                _modalScreen.Open(_questReturnModal);
            _questReturnModal = ModalScreenKind.None;
            return;
        }
        _completedQuest = null;
        _modalScreen.Close(ModalScreenKind.QuestJournal);
        _modalScreen.Close(ModalScreenKind.QuestComplete);
        if (_defaultNativeCursor is not null) Cursor = _defaultNativeCursor;
    }

    private void RecordQuestEvent(QuestEvent questEvent)
    {
        if (_activePlayer is null) return;
        var before = QuestService.Normalize(_activePlayer.Quests);
        var result = QuestService.Apply(
            before, _activePlayer.AdventureExperience, questEvent);
        if (result.Progress.SequenceEqual(before)) return;
        var oldMaximum = AdventureService.MaximumHealth(
            _activePlayer.AdventureExperience);
        var newMaximum = AdventureService.MaximumHealth(
            result.AdventureExperience);
        _activePlayer = _activePlayer with
        {
            Quests = result.Progress,
            AdventureExperience = result.AdventureExperience,
            Health = Math.Clamp(
                _activePlayer.Health + newMaximum - oldMaximum,
                0, newMaximum),
            UpdatedUtc = DateTime.UtcNow
        };
        _selectedPlayer = _activePlayer;
        _saves.SavePlayer(_activePlayer);
        if (result.CompletedQuest is null) return;
        _completedQuest = result.CompletedQuest;
        _questReturnModal = _modalScreen.Active;
        _modalScreen.Open(ModalScreenKind.QuestComplete);
        _chatUi.BlurInput();
        UseDefaultGameCursor();
    }

    private void ReconcileInventoryQuestProgress()
    {
        if (_activePlayer is null) return;
        var inventory = _activePlayer.Inventory;
        var quests = _activePlayer.Quests;
        if (ReferenceEquals(inventory, _lastQuestInventory) &&
            ReferenceEquals(quests, _lastQuestProgress))
            return;
        _lastQuestInventory = inventory;
        _lastQuestProgress = quests;
        var active = QuestService.ActiveQuest(quests);
        if (active is null)
        {
            _lastInventoryQuestId = null;
            return;
        }
        _lastInventoryQuestId = active.Value.Definition.Id;
        foreach (var questEvent in QuestService.InventoryProgressEvents(
                     _activePlayer.Quests, inventory))
        {
            RecordQuestEvent(questEvent);
            if (QuestService.ActiveQuest(_activePlayer.Quests)?
                    .Definition.Id != _lastInventoryQuestId)
            {
                // Reconcile the newly unlocked quest on the next update.
                _lastQuestInventory = null;
                _lastQuestProgress = null;
                _lastInventoryQuestId = null;
                return;
            }
            active = QuestService.ActiveQuest(_activePlayer.Quests);
            if (active is null) return;
        }
    }

    private void CompleteQuestFromCommand(string questId)
    {
        if (_activePlayer is null) return;
        var result = QuestService.Complete(
            _activePlayer.Quests,
            _activePlayer.AdventureExperience,
            questId);
        if (result.CompletedQuest is null)
        {
            CommandMessage(
                $"Quest '{questId}' was not found or is already complete.",
                warning: true);
            return;
        }
        var oldMaximum = AdventureService.MaximumHealth(
            _activePlayer.AdventureExperience);
        var newMaximum = AdventureService.MaximumHealth(
            result.AdventureExperience);
        _activePlayer = _activePlayer with
        {
            Quests = result.Progress,
            AdventureExperience = result.AdventureExperience,
            Health = Math.Clamp(
                _activePlayer.Health + newMaximum - oldMaximum,
                0, newMaximum),
            UpdatedUtc = DateTime.UtcNow
        };
        _selectedPlayer = _activePlayer;
        _saves.SavePlayer(_activePlayer);
        _completedQuest = result.CompletedQuest;
        _questReturnModal = _modalScreen.Active;
        _modalScreen.Open(ModalScreenKind.QuestComplete);
        CommandMessage($"Completed quest {result.CompletedQuest.Title}.");
    }

    private void UpdateQuestWindowInput(Vector2 pointer, bool leftDown)
    {
        if (_activePlayer is null) return;
        if (_modalScreen.Active == ModalScreenKind.QuestComplete)
        {
            if (leftDown && !_questLeftWasDown &&
                QuestContinueBounds(QuestCompletionBounds()).Contains(pointer))
                CloseQuestWindow();
            _questLeftWasDown = leftDown;
            return;
        }
        LayoutQuestList();
        _questList.UpdatePointer(pointer, leftDown);
        if (leftDown && !_questLeftWasDown)
        {
            var window = QuestWindowBounds();
            if (QuestCloseBounds(window).Contains(pointer) ||
                QuestBackBounds(window).Contains(pointer))
            {
                CloseQuestWindow();
                _questLeftWasDown = leftDown;
                return;
            }
            for (var filterIndex = 0; filterIndex < 3; filterIndex++)
            {
                if (!QuestFilterBounds(window, filterIndex).Contains(pointer))
                    continue;
                _questFilter = (QuestFilter)filterIndex;
                EnsureVisibleQuestSelected();
                _questLeftWasDown = leftDown;
                return;
            }
            var visible = VisibleQuestDefinitions();
            foreach (var index in _questList.VisibleIndices)
                if (_questList.RowBounds(index).Contains(pointer))
                {
                    _selectedQuestId = visible[index].Id;
                    break;
                }
        }
        _questLeftWasDown = leftDown;
    }

    private void ScrollQuestWindow(Vector2 pointer, float offset)
    {
        LayoutQuestList();
        _questList.Scroll(pointer, offset);
    }

    private void LayoutQuestList()
    {
        var window = QuestWindowBounds();
        var definitions = VisibleQuestDefinitions();
        _questList.Layout(
            new(window.X + 24, window.Y + 144, 246, window.W - 212),
            definitions.Select(value => value.Id).ToArray(),
            rowHeight: 58, rowGap: 6, deleteWidth: 0, actionGap: 0);
    }

    private void RenderQuestWindow()
    {
        if (_activePlayer is null) return;
        if (_modalScreen.Active == ModalScreenKind.QuestComplete)
        {
            RenderQuestComplete();
            return;
        }
        var window = QuestWindowBounds();
        var progress = QuestService.Normalize(_activePlayer.Quests);
        DrawAoEPanelBorder(window);
        DrawCenteredMenuTitle(
            "QUEST JOURNAL",
            new(window.X + 44, window.Y + 15, window.Z - 88, 42),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            $"{progress.Count(value => value.Status == QuestStatus.Complete)} " +
            $"OF {QuestService.Definitions.Count} COMPLETED",
            new(window.X + 44, window.Y + 57, window.Z - 88, 22),
            new(166, 153, 119, 255));
        DrawMenuButton(QuestCloseBounds(window), "X");
        RenderQuestFilters(window);
        LayoutQuestList();
        var visible = VisibleQuestDefinitions();
        foreach (var index in _questList.VisibleIndices)
        {
            var definition = visible[index];
            var state = progress.First(value =>
                value.QuestId == definition.Id);
            RenderQuestListRow(index, definition, state);
        }
        RenderListScrollbar(_questList);
        RenderQuestJournal(window, progress);
        DrawMenuButton(QuestBackBounds(window), "Back");
    }

    private void RenderActiveQuestTracker(Vector4 scene)
    {
        if (_activePlayer is null ||
            QuestService.ActiveQuest(_activePlayer.Quests) is not { } active)
            return;
        var objectiveCount = active.Definition.Objectives.Count;
        var width = Math.Min(310, Math.Max(230, scene.Z * .27f));
        var height = 55 + objectiveCount * 25;
        var bounds = new Vector4(
            scene.X + scene.Z - width - 16,
            scene.Y + MinimapControlState.Diameter + 48,
            width, height);
        DrawUiColor(bounds, new(.025f, .024f, .019f, .82f));
        DrawPanelOutline(bounds, 1, new(.34f, .27f, .14f, .9f));
        DrawUiText(
            active.Definition.Title,
            new(bounds.X + 12, bounds.Y + 10),
            new FSColor(231, 198, 91, 255));
        DrawUiColor(
            new(bounds.X + 12, bounds.Y + 37, bounds.Z - 24, 1),
            new(.32f, .25f, .12f, .9f));
        var counts = active.Progress.ObjectiveCounts ??
                     new Dictionary<string, int>();
        var y = bounds.Y + 45;
        foreach (var objective in active.Definition.Objectives)
        {
            var count = Math.Min(
                objective.Required,
                counts.GetValueOrDefault(objective.Id));
            var complete = count >= objective.Required;
            DrawUiText(
                complete ? "-" : ">",
                new(bounds.X + 12, y),
                complete
                    ? new FSColor(104, 181, 105, 255)
                    : new FSColor(224, 191, 92, 255));
            DrawUiText(
                $"{objective.Description}: {count}/{objective.Required}",
                new(bounds.X + 30, y),
                complete
                    ? new FSColor(133, 158, 127, 255)
                    : new FSColor(220, 211, 181, 255));
            y += 25;
        }
    }

    private void RenderQuestListRow(
        int index,
        QuestDefinition definition,
        QuestProgress state)
    {
        var row = _questList.RowBounds(index);
        var selected = definition.Id == _selectedQuestId;
        var hovered = row.Contains(MouseState.Position);
        DrawUiColor(
            row,
            selected
                ? new(.14f, .11f, .052f, .96f)
                : hovered
                    ? new(.085f, .071f, .043f, .94f)
                    : new(.040f, .037f, .030f, .90f));
        DrawPanelOutline(
            row, selected ? 2 : 1,
            selected
                ? new(.62f, .46f, .17f, 1)
                : new(.22f, .18f, .11f, 1));
        DrawUiText(
            QuestStatusMark(state.Status),
            new(row.X + 10, row.Y + 10),
            QuestStatusColor(state.Status));
        DrawUiText(
            definition.Title,
            new(row.X + 38, row.Y + 9),
            state.Status == QuestStatus.Locked
                ? new FSColor(118, 115, 105, 255)
                : new FSColor(218, 205, 166, 255));
        DrawUiText(
            definition.Category,
            new(row.X + 38, row.Y + 34),
            new FSColor(137, 130, 109, 255));
    }

    private void RenderQuestJournal(
        Vector4 window,
        IReadOnlyList<QuestProgress> progress)
    {
        if (_selectedQuestId is null)
        {
            var emptyPanel = new Vector4(
                window.X + 286, window.Y + 104,
                window.Z - 310, window.W - 172);
            DrawUiColor(
                emptyPanel,
                new(.030f, .029f, .024f, .84f));
            DrawPanelOutline(
                emptyPanel, 0, new(.27f, .22f, .12f, 1));
            DrawCenteredUiText(
                "NO QUESTS IN THIS VIEW",
                emptyPanel,
                new(154, 146, 123, 255));
            return;
        }
        var index = Math.Max(
            0,
            QuestService.Definitions.ToList().FindIndex(value =>
                value.Id == _selectedQuestId));
        var definition = QuestService.Definitions[index];
        var state = progress[index];
        var panel = new Vector4(
            window.X + 286, window.Y + 104,
            window.Z - 310, window.W - 172);
        DrawUiColor(panel, new(.030f, .029f, .024f, .84f));
        DrawPanelOutline(panel, 0, new(.27f, .22f, .12f, 1));
        DrawUiText(
            definition.Title.ToUpperInvariant(),
            new(panel.X + 22, panel.Y + 20),
            new FSColor(232, 217, 166, 255));
        DrawUiText(
            definition.Summary,
            new(panel.X + 22, panel.Y + 51),
            new FSColor(167, 158, 132, 255));
        DrawUiColor(
            new(panel.X + 22, panel.Y + 79, panel.Z - 44, 1),
            new(.27f, .22f, .12f, 1));
        var y = panel.Y + 99;
        if (state.Status == QuestStatus.Locked)
        {
            DrawUiText("LOCKED", new(panel.X + 22, y),
                new FSColor(145, 116, 108, 255));
            DrawUiText(
                $"Complete {QuestTitle(definition.PrerequisiteQuestId)} first.",
                new(panel.X + 22, y + 28),
                new FSColor(163, 153, 128, 255));
            return;
        }
        DrawUiText(
            state.Status == QuestStatus.Complete
                ? "JOURNAL"
                : "CURRENT OBJECTIVES",
            new(panel.X + 22, y),
            new FSColor(194, 174, 124, 255));
        y += 30;
        if (state.Status == QuestStatus.Complete)
        {
            DrawUiText(definition.CompletionText, new(panel.X + 22, y),
                new FSColor(190, 181, 151, 255));
        }
        else
        {
            var counts = state.ObjectiveCounts ??
                         new Dictionary<string, int>();
            foreach (var objective in definition.Objectives)
            {
                var count = counts.GetValueOrDefault(objective.Id);
                var complete = count >= objective.Required;
                DrawUiText(
                    complete ? "[x]" : "[ ]",
                    new(panel.X + 22, y),
                    complete
                        ? new FSColor(103, 185, 105, 255)
                        : new FSColor(183, 166, 119, 255));
                DrawUiText(
                    objective.Description,
                    new(panel.X + 54, y),
                    complete
                        ? new FSColor(143, 162, 130, 255)
                        : new FSColor(205, 194, 159, 255));
                y += 28;
            }
        }
        DrawUiText(
            "REWARD",
            new(panel.X + 22, panel.Y + panel.W - 72),
            new FSColor(194, 174, 124, 255));
        DrawUiText(
            $"{definition.AdventureExperience} Adventure XP",
            new(panel.X + 22, panel.Y + panel.W - 44),
            new FSColor(225, 207, 155, 255));
    }

    private void RenderQuestComplete()
    {
        if (_completedQuest is null) return;
        var panel = QuestCompletionBounds();
        DrawAoEPanelBorder(panel);
        DrawCenteredMenuTitle(
            "QUEST COMPLETE",
            new(panel.X + 28, panel.Y + 28, panel.Z - 56, 45),
            new(241, 222, 162, 255));
        DrawCenteredUiText(
            _completedQuest.Title.ToUpperInvariant(),
            new(panel.X + 32, panel.Y + 100, panel.Z - 64, 30),
            new(217, 201, 157, 255));
        DrawCenteredUiText(
            $"{_completedQuest.AdventureExperience} ADVENTURE XP",
            new(panel.X + 32, panel.Y + 151, panel.Z - 64, 30),
            new(224, 183, 86, 255));
        DrawMenuButton(QuestContinueBounds(panel), "Continue");
    }

    private Vector4 QuestWindowBounds()
    {
        var viewport = SceneClientBounds();
        var width = Math.Min(820, viewport.Z - 40);
        var height = Math.Min(620, viewport.W - 40);
        return new(
            viewport.X + (viewport.Z - width) * .5f,
            viewport.Y + (viewport.W - height) * .5f,
            width, height);
    }

    private Vector4 QuestCompletionBounds()
    {
        var viewport = SceneClientBounds();
        return new(
            viewport.X + (viewport.Z - 500) * .5f,
            viewport.Y + (viewport.W - 320) * .5f,
            500, 320);
    }

    private static Vector4 QuestCloseBounds(Vector4 window) =>
        new(window.X + window.Z - 40, window.Y + 12, 28, 28);
    private static Vector4 QuestBackBounds(Vector4 window) =>
        new(window.X + window.Z - 132, window.Y + window.W - 56, 108, 36);
    private static Vector4 QuestContinueBounds(Vector4 panel) =>
        new(panel.X + (panel.Z - 150) * .5f, panel.Y + panel.W - 68, 150, 42);

    private static string QuestStatusMark(QuestStatus status) =>
        status switch
        {
            QuestStatus.Complete => "[x]",
            QuestStatus.InProgress => "[>]",
            QuestStatus.Locked => "[-]",
            _ => "[ ]"
        };

    private static FSColor QuestStatusColor(QuestStatus status) =>
        status switch
        {
            QuestStatus.Complete => new(93, 190, 99, 255),
            QuestStatus.InProgress => new(230, 192, 80, 255),
            QuestStatus.Locked => new(119, 115, 104, 255),
            _ => new(205, 188, 145, 255)
        };

    private static string QuestTitle(string? questId) =>
        QuestService.Definitions.FirstOrDefault(value =>
            value.Id == questId)?.Title ?? "the previous quest";

    private void RenderQuestFilters(Vector4 window)
    {
        var labels = new[] { "ALL", "ACTIVE", "COMPLETE" };
        for (var index = 0; index < labels.Length; index++)
        {
            var bounds = QuestFilterBounds(window, index);
            var selected = (int)_questFilter == index;
            var hovered = bounds.Contains(MouseState.Position);
            DrawUiColor(
                bounds,
                selected
                    ? new(.16f, .125f, .052f, .98f)
                    : hovered
                        ? new(.085f, .072f, .043f, .95f)
                        : new(.038f, .035f, .029f, .90f));
            DrawPanelOutline(
                bounds, selected ? 2 : 1,
                selected
                    ? new(.58f, .43f, .16f, 1)
                    : new(.22f, .18f, .11f, 1));
            DrawCenteredUiText(
                labels[index],
                bounds,
                selected
                    ? new FSColor(236, 215, 159, 255)
                    : new FSColor(155, 147, 123, 255));
        }
    }

    private static Vector4 QuestFilterBounds(Vector4 window, int index)
    {
        const float gap = 4;
        const float width = (246 - gap * 2) / 3;
        return new(
            window.X + 24 + index * (width + gap),
            window.Y + 104,
            width,
            30);
    }

    private IReadOnlyList<QuestDefinition> VisibleQuestDefinitions()
    {
        var progress = QuestService.Normalize(_activePlayer?.Quests);
        return QuestService.Definitions.Where(definition =>
        {
            var status = progress.First(value =>
                value.QuestId == definition.Id).Status;
            return _questFilter switch
            {
                QuestFilter.Active => status == QuestStatus.InProgress,
                QuestFilter.Complete => status == QuestStatus.Complete,
                _ => true
            };
        }).ToArray();
    }

    private void EnsureVisibleQuestSelected()
    {
        var visible = VisibleQuestDefinitions();
        if (visible.Any(value => value.Id == _selectedQuestId))
            return;
        _selectedQuestId = visible.FirstOrDefault()?.Id;
    }
}
