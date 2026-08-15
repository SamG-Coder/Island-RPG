using FontStashSharp;
using IslandRpg.Assets;
using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using OpenTK.Mathematics;
using StbImageSharp;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const int PlayerUiIconCount = 13;
    private const int CombatSkillIconCount = 4;
    private readonly SpriteFrame?[] _playerUiIconFrames =
        new SpriteFrame?[PlayerUiIconCount];
    private readonly int[] _playerUiIconTextures =
        new int[PlayerUiIconCount];
    private readonly SpriteFrame?[] _combatSkillIconFrames =
        new SpriteFrame?[CombatSkillIconCount];
    private readonly int[] _combatSkillIconTextures =
        new int[CombatSkillIconCount];
    private const int ToolbarActionIconCount = 3;
    private readonly SpriteFrame?[] _toolbarActionIconFrames =
        new SpriteFrame?[ToolbarActionIconCount];
    private readonly int[] _toolbarActionIconTextures =
        new int[ToolbarActionIconCount];
    private float _starvationDamageRemainder;
    private TimedHealingState _playerTimedHealing;

    private void AwardAdventureExperience(int actionExperience)
    {
        if (_activePlayer is null || actionExperience <= 0) return;
        var oldMaximum = AdventureService.MaximumHealth(
            _activePlayer.AdventureExperience);
        var award = AdventureService.AwardFromAction(
            _activePlayer.AdventureExperience, actionExperience);
        var newMaximum = AdventureService.MaximumHealth(award.Experience);
        _activePlayer = _activePlayer with
        {
            AdventureExperience = award.Experience,
            Health = Math.Clamp(
                _activePlayer.Health + newMaximum - oldMaximum,
                0, newMaximum),
            UpdatedUtc = DateTime.UtcNow
        };
        if (award.LevelledUp)
        {
            _chatUi.AddMessage(
                $"Your Adventure level is now {award.Level}. Maximum health increased.",
                ChatMessageStyle.LevelUp);
        }
    }

    private void RenderPlayerWorldHealthBar(Vector4 scene)
    {
        if (_activePlayer is null ||
            GetPlayerVisual() is not { } visual)
            return;
        var key = PlayerFeedbackKey(_activePlayer.Id);
        if (!_entityFeedback.HealthVisible(key, _clock)) return;
        DrawEntityFeedback(
            scene,
            SpriteBounds(visual.Frame, visual.World, visual.Mirror),
            _activePlayer.Health /
            (float)Math.Max(1, ActivePlayerMaximumHealth()),
            key);
    }

    private void UpdateSurvival(float elapsed)
    {
        if (_activePlayer is null || _playerDefeated || elapsed <= 0) return;
        var maximumHealth = ActivePlayerMaximumHealth();
        var update = SurvivalService.Advance(
            _activePlayer.Hunger,
            _activePlayer.WellFedSeconds,
            Math.Clamp(_activePlayer.Health, 0, maximumHealth),
            elapsed,
            starvationDamageRemainder:
                _starvationDamageRemainder);
        if (update.Health < _activePlayer.Health)
            _playerTimedHealing = default;
        var regeneration = EntityHealthRegenerationService.Advance(
            update.Health,
            maximumHealth,
            elapsed,
            _player is not null && IsHumanNearLitCampfire(
                _player.Position, _activeWorldLevel)
                ? EntityHealthRegenerationService.LitCampfireHumanMultiplier
                : 1,
            _activePlayer.HealthRegenerationRemainder);
        var healing = TimedHealingService.Advance(
            regeneration.Health,
            maximumHealth,
            elapsed,
            _playerTimedHealing);
        _playerTimedHealing = healing.State;
        update = update with { Health = healing.Health };
        if (_godMode && update.Health < _activePlayer.Health)
            update = update with
            {
                Health = _activePlayer.Health,
                StarvationDamageRemainder = 0
            };
        _starvationDamageRemainder =
            update.StarvationDamageRemainder;
        _activePlayer = _activePlayer with
        {
            Hunger = update.Hunger,
            WellFedSeconds = update.WellFedSeconds,
            Health = update.Health,
            HealthRegenerationRemainder = regeneration.Remainder
        };
        if (update.Health <= 0)
            HandlePlayerDefeat("You succumb to starvation.");
    }

    internal void ApplyPlayerDamage(int damage, string source)
    {
        if (_activePlayer is null || _playerDefeated ||
            _godMode || damage <= 0) return;
        InterruptOpenItemContainer();
        _playerTimedHealing = default;
        var health = PlayerDeathService.ApplyDamage(
            _activePlayer.Health, damage);
        _activePlayer = _activePlayer with
        {
            Health = health,
            UpdatedUtc = DateTime.UtcNow
        };
        ShowEntityImpact(
            PlayerFeedbackKey(_activePlayer.Id), damage, true);
        _chatUi.AddMessage(
            $"{source} hits you for {damage}.",
            ChatMessageStyle.Damage);
        if (health <= 0)
            HandlePlayerDefeat($"You were defeated by {source.ToLowerInvariant()}.");
    }

    private void ForcePlayerDefeat(string message)
    {
        if (_activePlayer is null || _playerDefeated) return;
        _activePlayer = _activePlayer with
        {
            Health = 0,
            UpdatedUtc = DateTime.UtcNow
        };
        HandlePlayerDefeat(message);
    }

    private void HandlePlayerDefeat(string message)
    {
        if (_activePlayer is null || _player is null ||
            _activeWorld is null)
            return;

        CancelMeleeCombat();
        CancelWorldLevelWork(clearMinimap: true);
        UseDefaultGameCursor();
        CancelFishingBoatAction();
        _fishingBoat?.Stop();
        _fishingBoatBoarded = false;
        _player.Die();
        _playerDefeated = true;
        _deathMessage = message;
        _chatUi.AddMessage(message, ChatMessageStyle.Warning);

        var marker = new PlayerDeathMarker(
            _player.Position.X,
            _player.Position.Y,
            _activeWorldLevel,
            _player.Gender,
            DateTime.UtcNow,
            _player.Facing.X,
            _player.Facing.Y);
        _saves.AddPlayerDeath(
            _activeWorld.Id, _activePlayer.Id, marker);
        _playerDeaths = _saves.LoadPlayerDeaths(
            _activeWorld.Id, _activePlayer.Id);
        _saves.SavePlayer(_activePlayer);
        _deathOverlayAt = _clock + DeathAnimationSeconds();
    }

    private double DeathAnimationSeconds()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Die), out var animation))
            return 1.4;
        const int storedVillagerAngles = 5;
        var frames = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedVillagerAngles);
        return frames * animation.SecondsPerFrame;
    }

    private void EatInventoryItem(int slot, string itemId)
    {
        if (_activePlayer is null ||
            !SurvivalService.TryFoodEffect(itemId, out var effect))
            return;
        if (IsNetworkWorld)
        {
            SendNetworkConsume(slot);
            return;
        }
        var inventory = ActivePlayerInventory();
        if (inventory[slot]?.ItemId != itemId)
            return;
        var maximumHealth = ActivePlayerMaximumHealth();
        if (_activePlayer.Hunger >= SurvivalService.MaximumHunger &&
            _activePlayer.Health >= maximumHealth)
        {
            ReportBlockedAction(
                "already-full",
                "You are already full and healthy.");
            return;
        }
        if (!inventory.TryTake(slot, 1, out _)) return;
        var update = SurvivalService.Eat(
            effect, _activePlayer.Hunger,
            _activePlayer.WellFedSeconds,
            _activePlayer.Health, maximumHealth);
        if (effect.TimedHealing > 0)
            _playerTimedHealing = TimedHealingService.Start(effect);
        _activePlayer = _activePlayer with
        {
            Inventory = inventory.ItemIds(),
            InventoryQuantities = inventory.Quantities(),
            Hunger = update.Hunger,
            WellFedSeconds = update.WellFedSeconds,
            Health = update.Health,
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        var item = ItemCatalog.Get(itemId);
        _chatUi.AddMessage(
            item.HasTag(ItemTag.Medicine)
                ? $"You apply the {item.Name}; it can restore " +
                  $"{effect.TimedHealing:0} health over " +
                  $"{effect.TimedHealingSeconds:0} seconds unless interrupted."
                : $"You eat the {item.Name}. Hunger restored by " +
                  $"{effect.HungerRestored:0}; digestion slows hunger for " +
                  $"{effect.WellFedSeconds / 60f:0.#} min.",
            ChatMessageStyle.Action);
    }

    private void PreparePlayerUiIcons()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images", "Ui",
            "player-ui-icons.png");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        for (var cell = 0; cell < PlayerUiIconCount; cell++)
        {
            const int iconSize = 32;
            var column = cell % 4;
            var row = cell / 4;
            var left = column * sheet.Width / 4;
            var right = (column + 1) * sheet.Width / 4;
            var top = row * sheet.Height / 4;
            var bottom = (row + 1) * sheet.Height / 4;
            var contentLeft = right;
            var contentRight = left;
            var contentTop = bottom;
            var contentBottom = top;
            for (var sourceY = top; sourceY < bottom; sourceY++)
            for (var sourceX = left; sourceX < right; sourceX++)
            {
                var source =
                    (sourceY * sheet.Width + sourceX) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source))
                    continue;
                contentLeft = Math.Min(contentLeft, sourceX);
                contentRight = Math.Max(contentRight, sourceX);
                contentTop = Math.Min(contentTop, sourceY);
                contentBottom = Math.Max(contentBottom, sourceY);
            }
            if (contentRight < contentLeft ||
                contentBottom < contentTop)
                continue;
            var contentWidth = contentRight - contentLeft + 1;
            var contentHeight = contentBottom - contentTop + 1;
            var padding = Math.Max(
                3, Math.Max(contentWidth, contentHeight) / 24);
            contentLeft = Math.Max(left, contentLeft - padding);
            contentRight = Math.Min(right - 1, contentRight + padding);
            contentTop = Math.Max(top, contentTop - padding);
            contentBottom = Math.Min(bottom - 1, contentBottom + padding);
            contentWidth = contentRight - contentLeft + 1;
            contentHeight = contentBottom - contentTop + 1;
            // Preserve the subject aspect ratio while centring it in a
            // consistently filled square source region.
            var sourceSpan = Math.Max(contentWidth, contentHeight);
            var sourceCenterX = (contentLeft + contentRight) / 2;
            var sourceCenterY = (contentTop + contentBottom) / 2;
            var pixels = new byte[iconSize * iconSize * 4];
            for (var y = 0; y < iconSize; y++)
            for (var x = 0; x < iconSize; x++)
            {
                var sourceX = sourceCenterX - sourceSpan / 2 +
                    (x * 2 + 1) * sourceSpan / (iconSize * 2);
                var sourceY = sourceCenterY - sourceSpan / 2 +
                    (y * 2 + 1) * sourceSpan / (iconSize * 2);
                sourceX = Math.Min(right - 1, sourceX);
                sourceY = Math.Min(bottom - 1, sourceY);
                sourceX = Math.Max(left, sourceX);
                sourceY = Math.Max(top, sourceY);
                var source = (sourceY * sheet.Width + sourceX) * 4;
                var target = (y * iconSize + x) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source))
                    continue;
                pixels[target] = sheet.Data[source];
                pixels[target + 1] = sheet.Data[source + 1];
                pixels[target + 2] = sheet.Data[source + 2];
                pixels[target + 3] = 255;
            }
            var frame = new SpriteFrame(
                iconSize, iconSize, 16, 16, pixels);
            _playerUiIconFrames[cell] = frame;
            _playerUiIconTextures[cell] = Upload(frame);
        }
        PrepareCombatSkillIcons();
        PrepareToolbarActionIcons();
    }

    private void PrepareToolbarActionIcons()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images", "Ui",
            "quest-crafting-ui-icons-source.png");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        for (var cell = 0; cell < ToolbarActionIconCount; cell++)
        {
            var left = cell * sheet.Width / ToolbarActionIconCount;
            var right = (cell + 1) * sheet.Width / ToolbarActionIconCount;
            var contentLeft = right;
            var contentRight = left;
            var contentTop = sheet.Height;
            var contentBottom = 0;
            for (var y = 0; y < sheet.Height; y++)
            for (var x = left; x < right; x++)
            {
                var source = (y * sheet.Width + x) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source)) continue;
                contentLeft = Math.Min(contentLeft, x);
                contentRight = Math.Max(contentRight, x);
                contentTop = Math.Min(contentTop, y);
                contentBottom = Math.Max(contentBottom, y);
            }
            if (contentRight < contentLeft || contentBottom < contentTop)
                continue;
            var sourceSpan = Math.Max(
                contentRight - contentLeft + 1,
                contentBottom - contentTop + 1);
            var centerX = (contentLeft + contentRight) / 2;
            var centerY = (contentTop + contentBottom) / 2;
            const int iconSize = 32;
            var pixels = new byte[iconSize * iconSize * 4];
            for (var y = 0; y < iconSize; y++)
            for (var x = 0; x < iconSize; x++)
            {
                var sourceX = Math.Clamp(
                    centerX - sourceSpan / 2 +
                    (x * 2 + 1) * sourceSpan / (iconSize * 2),
                    left, right - 1);
                var sourceY = Math.Clamp(
                    centerY - sourceSpan / 2 +
                    (y * 2 + 1) * sourceSpan / (iconSize * 2),
                    0, sheet.Height - 1);
                var source = (sourceY * sheet.Width + sourceX) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source)) continue;
                var target = (y * iconSize + x) * 4;
                pixels[target] = sheet.Data[source];
                pixels[target + 1] = sheet.Data[source + 1];
                pixels[target + 2] = sheet.Data[source + 2];
                pixels[target + 3] = 255;
            }
            var frame = new SpriteFrame(
                iconSize, iconSize, 16, 16, pixels);
            _toolbarActionIconFrames[cell] = frame;
            _toolbarActionIconTextures[cell] = Upload(frame);
        }
    }

    private void PrepareCombatSkillIcons()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Resources", "Images", "Ui",
            "combat-skill-icons-source.png");
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        var sheet = ImageResult.FromStream(
            stream, ColorComponents.RedGreenBlueAlpha);
        for (var cell = 0; cell < CombatSkillIconCount; cell++)
        {
            var left = cell * sheet.Width / CombatSkillIconCount;
            var right = (cell + 1) * sheet.Width / CombatSkillIconCount;
            var contentLeft = right;
            var contentRight = left;
            var contentTop = sheet.Height;
            var contentBottom = 0;
            for (var y = 0; y < sheet.Height; y++)
            for (var x = left; x < right; x++)
            {
                var source = (y * sheet.Width + x) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source)) continue;
                contentLeft = Math.Min(contentLeft, x);
                contentRight = Math.Max(contentRight, x);
                contentTop = Math.Min(contentTop, y);
                contentBottom = Math.Max(contentBottom, y);
            }
            if (contentRight < contentLeft || contentBottom < contentTop)
                continue;
            var sourceSpan = Math.Max(
                contentRight - contentLeft + 1,
                contentBottom - contentTop + 1);
            var centerX = (contentLeft + contentRight) / 2;
            var centerY = (contentTop + contentBottom) / 2;
            const int iconSize = 32;
            var pixels = new byte[iconSize * iconSize * 4];
            for (var y = 0; y < iconSize; y++)
            for (var x = 0; x < iconSize; x++)
            {
                var sourceX = Math.Clamp(
                    centerX - sourceSpan / 2 +
                    (x * 2 + 1) * sourceSpan / (iconSize * 2),
                    left, right - 1);
                var sourceY = Math.Clamp(
                    centerY - sourceSpan / 2 +
                    (y * 2 + 1) * sourceSpan / (iconSize * 2),
                    0, sheet.Height - 1);
                var source = (sourceY * sheet.Width + sourceX) * 4;
                if (IsPlayerUiChromaKey(sheet.Data, source)) continue;
                var target = (y * iconSize + x) * 4;
                pixels[target] = sheet.Data[source];
                pixels[target + 1] = sheet.Data[source + 1];
                pixels[target + 2] = sheet.Data[source + 2];
                pixels[target + 3] = 255;
            }
            var frame = new SpriteFrame(
                iconSize, iconSize, 16, 16, pixels);
            _combatSkillIconFrames[cell] = frame;
            _combatSkillIconTextures[cell] = Upload(frame);
        }
    }

    private static bool IsPlayerUiChromaKey(
        byte[] pixels, int offset)
    {
        var red = pixels[offset];
        var green = pixels[offset + 1];
        var blue = pixels[offset + 2];
        // Generated antialiasing blends the flat key into dark outlines.
        // The authored icon palette contains no purple, so remove both the
        // pure key and every strongly magenta blended edge pixel.
        return red > 120 &&
               blue > 115 &&
               red - green > 55 &&
               blue - green > 55;
    }

    private void DrawPlayerUiIcon(int index, Vector4 bounds)
    {
        if ((uint)index >= PlayerUiIconCount ||
            _playerUiIconFrames[index] is not { } frame ||
            _playerUiIconTextures[index] == 0)
            return;
        DrawUiSprite(frame, _playerUiIconTextures[index], bounds);
    }

    private void DrawCombatSkillIcon(int index, Vector4 bounds)
    {
        if ((uint)index >= CombatSkillIconCount ||
            _combatSkillIconFrames[index] is not { } frame ||
            _combatSkillIconTextures[index] == 0)
            return;
        DrawUiSprite(frame, _combatSkillIconTextures[index], bounds);
    }

    private void DrawToolbarActionIcon(int index, Vector4 bounds)
    {
        if ((uint)index >= _toolbarActionIconFrames.Length ||
            _toolbarActionIconFrames[index] is not { } frame ||
            _toolbarActionIconTextures[index] == 0)
            return;
        DrawUiSprite(
            frame,
            _toolbarActionIconTextures[index],
            bounds);
    }

    private void RenderPlayerStatus()
    {
        if (_activePlayer is null) return;
        var map = _minimapUi.Bounds;
        var adventureLevel = AdventureService.LevelForExperience(
            _activePlayer.AdventureExperience);
        var adventureFloor = AdventureService.ExperienceForLevel(
            adventureLevel);
        var adventureCeiling = adventureLevel >=
                               AdventureService.MaximumLevel
            ? adventureFloor
            : AdventureService.ExperienceForLevel(adventureLevel + 1);
        var adventureProgress = adventureLevel >=
                                AdventureService.MaximumLevel
            ? 1
            : (_activePlayer.AdventureExperience - adventureFloor) /
              (float)Math.Max(1, adventureCeiling - adventureFloor);
        var maximumHealth = ActivePlayerMaximumHealth();
        DrawStatusOrb(
            map, 0, 2, adventureProgress,
            $"Lv {adventureLevel}",
            new(.53f, .40f, .12f, 1),
            new(.88f, .69f, .20f, 1));
        DrawStatusOrb(
            map, 1, 3,
            _activePlayer.Health / (float)Math.Max(1, maximumHealth),
            _activePlayer.Health.ToString(),
            new(.31f, .055f, .045f, 1),
            new(.78f, .10f, .075f, 1));
        DrawStatusOrb(
            map, 2, 4,
            _activePlayer.Hunger / SurvivalService.MaximumHunger,
            $"{_activePlayer.Hunger:0}" +
            (_activePlayer.WellFedSeconds > 0 ? "+" : ""),
            new(.31f, .19f, .035f, 1),
            new(.86f, .55f, .075f, 1));
    }

    private void DrawStatusOrb(
        Vector4 map,
        int row,
        int icon,
        float progress,
        string value,
        Vector4 emptyColor,
        Vector4 fillColor)
    {
        const int diameter = 46;
        const int radius = diameter / 2;
        var centerX = MathF.Round(map.X);
        var centerY = MathF.Round(
            map.Y + 27 + row * 51);
        var tab = new Vector4(
            centerX - radius - 31,
            centerY - 10,
            38,
            20);
        DrawUiColor(tab, new(.035f, .031f, .024f, .98f));
        DrawPanelOutline(tab, 0, new(.035f, .030f, .022f, 1));
        DrawPanelOutline(tab, 1, new(.48f, .37f, .16f, 1));
        DrawPanelOutline(tab, 3, new(.10f, .085f, .050f, 1));

        DrawUiCircle(centerX, centerY, radius,
            new(.025f, .022f, .018f, 1));
        DrawUiCircle(centerX, centerY, radius - 2,
            new(.46f, .35f, .15f, 1));
        DrawUiCircle(centerX, centerY, radius - 4,
            new(.075f, .063f, .040f, 1));
        DrawUiCircle(centerX, centerY, radius - 6, emptyColor);
        DrawUiCircleFill(
            centerX, centerY, radius - 6,
            Math.Clamp(progress, 0, 1),
            fillColor);

        DrawPlayerUiIcon(
            icon,
            new(centerX - 16, centerY - 16, 32, 32));
        DrawCenteredUiText(
            value,
            tab,
            new FSColor(244, 230, 190, 255));
    }

    private void DrawUiCircle(
        float centerX,
        float centerY,
        int radius,
        Vector4 color)
    {
        if (_uiCircleFrame is null || _uiCircleTexture == 0) return;
        var diameter = radius * 2 + 1;
        DrawUiSprite(
            _uiCircleFrame,
            _uiCircleTexture,
            new(
                centerX - radius,
                centerY - radius,
                diameter,
                diameter),
            tint: color.Xyz,
            tintAmount: 1,
            drawOpacity: color.W);
    }

    private void DrawUiCircleFill(
        float centerX,
        float centerY,
        int radius,
        float progress,
        Vector4 color)
    {
        if (progress <= 0 ||
            _uiCircleFrame is null ||
            _uiCircleTexture == 0)
            return;
        progress = Math.Clamp(progress, 0, 1);
        var diameter = radius * 2 + 1;
        var filledHeight = Math.Max(1, diameter * progress);
        DrawUiSprite(
            _uiCircleFrame,
            _uiCircleTexture,
            new(
                centerX - radius,
                centerY + radius + 1 - filledHeight,
                diameter,
                filledHeight),
            uvRectangle: new(0, 1 - progress, 1, progress),
            tint: color.Xyz,
            tintAmount: 1,
            drawOpacity: color.W);
    }

    private static SpriteFrame CreateUiCircleFrame()
    {
        const int size = 64;
        const float center = (size - 1) * .5f;
        const float radius = size * .5f - 1;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var distance = MathF.Sqrt(
                (x - center) * (x - center) +
                (y - center) * (y - center));
            var alpha = (byte)Math.Clamp(
                (int)MathF.Round(
                    (radius + .75f - distance) * 255),
                0,
                255);
            var offset = (y * size + x) * 4;
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = alpha;
        }
        return new(size, size, 0, 0, pixels);
    }
}
