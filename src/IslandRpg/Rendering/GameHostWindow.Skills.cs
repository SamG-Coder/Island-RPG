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
            var skills = SkillOverview();
            foreach (var index in _skillsList.VisibleIndices)
                DrawSkillListItem(
                    _skillsList.RowBounds(index),
                    skills[index].Skill,
                    skills[index].Name,
                    skills[index].Level);
            RenderListScrollbar(_skillsList);
            return;
        }

        var farming = _selectedSkill == 1;
        var crafting = _selectedSkill == 2;
        var fishing = _selectedSkill == 3;
        var cooking = _selectedSkill == 4;
        var firemaking = _selectedSkill == 5;
        var digging = _selectedSkill == 6;
        var name = digging ? "Digging" :
            firemaking ? "Firemaking" :
            cooking ? "Cooking" :
            fishing ? "Fishing" : crafting ? "Crafting" :
            farming ? "Farming" : "Woodcutting";
        var experience = digging
            ? _activePlayer?.DiggingExperience ?? 0
            : firemaking
            ? _activePlayer?.FiremakingExperience ?? 0
            : cooking
            ? _activePlayer?.CookingExperience ?? 0
            : fishing
            ? _activePlayer?.FishingExperience ?? 0
            : crafting
            ? _activePlayer?.CraftingExperience ?? 0
            : farming
                ? _activePlayer?.FarmingExperience ?? 0
                : _activePlayer?.WoodcuttingExperience ?? 0;
        var level = digging
            ? DiggingSkill.LevelForExperience(experience)
            : firemaking
            ? FiremakingSkill.LevelForExperience(experience)
            : cooking
            ? CookingSkill.LevelForExperience(experience)
            : fishing
            ? FishingSkill.LevelForExperience(experience)
            : crafting
            ? CraftingSkill.LevelForExperience(experience)
            : farming
                ? FarmingSkill.LevelForExperience(experience)
                : WoodcuttingSkill.LevelForExperience(experience);
        var maximumLevel = digging
            ? DiggingSkill.MaximumLevel
            : firemaking
            ? FiremakingSkill.MaximumLevel
            : cooking
            ? CookingSkill.MaximumLevel
            : fishing
            ? FishingSkill.MaximumLevel
            : crafting
            ? CraftingSkill.MaximumLevel
            : farming
                ? FarmingSkill.MaximumLevel
                : WoodcuttingSkill.MaximumLevel;
        var currentFloor = digging
            ? DiggingSkill.ExperienceForLevel(level)
            : firemaking
            ? FiremakingSkill.ExperienceForLevel(level)
            : cooking
            ? CookingSkill.ExperienceForLevel(level)
            : fishing
            ? FishingSkill.ExperienceForLevel(level)
            : crafting
            ? CraftingSkill.ExperienceForLevel(level)
            : farming
                ? FarmingSkill.ExperienceForLevel(level)
                : WoodcuttingSkill.ExperienceForLevel(level);
        var nextFloor = level >= maximumLevel
            ? currentFloor
            : digging
                ? DiggingSkill.ExperienceForLevel(level + 1)
                : firemaking
                ? FiremakingSkill.ExperienceForLevel(level + 1)
                : cooking
                ? CookingSkill.ExperienceForLevel(level + 1)
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
            panel, farming, crafting, fishing, cooking, firemaking, digging,
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
        int level,
        int experience)
    {
        var info = SkillPanelLayout.InformationBounds(panel);
        DrawUiColor(info, new(.052f, .047f, .035f, .96f));
        DrawPanelOutline(info, 1, new(.25f, .205f, .115f, 1));
        var remaining = digging
            ? DiggingSkill.ExperienceToNextLevel(experience)
            : firemaking
            ? FiremakingSkill.ExperienceToNextLevel(experience)
            : cooking
            ? CookingSkill.ExperienceToNextLevel(experience)
            : fishing
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
            digging ? "Excavate tougher ground more quickly" :
            firemaking ? "Longer, larger and brighter fires" :
            cooking ? "Higher levels reduce burning" :
            fishing ? "Unlocks more fish" :
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

    private void LayoutSkillsList()
    {
        var skills = SkillOverview();
        _skillsList.Layout(
            SkillPanelLayout.ListBounds(_gameUi.Panel.Bounds),
            skills.Select(skill => skill.Skill.ToString()).ToArray(),
            rowHeight: 54,
            rowGap: 6,
            deleteWidth: 0,
            actionGap: 0);
    }

    private (SkillType Skill, string Name, int Level)[] SkillOverview() =>
    [
        (
            SkillType.Woodcutting,
            "Woodcutting",
            WoodcuttingSkill.LevelForExperience(
                _activePlayer?.WoodcuttingExperience ?? 0)),
        (
            SkillType.Farming,
            "Farming",
            FarmingSkill.LevelForExperience(
                _activePlayer?.FarmingExperience ?? 0)),
        (
            SkillType.Crafting,
            "Crafting",
            CraftingSkill.LevelForExperience(
                _activePlayer?.CraftingExperience ?? 0)),
        (
            SkillType.Fishing,
            "Fishing",
            FishingSkill.LevelForExperience(
                _activePlayer?.FishingExperience ?? 0)),
        (
            SkillType.Cooking,
            "Cooking",
            CookingSkill.LevelForExperience(
                _activePlayer?.CookingExperience ?? 0)),
        (
            SkillType.Firemaking,
            "Firemaking",
            FiremakingSkill.LevelForExperience(
                _activePlayer?.FiremakingExperience ?? 0)),
        (
            SkillType.Digging,
            "Digging",
            DiggingSkill.LevelForExperience(
                _activePlayer?.DiggingExperience ?? 0))
    ];

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
