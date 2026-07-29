using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Guid? _combatTargetId;
    private double _nextMeleeAttackAt;
    private double _swingStartedForAttackAt;
    private bool _combatLeftWasDown;
    private CombatHitSplat? _combatHitSplat;

    private readonly record struct CombatHitSplat(
        Guid TargetId,
        int Damage,
        bool Hit,
        double ShownAt);

    private static readonly MeleeCombatStance[] MeleeStances =
        Enum.GetValues<MeleeCombatStance>();

    private void QueueTrainingDummyAttack(WorldGroundObject dummy) =>
        _worldActions.QueueTrainingDummyAttack(dummy);

    internal void BeginTrainingDummyCombat(Guid dummyId)
    {
        if (_player is null ||
            FindGroundObject(dummyId) is not { } dummy ||
            dummy.ItemId != ItemIds.TrainingDummy)
            return;
        _combatTargetId = dummyId;
        _nextMeleeAttackAt = _clock + MeleeImpactDelay();
        _swingStartedForAttackAt = _nextMeleeAttackAt;
        _player.RestartAttackAt(new(dummy.X, dummy.Y));
        _chatUi.AddMessage(
            "You begin attacking the training dummy.",
            ChatMessageStyle.Action);
    }

    internal void CancelMeleeCombat()
    {
        if (_combatTargetId is null) return;
        _combatTargetId = null;
        _nextMeleeAttackAt = 0;
        _swingStartedForAttackAt = 0;
        if (_player?.Action == EntityAction.Attack)
            _player.Stop();
    }

    internal void UpdateMeleeCombat()
    {
        if (_combatTargetId is not { } targetId ||
            _activePlayer is null ||
            _player is null)
            return;
        if (FindGroundObjectLocation(targetId) is not { } location ||
            location.Object.ItemId != ItemIds.TrainingDummy)
        {
            CancelMeleeCombat();
            return;
        }
        var target = new Vector2(
            location.Object.X, location.Object.Y);
        if ((_player.Position - target).Length >
            MeleeCombatService.AttackRange + .25f)
        {
            CancelMeleeCombat();
            return;
        }
        var impactDelay = MeleeImpactDelay();
        if (_clock >= _nextMeleeAttackAt - impactDelay &&
            _swingStartedForAttackAt != _nextMeleeAttackAt)
        {
            _player.RestartAttackAt(target);
            _swingStartedForAttackAt = _nextMeleeAttackAt;
        }
        else
        {
            _player.AttackAt(target);
        }
        if (_clock < _nextMeleeAttackAt) return;
        _nextMeleeAttackAt += MeleeCombatService.AttackIntervalSeconds;

        var roll = MeleeCombatService.Roll(
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle());
        if (!roll.Hit)
        {
            _combatHitSplat = new(
                targetId, 0, false, _clock);
            _chatUi.AddMessage("You miss.", ChatMessageStyle.Action);
            return;
        }

        _combatHitSplat = new(
            targetId, roll.Damage, true, _clock);
        var health = location.Object.Health <= 0
            ? MeleeCombatService.TrainingDummyMaximumHealth
            : location.Object.Health;
        health -= roll.Damage;
        if (health <= 0)
        {
            health = MeleeCombatService.TrainingDummyMaximumHealth;
            _chatUi.AddMessage(
                "The training dummy is knocked down and reset.",
                ChatMessageStyle.Action);
        }
        location.Chunk.GroundObjects[location.Index] =
            location.Object with
            {
                Health = health,
                MaxHealth = MeleeCombatService.TrainingDummyMaximumHealth
            };
        QueueChunkSave(location.Chunk);

        var award = SkillService.AwardExperience(
            MeleeCombatService.ExperienceForStance(
                _activePlayer, _activePlayer.CombatStance),
            roll.Experience);
        _activePlayer = _activePlayer.CombatStance switch
        {
            MeleeCombatStance.Accurate => _activePlayer with
            {
                AttackExperience = award.Experience
            },
            MeleeCombatStance.Aggressive => _activePlayer with
            {
                StrengthExperience = award.Experience
            },
            _ => _activePlayer with
            {
                DefenceExperience = award.Experience
            }
        };
        AwardAdventureExperience(award.Gained);
        _activePlayer = _activePlayer with
        {
            UpdatedUtc = DateTime.UtcNow
        };
        _saves.SavePlayer(_activePlayer);
        _chatUi.AddMessage(
            $"You hit for {roll.Damage}. +{award.Gained} " +
            $"{CombatStatName(_activePlayer.CombatStance)} XP.",
            ChatMessageStyle.Experience);
        if (award.LevelledUp)
            _chatUi.AddMessage(
                $"Your {CombatStatName(_activePlayer.CombatStance)} " +
                $"level is now {award.Level}.",
                ChatMessageStyle.LevelUp);
    }

    private void UpdateCombatPanelInput(Vector2 pointer, bool leftDown)
    {
        if (_gameUi.ActivePanel != GameUiPanel.Combat ||
            _activePlayer is null)
        {
            _combatLeftWasDown = leftDown;
            return;
        }
        if (leftDown && !_combatLeftWasDown)
        {
            for (var index = 0; index < MeleeStances.Length; index++)
            {
                if (!CombatStanceBounds(
                        _gameUi.Panel.Bounds, index).Contains(pointer))
                    continue;
                _activePlayer = _activePlayer with
                {
                    CombatStance = MeleeStances[index],
                    UpdatedUtc = DateTime.UtcNow
                };
                _saves.SavePlayer(_activePlayer);
                break;
            }
        }
        _combatLeftWasDown = leftDown;
    }

    private void RenderCombatPanel()
    {
        if (_activePlayer is null) return;
        var panel = _gameUi.Panel.Bounds;
        DrawPanelCaption("Unarmed combat", panel);
        DrawSmallCenteredUiText(
            "Choose an attack style",
            new(panel.X + 10, panel.Y + 43, panel.Z - 20, 17),
            new FSColor(190, 181, 150, 255));
        for (var index = 0; index < MeleeStances.Length; index++)
            DrawCombatStance(
                CombatStanceBounds(panel, index),
                MeleeStances[index],
                _activePlayer.CombatStance == MeleeStances[index]);

        var stats = new Vector4(
            panel.X + 10, panel.Y + 232, panel.Z - 20, 54);
        DrawUiColor(stats, new(.043f, .039f, .030f, .97f));
        DrawPanelOutline(stats, 1, new(.26f, .21f, .12f, 1));
        DrawCombatStat(
            stats, 0, "Attack", _activePlayer.AttackExperience);
        DrawCombatStat(
            stats, 1, "Strength", _activePlayer.StrengthExperience);
        DrawCombatStat(
            stats, 2, "Defence", _activePlayer.DefenceExperience);
    }

    private void RenderCombatTargetHealthBar(Vector4 scene)
    {
        if (_combatTargetId is not { } targetId ||
            FindGroundObjectLocation(targetId) is not { } location ||
            !TryGroundItemVisual(
                location.Object.ItemId,
                out var frame, out _, out _, out _))
            return;
        var maximum = location.Object.MaxHealth > 0
            ? location.Object.MaxHealth
            : MeleeCombatService.TrainingDummyMaximumHealth;
        var health = location.Object.Health > 0
            ? location.Object.Health
            : maximum;
        DrawWorldHealthBar(
            scene,
            SpriteBounds(frame, GroundObjectWorld(location.Object)),
            health / (float)maximum);
        RenderCombatHitSplat(
            scene,
            SpriteBounds(frame, GroundObjectWorld(location.Object)));
    }

    private void RenderCombatHitSplat(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) targetBounds)
    {
        if (_combatHitSplat is not { } splat ||
            splat.TargetId != _combatTargetId)
            return;
        var age = (float)(_clock - splat.ShownAt);
        if (age >= MeleeCombatService.HitSplatSeconds)
        {
            _combatHitSplat = null;
            return;
        }

        var sceneScale = scene.Z / ReferenceWidth;
        var fade = Math.Clamp(
            (MeleeCombatService.HitSplatSeconds - age) / .55f, 0, 1);
        var entrance = Math.Clamp(age / .08f, 0, 1);
        var centerX = scene.X +
            (targetBounds.Left + targetBounds.Right) * .5f * sceneScale;
        var centerY = scene.Y +
            (targetBounds.Top +
             (targetBounds.Bottom - targetBounds.Top) * .42f) * sceneScale;
        var fullRadius = Math.Max(10, (int)MathF.Round(12 * sceneScale));
        var radius = Math.Max(3, (int)MathF.Round(fullRadius * entrance));
        DrawCombatSplatBadge(
            centerX, centerY, radius, splat.Hit, fade);
        var textBounds = new Vector4(
            centerX - radius, centerY - radius - 2,
            radius * 2, radius * 2);
        DrawCenteredUiText(
            splat.Damage.ToString(),
            new(textBounds.X + 1, textBounds.Y + 1,
                textBounds.Z, textBounds.W),
            new FSColor(28, 10, 7, (int)(235 * fade)));
        DrawCenteredUiText(
            splat.Damage.ToString(),
            textBounds,
            new FSColor(255, 246, 218, (int)(255 * fade)));
    }

    private void DrawCombatSplatBadge(
        float centerX, float centerY, int radius, bool hit, float fade)
    {
        var edge = hit
            ? new Vector4(.48f, .025f, .015f, fade)
            : new Vector4(.045f, .16f, .52f, fade);
        var face = hit
            ? new Vector4(.78f, .055f, .030f, fade)
            : new Vector4(.06f, .28f, .74f, fade);

        // Use the same compact eight-point impact silhouette for hits and
        // misses; only the combat meaning changes the palette.
        var point = Math.Max(2, radius / 4);
        DrawUiColor(new(
            centerX - point / 2f, centerY - radius - 1,
            point, radius * 2 + 2), edge);
        DrawUiColor(new(
            centerX - radius - 1, centerY - point / 2f,
            radius * 2 + 2, point), edge);
        var diagonalOffset = radius * .68f;
        var diagonalSize = Math.Max(2, point);
        foreach (var (x, y) in new[]
                 {
                     (-diagonalOffset, -diagonalOffset),
                     (diagonalOffset, -diagonalOffset),
                     (-diagonalOffset, diagonalOffset),
                     (diagonalOffset, diagonalOffset)
                 })
            DrawUiColor(new(
                centerX + x - diagonalSize / 2f,
                centerY + y - diagonalSize / 2f,
                diagonalSize,
                diagonalSize), edge);
        DrawUiCircle(centerX, centerY, radius, edge);
        DrawUiCircle(centerX, centerY, Math.Max(1, radius - 2), face);
    }

    private double MeleeImpactDelay()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Attack), out var animation))
            return .65;
        const int storedVillagerAngles = 5;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedVillagerAngles);
        var impactFrame = Math.Clamp(
            (int)MathF.Round((framesPerAngle - 1) * .62f),
            0, framesPerAngle - 1);
        return impactFrame * animation.SecondsPerFrame;
    }

    private void DrawCombatStance(
        Vector4 bounds,
        MeleeCombatStance stance,
        bool selected)
    {
        var hovered = bounds.Contains(MouseState.Position);
        DrawUiColor(
            bounds,
            selected
                ? new(.20f, .145f, .055f, .99f)
                : hovered
                    ? new(.13f, .105f, .055f, .98f)
                    : new(.052f, .047f, .036f, .98f));
        DrawPanelOutline(
            bounds, selected ? 2 : 1,
            selected
                ? new(.63f, .45f, .16f, 1)
                : new(.27f, .22f, .13f, 1));
        DrawUiText(
            stance.ToString(),
            new(bounds.X + 10, bounds.Y + 8),
            selected
                ? new FSColor(245, 225, 169, 255)
                : new FSColor(210, 199, 164, 255));
        DrawSmallCenteredUiText(
            stance switch
            {
                MeleeCombatStance.Accurate => "Trains Attack",
                MeleeCombatStance.Aggressive => "Trains Strength",
                _ => "Trains Defence"
            },
            new(
                bounds.X + bounds.Z - 78,
                bounds.Y + 5,
                70,
                bounds.W - 10),
            new FSColor(179, 169, 138, 255));
    }

    private void DrawCombatStat(
        Vector4 bounds, int column, string name, int experience)
    {
        var width = bounds.Z / 3;
        var cell = new Vector4(
            bounds.X + column * width,
            bounds.Y + 5,
            width,
            bounds.W - 10);
        DrawSmallCenteredUiText(
            name,
            new(cell.X, cell.Y, cell.Z, 15),
            new FSColor(173, 164, 136, 255));
        DrawCenteredUiText(
            SkillService.LevelForExperience(experience).ToString(),
            new(cell.X, cell.Y + 14, cell.Z, 25),
            new FSColor(235, 218, 171, 255));
    }

    private static Vector4 CombatStanceBounds(
        Vector4 panel, int index) =>
        new(
            panel.X + 10,
            panel.Y + 63 + index * 53,
            panel.Z - 20,
            45);

    private static string CombatStatName(MeleeCombatStance stance) =>
        stance switch
        {
            MeleeCombatStance.Accurate => "Attack",
            MeleeCombatStance.Aggressive => "Strength",
            _ => "Defence"
        };
}
