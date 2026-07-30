using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private static readonly (SkillType Skill, string Name)[] SkillListEntries =
    [
        (SkillType.Adventure, "Adventure"),
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
                for (var index = 0;
                     index < SkillListEntries.Length;
                     index++)
                {
                    var cell = SkillGridCell(panel, index);
                    if (!SkillCellVisible(panel, cell) ||
                        !cell.Contains(pointer))
                        continue;
                    _selectedSkill = index;
                    break;
                }
            }
            else if (SkillPanelLayout.BackButtonBounds(panel)
                     .Contains(pointer))
                _selectedSkill = -1;
            else if (SkillGuideService.IsSupported(
                         SelectedSkill()) &&
                     SkillPanelLayout.TitleBounds(panel)
                         .Contains(pointer))
                OpenSkillGuideWindow(SelectedSkill());
            else if (SelectedSkill() == SkillType.Crafting &&
                     SkillPanelLayout.ActionButtonBounds(panel)
                         .Contains(pointer))
                OpenCraftingWindow();
        }
        _skillsLeftWasDown = leftDown;
    }

    private void RenderSkillsPanel()
    {
        var panel = _gameUi.Panel.Bounds;
        if (_selectedSkill < 0)
        {
            var totalLevel = SkillListEntries.Sum(entry =>
                SkillLevel(entry.Skill));
            DrawPanelCaption(
                $"Skills   Total {totalLevel}", panel);
            var content = SkillPanelLayout.ListBounds(panel);
            DrawUiColor(
                new(
                    content.X - 5,
                    content.Y - 5,
                    content.Z + 10,
                    content.W + 10),
                new(.035f, .032f, .027f, .58f));
            DrawPanelOutline(
                new(
                    content.X - 5,
                    content.Y - 5,
                    content.Z + 10,
                    content.W + 10),
                0,
                new(.19f, .16f, .10f, 1));
            for (var index = 0;
                 index < SkillListEntries.Length;
                 index++)
            {
                var entry = SkillListEntries[index];
                var cell = SkillGridCell(panel, index);
                if (!SkillCellVisible(panel, cell))
                    continue;
                DrawSkillGridItem(
                    cell,
                    entry.Skill,
                    SkillLevel(entry.Skill));
            }
            DrawSkillsScrollbar(panel);
            return;
        }

        var skill = SelectedSkill();
        var farming = skill == SkillType.Farming;
        var crafting = skill == SkillType.Crafting;
        var fishing = skill == SkillType.Fishing;
        var cooking = skill == SkillType.Cooking;
        var firemaking = skill == SkillType.Firemaking;
        var digging = skill == SkillType.Digging;
        var mining = skill == SkillType.Mining;
        var name = skill.ToString();
        DrawPanelCaption(name, panel);
        var experience = SkillExperience(skill);
        var level = SkillLevel(skill);
        var maximumLevel = SkillMaximumLevel(skill);
        var currentFloor = SkillExperienceForLevel(skill, level);
        var nextFloor = level >= maximumLevel
            ? currentFloor
            : SkillExperienceForLevel(skill, level + 1);
        var progress = level >= maximumLevel
            ? 1f
            : (experience - currentFloor) /
              (float)Math.Max(1, nextFloor - currentFloor);

        RenderSkillNavigation(panel, name);
        RenderSkillLevelCard(
            panel, skill, level, maximumLevel);
        RenderSkillProgress(
            panel, experience, level, maximumLevel,
            currentFloor, nextFloor, progress);
        RenderSkillInformation(
            panel, skill, farming, crafting, fishing, cooking, firemaking, digging,
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
            "< Skills", back, new(224, 213, 175, 255));
        var title = SkillPanelLayout.TitleBounds(panel);
        var opensGuide = SkillGuideService.IsSupported(
            SelectedSkill());
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
            opensGuide ? "Guide" : name,
            title, new(234, 221, 177, 255));
    }

    private void RenderSkillLevelCard(
        Vector4 panel,
        SkillType skill,
        int level,
        int maximumLevel)
    {
        var card = SkillPanelLayout.LevelCardBounds(panel);
        var accent = SkillAccent(skill);
        DrawUiColor(card, new(.048f, .043f, .032f, .98f));
        DrawPanelOutline(card, 0, new(.025f, .022f, .018f, 1));
        DrawPanelOutline(card, 1, new(.31f, .25f, .135f, 1));
        var iconCenterX = MathF.Round(card.X + 34);
        var iconCenterY = MathF.Round(card.Y + card.W * .5f);
        DrawUiCircle(
            iconCenterX, iconCenterY, 24,
            new(.025f, .022f, .018f, 1));
        DrawUiCircle(
            iconCenterX, iconCenterY, 21,
            new(
                accent.X * .38f,
                accent.Y * .38f,
                accent.Z * .38f,
                1));
        DrawUiCircle(
            iconCenterX, iconCenterY, 18,
            new(.060f, .054f, .040f, 1));
        DrawSkillIcon(
            skill,
            new(iconCenterX - 16, iconCenterY - 16, 32, 32));
        var levelArea = new Vector4(
            card.X + 64,
            card.Y + 8,
            card.Z - 70,
            25);
        DrawCenteredUiText(
            $"LEVEL {level}",
            levelArea,
            level >= maximumLevel
                ? new(225, 202, 111, 255)
                : new(235, 221, 177, 255));
        DrawSmallCenteredUiText(
            $"of {maximumLevel}",
            new(levelArea.X, card.Y + 34, levelArea.Z, 16),
            new(170, 161, 133, 255));
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
        DrawUiColor(track, new(.048f, .043f, .032f, .98f));
        DrawPanelOutline(track, 0, new(.25f, .20f, .11f, 1));
        var bar = new Vector4(
            track.X + 8,
            track.Y + 17,
            track.Z - 16,
            10);
        DrawUiColor(bar, new(.025f, .023f, .019f, .98f));
        if (progress > 0)
            DrawUiColor(
                new(
                    bar.X + 1, bar.Y + 1,
                    MathF.Round((bar.Z - 2) * progress),
                    bar.W - 2),
                new(.39f, .52f, .18f, 1));
        DrawPanelOutline(bar, 0, new(.30f, .24f, .12f, 1));
        DrawSmallCenteredUiText(
            level >= maximumLevel
                ? "Maximum level"
                : $"{experience - currentFloor}/" +
                  $"{nextFloor - currentFloor} XP",
            new(track.X + 4, track.Y + 1, track.Z - 8, 15),
            new(238, 227, 188, 255));
    }

    private void RenderSkillInformation(
        Vector4 panel,
        SkillType skill,
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
        DrawUiColor(info, new(.044f, .040f, .031f, .98f));
        DrawPanelOutline(info, 1, new(.25f, .205f, .115f, 1));
        var remaining = SkillExperienceToNextLevel(skill, experience);
        DrawSmallCenteredUiText(
            remaining == 0 ? $"Total XP: {experience}" :
            $"{remaining} XP to next level",
            new(info.X + 5, info.Y + 6, info.Z - 10, 15),
            new(194, 184, 151, 255));
        var lines = SkillBenefitLines(
            skill, farming, crafting, fishing, cooking,
            firemaking, digging, mining, level);
        for (var index = 0; index < lines.Length; index++)
            DrawSmallCenteredUiText(
                lines[index],
                new(
                    info.X + 5,
                    info.Y + 26 + index * 14,
                    info.Z - 10,
                    14),
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

    private Vector4 SkillGridCell(Vector4 panel, int index)
    {
        const float gap = 5;
        var list = SkillPanelLayout.ListBounds(panel);
        var width = (list.Z - gap) * .5f;
        var row = index / 2;
        var column = index % 2;
        return new(
            list.X + column * (width + gap),
            list.Y + (row - _skillsScrollRow) * 59,
            width,
            54);
    }

    private bool SkillCellVisible(Vector4 panel, Vector4 cell)
    {
        var list = SkillPanelLayout.ListBounds(panel);
        return cell.Y >= list.Y &&
               cell.Y + cell.W <= list.Y + list.W;
    }

    private SkillType SelectedSkill() =>
        SkillListEntries[
            Math.Clamp(_selectedSkill, 0, SkillListEntries.Length - 1)]
        .Skill;

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
        SkillType.Adventure =>
            _activePlayer?.AdventureExperience ?? 0,
        SkillType.Attack =>
            _activePlayer?.AttackExperience ?? 0,
        SkillType.Strength =>
            _activePlayer?.StrengthExperience ?? 0,
        SkillType.Defence =>
            _activePlayer?.DefenceExperience ?? 0,
        _ => 0
    };

    private void DrawSkillGridItem(
        Vector4 bounds, SkillType skill, int level)
    {
        var hovered = bounds.Contains(MouseState.Position);
        var accent = SkillAccent(skill);
        DrawUiColor(
            bounds,
            hovered
                ? new(.145f, .119f, .060f, .99f)
                : new(.050f, .046f, .036f, .98f));
        DrawPanelOutline(
            bounds, 0,
            new(.025f, .022f, .018f, 1));
        DrawPanelOutline(
            bounds, hovered ? 2 : 1,
            hovered
                ? new(.55f, .42f, .18f, 1)
                : new(.25f, .205f, .12f, 1));
        var iconCenterX = MathF.Round(bounds.X + 23);
        var iconCenterY = MathF.Round(bounds.Y + 21);
        DrawUiCircle(
            iconCenterX, iconCenterY, 19,
            new(.025f, .022f, .018f, 1));
        DrawUiCircle(
            iconCenterX, iconCenterY, 17,
            new(
                accent.X * .38f,
                accent.Y * .38f,
                accent.Z * .38f,
                1));
        DrawUiCircle(
            iconCenterX, iconCenterY, 15,
            new(.055f, .050f, .039f, 1));
        DrawSkillIcon(
            skill,
            new(iconCenterX - 16, iconCenterY - 16, 32, 32));
        var badge = new Vector4(
            bounds.X + bounds.Z - 30,
            bounds.Y + 5,
            25,
            18);
        DrawUiColor(badge, new(.030f, .027f, .022f, .98f));
        DrawPanelOutline(
            badge, 1,
            level >= SkillMaximumLevel(skill)
                ? new(.58f, .47f, .20f, 1)
                : new(.31f, .25f, .14f, 1));
        DrawSmallCenteredUiText(
            level.ToString(), badge,
            level >= SkillMaximumLevel(skill)
                ? new(210, 190, 105, 255)
                : new(220, 209, 170, 255));
        var nameBounds = new Vector4(
            bounds.X + 3,
            bounds.Y + 37,
            bounds.Z - 6,
            12);
        DrawSmallCenteredUiText(
            SkillDisplayName(skill),
            nameBounds,
            hovered
                ? new(245, 226, 171, 255)
                : new(194, 184, 151, 255));
        var experience = SkillExperience(skill);
        var maximumLevel = SkillMaximumLevel(skill);
        var floor = SkillExperienceForLevel(skill, level);
        var ceiling = level >= maximumLevel
            ? floor
            : SkillExperienceForLevel(skill, level + 1);
        var progress = level >= maximumLevel
            ? 1
            : (experience - floor) /
              (float)Math.Max(1, ceiling - floor);
        var track = new Vector4(
            bounds.X + 4,
            bounds.Y + bounds.W - 4,
            bounds.Z - 8,
            2);
        DrawUiColor(track, new(.025f, .024f, .020f, .95f));
        DrawUiColor(
            new(
                track.X, track.Y,
                track.Z * progress,
                track.W),
            accent);
    }

    private static string SkillDisplayName(SkillType skill) => skill switch
    {
        SkillType.Woodcutting => "Woodcut",
        SkillType.Firemaking => "Firemaking",
        _ => skill.ToString()
    };

    private static Vector4 SkillAccent(SkillType skill) => skill switch
    {
        SkillType.Woodcutting => new(.31f, .57f, .20f, 1),
        SkillType.Farming => new(.57f, .55f, .20f, 1),
        SkillType.Crafting => new(.63f, .38f, .14f, 1),
        SkillType.Fishing => new(.20f, .46f, .66f, 1),
        SkillType.Cooking => new(.72f, .32f, .12f, 1),
        SkillType.Digging => new(.48f, .34f, .18f, 1),
        SkillType.Mining => new(.58f, .61f, .65f, 1),
        SkillType.Adventure => new(.78f, .60f, .18f, 1),
        SkillType.Attack => new(.72f, .16f, .10f, 1),
        SkillType.Strength => new(.78f, .38f, .12f, 1),
        SkillType.Defence => new(.20f, .42f, .68f, 1),
        _ => new(.88f, .20f, .06f, 1)
    };

    private int SkillLevel(SkillType skill)
    {
        var experience = SkillExperience(skill);
        return skill == SkillType.Adventure
            ? AdventureService.LevelForExperience(experience)
            : SkillService.LevelForExperience(experience);
    }

    private static int SkillMaximumLevel(SkillType skill) =>
        skill == SkillType.Adventure
            ? AdventureService.MaximumLevel
            : SkillService.MaximumLevel;

    private static int SkillExperienceForLevel(
        SkillType skill, int level) =>
        skill == SkillType.Adventure
            ? AdventureService.ExperienceForLevel(level)
            : SkillService.ExperienceForLevel(level);

    private static int SkillExperienceToNextLevel(
        SkillType skill, int experience)
    {
        var level = skill == SkillType.Adventure
            ? AdventureService.LevelForExperience(experience)
            : SkillService.LevelForExperience(experience);
        var maximum = SkillMaximumLevel(skill);
        return level >= maximum
            ? 0
            : SkillExperienceForLevel(skill, level + 1) -
              Math.Max(0, experience);
    }

    private void DrawSkillIcon(SkillType skill, Vector4 bounds)
    {
        if (skill == SkillType.Adventure)
        {
            DrawPlayerUiIcon(2, bounds);
            return;
        }
        if (skill >= SkillType.Attack)
        {
            DrawCombatSkillIcon((int)skill - (int)SkillType.Attack, bounds);
            return;
        }
        DrawPlayerUiIcon(5 + (int)skill, bounds);
    }

    private void DrawSkillsScrollbar(Vector4 panel)
    {
        var totalRows = (SkillListEntries.Length + 1) / 2;
        const int visibleRows = 4;
        if (totalRows <= visibleRows) return;
        var list = SkillPanelLayout.ListBounds(panel);
        var track = new Vector4(
            list.X + list.Z + 3, list.Y, 4, list.W);
        DrawUiColor(track, new(.025f, .022f, .018f, .9f));
        var thumbHeight = MathF.Max(
            24, track.W * visibleRows / totalRows);
        var travel = track.W - thumbHeight;
        var offset = _skillsScrollRow /
            (float)(totalRows - visibleRows);
        DrawUiColor(
            new(track.X, track.Y + travel * offset,
                track.Z, thumbHeight),
            new(.48f, .37f, .16f, 1));
    }

    private static string[] SkillBenefitLines(
        SkillType skill,
        bool farming,
        bool crafting,
        bool fishing,
        bool cooking,
        bool firemaking,
        bool digging,
        bool mining,
        int level) =>
        skill switch
        {
            SkillType.Adventure =>
                ["Raised by every activity", "Increases maximum health"],
            SkillType.Attack =>
                ["Improves melee accuracy", "Trained with Accurate stance"],
            SkillType.Strength =>
                ["Improves maximum hit", "Trained with Aggressive stance"],
            SkillType.Defence =>
                ["Improves melee defence", "Trained with Defensive stance"],
            _ => mining
            ? [$"Hit chance {MiningSkill.HitChance(level) * 100:0}%",
               "Mine tougher deposits"]
            : digging
                ? ["Excavate faster", "Open cave passages"]
                : firemaking
                    ? ["Fires burn longer", "Larger light radius"]
                    : cooking
                        ? ["Reduce burning", "Unlock better meals"]
                        : fishing
                            ? ["Catch new fish", "Improve netting"]
                            : crafting
                                ? ["Unlock recipes", "Build new stations"]
                                : farming
                                    ? ["Plant and forage", "Improve harvests"]
                                    : [
                                        $"Hit chance " +
                                        $"{WoodcuttingSkill.HitChance(level) * 100:0}%",
                                        "Fell tougher trees"
                                    ],
        };

    private void DrawSmallCenteredUiText(
        string text, Vector4 bounds, FSColor color)
    {
        if (_quantityFont is null || _fontRenderer is null) return;
        _uiColorBatch.Flush();
        var size = _quantityFont.MeasureString(text);
        var position = new System.Numerics.Vector2(
            MathF.Round(bounds.X + (bounds.Z - size.X) * .5f),
            MathF.Round(bounds.Y + (bounds.W - size.Y) * .5f));
        _quantityFont.DrawText(
            _fontRenderer,
            text,
            position + System.Numerics.Vector2.One,
            new FSColor(0, 0, 0, 205));
        _quantityFont.DrawText(
            _fontRenderer, text, position, color);
    }
}
