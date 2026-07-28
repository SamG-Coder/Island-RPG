using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal sealed class CraftingWindowState
{
    private const double DoubleClickSeconds = .40;
    private bool _leftWasDown;
    private string? _lastClickedRecipeId;
    private long _lastRecipeClick;

    public bool Visible { get; private set; }
    public CraftingCategory Category { get; private set; } =
        CraftingCategory.All;
    public CraftingRecipe? SelectedRecipe { get; private set; }

    public void Open()
    {
        Visible = true;
        SelectedRecipe ??= VisibleRecipes().FirstOrDefault();
    }

    public void Close() => Visible = false;

    public IReadOnlyList<CraftingRecipe> VisibleRecipes() =>
        CraftingSkill.RecipesFor(Category);

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
                    SelectedRecipe = VisibleRecipes().FirstOrDefault();
                    break;
                }
                var recipes = VisibleRecipes();
                for (var index = 0; index < recipes.Count; index++)
                    if (RecipeBounds(window, index).Contains(pointer))
                    {
                        SelectedRecipe = recipes[index];
                        if (IsDoubleClick(recipes[index]))
                            activatedRecipe = recipes[index];
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
