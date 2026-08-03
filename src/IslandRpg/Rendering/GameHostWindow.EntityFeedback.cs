using FontStashSharp;
using IslandRpg.Gameplay;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private readonly EntityFeedbackState _entityFeedback = new();

    private static string PlayerFeedbackKey(string id) => $"player:{id}";
    private static string VillagerFeedbackKey(string id) => $"villager:{id}";
    private static string EnemyFeedbackKey(Guid id) => $"enemy:{id:N}";
    private static string TreeFeedbackKey(Guid id) => $"tree:{id:N}";
    private static string MiningFeedbackKey(string key) => $"mining:{key}";
    private static string GroundFeedbackKey(Guid id) => $"ground:{id:N}";
    private static string DigFeedbackKey(Guid id) => $"dig:{id:N}";
    private static string FishFeedbackKey(string key) => $"fish:{key}";

    private void ShowEntityImpact(
        string targetKey, int damage, bool hit) =>
        _entityFeedback.ShowImpact(targetKey, damage, hit, _clock);

    private void DrawEntityFeedback(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) bounds,
        float healthRatio,
        string targetKey,
        bool forceHealth = false)
    {
        if (forceHealth || _entityFeedback.HealthVisible(targetKey, _clock))
            DrawEntityHealthBar(scene, bounds, healthRatio);
        if (!_entityFeedback.TryGet(targetKey, out var feedback)) return;
        var age = (float)(_clock - feedback.ImpactAt);
        if (age < 0 || age >= MeleeCombatService.HitSplatSeconds) return;
        DrawEntityHitSplat(scene, bounds, feedback, age);
    }

    private void DrawEntityHealthBar(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) bounds,
        float ratio)
    {
        var scale = scene.Z / ReferenceWidth;
        var width = Math.Clamp(42 * _zoom, 28, 64);
        var bar = new Vector4(
            scene.X + ((bounds.Left + bounds.Right) * .5f -
                       width * .5f) * scale,
            scene.Y + (bounds.Top - 9) * scale,
            width * scale,
            Math.Max(5, 7 * scale));
        if (bar.X + bar.Z < scene.X || bar.X > scene.X + scene.Z ||
            bar.Y + bar.W < scene.Y || bar.Y > scene.Y + scene.W)
            return;
        DrawUiColor(bar, new(.035f, .028f, .022f, .96f));
        ratio = Math.Clamp(ratio, 0, 1);
        DrawUiColor(
            new(bar.X + 2, bar.Y + 2,
                Math.Max(0, (bar.Z - 4) * ratio),
                Math.Max(1, bar.W - 4)),
            ratio > .5f
                ? new(.24f, .62f, .18f, 1)
                : ratio > .25f
                    ? new(.74f, .55f, .12f, 1)
                    : new(.70f, .14f, .09f, 1));
        DrawPanelOutline(bar, 0, new(.10f, .08f, .05f, 1));
    }

    private void DrawEntityHitSplat(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) bounds,
        EntityFeedback feedback,
        float age)
    {
        var sceneScale = scene.Z / ReferenceWidth;
        var fade = Math.Clamp(
            (MeleeCombatService.HitSplatSeconds - age) / .55f, 0, 1);
        var entrance = Math.Clamp(age / .08f, 0, 1);
        var centerX = scene.X +
            (bounds.Left + bounds.Right) * .5f * sceneScale;
        var centerY = scene.Y +
            (bounds.Top + (bounds.Bottom - bounds.Top) * .42f) * sceneScale;
        const int fullRadius = 12;
        var radius = Math.Max(3, (int)MathF.Round(fullRadius * entrance));
        var displayText = feedback.Label ?? feedback.Damage.ToString();
        var contentHalfWidth = Math.Max(
            radius,
            (int)MathF.Ceiling((MeasureUiText(displayText) + 12) * .5f));
        var halfWidth = Math.Max(
            radius,
            (int)MathF.Round(contentHalfWidth * entrance));
        DrawEntitySplatBadge(
            centerX, centerY, halfWidth, radius,
            feedback.Label is null ? feedback.Hit : feedback.LabelSuccess,
            fade,
            feedback.Label is not null);
        var textBounds = new Vector4(
            centerX - halfWidth, centerY - radius - 2,
            halfWidth * 2, radius * 2);
        DrawCenteredUiText(
            displayText,
            new(textBounds.X + 1, textBounds.Y + 1,
                textBounds.Z, textBounds.W),
            new FSColor(28, 10, 7, (int)(235 * fade)));
        DrawCenteredUiText(
            displayText, textBounds,
            new FSColor(255, 255, 255, (int)(255 * fade)));
    }

    private void DrawEntitySplatBadge(
        float centerX, float centerY, int halfWidth, int radius,
        bool hit, float fade,
        bool outcome = false)
    {
        var edge = outcome && hit
            ? new Vector4(.018f, .22f, .045f, fade)
            : outcome
                ? new Vector4(.045f, .16f, .52f, fade)
                : hit
            ? new Vector4(.48f, .025f, .015f, fade)
            : new Vector4(.045f, .16f, .52f, fade);
        var face = outcome && hit
            ? new Vector4(.025f, .38f, .075f, fade)
            : outcome
                ? new Vector4(.06f, .28f, .74f, fade)
                : hit
            ? new Vector4(.78f, .055f, .030f, fade)
            : new Vector4(.06f, .28f, .74f, fade);
        var point = Math.Max(2, radius / 4);
        DrawUiColor(new(
            centerX - point / 2f, centerY - radius - 1,
            point, radius * 2 + 2), edge);
        DrawUiColor(new(
            centerX - halfWidth - 1, centerY - point / 2f,
            halfWidth * 2 + 2, point), edge);
        var diagonalX = Math.Max(radius * .68f, halfWidth - radius * .32f);
        var diagonalY = radius * .68f;
        var diagonalSize = Math.Max(2, point);
        foreach (var (x, y) in new[]
                 {
                     (-diagonalX, -diagonalY),
                     (diagonalX, -diagonalY),
                     (-diagonalX, diagonalY),
                     (diagonalX, diagonalY)
                 })
            DrawUiColor(new(
                centerX + x - diagonalSize / 2f,
                centerY + y - diagonalSize / 2f,
                diagonalSize, diagonalSize), edge);
        _uiColorBatch.Flush();
        _uiPillRenderer.Draw(
            centerX, centerY, halfWidth, radius, edge, face);
    }
}
