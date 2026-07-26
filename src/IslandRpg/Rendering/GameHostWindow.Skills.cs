using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
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
                for (var index = 0; index < 4; index++)
                    if (SkillPanelLayout.ListItemBounds(panel, index)
                        .Contains(pointer))
                    {
                        _selectedSkill = index;
                        break;
                    }
            }
            else if (SkillPanelLayout.BackButtonBounds(panel)
                     .Contains(pointer))
                _selectedSkill = -1;
            else if (_selectedSkill == 2 &&
                     SkillPanelLayout.ActionButtonBounds(panel)
                         .Contains(pointer))
                OpenCraftingWindow();
        }
        _skillsLeftWasDown = leftDown;
    }

    private void RenderSkillsPanel()
    {
        var panel = _gameUi.Panel.Bounds;
        DrawPanelCaption("Skills", panel);
        if (_selectedSkill < 0)
        {
            DrawSkillListItem(
                panel, 0, "Woodcutting",
                WoodcuttingSkill.LevelForExperience(
                    _activePlayer?.WoodcuttingExperience ?? 0));
            DrawSkillListItem(
                panel, 1, "Farming",
                FarmingSkill.LevelForExperience(
                    _activePlayer?.FarmingExperience ?? 0));
            DrawSkillListItem(
                panel, 2, "Crafting",
                CraftingSkill.LevelForExperience(
                    _activePlayer?.CraftingExperience ?? 0));
            DrawSkillListItem(
                panel, 3, "Fishing",
                FishingSkill.LevelForExperience(
                    _activePlayer?.FishingExperience ?? 0));
            return;
        }

        var farming = _selectedSkill == 1;
        var crafting = _selectedSkill == 2;
        var fishing = _selectedSkill == 3;
        var name = fishing ? "Fishing" : crafting ? "Crafting" :
            farming ? "Farming" : "Woodcutting";
        var experience = fishing
            ? _activePlayer?.FishingExperience ?? 0
            : crafting
            ? _activePlayer?.CraftingExperience ?? 0
            : farming
                ? _activePlayer?.FarmingExperience ?? 0
                : _activePlayer?.WoodcuttingExperience ?? 0;
        var level = fishing
            ? FishingSkill.LevelForExperience(experience)
            : crafting
            ? CraftingSkill.LevelForExperience(experience)
            : farming
                ? FarmingSkill.LevelForExperience(experience)
                : WoodcuttingSkill.LevelForExperience(experience);
        var maximumLevel = fishing
            ? FishingSkill.MaximumLevel
            : crafting
            ? CraftingSkill.MaximumLevel
            : farming
                ? FarmingSkill.MaximumLevel
                : WoodcuttingSkill.MaximumLevel;
        var currentFloor = fishing
            ? FishingSkill.ExperienceForLevel(level)
            : crafting
            ? CraftingSkill.ExperienceForLevel(level)
            : farming
                ? FarmingSkill.ExperienceForLevel(level)
                : WoodcuttingSkill.ExperienceForLevel(level);
        var nextFloor = level >= maximumLevel
            ? currentFloor
            : fishing
                ? FishingSkill.ExperienceForLevel(level + 1)
                : crafting
                ? CraftingSkill.ExperienceForLevel(level + 1)
                : farming
                    ? FarmingSkill.ExperienceForLevel(level + 1)
                    : WoodcuttingSkill.ExperienceForLevel(level + 1);
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
            panel, farming, crafting, fishing, level, experience);
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
            "< Back", back, new(224, 213, 175, 255));
        DrawCenteredUiText(
            name, SkillPanelLayout.TitleBounds(panel),
            new(234, 221, 177, 255));
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
        int level,
        int experience)
    {
        var info = SkillPanelLayout.InformationBounds(panel);
        DrawUiColor(info, new(.052f, .047f, .035f, .96f));
        DrawPanelOutline(info, 1, new(.25f, .205f, .115f, 1));
        var remaining = fishing
            ? FishingSkill.ExperienceToNextLevel(experience)
            : crafting
            ? CraftingSkill.ExperienceToNextLevel(experience)
            : farming
                ? FarmingSkill.ExperienceToNextLevel(experience)
                : WoodcuttingSkill.ExperienceToNextLevel(experience);
        DrawUiText(
            remaining == 0 ? $"Total XP: {experience}" :
            $"{remaining} XP to next level",
            new(info.X + 9, info.Y + 9),
            new(194, 184, 151, 255));
        DrawUiText(
            fishing ? "Levels unlock more difficult fish" :
            crafting ? "Browse learned recipes" :
            farming ? "Plant seeds to gain XP" :
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

    private void DrawSkillListItem(
        Vector4 panel, int index, string name, int level)
    {
        var bounds = SkillPanelLayout.ListItemBounds(panel, index);
        var hovered = bounds.Contains(MouseState.Position);
        DrawUiColor(
            bounds,
            hovered
                ? new(.18f, .145f, .075f, .98f)
                : new(.075f, .064f, .043f, .96f));
        DrawPanelOutline(bounds, 1, new(.35f, .27f, .13f, 1));
        DrawUiText(
            name,
            new(bounds.X + 10, bounds.Y + 9),
            new(229, 218, 177, 255));
        DrawUiText(
            $"Level {level}",
            new(bounds.X + 10, bounds.Y + 31),
            new(190, 181, 150, 255));
    }
}
