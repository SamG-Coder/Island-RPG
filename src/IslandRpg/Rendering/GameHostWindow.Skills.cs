using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private static readonly (SkillType Skill, string Name)[] SkillListEntries =
        Enum.GetValues<SkillType>()
            .Select(skill => (skill, skill.ToString()))
            .ToArray();
    private static readonly string[] SkillListIds =
        SkillListEntries
            .Select(entry => entry.Skill.ToString())
            .ToArray();

    private void UpdateSkillsPanelInput(Vector2 pointer, bool leftDown)
    {
        if (_craftingWindowOpen ||
            _gameUi.ActivePanel != GameUiPanel.Skills)
        {
            _skillsLeftWasDown = leftDown;
            return;
        }
        if (leftDown && !_skillsLeftWasDown)
        {
            var panel = _gameUi.Panel.Bounds;
            if (_selectedSkill < 0)
            {
                LayoutSkillsList();
                _skillsList.UpdatePointer(pointer, leftDown);
                if (_skillsList.TryHit(
                        pointer, out var index, out _))
                    _selectedSkill = index;
            }
            else if (SkillPanelLayout.BackButtonBounds(panel)
                     .Contains(pointer))
                _selectedSkill = -1;
            else if (SkillGuideService.IsSupported(
                         (SkillType)_selectedSkill) &&
                     SkillPanelLayout.TitleBounds(panel)
                         .Contains(pointer))
                OpenSkillGuideWindow((SkillType)_selectedSkill);
            else if (_selectedSkill == 2 &&
                     SkillPanelLayout.ActionButtonBounds(panel)
                         .Contains(pointer))
                OpenCraftingWindow();
        }
        else if (_selectedSkill < 0)
        {
            LayoutSkillsList();
            _skillsList.UpdatePointer(pointer, leftDown);
        }
        _skillsLeftWasDown = leftDown;
    }

    private void RenderSkillsPanel()
    {
        var panel = _gameUi.Panel.Bounds;
        DrawPanelCaption("Skills", panel);
        if (_selectedSkill < 0)
        {
            LayoutSkillsList();
            foreach (var index in _skillsList.VisibleIndices)
            {
                var entry = SkillListEntries[index];
                DrawSkillListItem(
                    _skillsList.RowBounds(index),
                    entry.Skill,
                    entry.Name,
                    SkillService.LevelForExperience(
                        SkillExperience(entry.Skill)));
            }
            RenderListScrollbar(_skillsList);
            return;
        }

        var skill = (SkillType)_selectedSkill;
        var farming = skill == SkillType.Farming;
        var crafting = skill == SkillType.Crafting;
        var fishing = skill == SkillType.Fishing;
        var cooking = skill == SkillType.Cooking;
        var firemaking = skill == SkillType.Firemaking;
        var digging = skill == SkillType.Digging;
        var mining = skill == SkillType.Mining;
        var name = skill.ToString();
        var experience = SkillExperience(skill);
        var level = SkillService.LevelForExperience(experience);
        var maximumLevel = SkillService.MaximumLevel;
        var currentFloor = SkillService.ExperienceForLevel(level);
        var nextFloor = level >= maximumLevel
            ? currentFloor
            : SkillService.ExperienceForLevel(level + 1);
        var progress = level >= maximumLevel
            ? 1f
            : (experience - currentFloor) /
              (float)Math.Max(1, nextFloor - currentFloor);

        RenderSkillNavigation(panel, name);
        RenderSkillLevelCard(panel, level, maximumLevel);
        RenderSkillProgress(
            panel, experience, level, maximumLevel,
            currentFloor, nextFloor, progress);
        RenderSkillInformation(
            panel, farming, crafting, fishing, cooking, firemaking, digging,
            mining,
            level, experience);
        if (crafting) RenderSkillAction(panel);
    }

    private void RenderSkillNavigation(Vector4 panel, string name)
    {
        var back = SkillPanelLayout.BackButtonBounds(panel);
        var hovered = back.Contains(MouseState.Position);
        DrawUiColor(
            back,
            hovered
                ? new(.22f, .17f, .075f, .98f)
                : new(.10f, .085f, .050f, .98f));
        DrawPanelOutline(back, 1, new(.42f, .32f, .15f, 1));
        DrawCenteredUiText(
            "Back", back, new(224, 213, 175, 255));
        var title = SkillPanelLayout.TitleBounds(panel);
        var opensGuide = SkillGuideService.IsSupported(
            (SkillType)_selectedSkill);
        if (opensGuide)
        {
            DrawUiColor(
                title,
                title.Contains(MouseState.Position)
                    ? new(.25f, .19f, .075f, .98f)
                    : new(.12f, .10f, .052f, .98f));
            DrawPanelOutline(title, 1, new(.48f, .36f, .15f, 1));
        }
        DrawCenteredUiText(
            name, title, new(234, 221, 177, 255));
    }

    private void RenderSkillLevelCard(
        Vector4 panel, int level, int maximumLevel)
    {
        var card = SkillPanelLayout.LevelCardBounds(panel);
        DrawUiColor(card, new(.075f, .064f, .042f, .97f));
        DrawPanelOutline(card, 1, new(.34f, .27f, .13f, 1));
        DrawCenteredUiText(
            $"Level {level}  /  {maximumLevel}",
            card, new(215, 203, 165, 255));
    }

    private void RenderSkillProgress(
        Vector4 panel,
        int experience,
        int level,
        int maximumLevel,
        int currentFloor,
        int nextFloor,
        float progress)
    {
        var track = SkillPanelLayout.ProgressBounds(panel);
        DrawUiColor(track, new(.030f, .028f, .023f, .98f));
        if (progress > 0)
            DrawUiColor(
                new(
                    track.X + 2, track.Y + 2,
                    MathF.Round((track.Z - 4) * progress),
                    track.W - 4),
                new(.39f, .52f, .18f, 1));
        DrawPanelOutline(track, 0, new(.28f, .22f, .12f, 1));
        DrawCenteredUiText(
            level >= maximumLevel
                ? "Maximum level"
                : $"{experience - currentFloor}/" +
                  $"{nextFloor - currentFloor} XP",
            track, new(238, 227, 188, 255));
    }

    private void RenderSkillInformation(
        Vector4 panel,
        bool farming,
        bool crafting,
        bool fishing,
        bool cooking,
        bool firemaking,
        bool digging,
        bool mining,
        int level,
        int experience)
    {
        var info = SkillPanelLayout.InformationBounds(panel);
        DrawUiColor(info, new(.052f, .047f, .035f, .96f));
        DrawPanelOutline(info, 1, new(.25f, .205f, .115f, 1));
        var remaining = SkillService.ExperienceToNextLevel(experience);
        DrawUiText(
            remaining == 0 ? $"Total XP: {experience}" :
            $"{remaining} XP to next level",
            new(info.X + 9, info.Y + 9),
            new(194, 184, 151, 255));
        DrawUiText(
            mining ? $"Hit chance: {MiningSkill.HitChance(level) * 100:0}%" :
            digging ? "Excavate tougher ground more quickly" :
            firemaking ? "Longer, larger and brighter fires" :
            cooking ? "Higher levels reduce burning" :
            fishing ? "Unlocks more fish" :
            crafting ? "Browse learned recipes" :
            farming ? "Plant seeds and forage berries to gain XP" :
            $"Hit chance: {WoodcuttingSkill.HitChance(level) * 100:0}%",
            new(info.X + 9, info.Y + 31),
            new(184, 175, 145, 255));
    }

    private void RenderSkillAction(Vector4 panel)
    {
        var action = SkillPanelLayout.ActionButtonBounds(panel);
        DrawUiColor(
            action,
            action.Contains(MouseState.Position)
                ? new(.28f, .21f, .085f, .98f)
                : new(.14f, .11f, .055f, .98f));
        DrawPanelOutline(action, 1, new(.52f, .39f, .16f, 1));
        DrawCenteredUiText(
            "Open Recipes", action,
            new(235, 222, 178, 255));
    }

    private void LayoutSkillsList()
    {
        _skillsList.Layout(
            SkillPanelLayout.ListBounds(_gameUi.Panel.Bounds),
            SkillListIds,
            rowHeight: 54,
            rowGap: 6,
            deleteWidth: 0,
            actionGap: 0);
    }

    private int SkillExperience(SkillType skill) => skill switch
    {
        SkillType.Woodcutting =>
            _activePlayer?.WoodcuttingExperience ?? 0,
        SkillType.Farming =>
            _activePlayer?.FarmingExperience ?? 0,
        SkillType.Crafting =>
            _activePlayer?.CraftingExperience ?? 0,
        SkillType.Fishing =>
            _activePlayer?.FishingExperience ?? 0,
        SkillType.Cooking =>
            _activePlayer?.CookingExperience ?? 0,
        SkillType.Firemaking =>
            _activePlayer?.FiremakingExperience ?? 0,
        SkillType.Digging =>
            _activePlayer?.DiggingExperience ?? 0,
        SkillType.Mining =>
            _activePlayer?.MiningExperience ?? 0,
        _ => 0
    };

    private void DrawSkillListItem(
        Vector4 bounds, SkillType skill, string name, int level)
    {
        var hovered = bounds.Contains(MouseState.Position);
        var accent = skill switch
        {
            SkillType.Woodcutting => new Vector4(.31f, .57f, .20f, 1),
            SkillType.Farming => new Vector4(.57f, .55f, .20f, 1),
            SkillType.Crafting => new Vector4(.63f, .38f, .14f, 1),
            SkillType.Fishing => new Vector4(.20f, .46f, .66f, 1),
            SkillType.Cooking => new Vector4(.72f, .32f, .12f, 1),
            SkillType.Digging => new Vector4(.48f, .34f, .18f, 1),
            SkillType.Mining => new Vector4(.58f, .61f, .65f, 1),
            _ => new Vector4(.88f, .20f, .06f, 1)
        };
        DrawUiColor(
            bounds,
            hovered
                ? new(.15f, .13f, .075f, .98f)
                : new(.060f, .055f, .040f, .97f));
        DrawUiColor(
            new(bounds.X + 3, bounds.Y + 3, 4, bounds.W - 6),
            accent);
        DrawPanelOutline(
            bounds, hovered ? 2 : 1,
            hovered
                ? new(.49f, .38f, .17f, 1)
                : new(.27f, .22f, .13f, 1));
        DrawUiText(
            name,
            new(bounds.X + 13, bounds.Y + 10),
            new(229, 218, 177, 255));
        var badge = new Vector4(
            bounds.X + bounds.Z - 57,
            bounds.Y + 8,
            48,
            24);
        DrawUiColor(badge, new(.035f, .032f, .025f, .92f));
        DrawPanelOutline(badge, 1, new(.25f, .21f, .13f, 1));
        DrawCenteredUiText(
            $"Lv {level}", badge,
            level >= SkillService.MaximumLevel
                ? new(210, 190, 105, 255)
                : new(192, 183, 151, 255));
        var track = new Vector4(
            bounds.X + 13,
            bounds.Y + bounds.W - 12,
            bounds.Z - 26,
            5);
        DrawUiColor(track, new(.025f, .024f, .020f, .95f));
        DrawUiColor(
            new(
                track.X, track.Y,
                track.Z * level / SkillService.MaximumLevel,
                track.W),
            accent);
    }
}
