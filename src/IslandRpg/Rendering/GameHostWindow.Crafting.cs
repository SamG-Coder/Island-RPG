using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly CraftingWindowState _craftingWindow = new();
    private bool _craftingWindowOpen => _craftingWindow.Visible;

    private void OpenCraftingWindow()
    {
        _craftingWindow.Open();
        _modalScreen.Open(ModalScreenKind.Crafting);
        _chatUi.BlurInput();
        _inventoryContext.Close();
        _treeContext.Close();
        _groundObjectContext.Close();
        _gameUi.Close();
        UseDefaultGameCursor();
    }

    private void CloseCraftingWindow()
    {
        _craftingWindow.Close();
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
        var previousLevel = CraftingSkill.LevelForExperience(
            _activePlayer.CraftingExperience);
        var maximumExperience = CraftingSkill.ExperienceForLevel(
            CraftingSkill.MaximumLevel);
        var experience = Math.Min(
            maximumExperience,
            _activePlayer.CraftingExperience + recipe.Experience);
        _activePlayer = _activePlayer with
        {
            CraftingExperience = experience,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"+{recipe.Experience} Crafting XP.",
            ChatMessageStyle.Experience);
        var level = CraftingSkill.LevelForExperience(experience);
        if (level > previousLevel)
            _chatUi.AddMessage(
                $"Your Crafting level is now {level}.",
                ChatMessageStyle.LevelUp);
    }

    private Vector4 CraftingWindowBounds() =>
        CraftingWindowState.WindowBounds(SceneClientBounds());

    private static Vector4 CraftingRecipesButtonBounds(Vector4 panel) =>
        new(panel.X + 18, panel.Y + 232, panel.Z - 36, 34);

    private void UpdateCraftingWindowInput(
        Vector2 pointer, bool leftDown)
    {
        _inventoryContext.UpdatePointer(pointer, leftDown);
        _craftingWindow.UpdatePointer(
            SceneClientBounds(), pointer, leftDown);
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
        if (!_craftingWindow.Visible)
            CloseCraftingWindow();
    }

    private void RenderCraftingWindow()
    {
        var window = CraftingWindowBounds();
        DrawAoEPanelBorder(window);
        DrawPanelCaption("Crafting Recipes", window);

        var close = CraftingWindowState.CloseBounds(window);
        DrawUiColor(close, new(.24f, .09f, .055f, .98f));
        DrawPanelOutline(close, 1, new(.50f, .27f, .14f, 1));
        DrawCenteredUiText("X", close, new(238, 220, 180, 255));

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
                recipe, level, inventory);
            DrawUiColor(bounds, new(.055f, .048f, .034f, .98f));
            DrawPanelOutline(
                bounds, 1,
                selected
                    ? new(.88f, .69f, .25f, 1)
                    : new(.28f, .23f, .13f, 1));
            DrawRecipeItem(recipe.ResultItemId, bounds);
            if (availability == RecipeAvailability.Locked)
                DrawUiColor(bounds, new(0, 0, 0, .72f));
            else if (availability == RecipeAvailability.MissingResources)
                DrawUiColor(bounds, new(.55f, .025f, .018f, .46f));
            if (availability == RecipeAvailability.Locked)
                DrawCenteredUiText(
                    $"Lv {recipe.RequiredLevel}", bounds,
                    new(185, 181, 172, 255));
        }

        RenderCraftingRecipeDetails(window, level, inventory);
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

    private void DrawRecipeItem(string itemId, Vector4 bounds)
    {
        var texture = InventoryItemTexture(itemId);
        var uv = InventoryItemUv(itemId);
        if (texture != 0 && uv is not null)
            DrawUiSprite(
                InventoryItemFrame(itemId), texture,
                new(bounds.X + 9, bounds.Y + 9, 32, 32),
                uvRectangle: uv);
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
            recipe, level, inventory);
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
