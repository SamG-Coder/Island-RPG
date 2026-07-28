using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly CraftingWindowState _craftingWindow = new();
    private readonly HashSet<string> _nearbyCraftingStations =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _craftingWindowOpen => _craftingWindow.Visible;

    private void OpenCraftingWindow(string? stationItemId = null)
    {
        CancelPlaceableObjectPlacement();
        RefreshNearbyCraftingStations();
        _craftingWindow.Open(stationItemId);
        _modalScreen.Open(ModalScreenKind.Crafting);
        _chatUi.BlurInput();
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _fishContext.Close();
        _vegetationContext.Close();
        _gameUi.Close();
        UseDefaultGameCursor();
    }

    private void QueueCraftingStation(WorldGroundObject station)
    {
        if (!CraftingStationService.IsStation(station.ItemId))
            return;
        _worldActions.QueuePath(
            new Vector2(station.X, station.Y),
            1.15f,
            WorldActionType.UseCraftingStation,
            groundObjectId: station.Id,
            clearTreeActions: true);
    }

    internal void UseCraftingStation(Guid stationId)
    {
        var station = FindGroundObject(stationId);
        if (station is null ||
            !CraftingStationService.IsStation(station.ItemId))
            return;
        OpenCraftingWindow(station.ItemId);
    }

    private void CloseCraftingWindow()
    {
        _craftingWindow.Close();
        _nearbyCraftingStations.Clear();
        _modalScreen.Close(ModalScreenKind.Crafting);
        if (_defaultNativeCursor is not null)
            Cursor = _defaultNativeCursor;
    }

    private bool CanCraftRecipe(string recipeId)
    {
        var recipe = CraftingSkill.Recipes.First(
            candidate => candidate.Id == recipeId);
        var level = CraftingSkill.LevelForExperience(
            _activePlayer?.CraftingExperience ?? 0);
        if (level >= recipe.RequiredLevel) return true;
        ReportBlockedAction(
            "crafting-level-too-low",
            $"You need Crafting level {recipe.RequiredLevel} to make " +
            $"{ItemCatalog.Get(recipe.ResultItemId).Name}.");
        return false;
    }

    private void AwardCraftingExperience(string recipeId)
    {
        if (_activePlayer is null) return;
        var recipe = CraftingSkill.Recipes.First(
            candidate => candidate.Id == recipeId);
        var award = CraftingSkill.AwardExperience(
            _activePlayer.CraftingExperience, recipe);
        _activePlayer = _activePlayer with
        {
            CraftingExperience = award.Experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{award.Gained} Crafting XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your Crafting level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    private Vector4 CraftingWindowBounds() =>
        CraftingWindowState.WindowBounds(SceneClientBounds());

    private void UpdateCraftingWindowInput(
        Vector2 pointer, bool leftDown)
    {
        var wasOpen = _craftingWindow.Visible;
        var activatedRecipe = _craftingWindow.UpdatePointer(
            SceneClientBounds(), pointer, leftDown);
        if (!_craftingWindow.Visible)
        {
            if (wasOpen) CloseCraftingWindow();
            return;
        }
        if (activatedRecipe is not null)
        {
            var craftButtonClicked =
                CraftingWindowState.CraftButtonBounds(
                    CraftingWindowBounds()).Contains(pointer);
            if (!craftButtonClicked ||
                RecipeAvailabilityFor(activatedRecipe) ==
                RecipeAvailability.Ready)
                TryCraftRecipe(activatedRecipe);
        }
        _inventoryContext.UpdatePointer(pointer, leftDown);
        var inventoryPanel = new InventoryPanelState(
            CraftingWindowState.InventoryBounds(
                CraftingWindowBounds()),
            _activePlayer?.Inventory ?? [],
            _activeInventorySlot,
            _inventoryDraggingSlot,
            allowDragOutsideToGame: false);
        UpdateInventoryInteraction(
            inventoryPanel, pointer, leftDown,
            MouseState.IsButtonDown(MouseButton.Right));
    }

    private void TryCraftRecipe(CraftingRecipe recipe)
    {
        if (_activePlayer is null) return;
        var level = CraftingSkill.LevelForExperience(
            _activePlayer.CraftingExperience);
        var availability = CraftingSkill.Availability(
            recipe, level, _activePlayer.Inventory,
            HasRequiredCraftingStation(recipe));
        if (availability == RecipeAvailability.Locked)
        {
            CanCraftRecipe(recipe.Id);
            return;
        }
        if (availability == RecipeAvailability.MissingResources)
        {
            ReportBlockedAction(
                $"crafting-missing-{recipe.Id}",
                $"You do not have the materials to make " +
                $"{ItemCatalog.Get(recipe.ResultItemId).Name}.");
            return;
        }
        if (availability == RecipeAvailability.MissingStation)
        {
            var station = ItemCatalog.Get(
                recipe.RequiredStationItemId!).Name;
            ReportBlockedAction(
                $"crafting-station-{recipe.Id}",
                $"Stand near a placed {station} to make " +
                $"{ItemCatalog.Get(recipe.ResultItemId).Name}.");
            return;
        }
        if (availability == RecipeAvailability.InventoryFull)
        {
            ReportBlockedAction(
                $"crafting-inventory-full-{recipe.Id}",
                "You do not have enough inventory space for every crafting step.");
            return;
        }
        var result = CraftingService.TryCraftDetailed(
            recipe, level, _activePlayer.Inventory,
            out var inventory,
            HasRequiredCraftingStation(recipe));
        if (result == CraftingService.CraftResult.InventoryFull)
        {
            ReportBlockedAction(
                $"crafting-inventory-full-{recipe.Id}",
                "You do not have enough inventory space for every crafting step.");
            return;
        }
        if (result != CraftingService.CraftResult.Success)
            return;

        _activePlayer = _activePlayer with
        {
            Inventory = inventory,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You craft {ItemCatalog.Get(recipe.ResultItemId).Name}.",
            ChatMessageStyle.Action);
        AwardCraftingExperience(recipe.Id);
    }

    private RecipeAvailability RecipeAvailabilityFor(
        CraftingRecipe recipe) =>
        CraftingSkill.Availability(
            recipe,
            CraftingSkill.LevelForExperience(
                _activePlayer?.CraftingExperience ?? 0),
            _activePlayer?.Inventory,
            HasRequiredCraftingStation(recipe));

    private bool HasRequiredCraftingStation(
        CraftingRecipe recipe)
    {
        if (recipe.RequiredStationItemId is not { } stationItemId)
            return true;
        return _nearbyCraftingStations.Contains(stationItemId);
    }

    private void RefreshNearbyCraftingStations()
    {
        _nearbyCraftingStations.Clear();
        if (_player is null) return;
        foreach (var gpu in _worldChunks.Values)
        {
            if (IsActiveWorldChunk(gpu) &&
                gpu.Chunk.GroundObjects.Count > 0)
                CraftingStationService.CollectWithinRange(
                    gpu.Chunk.GroundObjects,
                    _player.Position,
                    _nearbyCraftingStations);
        }
    }

    private void RenderCraftingWindow()
    {
        var window = CraftingWindowBounds();
        DrawAoEPanelBorder(window);
        DrawPanelCaption("Crafting Recipes", window);

        var close = CraftingWindowState.CloseBounds(window);
        DrawMenuButton(close, "X");

        var categories = Enum.GetValues<CraftingCategory>();
        for (var index = 0; index < categories.Length; index++)
        {
            var category = categories[index];
            var bounds = CraftingWindowState.CategoryBounds(window, index);
            var selected = category == _craftingWindow.Category;
            DrawUiColor(
                bounds,
                selected
                    ? new(.27f, .21f, .09f, .98f)
                    : new(.085f, .072f, .045f, .98f));
            DrawPanelOutline(
                bounds, 1,
                selected
                    ? new(.65f, .48f, .18f, 1)
                    : new(.31f, .25f, .13f, 1));
            DrawCenteredUiText(
                category.ToString(), bounds,
                new(224, 213, 175, 255));
        }

        var inventory = _activePlayer?.Inventory ?? [];
        var level = CraftingSkill.LevelForExperience(
            _activePlayer?.CraftingExperience ?? 0);
        var recipes = _craftingWindow.VisibleRecipes();
        for (var index = 0; index < recipes.Count; index++)
        {
            var recipe = recipes[index];
            var bounds = CraftingWindowState.RecipeBounds(window, index);
            var selected = recipe == _craftingWindow.SelectedRecipe;
            var availability = CraftingSkill.Availability(
                recipe, level, inventory,
                HasRequiredCraftingStation(recipe));
            DrawUiColor(bounds, new(.055f, .048f, .034f, .98f));
            DrawPanelOutline(
                bounds, 1,
                selected
                    ? new(.88f, .69f, .25f, 1)
                    : new(.28f, .23f, .13f, 1));
            DrawRecipeItem(recipe.ResultItemId, bounds);
            if (availability == RecipeAvailability.Locked)
                DrawUiColor(bounds, new(0, 0, 0, .72f));
            else if (availability is
                     RecipeAvailability.MissingResources or
                     RecipeAvailability.MissingStation or
                     RecipeAvailability.InventoryFull)
                DrawUiColor(bounds, new(.55f, .025f, .018f, .46f));
            if (availability == RecipeAvailability.Locked)
                DrawCenteredUiText(
                    $"Lv {recipe.RequiredLevel}", bounds,
                    new(185, 181, 172, 255));
        }

        RenderCraftingRecipeDetails(window, level, inventory);
        RenderCraftButton(window);
        var panel = CraftingWindowState.InventoryBounds(window);
        DrawAoEPanelBorder(panel);
        RenderInventoryPanel(
            new(
                panel, inventory,
                activeSlot: _activeInventorySlot,
                draggingSlot: _inventoryDraggingSlot),
            renderDragPreview: true);
        RenderInventoryContextMenu();
    }

    private void RenderCraftButton(Vector4 window)
    {
        var bounds = CraftingWindowState.CraftButtonBounds(window);
        var recipe = _craftingWindow.SelectedRecipe;
        var enabled = recipe is not null &&
                      RecipeAvailabilityFor(recipe) ==
                      RecipeAvailability.Ready;
        var hovered = enabled && bounds.Contains(MouseState.Position);
        DrawUiColor(
            bounds,
            enabled
                ? hovered
                    ? new(.34f, .26f, .10f, .98f)
                    : new(.23f, .18f, .075f, .98f)
                : new(.075f, .069f, .058f, .96f));
        DrawPanelOutline(
            bounds,
            1,
            enabled
                ? new(.67f, .49f, .17f, 1)
                : new(.24f, .22f, .18f, 1));
        DrawCenteredUiText(
            "Craft",
            bounds,
            enabled
                ? new(238, 222, 176, 255)
                : new(120, 116, 105, 255));
    }

    private void DrawRecipeItem(string itemId, Vector4 bounds)
    {
        var texture = InventoryItemTexture(itemId);
        var uv = InventoryItemUv(itemId);
        if (texture != 0 && uv is not null)
        {
            var frame = InventoryItemFrame(itemId);
            var pixelFrame = InventoryItemPixelFrame(itemId);
            DrawUiSprite(
                frame, texture,
                SpritePixelLayout.CenterOpaquePixels(
                    pixelFrame,
                    new(bounds.X + 9, bounds.Y + 9, 32, 32)),
                brightness: InventoryItemBrightness(itemId),
                uvRectangle: uv,
                grayscaleAmount: InventoryItemGrayscale(itemId));
        }
        else
            DrawCenteredUiText(
                InventoryItemCaption(itemId), bounds,
                new(211, 198, 158, 255));
    }

    private void RenderCraftingRecipeDetails(
        Vector4 window, int level, string?[] inventory)
    {
        var details = CraftingWindowState.DetailsBounds(window);
        DrawUiColor(details, new(.045f, .040f, .031f, .96f));
        DrawPanelOutline(details, 1, new(.30f, .24f, .13f, 1));
        var recipe = _craftingWindow.SelectedRecipe;
        if (recipe is null)
        {
            DrawCenteredUiText(
                "No recipes", details, new(190, 181, 150, 255));
            return;
        }

        var item = ItemCatalog.Get(recipe.ResultItemId);
        var availability = CraftingSkill.Availability(
            recipe, level, inventory,
            HasRequiredCraftingStation(recipe));
        DrawUiText(
            item.Name,
            new(details.X + 14, details.Y + 14),
            new(234, 220, 174, 255));
        DrawUiText(
            $"Crafting level {recipe.RequiredLevel}",
            new(details.X + 14, details.Y + 38),
            availability == RecipeAvailability.Locked
                ? new(150, 150, 145, 255)
                : new(190, 181, 150, 255));
        DrawUiText(
            "Materials",
            new(details.X + 14, details.Y + 72),
            new(224, 210, 168, 255));
        var y = details.Y + 96;
        foreach (var ingredient in recipe.Ingredients)
        {
            var held = inventory.Count(value =>
                value == ingredient.ItemId);
            DrawUiText(
                $"- {ingredient.Count} x " +
                ItemCatalog.Get(ingredient.ItemId).Name +
                $" ({held}/{ingredient.Count})",
                new(details.X + 14, y),
                held >= ingredient.Count
                    ? new(175, 207, 132, 255)
                    : new(226, 121, 103, 255));
            y += 23;
        }

        if (recipe.RequiredStationItemId is { } stationItemId)
        {
            var available = HasRequiredCraftingStation(recipe);
            y += 8;
            DrawUiText(
                "Required nearby station",
                new(details.X + 14, y),
                new(224, 210, 168, 255));
            y += 25;
            DrawUiText(
                $"- {ItemCatalog.Get(stationItemId).Name} " +
                (available ? "(nearby)" : "(not nearby)"),
                new(details.X + 14, y),
                available
                    ? new(175, 207, 132, 255)
                    : new(226, 121, 103, 255));
            y += 23;
        }

        if (recipe.RequiredTools is { Count: > 0 } tools)
        {
            y += 8;
            DrawUiText(
                "Required tools",
                new(details.X + 14, y),
                new(224, 210, 168, 255));
            y += 25;
            foreach (var tool in tools)
            {
                var held = inventory.Count(value =>
                    value is not null &&
                    ItemCatalog.Get(value).HasTag(tool.Tag));
                DrawUiText(
                    $"- {tool.Name} " +
                    $"({held}/{tool.Count}) - not consumed",
                    new(details.X + 14, y),
                    held >= tool.Count
                        ? new(175, 207, 132, 255)
                        : new(226, 121, 103, 255));
                y += 23;
            }
        }

        y += 12;
        DrawUiText(
            "How to make it",
            new(details.X + 14, y),
            new(224, 210, 168, 255));
        y += 26;
        for (var index = 0; index < recipe.Steps.Count; index++)
        {
            foreach (var line in WrapRecipeText(
                         $"{index + 1}. {recipe.Steps[index]}", 31))
            {
                DrawUiText(
                    line, new(details.X + 14, y),
                    new(190, 181, 150, 255));
                y += 20;
            }
            y += 4;
        }
    }

    private static IEnumerable<string> WrapRecipeText(
        string text, int maximumCharacters)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 &&
                line.Length + word.Length + 1 > maximumCharacters)
            {
                yield return line;
                line = word;
            }
            else
                line = line.Length == 0 ? word : $"{line} {word}";
        }
        if (line.Length > 0) yield return line;
    }
}
