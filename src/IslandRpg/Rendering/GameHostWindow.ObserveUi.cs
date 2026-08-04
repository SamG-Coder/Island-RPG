using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private enum ObserveVillagerTab : byte
    {
        Overview,
        Inventory,
        Skills,
        Memory
    }

    private string? _observedVillagerId;
    private bool _observeRosterLeftWasDown;
    private ObserveVillagerTab _observeVillagerTab;
    private int _observeMemoryOffset;
    private int _observeRosterOffset;
    private const int ObserveVisibleRosterRows = 5;

    private void UpdateObserveUi()
    {
        var scene = SceneClientBounds();
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        UpdateCommandHints();
        var leftDown = MouseState.IsButtonDown(MouseButton.Left);
        var clicked = leftDown && !_observeRosterLeftWasDown;
        _observeRosterLeftWasDown = leftDown;
        if (clicked)
        {
            var pointer = MouseState.Position;
            var rosterEnd = Math.Min(
                _villagers.Count,
                _observeRosterOffset + ObserveVisibleRosterRows);
            for (var index = _observeRosterOffset;
                 index < rosterEnd;
                 index++)
            {
                if (!ObserveVillagerRowBounds(
                        index - _observeRosterOffset).Contains(pointer))
                    continue;
                _observedVillagerId = _villagers[index].Id;
                _observeMemoryOffset = 0;
                SnapCameraToObservedVillager();
                return;
            }
            if (_observedVillagerId is not null)
                for (var tab = 0; tab < 4; tab++)
                    if (ObserveVillagerTabBounds(tab).Contains(pointer))
                    {
                        _observeVillagerTab = (ObserveVillagerTab)tab;
                        _observeMemoryOffset = 0;
                        break;
                    }
        }
        _chatUi.UpdatePointer(MouseState.Position, leftDown);
        if (_commandHints.UpdatePointer(
                MouseState.Position, leftDown) is { } hint)
            CompleteCommandHint(hint);
        UpdateChatCommandInput();
    }

    private void UpdateChatCommandInput()
    {
        if (KeyboardState.IsKeyPressed(Keys.Enter))
        {
            if (_chatUi.Input.Focused)
                _chatUi.Submit();
            else
                _chatUi.FocusInput();
        }
        if (_chatUi.Input.Focused &&
            KeyboardState.IsKeyPressed(Keys.Backspace))
            _chatUi.Backspace();
        if (!_chatUi.Input.Focused || !_commandHints.Visible) return;
        if (KeyboardState.IsKeyPressed(Keys.Up))
            _commandHints.MoveSelection(-1);
        else if (KeyboardState.IsKeyPressed(Keys.Down))
            _commandHints.MoveSelection(1);
        if (KeyboardState.IsKeyPressed(Keys.Tab) &&
            _commandHints.Selected() is { } selected)
            CompleteCommandHint(selected);
    }

    private bool IsPointerOverObserveUi(Vector2 pointer) =>
        ObserveVillagerPanelBounds().Contains(pointer) ||
        _observedVillagerId is not null &&
        ObserveVillagerDetailBounds().Contains(pointer) ||
        _chatUi.BlocksWorldInput(pointer) ||
        _commandHints.HitTest(pointer) ||
        _minimapUi.HitTest(pointer) ||
        _modalScreen.CapturesAllInput;

    private void RenderObserveUi()
    {
        _uiOpacity = _pauseMenu.IsPaused ? .28f : 1f;
        var scene = SceneClientBounds();
        _chatUi.Layout(scene);
        _minimapUi.Layout(scene);
        RenderVillagerOverheadSpeech(scene);
        RenderObserveVillagerRoster();
        RenderObserveVillagerDetails();
        RenderMinimap();
        RenderChatUi();
        RenderWorldClock(scene);
        _uiOpacity = 1;
    }

    private Vector4 ObserveVillagerPanelBounds()
    {
        const float width = 292;
        var height = 42 + Math.Min(
            ObserveVisibleRosterRows, _villagers.Count) * 70;
        return new(12, 12, width, height);
    }

    private Vector4 ObserveVillagerRowBounds(int visibleIndex)
    {
        var panel = ObserveVillagerPanelBounds();
        return new(panel.X + 8, panel.Y + 34 + visibleIndex * 70,
            panel.Z - 16, 62);
    }

    private void RenderObserveVillagerRoster()
    {
        var panel = ObserveVillagerPanelBounds();
        DrawAoEPanelBorder(panel);
        DrawPanelCaption("Survivors", panel);
        _observeRosterOffset = Math.Clamp(
            _observeRosterOffset, 0,
            Math.Max(0, _villagers.Count - ObserveVisibleRosterRows));
        var end = Math.Min(
            _villagers.Count,
            _observeRosterOffset + ObserveVisibleRosterRows);
        for (var index = _observeRosterOffset;
             index < end;
             index++)
        {
            var villager = _villagers[index];
            var row = ObserveVillagerRowBounds(index - _observeRosterOffset);
            var selected = villager.Id == _observedVillagerId;
            var hovered = row.Contains(MouseState.Position);
            DrawRoundedUiColor(
                row, 5,
                selected
                    ? new(.17f, .19f, .11f, .98f)
                    : hovered
                        ? new(.09f, .085f, .06f, .98f)
                        : new(.052f, .048f, .036f, .96f));
            var phase = _npcController.Phase(villager.Id);
            var status = phase is null
                ? $"{villager.Activity} · {villager.Action}"
                : $"{phase} · {villager.Action}";
            var thought = VillagerStatusService.CurrentThought(
                villager, _worldGameSeconds,
                _npcController.IsBusy(villager.Id));
            DrawUiText(villager.Name,
                new(row.X + 9, row.Y + 6),
                villager.Health > 0
                    ? new FSColor(239, 229, 194, 255)
                    : new FSColor(170, 110, 100, 255));
            DrawUiText(TrimObserveText(status, 38),
                new(row.X + 9, row.Y + 25),
                new FSColor(174, 190, 145, 255));
            DrawUiText(TrimObserveText(thought, 43),
                new(row.X + 9, row.Y + 43),
                new FSColor(176, 170, 151, 255));
        }
        if (_villagers.Count > ObserveVisibleRosterRows)
            DrawUiText(
                $"{_observeRosterOffset + 1}-{end} of {_villagers.Count}  ·  scroll",
                new(panel.X + panel.Z - 142, panel.Y + 12),
                new FSColor(157, 151, 129, 255));
    }

    private Vector4 ObserveVillagerDetailBounds() =>
        new(316, 12, 420, 398);

    private Vector4 ObserveVillagerTabBounds(int index)
    {
        var panel = ObserveVillagerDetailBounds();
        const float gap = 4;
        var width = (panel.Z - 24 - gap * 3) / 4;
        return new(
            panel.X + 12 + index * (width + gap),
            panel.Y + 48,
            width,
            28);
    }

    private void RenderObserveVillagerDetails()
    {
        var villager = _villagers.FirstOrDefault(value =>
            value.Id == _observedVillagerId);
        if (villager is null) return;
        var panel = ObserveVillagerDetailBounds();
        DrawAoEPanelBorder(panel);
        DrawPanelCaption(villager.Name, panel);
        string[] names = ["Overview", "Inventory", "Skills", "Memory"];
        for (var index = 0; index < names.Length; index++)
        {
            var bounds = ObserveVillagerTabBounds(index);
            var selected = index == (int)_observeVillagerTab;
            DrawUiColor(
                bounds,
                selected
                    ? new(.22f, .17f, .075f, .98f)
                    : bounds.Contains(MouseState.Position)
                        ? new(.14f, .115f, .060f, .98f)
                        : new(.075f, .067f, .043f, .98f));
            DrawPanelOutline(
                bounds, selected ? 2 : 1,
                selected
                    ? new(.55f, .42f, .18f, 1)
                    : new(.25f, .205f, .12f, 1));
            DrawSmallCenteredUiText(
                names[index], bounds,
                selected
                    ? new(245, 226, 171, 255)
                    : new(194, 184, 151, 255));
        }
        switch (_observeVillagerTab)
        {
            case ObserveVillagerTab.Inventory:
                RenderObserveInventory(villager, panel);
                break;
            case ObserveVillagerTab.Skills:
                RenderObserveSkills(villager, panel);
                break;
            case ObserveVillagerTab.Memory:
                RenderObserveMemories(villager, panel);
                break;
            default:
                RenderObserveOverview(villager, panel);
                break;
        }
    }

    private void RenderObserveOverview(VillagerState villager, Vector4 panel)
    {
        var content = new Vector4(
            panel.X + 12, panel.Y + 84, panel.Z - 24, panel.W - 96);
        DrawUiColor(content, new(.035f, .032f, .027f, .72f));
        DrawPanelOutline(content, 1, new(.19f, .16f, .10f, 1));
        var phase = _npcController.Phase(villager.Id)?.ToString() ??
                    villager.Activity.ToString();
        var thought = VillagerStatusService.CurrentThought(
            villager, _worldGameSeconds,
            _npcController.IsBusy(villager.Id));
        var project = villager.ProjectAssignment;
        var projectText = project is null
            ? "None"
            : ItemCatalog.Get(project.ProjectItemId).Name +
              (project.BuilderId == villager.Id ? " (builder)" : " (helper)");
        var goal = villager.Goals?.Count(value =>
            value.Status == CommitmentStatus.Active) ?? 0;
        var promises = villager.Promises?.Count(value =>
            value.Status == CommitmentStatus.Active) ?? 0;
        var plans = VillagerPromisePlanService.PlansFor(villager).Count;
        var totalSkillLevel = ObserveSkillEntries.Sum(entry =>
            VillagerSkillService.Level(villager, entry.Skill));
        var relationshipSummary = VillagerRelationshipClassifier.Summarize(
            villager.Relationships, villager.RecognizedLeaderId);
        var attractions = villager.Relationships?.Count(relationship =>
            RelationshipAttraction(villager, relationship) !=
            VillagerAttractionLevel.None) ?? 0;
        string[] left =
        [
            $"Status: {phase} / {villager.Action}",
            $"Role: {villager.WorkRole}" +
            (VillagerLeadershipService.IsLeader(villager) ? " / Leader" : ""),
            $"Leadership: {(VillagerLeadershipService.IsLeader(villager) ? "Leader" : "Follower")}",
            $"Need: {villager.Need}",
            $"Health: {villager.Health}",
            $"Hunger: {villager.Hunger:0.0}",
            $"Energy: {villager.Energy:0.0}",
            $"Move efficiency: {VillagerFatigueService.MovementEffectiveness(villager.Energy):P0}",
            $"Work efficiency: {VillagerFatigueService.WorkEffectiveness(villager.Energy):P0}",
            VillagerAdrenalineService.IsActive(villager, _worldGameSeconds)
                ? $"Adrenaline: active · Stress {villager.AdrenalineStress:0}/100"
                : $"Stress: {villager.AdrenalineStress:0}/100",
            $"Replan in: {VillagerStatusService.SecondsUntilDecision(villager, _worldGameSeconds):0.0}s",
            $"Action time: {villager.ActionTime:0.0}s",
            $"Blocked attempts: {villager.BlockedMoveAttempts}",
            $"Conflict: {villager.ConflictIntent}"
        ];
        string[] right =
        [
            $"Inventory: {PlayerInventory.Count(villager.Inventory)}/{villager.Inventory.Length}",
            $"Food carried: {VillagerSimulation.CountFood(villager.Inventory)}",
            $"Total skill level: {totalSkillLevel}",
            $"Goals/promises/plans: {goal}/{promises}/{plans}",
            $"F/B/R/E/A: {relationshipSummary.Friends}/{relationshipSummary.CloseBonds}/{relationshipSummary.Rivals}/{relationshipSummary.Enemies}/{attractions}",
            $"Memories: {villager.Memories?.Count ?? 0}",
            $"Location memories: {villager.LocationMemories?.Count ?? 0}",
            $"Known people: {villager.KnownPeople?.Count ?? 0}",
            $"Leader: {LeaderName(villager)}",
            $"Failed targets: {villager.FailedTargets?.Count ?? 0}",
            $"Project: {TrimObserveText(projectText, 24)}",
            $"S/H/B: {villager.Sociability:0.00} / {villager.Honesty:0.00} / {villager.Boldness:0.00}",
            $"Level/pos: {villager.WorldLevel} / {villager.PositionX:0},{villager.PositionY:0}"
        ];
        for (var index = 0; index < left.Length; index++)
            DrawUiText(
                left[index],
                new(content.X + 8, content.Y + 8 + index * 18),
                new FSColor(205, 196, 165, 255));
        for (var index = 0; index < right.Length; index++)
            DrawUiText(
                right[index],
                new(content.X + 204, content.Y + 8 + index * 18),
                new FSColor(190, 190, 158, 255));
        DrawUiText(
            "Current thought / reason",
            new(content.X + 8, content.Y + 230),
            new FSColor(224, 207, 150, 255));
        DrawUiText(
            TrimObserveText(thought, 60),
            new(content.X + 8, content.Y + 252),
            new FSColor(205, 196, 165, 255));
        if (villager.LastDeliberation is { } trace)
            DrawUiText(
                TrimObserveText(
                    $"Decision {trace.Decision}; action {trace.Action}; " +
                    $"priority {trace.Priority}, risk {trace.Risk}, " +
                    $"willingness {trace.Willingness}", 60),
                new(content.X + 8, content.Y + 274),
                new FSColor(166, 174, 145, 255));
    }

    private string LeaderName(VillagerState villager) =>
        villager.RecognizedLeaderId is not { } leaderId
            ? "None"
            : _villagers.FirstOrDefault(value => value.Id == leaderId)?.Name ??
              "Unknown";

    private void RenderObserveInventory(VillagerState villager, Vector4 panel)
    {
        var inventoryPanel = new InventoryPanelState(
            new(panel.X + 8, panel.Y + 80, panel.Z - 16, panel.W - 88),
            villager.Inventory,
            title: $"Inventory  {PlayerInventory.Count(villager.Inventory)}/" +
                   $"{villager.Inventory.Length}",
            columns: 7,
            showCount: false,
            gridTop: 48);
        RenderInventoryPanel(inventoryPanel, renderDragPreview: false);
        foreach (var slot in inventoryPanel.VisibleSlots)
            if (inventoryPanel.SlotBounds(slot).Contains(MouseState.Position) &&
                villager.Inventory.ElementAtOrDefault(slot) is { } itemId)
            {
                DrawCenteredUiText(
                    ItemCatalog.Get(itemId).Name,
                    new(panel.X + 12, panel.Y + panel.W - 34,
                        panel.Z - 24, 22),
                    new FSColor(224, 213, 175, 255));
                break;
            }
    }

    private static readonly (SkillType Skill, string Name)[]
        ObserveSkillEntries =
        [
            (SkillType.Attack, "Attack"),
            (SkillType.Strength, "Strength"),
            (SkillType.Defence, "Defence"),
            (SkillType.Woodcutting, "Woodcutting"),
            (SkillType.Farming, "Farming"),
            (SkillType.Fishing, "Fishing"),
            (SkillType.Cooking, "Cooking"),
            (SkillType.Firemaking, "Firemaking"),
            (SkillType.Crafting, "Crafting"),
            (SkillType.Digging, "Digging"),
            (SkillType.Mining, "Mining")
        ];

    private void RenderObserveSkills(VillagerState villager, Vector4 panel)
    {
        var startY = panel.Y + 86;
        for (var index = 0; index < ObserveSkillEntries.Length; index++)
        {
            var entry = ObserveSkillEntries[index];
            var column = index % 2;
            var row = index / 2;
            var bounds = new Vector4(
                panel.X + 12 + column * 199,
                startY + row * 48,
                195,
                43);
            var experience = VillagerSkillService.Experience(
                villager, entry.Skill);
            var level = VillagerSkillService.Level(villager, entry.Skill);
            DrawUiColor(bounds, new(.050f, .046f, .036f, .98f));
            DrawPanelOutline(bounds, 1, new(.25f, .205f, .12f, 1));
            DrawSkillIcon(
                entry.Skill,
                new(bounds.X + 5, bounds.Y + 5, 32, 32));
            DrawUiText(
                $"{entry.Name}  {level}",
                new(bounds.X + 42, bounds.Y + 5),
                new FSColor(224, 213, 175, 255));
            DrawUiText(
                $"{experience:N0} XP",
                new(bounds.X + 42, bounds.Y + 23),
                new FSColor(165, 174, 145, 255));
        }
    }

    private void RenderObserveMemories(VillagerState villager, Vector4 panel)
    {
        const int visible = 5;
        var memories = villager.Memories?
            .OrderByDescending(value => value.GameSeconds)
            .ToArray() ?? [];
        _observeMemoryOffset = Math.Clamp(
            _observeMemoryOffset, 0, Math.Max(0, memories.Length - visible));
        DrawUiText(
            $"Persisted memories  {memories.Length}",
            new(panel.X + 14, panel.Y + 86),
            new FSColor(224, 207, 150, 255));
        var page = memories.Skip(_observeMemoryOffset).Take(visible).ToArray();
        if (page.Length == 0)
        {
            DrawUiText(
                "No memories recorded.",
                new(panel.X + 14, panel.Y + 120),
                new FSColor(176, 170, 151, 255));
            return;
        }
        for (var index = 0; index < page.Length; index++)
        {
            var memory = page[index];
            var bounds = new Vector4(
                panel.X + 12, panel.Y + 112 + index * 53,
                panel.Z - 24, 48);
            DrawUiColor(bounds, new(.050f, .046f, .036f, .98f));
            DrawPanelOutline(bounds, 1, new(.22f, .185f, .11f, 1));
            DrawUiText(
                TrimObserveText(memory.Summary ?? memory.Kind, 58),
                new(bounds.X + 8, bounds.Y + 6),
                new FSColor(215, 204, 170, 255));
            DrawUiText(
                $"{memory.Kind}  confidence {memory.Confidence:P0}",
                new(bounds.X + 8, bounds.Y + 26),
                new FSColor(151, 164, 137, 255));
        }
        DrawUiText(
            "Mouse wheel to browse",
            new(panel.X + panel.Z - 150, panel.Y + panel.W - 20),
            new FSColor(142, 137, 119, 255));
    }

    private bool ScrollObserveMemories(Vector2 pointer, float offset)
    {
        if (_observedVillagerId is null ||
            _observeVillagerTab != ObserveVillagerTab.Memory ||
            !ObserveVillagerDetailBounds().Contains(pointer) ||
            offset == 0)
            return false;
        var count = _villagers.FirstOrDefault(value =>
            value.Id == _observedVillagerId)?.Memories?.Count ?? 0;
        _observeMemoryOffset = Math.Clamp(
            _observeMemoryOffset - Math.Sign(offset),
            0,
            Math.Max(0, count - 5));
        return true;
    }

    private bool ScrollObserveRoster(Vector2 pointer, float offset)
    {
        if (!ObserveVillagerPanelBounds().Contains(pointer) || offset == 0 ||
            _villagers.Count <= ObserveVisibleRosterRows)
            return false;
        _observeRosterOffset = Math.Clamp(
            _observeRosterOffset - Math.Sign(offset),
            0,
            _villagers.Count - ObserveVisibleRosterRows);
        return true;
    }

    private static string TrimObserveText(string value, int maximum) =>
        value.Length <= maximum
            ? value
            : value[..Math.Max(1, maximum - 1)] + "…";

    private void SnapCameraToObservedVillager()
    {
        var villager = _villagers.FirstOrDefault(value =>
            value.Id == _observedVillagerId && value.Health > 0);
        if (villager is null)
        {
            _observedVillagerId = null;
            return;
        }
        var terrain = SamplePlayerTerrain(
            villager.PositionX, villager.PositionY);
        var projected = IsometricTerrainProjection.Project(
            villager.PositionX, villager.PositionY, terrain.Height);
        _camera = -projected * _zoom;
    }
}
