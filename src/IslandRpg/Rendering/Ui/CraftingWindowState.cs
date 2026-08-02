using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class CraftingWindowState
{
    private const double DoubleClickSeconds = .40;
    private const int RecipeColumns = 4;
    private bool _leftWasDown;
    private string? _lastClickedRecipeId;
    private long _lastRecipeClick;

    public bool Visible { get; private set; }
    public CraftingCategory Category { get; private set; } =
        CraftingCategory.All;
    public CraftingRecipe? SelectedRecipe { get; private set; }
    public string? StationItemId { get; private set; }
    public int ScrollRow { get; private set; }

    public void Open(string? stationItemId = null)
    {
        Visible = true;
        StationItemId = stationItemId;
        Category = CraftingCategory.All;
        SelectedRecipe = null;
        ScrollRow = 0;
        _lastClickedRecipeId = null;
        _lastRecipeClick = 0;
    }

    public void Close()
    {
        Visible = false;
        StationItemId = null;
    }

    public IReadOnlyList<CraftingRecipe> VisibleRecipes() =>
        StationItemId is null
            ? CraftingSkill.RecipesFor(Category)
            : CraftingSkill.RecipesFor(Category, StationItemId);

    public int VisibleRecipeCount(Vector4 window)
    {
        var recipes = VisibleRecipes();
        var start = Math.Min(recipes.Count, ScrollRow * RecipeColumns);
        return Math.Min(
            recipes.Count - start,
            VisibleRecipeRows(window) * RecipeColumns);
    }

    public CraftingRecipe VisibleRecipeAt(Vector4 window, int index)
    {
        if ((uint)index >= (uint)VisibleRecipeCount(window))
            throw new ArgumentOutOfRangeException(nameof(index));
        return VisibleRecipes()[ScrollRow * RecipeColumns + index];
    }

    public bool Scroll(Vector4 viewport, Vector2 pointer, float wheelOffset)
    {
        if (!Visible || wheelOffset == 0) return false;
        var window = WindowBounds(viewport);
        if (!RecipeListBounds(window).Contains(pointer)) return false;
        var recipes = VisibleRecipes();
        var totalRows = (recipes.Count + RecipeColumns - 1) / RecipeColumns;
        ScrollRow = Math.Clamp(
            ScrollRow - Math.Sign(wheelOffset),
            0,
            Math.Max(0, totalRows - VisibleRecipeRows(window)));
        return true;
    }

    public CraftingRecipe? UpdatePointer(
        Vector4 viewport, Vector2 pointer, bool leftDown)
    {
        if (!Visible)
        {
            _leftWasDown = leftDown;
            return null;
        }
        CraftingRecipe? activatedRecipe = null;
        if (leftDown && !_leftWasDown)
        {
            var window = WindowBounds(viewport);
            if (CloseBounds(window).Contains(pointer))
                Close();
            else if (CraftButtonBounds(window).Contains(pointer))
                activatedRecipe = SelectedRecipe;
            else
            {
                var categories = Enum.GetValues<CraftingCategory>();
                for (var index = 0; index < categories.Length; index++)
                {
                    if (!CategoryBounds(window, index).Contains(pointer))
                        continue;
                    Category = categories[index];
                    SelectedRecipe = null;
                    ScrollRow = 0;
                    break;
                }
                var recipeCount = VisibleRecipeCount(window);
                for (var index = 0; index < recipeCount; index++)
                    if (RecipeBounds(window, index).Contains(pointer))
                    {
                        var recipe = VisibleRecipeAt(window, index);
                        SelectedRecipe = recipe;
                        if (IsDoubleClick(recipe))
                            activatedRecipe = recipe;
                        break;
                    }
            }
        }
        _leftWasDown = leftDown;
        return activatedRecipe;
    }

    private bool IsDoubleClick(CraftingRecipe recipe)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _lastRecipeClick) /
                      (double)System.Diagnostics.Stopwatch.Frequency;
        var doubleClicked =
            string.Equals(
                _lastClickedRecipeId, recipe.Id,
                StringComparison.Ordinal) &&
            elapsed <= DoubleClickSeconds;
        _lastClickedRecipeId = recipe.Id;
        _lastRecipeClick = now;
        return doubleClicked;
    }

    public static Vector4 WindowBounds(Vector4 viewport)
    {
        var width = Math.Min(900, Math.Max(700, viewport.Z - 40));
        var height = Math.Min(540, Math.Max(440, viewport.W - 40));
        return new(
            viewport.X + (viewport.Z - width) * .5f,
            viewport.Y + (viewport.W - height) * .5f,
            width, height);
    }

    public static Vector4 CloseBounds(Vector4 window) =>
        new(window.X + window.Z - 38, window.Y + 10, 26, 24);

    public static Vector4 CraftButtonBounds(Vector4 window) =>
        new(
            DetailsBounds(window).X + DetailsBounds(window).Z - 104,
            DetailsBounds(window).Y + DetailsBounds(window).W - 46,
            90,
            32);

    public static Vector4 CategoryBounds(Vector4 window, int index) =>
        new(window.X + 14, window.Y + 58 + index * 42, 104, 34);

    public static Vector4 RecipeBounds(Vector4 window, int index) =>
        new(
            window.X + 132 + index % 4 * 58,
            window.Y + 58 + index / 4 * 58,
            50, 50);

    public static Vector4 RecipeListBounds(Vector4 window) =>
        new(window.X + 126, window.Y + 52, 240, window.W - 66);

    private static int VisibleRecipeRows(Vector4 window) =>
        Math.Max(1, (int)((RecipeListBounds(window).W - 6) / 58));

    public static Vector4 DetailsBounds(Vector4 window) =>
        new(
            window.X + 374, window.Y + 52,
            InventoryBounds(window).X - window.X - 388,
            window.W - 66);

    public static Vector4 InventoryBounds(Vector4 window) =>
        new(
            window.X + window.Z - 186,
            window.Y + (window.W - GameUiControlState.PanelHeight) * .5f,
            172,
            GameUiControlState.PanelHeight);
}
