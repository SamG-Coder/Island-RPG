using FontStashSharp;
using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private Guid? _combatTargetId;
    private string? _combatVillagerId;
    private double _villagerCombatRepathAt;
    private Vector2 _villagerCombatPathTarget;
    private readonly Dictionary<string, double>
        _villagerAttackReactionAt = [];
    private double _nextMeleeAttackAt;
    private double _swingStartedForAttackAt;
    private double _meleeReturnToIdleAt;
    private bool _combatLeftWasDown;
    private CombatHitSplat? _combatHitSplat;

    private readonly record struct CombatHitSplat(
        string TargetKey,
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
        var target = new Vector2(dummy.X, dummy.Y);
        if (_nextMeleeAttackAt <= _clock)
        {
            _nextMeleeAttackAt = _clock + MeleeImpactDelay();
            _swingStartedForAttackAt = _nextMeleeAttackAt;
            _player.RestartAttackAt(target);
        }
        else if (_swingStartedForAttackAt == _nextMeleeAttackAt)
        {
            // Re-selecting a target during the current swing may adjust facing,
            // but must never restart the animation or its impact timing.
            _player.AttackAt(target);
        }
        _chatUi.AddMessage(
            "You begin attacking the training dummy.",
            ChatMessageStyle.Action);
    }

    internal void BeginVillagerCombat(string villagerId)
    {
        if (_player is null) return;
        var index = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (index < 0) return;
        var villager = _villagers[index];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        _combatTargetId = null;
        _combatVillagerId = villager.Id;
        if (Vector2.Distance(_player.Position, target) >
            MeleeCombatService.AttackRange + .3f)
        {
            _worldActions.QueueVillagerAttack(villager);
            return;
        }
        _nextMeleeAttackAt = _clock + MeleeImpactDelay();
        _swingStartedForAttackAt = _nextMeleeAttackAt;
        _player.RestartAttackAt(target);
        _chatUi.AddMessage(
            $"You begin attacking {villager.Name}.",
            ChatMessageStyle.Warning);
    }

    internal void CancelMeleeCombat()
    {
        if (_combatTargetId is null && _combatVillagerId is null)
            return;
        _combatTargetId = null;
        _combatVillagerId = null;
        _villagerCombatRepathAt = 0;
        // The ready time is global combat state. Movement or target changes
        // cancel targeting without granting an immediate fresh attack.
        _meleeReturnToIdleAt = 0;
        if (_player?.Action == EntityAction.Attack)
            _player.Stop();
    }

    internal void UpdateMeleeCombat()
    {
        if (_combatVillagerId is { } villagerId)
        {
            UpdateVillagerCombat(villagerId);
            return;
        }
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
        var interactionRange =
            MeleeCombatService.InteractionRange(
                _player.Position - target);
        if ((_player.Position - target).Length >
            interactionRange + .22f)
        {
            CancelMeleeCombat();
            return;
        }
        var impactDelay = MeleeImpactDelay();
        if (_swingStartedForAttackAt != _nextMeleeAttackAt &&
            _clock < _nextMeleeAttackAt - impactDelay)
        {
            if (_player.Action == EntityAction.Attack &&
                _clock >= _meleeReturnToIdleAt)
                _player.Stop();
            return;
        }
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
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();

        var roll = MeleeCombatService.Roll(
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle());
        if (!roll.Hit)
        {
            _combatHitSplat = new(
                targetId.ToString("N"), 0, false, _clock);
            _chatUi.AddMessage("You miss.", ChatMessageStyle.Action);
            return;
        }

        _combatHitSplat = new(
            targetId.ToString("N"), roll.Damage, true, _clock);
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

    private void UpdateVillagerCombat(string villagerId)
    {
        if (_activePlayer is null || _player is null)
            return;
        var index = _villagers.FindIndex(value =>
            value.Id == villagerId &&
            value.WorldLevel == _activeWorldLevel &&
            value.Health > 0);
        if (index < 0)
        {
            CancelMeleeCombat();
            return;
        }
        var villager = _villagers[index];
        var target = new Vector2(
            villager.PositionX, villager.PositionY);
        if (Vector2.Distance(_player.Position, target) >
            MeleeCombatService.AttackRange + .22f)
        {
            if (MeleeCombatService.ShouldRepathMovingTarget(
                    _clock,
                    _villagerCombatRepathAt,
                    _villagerCombatPathTarget,
                    target))
            {
                _villagerCombatRepathAt =
                    _clock +
                    MeleeCombatService.MovingTargetRepathSeconds;
                _villagerCombatPathTarget = target;
                _worldActions.QueueVillagerAttack(villager);
            }
            return;
        }
        var impactDelay = MeleeImpactDelay();
        if (_swingStartedForAttackAt != _nextMeleeAttackAt &&
            _clock < _nextMeleeAttackAt - impactDelay)
        {
            if (_player.Action == EntityAction.Attack &&
                _clock >= _meleeReturnToIdleAt)
                _player.Stop();
            return;
        }
        if (_clock >= _nextMeleeAttackAt - impactDelay &&
            _swingStartedForAttackAt != _nextMeleeAttackAt)
        {
            _player.RestartAttackAt(target);
            _swingStartedForAttackAt = _nextMeleeAttackAt;
        }
        else
            _player.AttackAt(target);
        if (_clock < _nextMeleeAttackAt) return;
        _nextMeleeAttackAt +=
            MeleeCombatService.AttackIntervalSeconds;
        _meleeReturnToIdleAt = _clock + MeleeRecoveryDelay();

        var roll = MeleeCombatService.Roll(
            _activePlayer.AttackExperience,
            _activePlayer.StrengthExperience,
            Random.Shared.NextSingle(),
            Random.Shared.NextSingle());
        if (!roll.Hit)
        {
            _combatHitSplat = new(
                villager.Id, 0, false, _clock);
            _chatUi.AddMessage(
                $"You miss {villager.Name}.",
                ChatMessageStyle.Miss);
            return;
        }
        villager = VillagerSimulation.RecordAttack(
            villager,
            _activePlayer.Id,
            _activePlayer.Name,
            roll.Damage,
            _worldGameSeconds);
        _combatHitSplat = new(
            villager.Id, roll.Damage, true, _clock);
        _villagers[index] = villager;
        _villagersDirty = true;
        ReactToVillagerAttack(index);
        _chatUi.AddMessage(
            $"You hit {villager.Name} for {roll.Damage}.",
            ChatMessageStyle.Damage);
        if (villager.Health <= 0)
        {
            _chatUi.AddMessage(
                $"{villager.Name} collapses.",
                ChatMessageStyle.Warning);
            CancelMeleeCombat();
        }
    }

    private void ReactToVillagerAttack(int victimIndex)
    {
        if (_activePlayer is null || _player is null ||
            (uint)victimIndex >= (uint)_villagers.Count)
            return;
        var victim = _villagers[victimIndex];
        if (_villagerAttackReactionAt.TryGetValue(
                victim.Id, out var nextReactionAt) &&
            _clock < nextReactionAt)
            return;
        _villagerAttackReactionAt[victim.Id] = _clock + 8;

        if (victim.Health > 0)
        {
            var victimPosition = new Vector2(
                victim.PositionX, victim.PositionY);
            var away = victimPosition - _player.Position;
            if (away.LengthSquared <= .0001f)
                away = Vector2.UnitX;
            else
                away = away.Normalized();
            var fleeTarget =
                WorldLevelNavigation.ReachableWalkableTarget(
                    _worldSeed,
                    victimPosition,
                    victimPosition + away * 4,
                    victim.WorldLevel,
                    maximumRadius: 2);
            victim = VillagerSimulation.ApplyDecision(
                victim,
                new(VillagerNeed.Safe, fleeTarget),
                VillagerSimulationTier.Nearby,
                _worldGameSeconds);
            _villagers[victimIndex] = victim;
            ShowVillagerCombatReaction(
                victimIndex,
                "Stop! Why are you attacking me?");
        }

        var closestWitnessIndex = -1;
        var closestDistance = float.MaxValue;
        var attackedPosition = new Vector2(
            victim.PositionX, victim.PositionY);
        for (var index = 0; index < _villagers.Count; index++)
        {
            if (index == victimIndex) continue;
            var witness = _villagers[index];
            if (witness.Health <= 0 ||
                witness.WorldLevel != victim.WorldLevel)
                continue;
            var distance = Vector2.DistanceSquared(
                new(witness.PositionX, witness.PositionY),
                attackedPosition);
            if (distance > 10 * 10) continue;
            witness = VillagerSimulation.RecordWitnessedAttack(
                witness,
                _activePlayer.Id,
                _activePlayer.Name,
                victim.Id,
                victim.Name,
                _worldGameSeconds);
            _villagers[index] = witness;
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closestWitnessIndex = index;
        }
        if (closestWitnessIndex >= 0)
            ShowVillagerCombatReaction(
                closestWitnessIndex,
                $"Stop attacking {victim.Name}!");
        _villagersDirty = true;
    }

    private void ShowVillagerCombatReaction(
        int villagerIndex,
        string message)
    {
        if ((uint)villagerIndex >= (uint)_villagers.Count)
            return;
        var villager = _villagers[villagerIndex];
        var seconds = ConversationLineSeconds(message);
        _villagerSpeechBubbles[villager.Id] =
            new(message, _clock + seconds);
        _chatUi.AddMessage(
            $"{villager.Name}: {message}",
            ChatMessageStyle.Warning);
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
        DrawPanelCaption("Combat", panel);
        for (var index = 0; index < MeleeStances.Length; index++)
            DrawCombatStance(
                CombatStanceBounds(panel, index),
                MeleeStances[index],
                _activePlayer.CombatStance == MeleeStances[index]);
    }

    private void RenderCombatTargetHealthBar(Vector4 scene)
    {
        var displayedVillagerId = _combatVillagerId;
        if (displayedVillagerId is null &&
            _combatHitSplat is { } recentSplat &&
            _clock - recentSplat.ShownAt <
            MeleeCombatService.HitSplatSeconds &&
            _villagers.Any(value =>
                value.Id == recentSplat.TargetKey))
            displayedVillagerId = recentSplat.TargetKey;
        if (displayedVillagerId is { } villagerId)
        {
            var villager = _villagers.FirstOrDefault(value =>
                value.Id == villagerId &&
                value.WorldLevel == _activeWorldLevel);
            if (villager is null ||
                !TryVillagerSpriteBounds(
                    villager, out var villagerBounds))
                return;
            DrawWorldHealthBar(
                scene,
                villagerBounds,
                villager.Health /
                (float)AdventureService.BaseMaximumHealth);
            RenderCombatHitSplat(
                scene, villagerBounds, villager.Id);
            return;
        }
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
            SpriteBounds(frame, GroundObjectWorld(location.Object)),
            targetId.ToString("N"));
    }

    private void RenderCombatHitSplat(
        Vector4 scene,
        (float Left, float Top, float Right, float Bottom) targetBounds,
        string targetKey)
    {
        if (_combatHitSplat is not { } splat ||
            splat.TargetKey != targetKey)
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
        // The target position belongs to the scaled world scene, but the
        // hit-splat is a UI element. Keep its pixel size stable across zoom,
        // window resolutions, and fullscreen display modes.
        const int fullRadius = 12;
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

    private double MeleeRecoveryDelay()
    {
        if (_player is null ||
            !_entityAnimations.TryGetValue(
                (_player.Gender, EntityAction.Attack), out var animation))
            return .4;
        const int storedVillagerAngles = 5;
        var framesPerAngle = Math.Max(
            1, animation.Graphic.Sprite.Frames.Count / storedVillagerAngles);
        var fullAnimationSeconds =
            framesPerAngle * animation.SecondsPerFrame;
        return Math.Max(
            animation.SecondsPerFrame,
            fullAnimationSeconds - MeleeImpactDelay());
    }

    private void DrawCombatStance(
        Vector4 bounds,
        MeleeCombatStance stance,
        bool selected)
    {
        var hovered = bounds.Contains(MouseState.Position);
        var accent = stance switch
        {
            MeleeCombatStance.Accurate =>
                new Vector4(.67f, .16f, .09f, 1),
            MeleeCombatStance.Aggressive =>
                new Vector4(.72f, .36f, .10f, 1),
            _ => new Vector4(.18f, .40f, .67f, 1)
        };
        DrawUiColor(
            bounds,
            selected
                ? new(.115f, .090f, .045f, .99f)
                : hovered
                    ? new(.085f, .072f, .045f, .98f)
                    : new(.052f, .047f, .036f, .98f));
        DrawPanelOutline(
            bounds, selected ? 2 : 1,
            selected
                ? new(.58f, .43f, .17f, 1)
                : new(.27f, .22f, .13f, 1));
        if (selected)
            DrawUiColor(
                new(bounds.X + 2, bounds.Y + 3, 3, bounds.W - 6),
                accent);
        var icon = stance switch
        {
            MeleeCombatStance.Accurate => 0,
            MeleeCombatStance.Aggressive => 1,
            _ => 2
        };
        DrawUiCircle(
            bounds.X + 23, bounds.Y + bounds.W * .5f, 16,
            new(.025f, .022f, .018f, 1));
        DrawUiCircle(
            bounds.X + 23, bounds.Y + bounds.W * .5f, 14,
            new(accent.X * .35f, accent.Y * .35f, accent.Z * .35f, 1));
        DrawCombatSkillIcon(
            icon,
            new(bounds.X + 9, bounds.Y + bounds.W * .5f - 14, 28, 28));
        DrawUiText(
            stance.ToString(),
            new(bounds.X + 44, bounds.Y + 6),
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
            new(bounds.X + 42, bounds.Y + 24, bounds.Z - 49, 14),
            selected
                ? new FSColor(198, 184, 143, 255)
                : new FSColor(153, 145, 119, 255));
    }

    private static Vector4 CombatStanceBounds(
        Vector4 panel, int index) =>
        new(
            panel.X + 10,
            panel.Y + 45 + index * 58,
            panel.Z - 20,
            51);

    private static string CombatStatName(MeleeCombatStance stance) =>
        stance switch
        {
            MeleeCombatStance.Accurate => "Attack",
            MeleeCombatStance.Aggressive => "Strength",
            _ => "Defence"
        };
}
