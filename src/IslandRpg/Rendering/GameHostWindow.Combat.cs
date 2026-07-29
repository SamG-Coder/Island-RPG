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
    private bool _combatLeftWasDown;

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
        _nextMeleeAttackAt = _clock + .55;
        _player.AttackAt(new(dummy.X, dummy.Y));
        _chatUi.AddMessage(
            "You begin attacking the training dummy.",
            ChatMessageStyle.Action);
    }

    internal void CancelMeleeCombat()
    {
        if (_combatTargetId is null) return;
        _combatTargetId = null;
        _nextMeleeAttackAt = 0;
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
        _player.AttackAt(target);
        if (_clock < _nextMeleeAttackAt) return;
        _nextMeleeAttackAt += MeleeCombatService.AttackIntervalSeconds;

        var roll = MeleeCombatService.Roll(
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle());
        if (!roll.Hit)
        {
            _chatUi.AddMessage("You miss.", ChatMessageStyle.Action);
            return;
        }

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
