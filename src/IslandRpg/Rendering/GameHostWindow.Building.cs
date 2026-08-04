using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool _buildingPanelOpen;
    private bool _buildingPanelLeftWasDown;
    private Guid? _activePlayerConstructionId;

    private static CraftingRecipe WoodenWallRecipe =>
        CraftingSkill.Recipes.First(value =>
            value.ResultItemId == ItemIds.WoodenWall);

    private void ToggleBuildingPanel()
    {
        _buildingPanelOpen = !_buildingPanelOpen;
        CancelPlaceableObjectPlacement();
    }

    private Vector4 BuildingPanelBounds()
    {
        var button = _gameUi.BuildButton.Bounds;
        return new(button.X - 278, button.Y - 278, 330, 270);
    }

    private static Vector4 BuildingWallButtonBounds(Vector4 panel) =>
        new(panel.X + 14, panel.Y + 48, panel.Z - 28, 158);

    private void UpdateBuildingPanelInput(Vector2 pointer, bool leftDown)
    {
        if (!_buildingPanelOpen)
        {
            _buildingPanelLeftWasDown = leftDown;
            return;
        }
        if (leftDown && !_buildingPanelLeftWasDown &&
            BuildingWallButtonBounds(BuildingPanelBounds()).Contains(pointer))
            BeginPlayerBuildingPlacement(WoodenWallRecipe);
        _buildingPanelLeftWasDown = leftDown;
    }

    private void BeginPlayerBuildingPlacement(CraftingRecipe recipe)
    {
        if (!TryCraftRecipe(recipe)) return;
        var slot = Array.FindLastIndex(
            _activePlayer?.Inventory ?? [],
            value => value == recipe.ResultItemId);
        if (slot < 0) return;
        _buildingPanelOpen = false;
        BeginPlaceableObjectPlacement(slot, recipe.ResultItemId);
    }

    private void RenderBuildingPanel()
    {
        if (!_buildingPanelOpen) return;
        var panel = BuildingPanelBounds();
        DrawUiColor(panel, new(.055f, .047f, .031f, .96f));
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText(
            "CONSTRUCTION", new(panel.X, panel.Y + 10, panel.Z, 27),
            new(232, 219, 177, 255));
        var option = BuildingWallButtonBounds(panel);
        var hovered = option.Contains(MouseState.Position);
        DrawUiColor(option, hovered
            ? new(.19f, .15f, .075f, .97f)
            : new(.105f, .09f, .052f, .97f));
        DrawPanelOutline(option, hovered ? 2 : 1, hovered
            ? new(.78f, .59f, .22f, 1)
            : new(.49f, .38f, .17f, 1));
        var icon = new Vector4(
            option.X + 10, option.Y + 10, 136, option.W - 20);
        DrawUiColor(icon, new(.035f, .032f, .024f, .9f));
        DrawPanelOutline(icon, 1, new(.29f, .24f, .14f, 1));
        DrawWoodenWallBuildIcon(icon);
        var textX = option.X + 160;
        DrawUiText(
            "Wooden wall", new(textX, option.Y + 18),
            new(236, 222, 178, 255));
        DrawUiText(
            "Palisade defence", new(textX, option.Y + 45),
            new(165, 155, 127, 255));
        DrawUiText(
            "5 logs", new(textX, option.Y + 79),
            new(214, 196, 149, 255));
        DrawUiText(
            "Crafting level 1", new(textX, option.Y + 103),
            new(193, 181, 145, 255));
        DrawUiText(
            "Hammer required", new(textX, option.Y + 127),
            new(193, 181, 145, 255));
        DrawCenteredUiText(
            "Select a structure, then place its foundation",
            new(panel.X + 10, panel.Y + 220, panel.Z - 20, 30),
            new(181, 170, 139, 255));
    }

    private void DrawWoodenWallBuildIcon(Vector4 bounds)
    {
        var frontWallKey = PalisadeWallVisuals.FrontFrameKey;
        if (!_treeAtlas.TryGetValue(frontWallKey, out var wall) ||
            _treeAtlasTexture == 0)
            return;
        var scale = MathF.Min(
            bounds.Z / Math.Max(1, wall.Frame.Width),
            bounds.W / Math.Max(1, wall.Frame.Height));
        var width = wall.Frame.Width * scale;
        var height = wall.Frame.Height * scale;
        DrawUiSprite(
            wall.Frame,
            _treeAtlasTexture,
            new(
                bounds.X + (bounds.Z - width) * .5f,
                bounds.Y + (bounds.W - height) * .5f,
                width,
                height),
            brightness: .08f,
            uvRectangle: new(
                wall.U0, wall.V0,
                wall.U1 - wall.U0,
                wall.V1 - wall.V0));
    }

    internal void UpdatePlayerConstruction()
    {
        if (_player is null || _activePlayerConstructionId is not { } siteId)
            return;
        var location = FindGroundObjectLocation(siteId);
        if (location is null ||
            !ConstructionService.IsConstructionSite(location.Value.Object))
        {
            _activePlayerConstructionId = null;
            if (_player.Action == EntityAction.Work) _player.Stop();
            return;
        }
        if (_player.Action != EntityAction.Work)
        {
            _activePlayerConstructionId = null;
            return;
        }
        if (_player.ActionTime < GroundItemActionSeconds) return;
        var level = CraftingSkill.LevelForExperience(
            _activePlayer?.CraftingExperience ?? 0);
        var addedHealth = ConstructionService.WorkHealth(level, 100);
        var updated = ConstructionService.AddWork(
            location.Value.Object, addedHealth);
        location.Value.Chunk.GroundObjects[location.Value.Index] = updated;
        if (_activePlayer is not null)
            _activePlayer = _activePlayer with
            {
                CraftingExperience = SkillService.AwardExperience(
                    _activePlayer.CraftingExperience, 6).Experience,
                UpdatedUtc = DateTime.UtcNow
            };
        QueueChunkSave(location.Value.Chunk);
        if (ConstructionService.IsConstructionSite(updated))
        {
            _player.WorkAt(new(updated.X, updated.Y));
            return;
        }
        _activePlayerConstructionId = null;
        _player.Stop();
        _chatUi.AddMessage(
            $"You finish building {ItemCatalog.Get(updated.ItemId).Name}.",
            ChatMessageStyle.Action);
        if (_activePlayer is not null) _saves.SavePlayer(_activePlayer);
    }
}
